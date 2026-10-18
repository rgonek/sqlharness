using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

/// <summary>
/// Pure aggregation of measured runs into journal rows: grouping by
/// (variant, parameter set, matrix cell), medians, plan diagnostics, plan
/// identity links, and distinct plan documents. Reads no parameter or matrix values.
/// </summary>
internal static class JournalBenchmarkBuilder
{
    internal const int MaximumWaits = 10;
    private static readonly Regex TableVariableName = new(@"^(?:\[dbo\]\.)?\[?(?<name>#[0-9A-Fa-f]{8})\]?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static BenchmarkJournalRecord Build(IReadOnlyList<CompareRunArtifact> runs, ConditionsModel? conditions = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var documents = new Dictionary<string, JournalPlanDocument>(StringComparer.Ordinal);
        var variants = runs
            .GroupBy(run => (run.Variant, run.ParameterSet, run.MatrixCell))
            .Select(group => BuildVariant(group.Key.Variant, group.Key.ParameterSet, group.Key.MatrixCell, group.ToArray(), documents))
            .ToArray();
        var caseCount = Math.Max(1, runs.Where(run => run.MatrixCell.HasValue).Select(run => run.MatrixCell!.Value)
            .DefaultIfEmpty(0).Max() + 1);
        return new BenchmarkJournalRecord(
            variants.Select(metric => WithFallbackMetricIdentity(metric)).ToArray(), documents.Values.ToArray())
        {
            Statements = BuildStatements(runs).Select(statement => WithFallbackStatementIdentity(statement)).ToArray(),
        };

        JournalVariantMetrics WithFallbackMetricIdentity(JournalVariantMetrics metric)
        {
            var identity = FallbackIdentity(metric.MatrixCell);
            return metric with { CaseId = identity.Id, CaseLabel = identity.Label };
        }

        JournalStatementMetrics WithFallbackStatementIdentity(JournalStatementMetrics statement)
        {
            var identity = FallbackIdentity(statement.MatrixCell);
            return statement with { CaseId = identity.Id, CaseLabel = identity.Label };
        }

        (string Id, string Label) FallbackIdentity(int? matrixCell)
        {
            var index = matrixCell ?? 0;
            var conditionCase = conditions?.Cases.FirstOrDefault(item => item.Index == index);
            if (conditionCase is not null)
                return (conditionCase.Id, conditionCase.Label);
            return (index.ToString(CultureInfo.InvariantCulture), $"Case {index + 1} of {caseCount}");
        }
    }

    private static IReadOnlyList<JournalStatementMetrics> BuildStatements(IReadOnlyList<CompareRunArtifact> runs)
    {
        var extracted = new List<(CompareRunArtifact Run, StatementMetrics Metric)>();
        foreach (var run in runs)
        {
            var ordinalOffset = 0;
            foreach (var plan in run.PlanXmls)
            {
                var statements = StatementMetricsExtractor.Extract(plan, out var omitted);
                extracted.AddRange(statements.Select(statement => (run, statement with
                {
                    StatementOrdinal = statement.StatementOrdinal + ordinalOffset,
                })));
                ordinalOffset += statements.Count + omitted;
            }
        }

        return extracted
            .GroupBy(item => (item.Run.Variant, item.Run.ParameterSet, item.Run.MatrixCell, item.Metric.StatementOrdinal))
            .Select(group =>
            {
                var samples = group.ToArray();
                var first = samples[0].Metric;
                var dops = samples.Select(sample => sample.Metric.DegreeOfParallelism).OfType<int>().ToArray();
                return new JournalStatementMetrics(
                    group.Key.Variant,
                    group.Key.ParameterSet,
                    group.Key.MatrixCell,
                    group.Key.StatementOrdinal,
                    first.StatementHash,
                    StatementMedian(samples.Select(sample => sample.Metric.CpuTimeMilliseconds)),
                    StatementMedian(samples.Select(sample => sample.Metric.ElapsedTimeMilliseconds)),
                    dops.Length == 0 ? null : dops.Max(),
                    first.TopOperators);
            })
            .OrderBy(statement => statement.Variant, StringComparer.Ordinal)
            .ThenBy(statement => statement.MatrixCell)
            .ThenBy(statement => statement.ParameterSet, StringComparer.Ordinal)
            .ThenBy(statement => statement.StatementOrdinal)
            .ToArray();
    }

