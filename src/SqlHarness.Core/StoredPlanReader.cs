using System.Text.Json;

namespace SqlHarness.Core;

/// <summary>Reads the bounded JSON representation written beside benchmark plans.</summary>
public static class StoredPlanReader
{
    private const int MaximumDepth = 128;
    private const int MaximumElements = 100_000;

    public static DistilledPlan Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ArtifactReader.MaxReportBytes)
            throw new JsonException("Stored plan exceeds the artifact size limit.");

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !TryGet(root, "statements", out var statements)
            || statements.ValueKind != JsonValueKind.Array)
            throw new JsonException("Stored plan statements are invalid.");

        var count = 0;
        var result = new List<PlanStatement>();
        foreach (var statement in statements.EnumerateArray())
        {
            if (++count > MaximumElements || statement.ValueKind != JsonValueKind.Object
                || !TryGet(statement, "root", out var node))
                throw new JsonException("Stored plan statement is invalid.");
            var sql = TryGet(statement, "sql", out var sqlElement) && sqlElement.ValueKind == JsonValueKind.String
                ? sqlElement.GetString() : null;
            var indexes = new List<MissingIndex>();
            if (TryGet(statement, "missingIndexes", out var indexArray))
            {
                if (indexArray.ValueKind != JsonValueKind.Array) throw new JsonException("Stored plan indexes are invalid.");
                foreach (var index in indexArray.EnumerateArray())
                {
                    if (++count > MaximumElements || index.ValueKind != JsonValueKind.Object
                        || !TryGet(index, "table", out var table) || table.ValueKind != JsonValueKind.String)
                        throw new JsonException("Stored plan index is invalid.");
                    indexes.Add(new MissingIndex(String(index, "database"), String(index, "schema"), table.GetString()!,
                        Strings(index, "equalityColumns"), Strings(index, "inequalityColumns"), Strings(index, "includeColumns"),
                        Number(index, "impact") ?? 0));
                }
            }
            result.Add(new PlanStatement(sql, ReadNode(node, 0, ref count), indexes));
        }
        return new DistilledPlan(result);
    }

    private static PlanNode ReadNode(JsonElement value, int depth, ref int count)
    {
        if (depth > MaximumDepth || ++count > MaximumElements || value.ValueKind != JsonValueKind.Object
            || !TryGet(value, "physicalOp", out var physical) || physical.ValueKind != JsonValueKind.String)
            throw new JsonException("Stored plan node is invalid.");
        var children = TryGet(value, "children", out var childArray) ? childArray : default;
        if (children.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array))
            throw new JsonException("Stored plan node is invalid.");
        var parsedChildren = new List<PlanNode>();
        if (children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) parsedChildren.Add(ReadNode(child, depth + 1, ref count));
        return new PlanNode(physical.GetString()!, String(value, "logicalOp"), String(value, "objectName"), String(value, "indexName"),
            Number(value, "estimatedRows"), Long(value, "actualRows"), Long(value, "executions"), Number(value, "costFraction"),
            String(value, "predicate"), Strings(value, "warnings"), parsedChildren);
    }

    private static IReadOnlyList<string> Strings(JsonElement parent, string name)
    {
        if (!TryGet(parent, name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new JsonException($"Stored plan {name} is invalid.");
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new JsonException($"Stored plan {name} is invalid.")).ToArray();
    }

    private static string? String(JsonElement parent, string name) =>
        !TryGet(parent, name, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException("Stored plan field is invalid.");
    private static double? Number(JsonElement parent, string name) =>
        !TryGet(parent, name, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw new JsonException("Stored plan number is invalid.");
    private static long? Long(JsonElement parent, string name) =>
        !TryGet(parent, name, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        value.TryGetInt64(out var number) ? number : throw new JsonException("Stored plan integer is invalid.");
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}