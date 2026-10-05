using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

internal sealed record CollectedBenchmarkRun(
    CompareRunArtifact Artifact,
    IReadOnlyList<ExecutionPlan> Plans,
    IReadOnlyList<string> PlanHashes)
{
    // Compare equivalence still reads the dialect fingerprint. Measure does not.
    public CanonicalComparisonResult Comparison { get; init; } = BenchmarkCollector.EmptyComparison;

    public string Variant => Artifact.Variant;
    public string ResultHash => Artifact.ResultHash;
}

internal static class BenchmarkRunner
{
    internal static async Task<CollectedBenchmarkRun> ExecuteAsync(
        ISqlDialect dialect,
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        string? parameterSet,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct)
    {
        var collected = await dialect.ExecuteBenchmarkRunAsync(
            session,
            sql,
            parameters,
            timeoutSeconds,
            repetition,
            variant,
            raw,
            captureComparison,
            comparisonMaximumRows,
            ct);
        var artifact = collected.Artifact with { ParameterSet = parameterSet };
        return new CollectedBenchmarkRun(artifact, collected.Plans, artifact.PlanXmls.Select(PlanIdentity.Hash).ToArray())
        {
            Comparison = collected.Comparison,
        };
    }

    /// <summary>
    /// Executes setup SQL once per session, draining results into the shared raw
    /// accumulator. Message snapshotting is best-effort cleanup: it never masks
    /// the primary setup failure (see <see cref="OperationFailureMapper"/>).
    /// </summary>
    internal static async Task ExecuteRawAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        var messageStart = session.Messages.Count;
        Exception? primaryException = null;
        try
        {
            await using var reader = await session.ExecuteReaderAsync(command, ct);
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => new CanonicalColumn(
                        index,
                        reader.GetName(index),
                        reader.GetFieldType(index).FullName ?? reader.GetFieldType(index).Name,
                        reader.GetAllowNull(index)))
                    .ToArray();
                raw.BeginResultSet(columns);
                while (await reader.ReadAsync(ct))
                {
                    raw.AddRow(Enumerable.Range(0, reader.FieldCount)
                        .Select(index => BenchmarkCollector.NormalizeValue(reader.GetValue(index)))
                        .ToArray());
                }
                raw.EndResultSet();
            } while (await reader.NextResultAsync(ct));
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            OperationFailureMapper.CompleteCleanup(
                primaryException,
                () => BenchmarkCollector.AppendMessages(session, messageStart, raw));
        }
    }
}

/// <summary>
/// Report owner for benchmark results: variant distributions and parameter
/// metadata. Runners and the facade dispatch compose against this type instead
/// of static helpers on the facade.
/// </summary>
internal static class BenchmarkReports
{
    internal static CompareVariantReport CreateVariantReport(string name, IReadOnlyList<CollectedBenchmarkRun> runs)
    {
        var operators = runs
            .SelectMany(run => run.Plans)
            .SelectMany(plan => plan.Operators)
            .Select(op => new CompareOperatorReport(op.NodeId, op.PhysicalOp, op.Object, op.HasWarnings, op.HasSpill, op.HasImplicitConversion))
            .Distinct()
            .ToArray();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        if (operators.Any(op => op.HasWarnings)) warnings.Add("PlanWarning");
        if (operators.Any(op => op.HasSpill)) warnings.Add("SpillToTempDb");
        if (operators.Any(op => op.HasImplicitConversion)) warnings.Add("ImplicitConversion");
        var tables = runs.SelectMany(run => run.Artifact.LogicalReadsByTable)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
        var tableNames = runs
            .SelectMany(run => run.Artifact.LogicalReadsByTable.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var logicalReadsByTable = tableNames.ToDictionary(
            table => table,
            table => ToPublic(Distribution.From(runs.Select(run =>
                run.Artifact.LogicalReadsByTable.TryGetValue(table, out var reads) ? reads : 0L))),
            StringComparer.Ordinal);
        return new CompareVariantReport(
            name,
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.CpuTimeMilliseconds))),
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.ElapsedTimeMilliseconds))),
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.LogicalReads))),
            tables,
            operators,
            warnings.Order(StringComparer.Ordinal).ToArray())
        {
            LogicalReadsByTable = logicalReadsByTable,
            MetricReport = BenchmarkMetricReport.FromArtifacts(runs.Select(run => run.Artifact).ToArray()),
        };
    }

    internal static IReadOnlyList<BenchmarkParameterReport> ToParameterReports(IReadOnlyList<SqlHarnessParameter> parameters) =>
        parameters.Select(parameter => new BenchmarkParameterReport(
            parameter.Name,
            FormatParameterType(parameter),
            parameter.Size,
            parameter.Precision,
            parameter.Scale)).ToArray();

    internal static string ClassificationLabel(SqlSafetyDecision? decision) =>
        decision is null ? "none"
        : decision.HasMutation ? "mutation"
        : decision.HasSessionLocalWork ? "session-local"
        : "read-only";

    private static CompareDistribution ToPublic(Distribution value) => new(value.Min, value.Median, value.Max);

    private static string FormatParameterType(SqlHarnessParameter parameter) =>
        parameter.Type == System.Data.SqlDbType.Udt && !string.IsNullOrEmpty(parameter.UdtTypeName)
            ? parameter.UdtTypeName.ToLowerInvariant()
            : parameter.Type.ToString().ToLowerInvariant();
}