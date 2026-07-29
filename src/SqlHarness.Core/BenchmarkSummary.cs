namespace SqlHarness.Core;

public sealed record BenchmarkVariantSummary(
    CompareDistribution CpuTimeMilliseconds,
    CompareDistribution ElapsedTimeMilliseconds,
    CompareDistribution LogicalReads,
    IReadOnlyDictionary<string, CompareDistribution> LogicalReadsByTable,
    IReadOnlyList<string> Warnings);

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
    string? ArtifactDirectory);

public sealed record MeasureBenchmarkSummary(
    SqlHarnessTargetIdentityReport Target,
    int Repetitions,
    bool ResultsStable,
    BenchmarkClassificationReport Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    BenchmarkVariantSummary Query,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    string? ArtifactDirectory);

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
            report.ArtifactDirectory);
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

    private static BenchmarkVariantSummary ProjectVariant(CompareVariantReport variant) =>
        new(
            variant.CpuTimeMilliseconds,
            variant.ElapsedTimeMilliseconds,
            variant.LogicalReads,
            variant.LogicalReadsByTable,
            variant.Warnings);

    private static IReadOnlyList<NoteworthyOperatorSummary> SelectCompareNoteworthy(
        IReadOnlyList<CompareOperatorReport> baseline,
        IReadOnlyList<CompareOperatorReport> candidate)
    {
        var baselineByKey = baseline
            .GroupBy(OperatorKey, OperatorKeyComparer.Instance)
            .ToDictionary(group => group.Key, group => group.First(), OperatorKeyComparer.Instance);
        var candidateByKey = candidate
            .GroupBy(OperatorKey, OperatorKeyComparer.Instance)
            .ToDictionary(group => group.Key, group => group.First(), OperatorKeyComparer.Instance);

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
