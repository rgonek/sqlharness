using System.Text.Json.Serialization;

namespace SqlHarness.Core;

public sealed record BenchmarkVariantSummary(
    CompareDistribution CpuTimeMilliseconds,
    CompareDistribution ElapsedTimeMilliseconds,
    CompareDistribution LogicalReads,
    IReadOnlyDictionary<string, CompareDistribution> LogicalReadsByTable,
    IReadOnlyList<string> Warnings)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkMetricReport? MetricReport { get; init; }
}

public sealed record NoteworthyOperatorSummary(
    string Side,
    int NodeId,
    string PhysicalOp,
    string? Object,
    bool HasWarnings,
    bool HasSpill,
    bool HasImplicitConversion);

public sealed record CompareBenchmarkSummary(
    SqlHarnessTargetIdentityReport Target,
    int Repetitions,
    CompareClassificationReport Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    ResultEquivalenceReport Equivalence,
    BenchmarkVariantSummary Baseline,
    BenchmarkVariantSummary Candidate,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    string? ArtifactDirectory)
{
    /// <summary>Absent when the report predates the regression verdict.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegressionReport? Regression { get; init; }
}

public sealed record MeasureBenchmarkSummary(
    SqlHarnessTargetIdentityReport Target,
    int Repetitions,
    bool ResultsStable,
    BenchmarkClassificationReport Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    BenchmarkVariantSummary Query,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    string? ArtifactDirectory);

public sealed record MeasureParameterSetSummary(
    string Name,
    IReadOnlyList<MeasureParameterMetadata> Parameters,
    string ValueHash,
    bool ResultsStable,
    long MedianElapsedMilliseconds,
    long MedianCpuMilliseconds,
    long MedianLogicalReads,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkMetricReport? MetricReport { get; init; }

    public int Repetitions { get; init; }

    public long MinimumElapsedMilliseconds { get; init; }

    public long MaximumElapsedMilliseconds { get; init; }

    public long MinimumCpuMilliseconds { get; init; }

    public long MaximumCpuMilliseconds { get; init; }

    public long MinimumLogicalReads { get; init; }

    public long MaximumLogicalReads { get; init; }
}

public sealed record MeasureSetBenchmarkSummary(
    SqlHarnessTargetIdentityReport Target,
    string MeasuredOrderRule,
    string PlanCacheWarning,
    IReadOnlyList<MeasureParameterSetSummary> Sets,
    MeasureCrossSetSummary CrossSetSummary,
    string? ArtifactDirectory);

public sealed record AgentMedianMetrics(decimal? CpuMilliseconds, decimal? ElapsedMilliseconds, long? LogicalReads);

/// <summary>Decision-oriented outcome for one completed matrix case.</summary>
public sealed record AgentMatrixCase(
    int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactId,
    bool? Equivalent,
    string EquivalenceAvailability,
    AgentMedianMetrics Baseline,
    AgentMedianMetrics Candidate,
    AgentMedianMetrics Delta,
    IReadOnlyList<string> Warnings,
    string? ParameterValue = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetName { get; init; }
}

/// <summary>Optional per-case diagnostics; matrix-wide identity and method live on the envelope.</summary>
public sealed record CompareMatrixCellDetailContent(
    BenchmarkVariantSummary Baseline,
    BenchmarkVariantSummary Candidate,
    ResultEquivalenceReport Equivalence,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegressionReport? Regression);

public sealed record CompareMatrixCellDetail(int Index, CompareMatrixCellDetailContent Compare);

public sealed record CompareMatrixBenchmarkSummary(
    SqlHarnessTargetIdentityReport? Target,
    int? Repetitions,
    CompareClassificationReport? Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    ResultComparisonMode? ComparisonMode,
    string? ParameterName,
    string? ParameterType,
    IReadOnlyList<AgentMatrixCase> Cases,
    IReadOnlyList<CompareMatrixCellDetail> Details,
    int CaseCount,
    int OmittedCaseCount,
    int OmittedDetailCount,
    string? MatrixArtifactId,
    int? Continuation);

public static class BenchmarkSummaryProjector
{
    private const int MaximumNoteworthyOperators = 10;

    public static CompareBenchmarkSummary Project(SqlHarnessCompareReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new CompareBenchmarkSummary(
            report.Target,
            report.Repetitions,
            report.Classification,
            report.Parameters,
            report.Equivalence,
            ProjectVariant(report.Baseline),
            ProjectVariant(report.Candidate),
            SelectCompareNoteworthy(report.Baseline.Operators, report.Candidate.Operators),
            report.ArtifactDirectory)
        {
            Regression = report.Regression,
        };
    }

    public static MeasureBenchmarkSummary Project(SqlHarnessMeasureReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new MeasureBenchmarkSummary(
            report.Target,
            report.Repetitions,
            report.ResultsStable,
            report.Classification,
            report.Parameters,
            ProjectVariant(report.Query),
            SelectMeasureNoteworthy(report.Query.Operators),
            report.ArtifactDirectory);
    }

