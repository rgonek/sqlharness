namespace SqlHarness.Core;

/// <summary>
/// Pure aggregation of measured runs into journal rows: grouping by
/// (variant, parameter set, matrix cell), medians, plan diagnostics, plan
/// identity links, and distinct plan documents. Reads no parameter or matrix values.
/// </summary>
internal static class JournalBenchmarkBuilder
{
    internal const int MaximumWaits = 10;

    internal static BenchmarkJournalRecord Build(IReadOnlyList<CompareRunArtifact> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var documents = new Dictionary<string, JournalPlanDocument>(StringComparer.Ordinal);
        var variants = runs
            .GroupBy(run => (run.Variant, run.ParameterSet, run.MatrixCell))
            .Select(group => BuildVariant(group.Key.Variant, group.Key.ParameterSet, group.Key.MatrixCell, group.ToArray(), documents))
            .ToArray();
        return new BenchmarkJournalRecord(variants, documents.Values.ToArray());
    }

    private static JournalVariantMetrics BuildVariant(
        string variant,
        string? parameterSet,
        int? matrixCell,
        CompareRunArtifact[] runs,
        Dictionary<string, JournalPlanDocument> documents)
    {
        var cpuAvailable = runs.All(run => run.Metrics?.CpuTimeAvailability != BenchmarkMetricReport.Unavailable);
        var elapsedAvailable = runs.All(run => run.Metrics?.ElapsedTimeAvailability != BenchmarkMetricReport.Unavailable);
        var readsAvailable = runs.All(run => run.Metrics?.LogicalReadsAvailability != BenchmarkMetricReport.Unavailable);

        var planMetrics = runs.Select(run => PlanMetricsExtractor.Extract(run.PlanXmls)).ToArray();
        var links = new List<JournalPlanLink>();
        foreach (var run in runs)
        {
            for (var ordinal = 0; ordinal < run.PlanXmls.Count; ordinal++)
            {
                var document = run.PlanXmls[ordinal];
                var hash = PlanIdentity.Hash(document);
                links.Add(new JournalPlanLink(run.Repetition, ordinal, hash));
                documents.TryAdd(hash, new JournalPlanDocument(hash, IsJson(document) ? "explain-json" : "showplan-xml", document));
            }
        }

        var waits = planMetrics
            .SelectMany(metrics => metrics.Waits)
            .GroupBy(wait => wait.WaitType, StringComparer.Ordinal)
            .Select(group => new JournalWait(
                group.Key,
                group.Sum(wait => (double)wait.WaitTimeMs) / runs.Length,
                group.Sum(wait => (double)wait.WaitCount) / runs.Length))
            .OrderByDescending(wait => wait.AverageWaitMs).ThenBy(wait => wait.WaitType, StringComparer.Ordinal)
            .Take(MaximumWaits)
            .ToArray();
        var postgres = planMetrics.Select(metrics => metrics.Postgres).OfType<PostgresBufferCounters>().ToArray();

        return new JournalVariantMetrics(
            variant,
            parameterSet,
            matrixCell,
            runs.Length,
            elapsedAvailable ? Spread(runs.Select(run => run.ElapsedTimeMilliseconds)) : null,
            cpuAvailable ? Spread(runs.Select(run => run.CpuTimeMilliseconds)) : null,
            readsAvailable ? Spread(runs.Select(run => run.LogicalReads)) : null,
            Median(planMetrics.Select(metrics => metrics.GrantRequestedKb)),
            Median(planMetrics.Select(metrics => metrics.GrantGrantedKb)),
            Median(planMetrics.Select(metrics => metrics.GrantMaxUsedKb)),
            planMetrics.Max(metrics => metrics.Dop),
            Median(planMetrics.Select(metrics => metrics.CompileTimeMs)),
            Median(planMetrics.Select(metrics => metrics.CompileCpuMs)),
            planMetrics.Max(metrics => metrics.SpillCount),
            planMetrics.Any(metrics => metrics.HasWarnings),
            planMetrics.Any(metrics => metrics.HasImplicitConversion),
            planMetrics.Max(metrics => metrics.MissingIndexCount),
            waits,
            postgres.Length == 0 ? null : new PostgresBufferCounters(
                MedianOf(postgres.Select(p => p.SharedHit)), MedianOf(postgres.Select(p => p.SharedRead)),
                MedianOf(postgres.Select(p => p.SharedDirtied)), MedianOf(postgres.Select(p => p.SharedWritten)),
                MedianOf(postgres.Select(p => p.TempRead)), MedianOf(postgres.Select(p => p.TempWritten))),
            readsAvailable ? TableIo(runs) : [],
            links);
    }

    private static IReadOnlyList<JournalTableIo> TableIo(CompareRunArtifact[] runs)
    {
        var detailed = runs.All(run => run.TableIo.Count > 0);
        var tables = runs
            .SelectMany(run => detailed ? run.TableIo.Select(io => io.Table) : run.LogicalReadsByTable.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return tables.Select(table =>
        {
            if (!detailed)
            {
                return new JournalTableIo(
                    table,
                    MedianOf(runs.Select(run => run.LogicalReadsByTable.GetValueOrDefault(table))),
                    null, null, null, null, null, null, null, 0);
            }

            var counters = runs
                .Select(run => run.TableIo.FirstOrDefault(io => io.Table == table)
                    ?? new TableIoCounters(table, 0, 0, 0, 0, 0, 0, 0, 0))
                .ToArray();
            return new JournalTableIo(
                table,
                MedianOf(counters.Select(c => c.LogicalReads)),
                MedianOf(counters.Select(c => c.ScanCount)),
                MedianOf(counters.Select(c => c.PhysicalReads)),
                MedianOf(counters.Select(c => c.PageServerReads)),
                MedianOf(counters.Select(c => c.ReadAheadReads)),
                MedianOf(counters.Select(c => c.LobLogicalReads)),
                MedianOf(counters.Select(c => c.LobPhysicalReads)),
                MedianOf(counters.Select(c => c.LobReadAheadReads)),
                counters.Count(IsCold));
        }).ToArray();
    }

    // Counters are non-negative; testing each one avoids overflow from summing them.
    private static bool IsCold(TableIoCounters counters) =>
        counters.PhysicalReads > 0 || counters.ReadAheadReads > 0
        || counters.LobPhysicalReads > 0 || counters.LobReadAheadReads > 0;

    private static CompareDistribution Spread(IEnumerable<long> values)
    {
        var distribution = Distribution.From(values);
        return new CompareDistribution(distribution.Min, distribution.Median, distribution.Max);
    }

    private static long MedianOf(IEnumerable<long> values) => Distribution.From(values).Median;

    private static long? Median(IEnumerable<long?> values)
    {
        var present = values.OfType<long>().ToArray();
        return present.Length == 0 ? null : MedianOf(present);
    }

    private static bool IsJson(string document)
    {
        var trimmed = document.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[';
    }
}