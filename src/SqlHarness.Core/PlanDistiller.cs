using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace SqlHarness.Core;

/// <summary>A compact, deterministic projection of SQL Server Showplan XML.</summary>
/// <remarks>The model is produced from XML and serialized to JSON; JSON deserialization is not supported.</remarks>
public sealed record DistilledPlan(IReadOnlyList<PlanStatement> Statements);

[JsonConverter(typeof(PlanStatementJsonConverter))]
public sealed record PlanStatement(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StatementText,
    PlanNode Root,
    IReadOnlyList<MissingIndex> MissingIndexes);

[JsonConverter(typeof(PlanNodeJsonConverter))]
public sealed record PlanNode(
    string PhysicalOp,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogicalOp,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObjectName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IndexName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? EstimatedRows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ActualRows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Executions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? CostFraction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Predicate,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PlanNode> Children);

public sealed record MissingIndex(
    string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    double Impact);

internal sealed record PlanDistillerLimits(int MaximumCharacters, int MaximumElements, int MaximumDepth);

public static class PlanDistiller
{
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly PlanDistillerLimits DefaultLimits = new(16 * 1024 * 1024, 100_000, 128);
    private const int MaximumPredicateLength = 200;
    private const string InvalidPlanMessage = "The execution plan is not a valid SQL Server Showplan document.";

    /// <summary>Distills SQL Server Showplan XML without database or network access.</summary>
    /// <remarks>The returned model supports deterministic JSON serialization only; its converters intentionally reject JSON deserialization.</remarks>
    public static DistilledPlan Distill(string showplanXml) => Distill(showplanXml, DefaultLimits);

    internal static DistilledPlan Distill(string showplanXml, PlanDistillerLimits limits)
    {
        if (showplanXml is null
            || limits.MaximumCharacters <= 0
            || limits.MaximumElements <= 0
            || limits.MaximumDepth <= 0
            || showplanXml.Length > limits.MaximumCharacters)
            throw SafetyFailure();

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = limits.MaximumCharacters,
                MaxCharactersFromEntities = 0
            };

            using var stringReader = new StringReader(showplanXml);
            using var reader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            ValidateDocument(document, limits);

            var statements = document
                .Descendants(Showplan("StmtSimple"))
                .Select(ParseStatement)
                .ToArray();

