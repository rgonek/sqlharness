using System.Text;
using System.Text.Json;

namespace SqlHarness.Core;

/// <summary>Reads fixed typed parameters from a bounded .sqljson file.</summary>
public static class ParameterInputFileReader
{
    public static async Task<IReadOnlyList<SqlHarnessParameterInput>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".sqljson", StringComparison.OrdinalIgnoreCase))
            throw new ParameterSetFileException("Fixed parameter file must use the .sqljson extension.");
        byte[] bytes;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > ParameterSetFileReader.MaximumBytes)
                throw new ParameterSetFileException($"Fixed parameter file '{path}' exceeds the 64 KiB limit.");
            bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }
        catch (ParameterSetFileException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (exception is OperationCanceledException)
                throw;
            throw new ParameterSetFileException($"Fixed parameter file '{path}' could not be read.");
        }

        try
        {
            return Parse(bytes, path);
        }
        catch (JsonException)
        {
            throw new ParameterSetFileException($"Fixed parameter file '{path}' has invalid JSON.");
        }
    }

    public static IReadOnlyList<SqlHarnessParameterInput> Parse(byte[] bytes, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > ParameterSetFileReader.MaximumBytes)
            throw new ParameterSetFileException($"Fixed parameter file '{path}' exceeds the 64 KiB limit.");

        var json = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsMemory(Encoding.UTF8.Preamble.Length) : bytes.AsMemory();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        });
        var root = document.RootElement;
        var rootProperties = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().ToArray() : [];
        if (rootProperties.Length != 1 || rootProperties[0].Name != "parameters" || rootProperties[0].Value.ValueKind != JsonValueKind.Array)
            throw new ParameterSetFileException($"Fixed parameter file '{path}' must contain only a parameters array.");
        var parameters = rootProperties[0].Value;

        var output = new List<SqlHarnessParameterInput>();
        foreach (var element in parameters.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new ParameterSetFileException($"Fixed parameter file '{path}' contains a non-object parameter.");
            var allowed = new HashSet<string>(["name", "type", "value", "kind", "rows", "rowsFrom", "values", "valuesFrom"], StringComparer.Ordinal);
            if (element.EnumerateObject().Any(property => !allowed.Remove(property.Name)))
                throw new ParameterSetFileException($"Fixed parameter file '{path}' contains an unknown or duplicate parameter property.");
            if (!element.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameElement.GetString()))
                throw new ParameterSetFileException($"Fixed parameter file '{path}' contains a parameter without a name.");
            var name = nameElement.GetString()!;
            var type = ReadString(element, "type");
            var value = ReadString(element, "value");
            var kind = ReadString(element, "kind") ?? "scalar";
            IReadOnlyList<IReadOnlyList<string?>>? rows = null;
            IReadOnlyList<string?>? values = null;
            var rowsFrom = ReadString(element, "rowsFrom");
            var valuesFrom = ReadString(element, "valuesFrom");
            if (element.TryGetProperty("rows", out var rowsElement))
            {
                if (rowsElement.ValueKind != JsonValueKind.Array)
                    throw new ParameterSetFileException($"Fixed parameter file '{path}' has invalid rows for parameter '{name}'.");
                rows = rowsElement.EnumerateArray().Select(row =>
                {
                    if (row.ValueKind != JsonValueKind.Array)
                        throw new ParameterSetFileException($"Fixed parameter file '{path}' has invalid rows for parameter '{name}'.");
                    return (IReadOnlyList<string?>)row.EnumerateArray().Select(item => item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Null => null,
                        _ => throw new ParameterSetFileException($"Fixed parameter file '{path}' has invalid rows for parameter '{name}'."),
                    }).ToArray();
                }).ToArray();
            }
            if (element.TryGetProperty("values", out var valuesElement))
            {
                if (valuesElement.ValueKind != JsonValueKind.Array)
                    throw new ParameterSetFileException($"Fixed parameter file '{path}' has invalid values for parameter '{name}'.");
                values = valuesElement.EnumerateArray().Select(item => item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Null => null,
                    _ => throw new ParameterSetFileException($"Fixed parameter file '{path}' has invalid values for parameter '{name}'."),
                }).ToArray();
            }
            if (rowsFrom is not null && valuesFrom is not null)
                throw new ParameterSetFileException($"Fixed parameter file '{path}' parameter '{name}' has multiple source SQL fields.");

            output.Add(new SqlHarnessParameterInput(name, type, value)
            {
                Kind = kind,
                Rows = rows,
                Values = values,
                SourceSql = rowsFrom ?? valuesFrom,
            });
        }

        if (output.Count == 0)
            throw new ParameterSetFileException($"Fixed parameter file '{path}' has no parameters.");
        return output;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null when name is "type" or "value" => null,
            _ => throw new JsonException(),
        };
    }
}
