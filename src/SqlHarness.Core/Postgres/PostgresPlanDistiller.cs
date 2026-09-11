using System.Text.Json;

namespace SqlHarness.Core.Postgres;

/// <summary>A compact, deterministic projection of Postgres EXPLAIN FORMAT JSON.</summary>
public static class PostgresPlanDistiller
{
    private static readonly PlanDistillerLimits DefaultLimits = new(16 * 1024 * 1024, 100_000, 128);
    private const int MaximumPredicateLength = 200;
    private const string InvalidPlanMessage = "The execution plan is not a valid Postgres EXPLAIN JSON document.";
    private static readonly string[] PredicateKeys = ["Filter", "Index Cond", "Hash Cond", "Recheck Cond"];
    private static readonly MissingIndex[] NoMissingIndexes = [];

    /// <summary>Distills Postgres EXPLAIN JSON without database or network access.</summary>
    public static DistilledPlan Distill(string explainJson) => Distill(explainJson, DefaultLimits);

    internal static DistilledPlan Distill(string explainJson, PlanDistillerLimits limits)
    {
        if (explainJson is null
            || limits.MaximumCharacters <= 0
            || limits.MaximumElements <= 0
            || limits.MaximumDepth <= 0
            || explainJson.Length > limits.MaximumCharacters)
            throw SafetyFailure();

        try
        {
            var options = new JsonDocumentOptions
            {
                MaxDepth = checked(limits.MaximumDepth + 16),
            };
            using var document = JsonDocument.Parse(explainJson, options);
            var elementCount = 0;
            ValidateBounds(document.RootElement, depth: 1, limits, ref elementCount);
            var statements = ParseStatements(document.RootElement, limits);
            if (statements.Count == 0)
                throw SafetyFailure();
            return new DistilledPlan(statements);
        }
        catch (SqlHarnessSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or FormatException
                or OverflowException
                or ArgumentException)
        {
            throw SafetyFailure();
        }
    }

    private static void ValidateBounds(
        JsonElement element,
        int depth,
        PlanDistillerLimits limits,
        ref int elementCount)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return;

        if (depth > limits.MaximumDepth || ++elementCount > limits.MaximumElements)
            throw SafetyFailure();

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                ValidateBounds(property.Value, depth + 1, limits, ref elementCount);
            return;
        }

        foreach (var item in element.EnumerateArray())
            ValidateBounds(item, depth + 1, limits, ref elementCount);
    }

    private static IReadOnlyList<PlanStatement> ParseStatements(JsonElement root, PlanDistillerLimits limits)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            var statements = new List<PlanStatement>(root.GetArrayLength());
            foreach (var element in root.EnumerateArray())
                statements.Add(ParseStatement(element, limits));
            return statements;
        }

        if (root.ValueKind == JsonValueKind.Object)
            return [ParseStatement(root, limits)];

        throw SafetyFailure();
    }

    private static PlanStatement ParseStatement(JsonElement element, PlanDistillerLimits limits)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("Plan", out var plan)
            || plan.ValueKind != JsonValueKind.Object)
            throw SafetyFailure();

        var rootCost = Number(plan, "Total Cost");
        return new PlanStatement(
            StatementText: null,
            ParseNode(plan, rootCost, limits, depth: 1),
            NoMissingIndexes);
    }

    private static PlanNode ParseNode(JsonElement node, double? rootCost, PlanDistillerLimits limits, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object || depth > limits.MaximumDepth)
            throw SafetyFailure();

        var physicalOp = RequiredString(node, "Node Type");
        var nodeCost = Number(node, "Total Cost");
        var children = ParseChildren(node, rootCost, limits, depth);

        return new PlanNode(
            physicalOp,
            OptionalString(node, "Join Type"),
            OptionalString(node, "Relation Name"),
            OptionalString(node, "Index Name"),
            Number(node, "Plan Rows"),
            Long(node, "Actual Rows"),
            Long(node, "Actual Loops"),
            rootCost is > 0 && nodeCost is not null ? nodeCost / rootCost : null,
            ParsePredicate(node),
            ParseWarnings(node),
            children);
    }

    private static IReadOnlyList<PlanNode> ParseChildren(
        JsonElement node,
        double? rootCost,
        PlanDistillerLimits limits,
        int depth)
    {
        if (!node.TryGetProperty("Plans", out var plans) || plans.ValueKind == JsonValueKind.Null)
            return [];

        if (plans.ValueKind != JsonValueKind.Array)
            throw SafetyFailure();

        var children = new List<PlanNode>(plans.GetArrayLength());
        foreach (var child in plans.EnumerateArray())
            children.Add(ParseNode(child, rootCost, limits, depth + 1));
        return children;
    }

    private static string? ParsePredicate(JsonElement node)
    {
        foreach (var key in PredicateKeys)
        {
            if (!node.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            var text = value.GetString();
            if (string.IsNullOrEmpty(text))
                continue;

            return text.Length <= MaximumPredicateLength
                ? text
                : text[..MaximumPredicateLength];
        }

        return null;
    }

    private static IReadOnlyList<string> ParseWarnings(JsonElement node)
    {
        if (!node.TryGetProperty("Warnings", out var warnings))
            return [];

        if (warnings.ValueKind == JsonValueKind.String)
        {
            var text = warnings.GetString();
            return string.IsNullOrEmpty(text) ? [] : [text];
        }

        if (warnings.ValueKind != JsonValueKind.Array)
            throw SafetyFailure();

        var list = new List<string>(warnings.GetArrayLength());
        foreach (var item in warnings.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw SafetyFailure();
            var text = item.GetString();
            if (!string.IsNullOrEmpty(text))
                list.Add(text);
        }

        return list;
    }

    private static string RequiredString(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw SafetyFailure();
        return value.GetString() ?? throw SafetyFailure();
    }

    private static string? OptionalString(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw SafetyFailure();
        return value.GetString();
    }

    private static double? Number(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var parsed)
            || !double.IsFinite(parsed))
            throw SafetyFailure();
        return parsed;
    }

    private static long? Long(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number)
            throw SafetyFailure();
        if (value.TryGetInt64(out var parsed))
            return parsed;
        if (!value.TryGetDouble(out var asDouble) || !double.IsFinite(asDouble))
            throw SafetyFailure();
        return checked((long)asDouble);
    }

    private static SqlHarnessSafetyException SafetyFailure() => new(InvalidPlanMessage);
}
