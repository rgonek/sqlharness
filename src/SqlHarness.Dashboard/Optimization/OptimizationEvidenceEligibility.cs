namespace SqlHarness.Dashboard;

internal static class OptimizationEvidenceEligibility
{
    internal static bool HasVariant(CompareEvidence compare, string variant) =>
        compare.Status == "succeeded"
        && (variant == "candidate" ? compare.CandidateHash : compare.BaselineHash) is not null
        && compare.Metrics.Any(metric => metric.Variant.Equals(variant, StringComparison.OrdinalIgnoreCase)
            && (metric.LogicalReadsMedian.HasValue || metric.ElapsedMedianMs.HasValue || metric.CpuMedianMs.HasValue
                || metric.ScansSum.HasValue || metric.Postgres is not null || metric.RowsReturned.HasValue
                || metric.TableRows.Count > 0 || metric.Statements.Count > 0));

    internal static bool HasAny(CompareEvidence compare) => compare.IsMeasure
        ? HasVariant(compare, "measure")
        : HasVariant(compare, "baseline") || HasVariant(compare, "candidate");
}