    public static MeasureSetBenchmarkSummary Project(SqlHarnessMeasureSetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(report.Sets);

        return new MeasureSetBenchmarkSummary(
            report.Target,
            report.MeasuredOrderRule,
            report.PlanCacheWarning,
            report.Sets.Select(ProjectSet).ToArray(),
            report.CrossSetSummary,
            report.ArtifactDirectory);
    }

    public static CompareMatrixBenchmarkSummary Project(SqlHarnessCompareMatrixReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var cells = report.Cells.OrderBy(cell => cell.Index).ToArray();
        var first = cells.FirstOrDefault()?.Compare;
        return new CompareMatrixBenchmarkSummary(
            first?.Target,
            first?.Repetitions,
            first?.Classification,
            first?.Parameters ?? [],
            first?.Equivalence.Mode,
            report.ParameterName,
            report.ParameterType,
            cells.Select(ProjectCase).ToArray(),
            cells.Select(cell => ProjectDetail(cell.Index, cell.Compare)).ToArray(),
            cells.Length,
            0,
            0,
            cells.FirstOrDefault() is { } firstCell ? SafeArtifactId(firstCell.Compare.ArtifactDirectory) : null,
            null);
    }

    public static AgentMatrixCase ProjectCase(CompareMatrixCellReport cell) =>
        ProjectCase(cell.Index, SafeArtifactId(cell.Compare.ArtifactDirectory), cell.Compare, cell.ParameterValue,
            string.IsNullOrEmpty(cell.SetName) ? null : cell.SetName);

    public static AgentMatrixCase ProjectCase(int index, string? artifactId, SqlHarnessCompareReport compare, string? parameterValue = null, string? setName = null)
    {
        var equivalence = compare.Equivalence;
        var availability = equivalence.Mode == ResultComparisonMode.Off
            ? "not-requested"
            : equivalence.Equivalent is null ? "unavailable" : "compared";
        var equivalent = availability == "compared" ? equivalence.Equivalent : null;
        var baseline = MedianMetrics(compare.Baseline);
        var candidate = MedianMetrics(compare.Candidate);
        return new AgentMatrixCase(
            index,
            artifactId,
            equivalent,
            availability,
            baseline,
            candidate,
            new AgentMedianMetrics(
                Delta(baseline.CpuMilliseconds, candidate.CpuMilliseconds),
                Delta(baseline.ElapsedMilliseconds, candidate.ElapsedMilliseconds),
                Delta(baseline.LogicalReads, candidate.LogicalReads)),
            compare.Baseline.Warnings.Concat(compare.Candidate.Warnings).Distinct(StringComparer.Ordinal).ToArray(),
            parameterValue)
        {
            SetName = setName,
        };
    }

    public static CompareMatrixCellDetail ProjectDetail(int index, SqlHarnessCompareReport compare)
    {
        var projected = Project(compare);
        return new CompareMatrixCellDetail(index, new CompareMatrixCellDetailContent(projected.Baseline, projected.Candidate,
            projected.Equivalence, projected.NoteworthyOperators, projected.Regression));
    }

    public static MeasureParameterSetSummary ProjectSet(MeasureParameterSetReport set) =>
        ProjectSet(set, SelectMeasureNoteworthy(set.Metrics.Operators));

    public static MeasureParameterSetSummary ProjectSet(
        MeasureParameterSetReport set, IReadOnlyList<NoteworthyOperatorSummary> operators) =>
        new(
            set.Name,
            set.Parameters,
            set.ValueHash,
            set.ResultsStable,
            set.Metrics.ElapsedTimeMilliseconds.Median,
            set.Metrics.CpuTimeMilliseconds.Median,
            set.Metrics.LogicalReads.Median,
            operators)
        {
            MetricReport = set.Metrics.MetricReport,
            Repetitions = set.Repetitions,
            MinimumElapsedMilliseconds = set.Metrics.ElapsedTimeMilliseconds.Min,
            MaximumElapsedMilliseconds = set.Metrics.ElapsedTimeMilliseconds.Max,
            MinimumCpuMilliseconds = set.Metrics.CpuTimeMilliseconds.Min,
            MaximumCpuMilliseconds = set.Metrics.CpuTimeMilliseconds.Max,
            MinimumLogicalReads = set.Metrics.LogicalReads.Min,
            MaximumLogicalReads = set.Metrics.LogicalReads.Max,
        };

    private static AgentMedianMetrics MedianMetrics(CompareVariantReport variant)
    {
        var report = variant.MetricReport;
        decimal? elapsed = report?.ElapsedTimeAvailability == BenchmarkMetricReport.Unavailable
            ? null
            : report?.ElapsedTimeMillisecondsExact?.Median ?? variant.ElapsedTimeMilliseconds.Median;
        return new(
            report?.CpuTimeAvailability == BenchmarkMetricReport.Unavailable ? null : variant.CpuTimeMilliseconds.Median,
            elapsed,
            report?.LogicalReadsAvailability == BenchmarkMetricReport.Unavailable ? null : variant.LogicalReads.Median);
    }

