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

public sealed record OperatorPlanRef(
    string ArtifactId,
    string Variant,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParameterSet,
    int Statement,
    int Repetition,
    string NodePath,
    int Cursor);

public sealed record NoteworthyOperatorSideEvidence(bool HasWarnings, bool HasSpill, bool HasImplicitConversion);

public sealed record NoteworthyOperatorSummary(
    string Side,
    int NodeId,
    string PhysicalOp,
    string? Object,
    bool HasWarnings,
    bool HasSpill,
    bool HasImplicitConversion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OperatorPlanRef>? PlanRefs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NoteworthyOperatorSideEvidence? Baseline { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NoteworthyOperatorSideEvidence? Candidate { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SourceIsCapped { get; init; }
}

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

    public IReadOnlyList<string> Warnings { get; init; } = [];

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

public sealed record AgentMedianMetrics(decimal? CpuMilliseconds, decimal? ElapsedMilliseconds, long? LogicalReads)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ElapsedMinimum { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ElapsedMaximum { get; init; }
}

/// <summary>Policy v2 verdict carried on a matrix case without duplicating metric diagnostics.</summary>
public sealed record AgentMatrixRegression(string Verdict, IReadOnlyList<string> Reasons);

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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BaselineStable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CandidateStable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SchemaEquivalent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MismatchReasons { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentMatrixRegression? Regression { get; init; }
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
    int? Continuation)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MatrixExecutionCoverage? Coverage { get; init; }
}

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
            SelectCompareNoteworthy(report.Baseline.Operators, report.Candidate.Operators, SafeArtifactId(report.ArtifactDirectory)),
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
            SelectMeasureNoteworthy(report.Query.Operators, SafeArtifactId(report.ArtifactDirectory), "query", null),
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
            report.Sets.Select(set => ProjectSet(
                set,
                SelectMeasureNoteworthy(set.Metrics.Operators, SafeArtifactId(report.ArtifactDirectory), "query", set.Name))).ToArray(),
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
            null)
        {
            Coverage = report.Coverage,
        };
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
            BaselineStable = equivalence.BaselineStable,
            CandidateStable = equivalence.CandidateStable,
            SchemaEquivalent = equivalence.SchemaEquivalent,
            MismatchReasons = equivalence.MismatchReasons,
            Regression = compare.Regression is { } regression
                ? new AgentMatrixRegression(regression.Verdict, regression.Reasons)
                : null,
        };
    }

    public static CompareMatrixCellDetail ProjectDetail(int index, SqlHarnessCompareReport compare)
    {
        var projected = Project(compare);
        return new CompareMatrixCellDetail(index, new CompareMatrixCellDetailContent(projected.Baseline, projected.Candidate,
            projected.Equivalence, projected.NoteworthyOperators, projected.Regression));
    }

    public static MeasureParameterSetSummary ProjectSet(MeasureParameterSetReport set) =>
        ProjectSet(set, SelectMeasureNoteworthy(set.Metrics.Operators, artifactId: null, "query", set.Name));

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
            Warnings = set.Metrics.Warnings,
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
        var elapsedUnavailable = report?.ElapsedTimeAvailability == BenchmarkMetricReport.Unavailable;
        decimal? elapsed = elapsedUnavailable
            ? null
            : report?.ElapsedTimeMillisecondsExact?.Median ?? variant.ElapsedTimeMilliseconds.Median;
        decimal? elapsedMin = elapsedUnavailable
            ? null
            : report?.ElapsedTimeMillisecondsExact?.Min ?? variant.ElapsedTimeMilliseconds.Min;
        decimal? elapsedMax = elapsedUnavailable
            ? null
            : report?.ElapsedTimeMillisecondsExact?.Max ?? variant.ElapsedTimeMilliseconds.Max;
        return new(
            report?.CpuTimeAvailability == BenchmarkMetricReport.Unavailable ? null : variant.CpuTimeMilliseconds.Median,
            elapsed,
            report?.LogicalReadsAvailability == BenchmarkMetricReport.Unavailable ? null : variant.LogicalReads.Median)
        {
            ElapsedMinimum = elapsedMin,
            ElapsedMaximum = elapsedMax,
        };
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

    internal static IReadOnlyList<NoteworthyOperatorSummary> SelectCompareNoteworthy(
        IReadOnlyList<CompareOperatorReport> baseline,
        IReadOnlyList<CompareOperatorReport> candidate,
        string? artifactId)
    {
        var baselineByKey = GroupSide(baseline);
        var candidateByKey = GroupSide(candidate);

        var selected = new List<NoteworthyOperatorSummary>();
        foreach (var key in baselineByKey.Keys.Union(candidateByKey.Keys, OperatorKeyComparer.Instance))
        {
            baselineByKey.TryGetValue(key, out var left);
            candidateByKey.TryGetValue(key, out var right);
            left ??= [];
            right ??= [];
            var oneSideOnly = left.Count == 0 || right.Count == 0;
            var leftFlags = Flags(left);
            var rightFlags = Flags(right);
            var hasWarnings = leftFlags.HasWarnings || rightFlags.HasWarnings;
            var hasSpill = leftFlags.HasSpill || rightFlags.HasSpill;
            var hasImplicit = leftFlags.HasImplicitConversion || rightFlags.HasImplicitConversion;
            if (!oneSideOnly && !(hasWarnings || hasSpill || hasImplicit))
                continue;

            var leftRep = PickRepresentative(left);
            var rightRep = PickRepresentative(right);
            var side = left.Count == 0 ? "candidate" : right.Count == 0 ? "baseline" : "both";
            var representative = side == "candidate" ? rightRep! : leftRep!;
            var planRefs = new List<OperatorPlanRef>(2);
            if (leftRep is not null && TryPlanRef(artifactId, "baseline", leftRep, out var leftRef))
                planRefs.Add(leftRef);
            if (rightRep is not null && TryPlanRef(artifactId, "candidate", rightRep, out var rightRef))
                planRefs.Add(rightRef);
            selected.Add(new NoteworthyOperatorSummary(
                side,
                representative.NodeId,
                representative.PhysicalOp,
                representative.Object,
                hasWarnings,
                hasSpill,
                hasImplicit)
            {
                PlanRefs = planRefs.Count == 0 ? null : planRefs,
                Baseline = side == "both" ? leftFlags : null,
                Candidate = side == "both" ? rightFlags : null,
                SourceIsCapped = left.Count > 1 || right.Count > 1,
            });
        }

        var ordered = selected
            .OrderByDescending(HasAttention)
            .ThenBy(op => op.PhysicalOp, StringComparer.Ordinal)
            .ThenBy(op => op.Object ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(op => op.Side, StringComparer.Ordinal)
            .ThenBy(op => op.NodeId)
            .ToArray();
        var listCapped = ordered.Length > MaximumNoteworthyOperators;
        return ordered
            .Take(MaximumNoteworthyOperators)
            .Select(op => op with { SourceIsCapped = listCapped || op.SourceIsCapped })
            .ToArray();
    }

    internal static IReadOnlyList<NoteworthyOperatorSummary> SelectMeasureNoteworthy(
        IReadOnlyList<CompareOperatorReport> operators,
        string? artifactId,
        string variant,
        string? parameterSet)
    {
        var flagged = operators.Where(HasAttention).ToArray();
        var ordered = flagged
            .GroupBy(OperatorKey, OperatorKeyComparer.Instance)
            .Select(group =>
            {
                var representative = PickRepresentative(group)!;
                TryPlanRef(artifactId, variant, representative with { ParameterSet = representative.ParameterSet ?? parameterSet }, out var planRef);
                return new NoteworthyOperatorSummary(
                    variant,
                    representative.NodeId,
                    representative.PhysicalOp,
                    representative.Object,
                    group.Any(op => op.HasWarnings),
                    group.Any(op => op.HasSpill),
                    group.Any(op => op.HasImplicitConversion))
                {
                    PlanRefs = planRef is null ? null : [planRef],
                    SourceIsCapped = group.Count() > 1,
                };
            })
            .OrderByDescending(HasAttention)
            .ThenBy(op => op.PhysicalOp, StringComparer.Ordinal)
            .ThenBy(op => op.Object ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(op => op.NodeId)
            .ToArray();
        var listCapped = ordered.Length > MaximumNoteworthyOperators || flagged.Length > MaximumNoteworthyOperators;
        return ordered
            .Take(MaximumNoteworthyOperators)
            .Select(op => op with { SourceIsCapped = listCapped || op.SourceIsCapped })
            .ToArray();
    }

    private static bool HasAttention(NoteworthyOperatorSummary op) =>
        op.HasWarnings || op.HasSpill || op.HasImplicitConversion;

    private static bool HasAttention(CompareOperatorReport op) =>
        op.HasWarnings || op.HasSpill || op.HasImplicitConversion;

    private static Dictionary<(string PhysicalOp, string Object), IReadOnlyList<CompareOperatorReport>> GroupSide(
        IReadOnlyList<CompareOperatorReport> operators) =>
        operators
            .GroupBy(OperatorKey, OperatorKeyComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CompareOperatorReport>)group.ToArray(),
                OperatorKeyComparer.Instance);

    private static NoteworthyOperatorSideEvidence Flags(IReadOnlyList<CompareOperatorReport> operators) =>
        new(
            operators.Any(op => op.HasWarnings),
            operators.Any(op => op.HasSpill),
            operators.Any(op => op.HasImplicitConversion));

    private static CompareOperatorReport? PickRepresentative(IEnumerable<CompareOperatorReport> operators) =>
        operators
            .OrderByDescending(HasAttention)
            .ThenBy(op => op.Statement ?? int.MaxValue)
            .ThenBy(op => op.Repetition ?? int.MaxValue)
            .ThenBy(op => op.NodePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(op => op.NodeId)
            .FirstOrDefault();

    private static bool TryPlanRef(string? artifactId, string variant, CompareOperatorReport op, out OperatorPlanRef planRef)
    {
        planRef = null!;
        if (artifactId is null
            || op.NodePath is not { Length: > 0 }
            || op.Statement is not >= 0
            || op.Repetition is not > 0
            || op.Cursor is not >= 0)
            return false;
        planRef = new OperatorPlanRef(
            artifactId,
            variant,
            op.ParameterSet,
            op.Statement.Value,
            op.Repetition.Value,
            op.NodePath,
            op.Cursor.Value);
        return true;
    }

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