            return new DistilledPlan(statements);
        }
        catch (SqlHarnessSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or FormatException or OverflowException)
        {
            throw SafetyFailure();
        }
    }

    private static PlanStatement ParseStatement(XElement statement)
    {
        var queryPlan = statement.Elements(Showplan("QueryPlan")).FirstOrDefault()
            ?? throw SafetyFailure();
        var root = queryPlan.Elements(Showplan("RelOp")).FirstOrDefault()
            ?? throw SafetyFailure();
        var rootCost = Number(root, "EstimatedTotalSubtreeCost");

        return new PlanStatement(
            Attribute(statement, "StatementText"),
            ParseNode(root, rootCost),
            ParseMissingIndexes(queryPlan));
    }

    private static PlanNode ParseNode(XElement relOp, double? rootCost)
    {
        var owned = OwnedElements(relOp).ToArray();
        var objectElement = owned.FirstOrDefault(element => element.Name == Showplan("Object"));
        var runtime = owned.FirstOrDefault(element => element.Name == Showplan("RunTimeInformation"));
        var counters = runtime?.Elements(Showplan("RunTimeCountersPerThread")).ToArray();
        var nodeCost = Number(relOp, "EstimatedTotalSubtreeCost");

        return new PlanNode(
            Attribute(relOp, "PhysicalOp") ?? string.Empty,
            Attribute(relOp, "LogicalOp"),
            Attribute(objectElement, "Table"),
            Attribute(objectElement, "Index"),
            Number(relOp, "EstimateRows"),
            counters is null ? null : Sum(counters, "ActualRows"),
            counters is null ? null : Sum(counters, "ActualExecutions"),
            rootCost is > 0 && nodeCost is not null ? nodeCost / rootCost : null,
            ParsePredicate(owned),
            ParseWarnings(owned),
            DirectChildOperators(relOp).Select(child => ParseNode(child, rootCost)).ToArray());
    }

    private static IReadOnlyList<MissingIndex> ParseMissingIndexes(XElement queryPlan) =>
        queryPlan
            .Elements(Showplan("MissingIndexes"))
            .Elements(Showplan("MissingIndexGroup"))
            .SelectMany(group => group.Elements(Showplan("MissingIndex")), (group, index) => new MissingIndex(
                Attribute(index, "Table") ?? string.Empty,
                Columns(index, "EQUALITY"),
                Columns(index, "INEQUALITY"),
                Columns(index, "INCLUDE"),
                Number(group, "Impact") ?? 0))
            .ToArray();

    private static IReadOnlyList<string> Columns(XElement index, string usage) =>
        index.Elements(Showplan("ColumnGroup"))
            .Where(group => string.Equals(Attribute(group, "Usage"), usage, StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => group.Elements(Showplan("Column")))
            .Select(column => Attribute(column, "Name"))
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();

    private static string? ParsePredicate(IReadOnlyList<XElement> owned)
    {
        var predicate = owned.FirstOrDefault(element =>
            element.Name == Showplan("SeekPredicates") || element.Name == Showplan("Predicate"));
        var scalar = predicate?.DescendantsAndSelf(Showplan("ScalarOperator"))
            .Select(element => Attribute(element, "ScalarString"))
            .FirstOrDefault(value => value is not null);

        return scalar is null || scalar.Length <= MaximumPredicateLength
            ? scalar
            : scalar[..MaximumPredicateLength];
    }

    private static IReadOnlyList<string> ParseWarnings(IReadOnlyList<XElement> owned)
    {
        var warnings = owned.Where(element => element.Name == Showplan("Warnings"));
        return warnings.SelectMany(warning =>
                warning.Attributes().Where(IsUnqualified).Select(FormatAttribute)
                    .Concat(warning.Elements()
                        .Where(element => element.Name.NamespaceName == ShowplanNamespace)
                        .Select(FormatElement)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatElement(XElement element)
    {
        var attributes = element.Attributes()
            .Where(IsUnqualified)
            .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
            .Select(attribute => $"{attribute.Name.LocalName}={attribute.Value}");
        var suffix = string.Join(';', attributes);
        return suffix.Length == 0 ? element.Name.LocalName : $"{element.Name.LocalName}[{suffix}]";
    }

    private static string FormatAttribute(XAttribute attribute) =>
        $"{attribute.Name.LocalName}={attribute.Value}";

    private static IEnumerable<XElement> OwnedElements(XElement relOp)
    {
        foreach (var child in relOp.Elements())
        {
            if (child.Name == Showplan("RelOp"))
                continue;

            yield return child;
            foreach (var descendant in OwnedElements(child))
                yield return descendant;
        }
    }

    private static IEnumerable<XElement> DirectChildOperators(XElement relOp)
    {
        foreach (var child in relOp.Elements())
        {
            if (child.Name == Showplan("RelOp"))
            {
                yield return child;
                continue;
            }

            foreach (var descendant in DirectChildOperators(child))
                yield return descendant;
        }
    }

    private static long Sum(IEnumerable<XElement> counters, string attributeName)
    {
        long total = 0;
        foreach (var counter in counters)
        {
            var value = Attribute(counter, attributeName);
            if (value is null || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw SafetyFailure();
            total = checked(total + parsed);
        }
        return total;
    }

    private static double? Number(XElement element, string attributeName)
    {
        var value = Attribute(element, attributeName);
        if (value is null)
            return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
            throw SafetyFailure();
        return parsed;
    }

    private static string? Attribute(XElement? element, string localName) =>
        element?.Attribute(localName)?.Value;

    private static bool IsUnqualified(XAttribute attribute) => attribute.Name.NamespaceName.Length == 0;

    private static XName Showplan(string localName) => XName.Get(localName, ShowplanNamespace);

    private static void ValidateDocument(XDocument document, PlanDistillerLimits limits)
    {
        if (document.Root?.Name != Showplan("ShowPlanXML"))
            throw SafetyFailure();

        var elementCount = 0;
        foreach (var element in document.Descendants())
        {
            if (++elementCount > limits.MaximumElements
                || element.Ancestors().Take(limits.MaximumDepth + 1).Count() > limits.MaximumDepth)
                throw SafetyFailure();
        }
    }

    private static SqlHarnessSafetyException SafetyFailure() => new(InvalidPlanMessage);
}

internal sealed class PlanStatementJsonConverter : JsonConverter<PlanStatement>
{
    public override PlanStatement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, PlanStatement value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.StatementText is not null)
            writer.WriteString(Name(options, nameof(value.StatementText)), value.StatementText);
        writer.WritePropertyName(Name(options, nameof(value.Root)));
        JsonSerializer.Serialize(writer, value.Root, options);
        if (value.MissingIndexes.Count > 0)
        {
            writer.WritePropertyName(Name(options, nameof(value.MissingIndexes)));
            JsonSerializer.Serialize(writer, value.MissingIndexes, options);
        }
        writer.WriteEndObject();
    }

    private static string Name(JsonSerializerOptions options, string name) =>
        options.PropertyNamingPolicy?.ConvertName(name) ?? name;
}

internal sealed class PlanNodeJsonConverter : JsonConverter<PlanNode>
{
    public override PlanNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, PlanNode value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(Name(options, nameof(value.PhysicalOp)), value.PhysicalOp);
        Write(writer, Name(options, nameof(value.LogicalOp)), value.LogicalOp);
        Write(writer, Name(options, nameof(value.ObjectName)), value.ObjectName);
        Write(writer, Name(options, nameof(value.IndexName)), value.IndexName);
        Write(writer, Name(options, nameof(value.EstimatedRows)), value.EstimatedRows);
        Write(writer, Name(options, nameof(value.ActualRows)), value.ActualRows);
        Write(writer, Name(options, nameof(value.Executions)), value.Executions);
        Write(writer, Name(options, nameof(value.CostFraction)), value.CostFraction);
        Write(writer, Name(options, nameof(value.Predicate)), value.Predicate);
        if (value.Warnings.Count > 0)
        {
            writer.WritePropertyName(Name(options, nameof(value.Warnings)));
            JsonSerializer.Serialize(writer, value.Warnings, options);
        }
        if (value.Children.Count > 0)
        {
            writer.WritePropertyName(Name(options, nameof(value.Children)));
            JsonSerializer.Serialize(writer, value.Children, options);
        }
        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }

    private static void Write(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is not null)
            writer.WriteNumber(name, value.Value);
    }

    private static void Write(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is not null)
            writer.WriteNumber(name, value.Value);
    }

    private static string Name(JsonSerializerOptions options, string name) =>
        options.PropertyNamingPolicy?.ConvertName(name) ?? name;
}
