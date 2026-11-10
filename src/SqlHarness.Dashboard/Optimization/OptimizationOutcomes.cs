namespace SqlHarness.Dashboard;

internal static class OptimizationOutcomes
{
    public static IReadOnlyList<CaseOutcomeDetail> Build(CompareEvidence? direct,
        IReadOnlyDictionary<string, CompareMetricEvidence> before, IReadOnlyDictionary<string, CompareMetricEvidence> after,
        IEnumerable<string> ids, IReadOnlyDictionary<string, string> labels, bool forward,
        bool baseline = false, bool approximate = false)
    {
        return ids.Select(id =>
        {
            var matchingCases = direct?.Cases.Where(c => (c.MeasurementFingerprint ?? c.CaseId) == id).Take(2).ToArray() ?? [];
            var ambiguous = matchingCases.Length > 1;
            var left = ambiguous ? null : before.GetValueOrDefault(id);
            var right = ambiguous ? null : after.GetValueOrDefault(id);
            var evidence = matchingCases.Length == 1 ? matchingCases[0] : null;
            var readsBefore = left?.LogicalReadsMedian;
            var readsAfter = right?.LogicalReadsMedian;
            var io = "unavailable";
            if (!baseline && !approximate && left is not null && right is not null)
            {
                var attribution = OptimizationComparer.CompareCase(left, right);
                var a = attribution.VerdictBasis == "changed statements" ? attribution.ChangedBefore : readsBefore;
                var b = attribution.VerdictBasis == "changed statements" ? attribution.ChangedAfter : readsAfter;
                if (a.HasValue && b.HasValue) io = OptimizationComparer.Outcome(a.Value, b.Value);
            }
            var recorded = !baseline && forward && !approximate && evidence is not null;
            var verdict = recorded ? evidence!.RegressionVerdict : null;
            var reason = baseline ? "Baseline measurement; no improvement or policy verdict against itself."
                : ambiguous ? "Ambiguous case identity; no unique recorded policy can be selected."
                : approximate ? "Different conditions; no direct recorded policy applies."
                : direct is null ? "Measured independently; no direct recorded policy applies."
                : !forward ? "The recorded policy applies to the original A/B direction."
                : verdict is null ? "No policy verdict was recorded for this case."
                : verdict is not ("pass" or "fail") ? "The recorded policy was inconclusive." : null;
            return new CaseOutcomeDetail(id, labels.GetValueOrDefault(id, id),
                approximate ? null : evidence?.ResultsEquivalent, io,
                verdict is "pass" or "fail" ? verdict : null, reason,
                recorded ? evidence!.PolicyReasons : [], readsBefore, baseline ? null : readsAfter,
                left?.ElapsedMedianMs, baseline ? null : right?.ElapsedMedianMs, left?.CpuMedianMs, baseline ? null : right?.CpuMedianMs);
        }).OrderBy(row => row.PolicyVerdict == "fail" ? 0 : row.IoOutcome == "worse" ? 1 : 2).ToArray();
    }

    public static OutcomeCounts Count(IReadOnlyList<CaseOutcomeDetail> rows) => new(
        rows.Count(r => r.ResultsEquivalent == true), rows.Count(r => r.ResultsEquivalent == false), rows.Count(r => r.ResultsEquivalent is null),
        rows.Count(r => r.IoOutcome == "better"), rows.Count(r => r.IoOutcome == "same"), rows.Count(r => r.IoOutcome == "worse"), rows.Count(r => r.IoOutcome == "unavailable"),
        rows.Count(r => r.PolicyVerdict == "pass"), rows.Count(r => r.PolicyVerdict == "fail"), rows.Count(r => r.PolicyVerdict is null));

}