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
    IReadOnlyDictionary<string, long> Tables,
    BenchmarkRunMetrics Metrics);

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
        var hasPlanning = TryDecimal(root, "Planning Time", out var planning);
        var hasExecution = TryDecimal(root, "Execution Time", out var execution);
        var timingComplete = hasPlanning && hasExecution;
        decimal? exact = timingComplete ? planning + execution : null;
        var elapsedMs = exact is null ? 0 : RoundMilliseconds(exact.Value);
        var warnings = new List<string> { BenchmarkMetricText.PostgresCpuUnavailable };
        var seen = new HashSet<string>(StringComparer.Ordinal) { BenchmarkMetricText.PostgresCpuUnavailable };
        if (!timingComplete)
            AddWarning(warnings, seen, BenchmarkMetricText.PostgresTimingMissing);
        if (exact is > 0 && elapsedMs == 0)
            AddWarning(warnings, seen, BenchmarkMetricText.PostgresSubMillisecond);

        long logicalReads = 0;
        var logicalReadsAvailability = BenchmarkMetricReport.Unavailable;
        var tables = new Dictionary<string, long>(StringComparer.Ordinal);
        if (root.TryGetProperty("Plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
        {
            // Parent counters already include child plans and parallel workers.
            if (TryBuffers(plan, out var blocks))
            {
                logicalReads = blocks;
                logicalReadsAvailability = BenchmarkMetricReport.Measured;
            }
            else
            {
                AddWarning(warnings, seen, BenchmarkMetricText.PostgresBuffersMissing);
            }

            CollectRelations(plan, tables, warnings, seen);
        }
        else
        {
            AddWarning(warnings, seen, BenchmarkMetricText.PostgresBuffersMissing);
        }

        var metrics = new BenchmarkRunMetrics(
            BenchmarkMetricReport.Unavailable,
            timingComplete ? BenchmarkMetricReport.Measured : BenchmarkMetricReport.Unavailable,
            exact,
            timingComplete && exact!.Value == elapsedMs,
            hasPlanning ? planning : null,
            hasExecution ? execution : null,
            logicalReadsAvailability,
            BenchmarkMetricText.PostgresLogicalReadsSource,
            BenchmarkMetricText.PostgresRelationBufferSource,
            false,
            BenchmarkMetricReport.ResultNotCaptured,
            BenchmarkMetricText.RowsNotCaptured,
            warnings);
        return new PostgresBenchmarkStats(0, elapsedMs, logicalReads, tables, metrics);
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
        // EXPLAIN returns no rows. Measured measure still hashes the canonical result.
        // captureComparison false does not retain the comparison fingerprint.
        // Warm-up (repetition 0) and compare-results off stay EXPLAIN-only.
        var hashRows = captureComparison
            || (repetition > 0 && string.Equals(variant, "measure", StringComparison.Ordinal));
        var metrics = stats.Metrics with
        {
            ResultRowSource = hashRows
                ? BenchmarkMetricReport.ResultUnmeasuredSidecar
                : BenchmarkMetricReport.ResultNotCaptured,
            ResultRowSourceDescription = hashRows
                ? BenchmarkMetricText.SidecarRows
                : BenchmarkMetricText.RowsNotCaptured,
        };
        if (hashRows)
        {
            await using var sidecar = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(sql, parameters, timeoutSeconds), ct);
            var collected = await BenchmarkCollector.CollectCompareAsync(
                sidecar, raw, captureComparison, comparisonMaximumRows, ct);
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
            messages.Length,
            Metrics: metrics);
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

    private static readonly string[] BufferCounters =
    [
        "Shared Hit Blocks",
        "Shared Read Blocks",
        "Local Hit Blocks",
        "Local Read Blocks",
    ];

    private static void CollectRelations(
        JsonElement node,
        Dictionary<string, long> tables,
        List<string> warnings,
        HashSet<string> seen)
    {
        long childSum = 0;
        var childrenComplete = true;
        if (node.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in plans.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object)
                    continue;
                CollectRelations(child, tables, warnings, seen);
                if (TryBuffers(child, out var childBlocks))
                    childSum += childBlocks;
                else
                    childrenComplete = false;
            }
        }

        // Workers repeat this node's counters. They are not child plans and are not added again.
        if (!TryRelation(node, out var name))
            return;
        if (!TryBuffers(node, out var blocks))
        {
            AddWarning(warnings, seen, BenchmarkMetricText.PostgresRelationBuffersMissing);
            return;
        }

        if (!childrenComplete)
        {
            AddWarning(warnings, seen, BenchmarkMetricText.PostgresRelationChildBuffersMissing);
            return;
        }

        var exclusive = blocks - childSum;
        if (exclusive < 0)
        {
            AddWarning(warnings, seen, BenchmarkMetricText.PostgresRelationBuffersExceedParent);
            return;
        }

        tables[name] = tables.GetValueOrDefault(name) + exclusive;
    }

    private static bool TryRelation(JsonElement node, out string name)
    {
        name = string.Empty;
        if (!node.TryGetProperty("Relation Name", out var relation) || relation.ValueKind != JsonValueKind.String)
            return false;

        string? schema = null;
        if (node.TryGetProperty("Schema", out var schemaValue) && schemaValue.ValueKind == JsonValueKind.String)
            schema = schemaValue.GetString();
        var qualified = PostgresPlanDistiller.QualifiedRelationName(schema, relation.GetString());
        if (qualified is null)
            return false;

        name = qualified;
        return true;
    }

    private static bool TryBuffers(JsonElement node, out long blocks)
    {
        blocks = 0;
        var present = false;
        foreach (var name in BufferCounters)
        {
            if (!node.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
                continue;
            present = true;
            blocks += value.TryGetInt64(out var whole) ? whole : (long)value.GetDouble();
        }

        return present;
    }

    private static bool TryDecimal(JsonElement element, string name, out decimal value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out value);
    }

    private static long RoundMilliseconds(decimal value) =>
        (long)Math.Round(value, 0, MidpointRounding.AwayFromZero);

    private static void AddWarning(List<string> warnings, HashSet<string> seen, string warning)
    {
        if (seen.Add(warning))
            warnings.Add(warning);
    }
}