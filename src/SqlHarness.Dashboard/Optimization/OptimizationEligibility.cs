namespace SqlHarness.Dashboard;

/// <summary>Interrupted attempt evidence stays inspectable, but cannot establish versions or comparisons.</summary>
internal static class OptimizationEligibility
{
    public static bool IsEligible(string status, bool usableMeasurement) => status == "succeeded" && usableMeasurement;

    public static bool IsEligible(CompareEvidence evidence) => IsEligible(evidence.Status, evidence.Metrics.Any(metric =>
            (evidence.IsMeasure
                ? metric.Variant.Equals("measure", StringComparison.OrdinalIgnoreCase) && evidence.BaselineHash is not null
                : metric.Variant.Equals("baseline", StringComparison.OrdinalIgnoreCase) && evidence.BaselineHash is not null
                    || metric.Variant.Equals("candidate", StringComparison.OrdinalIgnoreCase) && evidence.CandidateHash is not null)
            && (metric.LogicalReadsMedian.HasValue || metric.ElapsedMedianMs.HasValue || metric.CpuMedianMs.HasValue
                || metric.RowsReturned.HasValue || metric.Postgres is not null || metric.TableRows.Count > 0)));
}