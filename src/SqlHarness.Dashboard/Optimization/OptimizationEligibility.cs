namespace SqlHarness.Dashboard;

/// <summary>Interrupted attempt evidence stays inspectable, but cannot establish versions or comparisons.</summary>
internal static class OptimizationEligibility
{
    public static bool IsEligible(string status, bool usableMeasurement) => status == "succeeded" && usableMeasurement;

    public static bool IsEligible(CompareEvidence evidence) => OptimizationEvidenceEligibility.HasAny(evidence);
}