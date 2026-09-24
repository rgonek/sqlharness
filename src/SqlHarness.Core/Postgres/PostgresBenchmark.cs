using System.Globalization;
using System.Text.Json;

using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace SqlHarness.Core.Postgres;

internal sealed record PostgresBenchmarkStats(
    long CpuTimeMs,
    long ElapsedTimeMs,
    long LogicalReads,
    IReadOnlyDictionary<string, long> Tables);

internal static class PostgresBenchmark
{
    internal const string ExplainPrefix = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\n";
    private const string MeasuredBatchMessage =
        "Measured SQL must be a single SELECT, VALUES, or WITH … SELECT statement.";

    internal static string Wrap(string sql)
    {
        var trimmed = sql.TrimEnd();
        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1];
        return ExplainPrefix + trimmed;
    }

    internal static PostgresBenchmarkStats ParseStats(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = ExplainObject(document.RootElement);
        var elapsedMs = (long)Math.Round(
            Number(root, "Planning Time") + Number(root, "Execution Time"),
            MidpointRounding.AwayFromZero);
        var tables = new Dictionary<string, long>(StringComparer.Ordinal);
        long logicalReads = 0;
        if (root.TryGetProperty("Plan", out var plan))
            WalkBuffers(plan, tables, ref logicalReads);
        return new PostgresBenchmarkStats(0, elapsedMs, logicalReads, tables);
    }

    internal static void ValidateMeasuredBatch(string sql)
    {
        Sequence<Statement> statements;
        try
        {
            statements = new SqlQueryParser().Parse(sql.AsSpan(), new PostgreSqlDialect());
        }
        catch (Exception)
        {
            throw new SqlHarnessSafetyException(MeasuredBatchMessage);
        }

        if (statements.Count != 1 || statements[0] is not Statement.Select select || !IsExplainableSelect(select.Query))
            throw new SqlHarnessSafetyException(MeasuredBatchMessage);
    }

    internal static async Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct)
    {
        ValidateMeasuredBatch(sql);

        var messageStart = session.Messages.Count;
        string planJson;
        await using (var reader = await session.ExecuteReaderAsync(
            new SqlExecutionCommand(Wrap(sql), parameters, timeoutSeconds), ct))
        {
            planJson = await ReadExplainJsonAsync(reader, ct);
        }

        var messages = session.Messages.Skip(messageStart).ToArray();
        foreach (var message in messages)
            raw.AddMessage("sql", message);
        raw.AddMessage("planXml", planJson);

        var stats = ParseStats(planJson);
        var plans = new[] { ExecutionPlanParser.Parse(planJson) };

        CanonicalResult canonical;
        CanonicalComparisonResult comparison;
        if (captureComparison)
        {
            await using var sidecar = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(sql, parameters, timeoutSeconds), ct);
            var collected = await BenchmarkCollector.CollectCompareAsync(
                sidecar, raw, captureComparison: true, comparisonMaximumRows, ct);
            canonical = collected.Canonical;
            comparison = collected.Comparison;
        }
        else
        {
            using var empty = new CanonicalResultAccumulator();
            canonical = empty.Complete();
            comparison = BenchmarkCollector.EmptyComparison;
        }

        var artifact = new CompareRunArtifact(
            variant,
            repetition,
            stats.CpuTimeMs,
            stats.ElapsedTimeMs,
            stats.LogicalReads,
            stats.Tables,
            canonical.Hash,
            [planJson],
            messages.Length);
        return new CollectedCompareRun(artifact, plans, comparison);
    }

    private static async Task<string> ReadExplainJsonAsync(ISqlReader reader, CancellationToken ct)
    {
        if (reader.FieldCount < 1 || !await reader.ReadAsync(ct))
            throw new InvalidOperationException("EXPLAIN did not return a JSON plan document.");

        var json = ReadJsonValue(reader.GetValue(0));
        while (await reader.ReadAsync(ct))
        {
        }

        while (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
            }
        }

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("EXPLAIN did not return a JSON plan document.");
        return json;
    }

    private static string ReadJsonValue(object value) => value switch
    {
        string text => text,
        JsonElement element => element.GetRawText(),
        JsonDocument document => document.RootElement.GetRawText(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static bool IsExplainableSelect(Query query) =>
        !HasSelectInto(query) && !HasWrite(query);

    private static bool HasSelectInto(Query query)
    {
        if (query.With is { } with)
        {
            foreach (var cte in with.CteTables)
            {
                if (HasSelectInto(cte.Query))
                    return true;
            }
        }

        return HasSelectInto(query.Body);
    }

    private static bool HasSelectInto(SetExpression body) => body switch
    {
        SetExpression.SelectExpression select => select.Select.Into is not null,
        SetExpression.QueryExpression nested => HasSelectInto(nested.Query),
        SetExpression.SetOperation op => HasSelectInto(op.Left) || HasSelectInto(op.Right),
        _ => false,
    };

    private static bool HasWrite(Query query)
    {
        if (query.With is { } with)
        {
            foreach (var cte in with.CteTables)
            {
                if (HasWrite(cte.Query))
                    return true;
            }
        }

        return HasWrite(query.Body);
    }

    private static bool HasWrite(SetExpression body) => body switch
    {
        SetExpression.SelectExpression => false,
        SetExpression.ValuesExpression => false,
        SetExpression.TableExpression => false,
        SetExpression.QueryExpression nested => HasWrite(nested.Query),
        SetExpression.SetOperation op => HasWrite(op.Left) || HasWrite(op.Right),
        _ => true,
    };

    private static JsonElement ExplainObject(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

    private static void WalkBuffers(JsonElement node, Dictionary<string, long> tables, ref long logicalReads)
    {
        var blocks = Blocks(node, "Shared Hit Blocks")
            + Blocks(node, "Shared Read Blocks")
            + Blocks(node, "Local Hit Blocks")
            + Blocks(node, "Local Read Blocks");
        logicalReads += blocks;
        if (node.TryGetProperty("Relation Name", out var relation) && relation.ValueKind == JsonValueKind.String)
        {
            var name = relation.GetString() ?? string.Empty;
            tables[name] = tables.GetValueOrDefault(name) + blocks;
        }

        if (node.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in plans.EnumerateArray())
                WalkBuffers(child, tables, ref logicalReads);
        }
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static long Blocks(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        return value.TryGetInt64(out var blocks) ? blocks : (long)value.GetDouble();
    }
}