    private static long? StatementMedian(IEnumerable<long?> values)
    {
        var present = values.OfType<long>().ToArray();
        return present.Length == 0 ? null : Distribution.From(present).Median;
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
        var tableIoAvailable = runs.All(run =>
            run.TableIoComplete ?? run.Metrics?.LogicalReadsAvailability != BenchmarkMetricReport.Unavailable);
        var readsAvailable = tableIoAvailable;

        var planMetrics = runs.Select(run => PlanMetricsExtractor.Extract(run.PlanXmls)).ToArray();
        var links = new List<JournalPlanLink>();
        var occurrences = new List<JournalPlanOccurrence>();
        foreach (var run in runs)
        {
            for (var ordinal = 0; ordinal < run.PlanXmls.Count; ordinal++)
            {
                var document = run.PlanXmls[ordinal];
                var sha256 = PlanIdentity.DocumentSha256(document);
                var shapeHash = PlanIdentity.Hash(document);
                links.Add(new JournalPlanLink(run.Repetition, ordinal, sha256));
                var statementCount = StatementMetricsExtractor.CountStatements(document);
                if (statementCount == 0)
                    statementCount = 1;
                for (var statementIndex = 0; statementIndex < statementCount; statementIndex++)
                    occurrences.Add(new JournalPlanOccurrence(run.Repetition, ordinal, statementIndex, sha256));
                documents.TryAdd(sha256, new JournalPlanDocument(sha256, shapeHash,
                    IsJson(document) ? "explain-json" : "showplan-xml", document));
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
            readsAvailable ? Spread(runs.Select(run => TotalLogicalReads(run, tableIoAvailable))) : null,
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
            tableIoAvailable ? TableIo(runs) : [],
            links)
        {
            PlanOccurrences = occurrences,
        };
    }

    private static IReadOnlyList<JournalTableIo> TableIo(CompareRunArtifact[] runs)
    {
        var detailed = runs.All(run => run.TableIo.Count > 0);
        var normalized = runs.Select(run => Normalize(run, detailed)).ToArray();
        var tables = normalized
            .SelectMany(run => run.Select(io => io.Table))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return tables.Select(table =>
        {
            if (!detailed)
            {
                return new JournalTableIo(
                    table,
                    MedianOf(normalized.Select(run => run.FirstOrDefault(io => io.Table == table)?.LogicalReads ?? 0)),
                    null, null, null, null,
                    MedianOf(normalized.Select(run => run.FirstOrDefault(io => io.Table == table)?.LobLogicalReads ?? 0)),
                    null, null, 0);
            }

            var counters = normalized
                .Select(run => run.FirstOrDefault(io => io.Table == table)
                    ?? new TableIoCounters(table, 0, 0, 0, 0, 0, 0, 0, 0))
                .ToArray();
            return new JournalTableIo(
                table,
                MedianOf(counters.Select(c => SaturatingAdd(c.LogicalReads, c.LobLogicalReads))),
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

    private static long TotalLogicalReads(CompareRunArtifact run, bool detailed) =>
        Normalize(run, detailed).Aggregate(0L, (total, counters) =>
            SaturatingAdd(total, SaturatingAdd(counters.LogicalReads, counters.LobLogicalReads)));

    private static IReadOnlyList<TableIoCounters> Normalize(CompareRunArtifact run, bool detailed)
    {
        var counters = detailed
            ? run.TableIo
            : run.LogicalReadsByTable.Select(pair => new TableIoCounters(pair.Key, 0, pair.Value, 0, 0, 0, 0, 0, 0)).ToArray();
        return counters
            .GroupBy(counter => NormalizeName(counter.Table), StringComparer.Ordinal)
            .Select(group => new TableIoCounters(
                group.Key,
                Sum(group, item => item.ScanCount),
                Sum(group, item => item.LogicalReads),
                Sum(group, item => item.PhysicalReads),
                Sum(group, item => item.PageServerReads),
                Sum(group, item => item.ReadAheadReads),
                Sum(group, item => item.LobLogicalReads),
                Sum(group, item => item.LobPhysicalReads),
                Sum(group, item => item.LobReadAheadReads)))
            .ToArray();

        static long Sum(IEnumerable<TableIoCounters> counters, Func<TableIoCounters, long> select) =>
            counters.Aggregate(0L, (total, item) => SaturatingAdd(total, select(item)));
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private static string NormalizeName(string table) => TableVariableName.IsMatch(table) ? "table variables" : table;

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
