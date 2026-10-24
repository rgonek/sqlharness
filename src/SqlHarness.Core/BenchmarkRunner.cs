using SqlHarness.Core.Dialect;
using SqlHarness.Core.Journal;

using JournalOperationPhase = SqlHarness.Core.Journal.OperationPhase;

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
    public IReadOnlyList<string> Warnings { get; init; } = [];
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
        CanonicalResultAccumulator? raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct,
        SessionState? sessionState = null)
    {
        CollectedCompareRun collected;
        IReadOnlyList<string> warnings;
        try
        {
            using (OperationPhaseRecorder.Current?.Start(JournalOperationPhase.Execute))
                collected = await dialect.ExecuteBenchmarkRunAsync(
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
        }
        catch
        {
            try
            {
                // Cleanup still runs so a connection with an open transaction is
                // discarded. Its diagnostic must not replace the SQL failure that
                // caused this path; SessionCleanupException carries its warnings to
                // the owning operation when cleanup itself is the primary failure.
                _ = await SqlSessionLogicalBatch.EndAsync(session, ct);
            }
            catch (SessionCleanupException)
            {
                // A failed rollback/discard is the authoritative operation failure.
                throw;
            }
            catch
            {
                // Preserve the original execution exception.
            }
            if (sessionState is not null)
            {
                try
                {
                    await dialect.RestoreSessionStateAsync(session, sessionState, ct);
                }
                catch
                {
                    // Preserve the benchmark failure; the session will be disposed by its owning runner.
                }
            }
            throw;
        }
        warnings = await SqlSessionLogicalBatch.EndAsync(session, ct);
        await dialect.RestoreSessionStateAsync(session, sessionState, ct);
        var artifact = collected.Artifact with { ParameterSet = parameterSet, ExecutedSql = sql };
        return new CollectedBenchmarkRun(artifact, collected.Plans, artifact.PlanXmls.Select(PlanIdentity.Hash).ToArray())
        {
            Comparison = collected.Comparison,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Executes setup SQL once per session, optionally draining results into a
    /// raw accumulator. Message snapshotting is best-effort cleanup: it never
    /// masks the primary setup failure (see <see cref="OperationFailureMapper"/>).
    /// </summary>
    internal static async Task ExecuteRawAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        CanonicalResultAccumulator? raw,
        CancellationToken ct,
        bool endLogicalBatch = true)
    {
        var messageStart = session.Messages.Count;
        Exception? primaryException = null;
        try
        {
            await using var reader = await session.ExecuteReaderAsync(command, ct);
            do
            {
                if (raw is null)
                {
                    while (await reader.ReadAsync(ct))
                    {
                    }
                    continue;
                }

                if (reader.FieldCount == 0)
                    continue;
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => new CanonicalColumn(
                        index,
                        reader.GetName(index),
                        reader.GetFieldType(index).FullName ?? reader.GetFieldType(index).Name,
                        reader.GetAllowNull(index)))
                    .ToArray();
                raw?.BeginResultSet(columns);
                while (await reader.ReadAsync(ct))
                {
                    var values = Enumerable.Range(0, reader.FieldCount)
                        .Select(index => BenchmarkCollector.NormalizeValue(reader.GetValue(index)))
                        .ToArray();
                    raw?.AddRow(values);
                }
                raw?.EndResultSet();
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
            try
            {
                _ = session.ConsumeMessages(messageStart);
            }
            catch when (primaryException is not null)
            {
                // Preserve the SQL failure when diagnostics cannot be snapshotted.
            }
            if (endLogicalBatch)
            {
                try
                {
                    _ = await SqlSessionLogicalBatch.EndAsync(session, ct);
                }
                catch (SessionCleanupException)
                {
                    throw;
                }
                catch when (primaryException is not null)
                {
                    // Keep setup/query failure identity while session cleanup still
                    // performs its fail-closed discard behavior.
                }
            }
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
    internal static IReadOnlyDictionary<StatisticsIoTableIdentity, string> AllocateTableLabels(
        IEnumerable<CollectedBenchmarkRun> runs)
        => AllocateTableLabels(runs.Select(run => run.Artifact));

    internal static IReadOnlyDictionary<StatisticsIoTableIdentity, string> AllocateTableLabels(
        IEnumerable<CompareRunArtifact> artifacts)
    {
        var identities = new HashSet<StatisticsIoTableIdentity>();
        foreach (var artifact in artifacts)
        {
            if (artifact.LogicalReadsByTableIdentity is { } typed)
                identities.UnionWith(typed.Keys);
            else
                identities.UnionWith(artifact.LogicalReadsByTable.Keys.Select(StatisticsIoTableIdentity.Unknown));
            identities.UnionWith(artifact.TableIo.Select(counter =>
                counter.Identity ?? StatisticsIoTableIdentity.Unknown(counter.Table)));
        }
        return StatisticsIoTableIdentity.AllocateDisplayNames(identities);
    }

    internal static CompareVariantReport CreateVariantReport(
        string name,
        IReadOnlyList<CollectedBenchmarkRun> runs,
        IReadOnlyDictionary<StatisticsIoTableIdentity, string>? sharedTableLabels = null)
    {
        var operators = new List<CompareOperatorReport>();
        foreach (var run in runs)
        {
            var ordinalOffset = 0;
            foreach (var plan in run.Plans)
            {
                foreach (var op in plan.Operators)
                    operators.Add(ToOperatorReport(op, run, ordinalOffset));
                ordinalOffset += plan.StatementCount;
            }
        }
        var uniqueOperators = operators.Distinct().ToArray();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in runs.SelectMany(run => run.Warnings))
            warnings.Add(warning);
        if (uniqueOperators.Any(op => op.HasWarnings)) warnings.Add("PlanWarning");
        if (uniqueOperators.Any(op => op.HasSpill)) warnings.Add("SpillToTempDb");
        if (uniqueOperators.Any(op => op.HasImplicitConversion)) warnings.Add("ImplicitConversion");
        var identityTables = new HashSet<StatisticsIoTableIdentity>();
        foreach (var run in runs)
        {
            if (run.Artifact.LogicalReadsByTableIdentity is { } typed)
                identityTables.UnionWith(typed.Keys);
            else
                identityTables.UnionWith(run.Artifact.LogicalReadsByTable.Keys.Select(StatisticsIoTableIdentity.Unknown));
        }
        var tableLabels = sharedTableLabels ?? StatisticsIoTableIdentity.AllocateDisplayNames(identityTables);
        var tables = identityTables.ToDictionary(
            identity => tableLabels[identity],
            identity => runs.Sum(run => ReadsForIdentity(run.Artifact, identity)),
            StringComparer.Ordinal);
        var logicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal);
        foreach (var identity in identityTables
            .OrderBy(identity => identity.Kind == StatisticsIoTableKind.AnonymousTableVariable ? 0 : 1)
            .ThenBy(identity => identity.DisplayName, StringComparer.Ordinal)
            .ThenBy(identity => identity.Kind))
        {
            if (identity.Kind != StatisticsIoTableKind.Unknown &&
                runs.Any(run => run.Artifact.LogicalReadsByTableIdentity is null))
            {
                warnings.Add("TableIoDistributionUnavailable");
                continue;
            }

            var samples = runs.Select(run =>
            {
                if (run.Artifact.LogicalReadsByTableIdentity is { } typed)
                    return typed.GetValueOrDefault(identity);
                return identity.Kind == StatisticsIoTableKind.Unknown
                    ? run.Artifact.LogicalReadsByTable.GetValueOrDefault(identity.Name)
                    : 0L;
            });
            logicalReadsByTable[tableLabels[identity]] = ToPublic(Distribution.From(samples));
        }
        return new CompareVariantReport(
            name,
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.CpuTimeMilliseconds))),
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.ElapsedTimeMilliseconds))),
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.LogicalReads))),
            tables,
            uniqueOperators,
            warnings.Order(StringComparer.Ordinal).ToArray())
        {
            LogicalReadsByTable = logicalReadsByTable,
            MetricReport = BenchmarkMetricReport.FromArtifacts(runs.Select(run => run.Artifact).ToArray()),
        };
    }

    private static long ReadsForIdentity(CompareRunArtifact artifact, StatisticsIoTableIdentity identity)
    {
        if (artifact.LogicalReadsByTableIdentity is { } typed)
            return typed.GetValueOrDefault(identity);
        return identity.Kind == StatisticsIoTableKind.Unknown
            ? artifact.LogicalReadsByTable.GetValueOrDefault(identity.Name)
            : 0L;
    }

    private static CompareOperatorReport ToOperatorReport(PlanOperator op, CollectedBenchmarkRun run, int ordinalOffset)
    {
        var report = new CompareOperatorReport(op.NodeId, op.PhysicalOp, op.Object, op.HasWarnings, op.HasSpill, op.HasImplicitConversion);
        if (op.NodePath is null || op.Cursor is null)
            return report;
        return report with
        {
            Statement = op.StatementOrdinal + ordinalOffset,
            Repetition = run.Artifact.Repetition,
            NodePath = op.NodePath,
            Cursor = op.Cursor,
            ParameterSet = run.Artifact.ParameterSet,
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