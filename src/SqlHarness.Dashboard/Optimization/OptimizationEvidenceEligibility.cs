namespace SqlHarness.Dashboard;

internal static class OptimizationEvidenceEligibility
{
    internal static bool HasVariant(CompareEvidence compare, string variant) =>
        compare.Status == "succeeded"
        && (variant == "candidate" ? compare.CandidateHash : compare.BaselineHash) is not null
        && compare.Metrics.Any(metric => metric.Variant.Equals(variant, StringComparison.OrdinalIgnoreCase)
            && (metric.LogicalReadsMedian.HasValue || metric.ElapsedMedianMs.HasValue || metric.CpuMedianMs.HasValue
                || metric.ScansSum.HasValue || metric.Postgres is not null || metric.RowsReturned.HasValue
                || metric.TableRows.Count > 0 || metric.Statements.Any(HasStatementMeasurement)));

    private static bool HasStatementMeasurement(CompareStatementEvidence statement) =>
        statement.Status == "confirmed"
        && (statement.Reads.HasValue || statement.Scans.HasValue || statement.Executions.HasValue
            || statement.CpuMs.HasValue || statement.ElapsedMs.HasValue || statement.UdfElapsedMilliseconds.HasValue
            || statement.GrantRequestedKb.HasValue || statement.GrantGrantedKb.HasValue
            || statement.GrantMaxUsedKb.HasValue || statement.HasSpillWarning.HasValue);

    internal static bool HasAny(CompareEvidence compare) => compare.IsMeasure
        ? HasVariant(compare, "measure")
        : HasVariant(compare, "baseline") || HasVariant(compare, "candidate");
}