    private static decimal? Delta(decimal? baseline, decimal? candidate) =>
        baseline is { } left && candidate is { } right ? right - left : null;

    private static long? Delta(long? baseline, long? candidate) =>
        baseline is { } left && candidate is { } right ? right - left : null;

    private static string? SafeArtifactId(string? path) => ArtifactIds.FromDirectory(path);

    private static BenchmarkVariantSummary ProjectVariant(CompareVariantReport variant) =>
        new(
            variant.CpuTimeMilliseconds,
            variant.ElapsedTimeMilliseconds,
            variant.LogicalReads,
            variant.LogicalReadsByTable,
            variant.Warnings)
        {
            MetricReport = variant.MetricReport,
        };

    private static IReadOnlyList<NoteworthyOperatorSummary> SelectCompareNoteworthy(
        IReadOnlyList<CompareOperatorReport> baseline,
        IReadOnlyList<CompareOperatorReport> candidate)
    {
        // Collapse by (PhysicalOp, Object) per side, OR-ing attention flags within the group so a
        // later flagged RelOp is not dropped when an earlier clean sibling shares the same key.
        var baselineByKey = CollapseSide(baseline);
        var candidateByKey = CollapseSide(candidate);

        var selected = new List<NoteworthyOperatorSummary>();
        foreach (var key in baselineByKey.Keys.Union(candidateByKey.Keys, OperatorKeyComparer.Instance))
        {
            baselineByKey.TryGetValue(key, out var left);
            candidateByKey.TryGetValue(key, out var right);
            var oneSideOnly = left is null || right is null;
            var hasWarnings = (left?.HasWarnings ?? false) || (right?.HasWarnings ?? false);
            var hasSpill = (left?.HasSpill ?? false) || (right?.HasSpill ?? false);
            var hasImplicit = (left?.HasImplicitConversion ?? false) || (right?.HasImplicitConversion ?? false);
            if (!oneSideOnly && !(hasWarnings || hasSpill || hasImplicit))
                continue;

            var representative = left ?? right!;
            var side = left is null ? "candidate" : right is null ? "baseline" : "both";
            selected.Add(new NoteworthyOperatorSummary(
                side,
                representative.NodeId,
                representative.PhysicalOp,
                representative.Object,
                hasWarnings,
                hasSpill,
                hasImplicit));
        }

        return selected
            .OrderByDescending(HasAttention)
            .ThenBy(op => op.PhysicalOp, StringComparer.Ordinal)
            .ThenBy(op => op.Object ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(op => op.Side, StringComparer.Ordinal)
            .ThenBy(op => op.NodeId)
            .Take(MaximumNoteworthyOperators)
            .ToArray();
    }

    private static Dictionary<(string PhysicalOp, string Object), CompareOperatorReport> CollapseSide(
        IReadOnlyList<CompareOperatorReport> operators) =>
        operators
            .GroupBy(OperatorKey, OperatorKeyComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var first = group.First();
                    return first with
                    {
                        HasWarnings = group.Any(op => op.HasWarnings),
                        HasSpill = group.Any(op => op.HasSpill),
                        HasImplicitConversion = group.Any(op => op.HasImplicitConversion),
                    };
                },
                OperatorKeyComparer.Instance);

    private static IReadOnlyList<NoteworthyOperatorSummary> SelectMeasureNoteworthy(
        IReadOnlyList<CompareOperatorReport> operators) =>
        operators
            .Where(op => op.HasWarnings || op.HasSpill || op.HasImplicitConversion)
            .OrderByDescending(HasAttention)
            .ThenBy(op => op.PhysicalOp, StringComparer.Ordinal)
            .ThenBy(op => op.Object ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(op => op.NodeId)
            .Take(MaximumNoteworthyOperators)
            .Select(op => new NoteworthyOperatorSummary(
                "query",
                op.NodeId,
                op.PhysicalOp,
                op.Object,
                op.HasWarnings,
                op.HasSpill,
                op.HasImplicitConversion))
            .ToArray();

    private static bool HasAttention(NoteworthyOperatorSummary op) =>
        op.HasWarnings || op.HasSpill || op.HasImplicitConversion;

    private static bool HasAttention(CompareOperatorReport op) =>
        op.HasWarnings || op.HasSpill || op.HasImplicitConversion;

    private static (string PhysicalOp, string Object) OperatorKey(CompareOperatorReport op) =>
        (op.PhysicalOp, op.Object ?? string.Empty);

    private sealed class OperatorKeyComparer : IEqualityComparer<(string PhysicalOp, string Object)>
    {
        public static OperatorKeyComparer Instance { get; } = new();

        public bool Equals((string PhysicalOp, string Object) x, (string PhysicalOp, string Object) y) =>
            string.Equals(x.PhysicalOp, y.PhysicalOp, StringComparison.Ordinal)
            && string.Equals(x.Object, y.Object, StringComparison.Ordinal);

        public int GetHashCode((string PhysicalOp, string Object) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.PhysicalOp),
                StringComparer.Ordinal.GetHashCode(obj.Object));
    }
}