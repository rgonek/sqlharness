using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SqlHarness.Core;

/// <summary>Uppercase SHA-256 of a plan's operator tree, not one execution's runtime counters.</summary>
internal static class PlanIdentity
{
    private static readonly HashSet<string> RuntimeElements = new(StringComparer.Ordinal)
    {
        "ParameterList",
        "QueryTimeStats",
        "RunTimeInformation",
        "RuntimeInformation",
        "WaitStats",
    };

    internal static string Hash(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = document.Length > 0 && document[0] == '\uFEFF' ? document[1..] : document;
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty)
            return Sha256(ReadOnlySpan<byte>.Empty);

        if (trimmed[0] is '{' or '[')
        {
            try
            {
                return HashPostgres(text);
            }
            catch (JsonException)
            {
                return Sha256(text);
            }
        }

        if (trimmed[0] is '<')
        {
            try
            {
                return HashShowplan(text);
            }
            catch (XmlException)
            {
                return Sha256(text);
            }
        }

        return Sha256(text);
    }

    private static string HashShowplan(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root is null)
            return Sha256(string.Empty);

        // QueryPlanHash is the server's operator-tree identity. Runtime XML is not.
        var planHashes = new List<string>();
        foreach (var element in document.Root.DescendantsAndSelf())
        {
            var value = element.Attribute("QueryPlanHash")?.Value;
            if (!string.IsNullOrEmpty(value))
                planHashes.Add(value);
        }

        if (planHashes.Count > 0)
            return Sha256(string.Join('\n', planHashes));

        foreach (var element in document.Root.Descendants().Where(element => RuntimeElements.Contains(element.Name.LocalName)).ToArray())
        {
            if (element.Parent is not null)
                element.Remove();
        }

        foreach (var element in document.Root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().Where(attribute => IsRuntimeAttribute(attribute.Name.LocalName)).ToArray())
                attribute.Remove();
        }

        foreach (var whitespace in document.DescendantNodes().OfType<XText>().Where(node => string.IsNullOrWhiteSpace(node.Value)).ToArray())
            whitespace.Remove();

        return Sha256(document.ToString(SaveOptions.DisableFormatting));
    }

    private static bool IsRuntimeAttribute(string localName) =>
        localName.StartsWith("Actual", StringComparison.Ordinal)
        || localName is "ParameterCompiledValue" or "ParameterRuntimeValue" or "CpuTime" or "ElapsedTime";

    private static string HashPostgres(string text)
    {
        using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            WritePlanRoots(writer, parsed.RootElement);
            writer.WriteEndArray();
        }

        return Sha256(buffer.ToArray());
    }

    private static void WritePlanRoots(Utf8JsonWriter writer, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                WritePlanRoot(writer, item);
            return;
        }

        WritePlanRoot(writer, root);
    }

    private static void WritePlanRoot(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("Plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
        {
            WriteJson(writer, plan);
            return;
        }

        if (element.TryGetProperty("Node Type", out _))
            WriteJson(writer, element);
    }

    // Actual time, rows, and buffer counts change every execution. The node tree does not.
    private static bool IsRuntimePlanProperty(string name) =>
        name.StartsWith("Actual", StringComparison.Ordinal)
        || name.EndsWith(" Blocks", StringComparison.Ordinal)
        || name.EndsWith(" Time", StringComparison.Ordinal)
        || name.StartsWith("Rows Removed by ", StringComparison.Ordinal);

    private static void WriteJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = new List<JsonProperty>();
                foreach (var property in element.EnumerateObject())
                {
                    if (!IsRuntimePlanProperty(property.Name))
                        properties.Add(property);
                }

                properties.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteJson(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteJson(writer, item);
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                writer.WriteRawValue(element.GetRawText());
                return;
            default:
                writer.WriteNullValue();
                return;
        }
    }

    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}