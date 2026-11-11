namespace SqlHarness.Dashboard;

internal static class OptimizationOutcomes
{
    public static IReadOnlyList<CaseOutcomeDetail> Build(CompareEvidence? direct,
        IReadOnlyDictionary<string, CompareMetricEvidence> before, IReadOnlyDictionary<string, CompareMetricEvidence> after,
        IEnumerable<string> ids, IReadOnlyDictionary<string, string> labels, bool forward,
        bool baseline = false, bool approximate = false, IReadOnlyDictionary<string, int>? caseCounts = null)
    {
        return ids.Select(id =>
        {
            var matchingCases = direct?.Cases.Where(c => (c.MeasurementFingerprint ?? c.CaseId) == id).Take(2).ToArray() ?? [];
            var caseCount = Math.Max(1, Math.Max(matchingCases.Length, caseCounts?.GetValueOrDefault(id) ?? 1));
            var ambiguous = matchingCases.Length > 1 || before.GetValueOrDefault(id)?.CaseIdentityAmbiguous == true
                || after.GetValueOrDefault(id)?.CaseIdentityAmbiguous == true;
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
            var recorded = !baseline && forward && !approximate && !ambiguous && evidence is not null;
            var verdict = recorded ? evidence!.RegressionVerdict : null;
            var reason = baseline ? "Baseline measurement; no improvement or policy verdict against itself."
                : ambiguous ? "Ambiguous case identity; no unique recorded policy can be selected."
                : approximate ? "Different conditions; no direct recorded policy applies."
                : direct is null ? "Measured independently; no direct recorded policy applies."
                : !forward ? "The recorded policy applies to the original A/B direction."
                : verdict is null ? "No policy verdict was recorded for this case."
                : verdict is not ("pass" or "fail") ? "The recorded policy was inconclusive." : null;
            return new CaseOutcomeDetail(id, ambiguous ? $"Ambiguous case identity ({caseCount} cases)" : labels.GetValueOrDefault(id, id),
                approximate || ambiguous ? null : evidence?.ResultsEquivalent, io,
                verdict is "pass" or "fail" ? verdict : null, reason,
                recorded ? evidence!.PolicyReasons : [], readsBefore, baseline ? null : readsAfter,
                left?.ElapsedMedianMs, baseline ? null : right?.ElapsedMedianMs, left?.CpuMedianMs, baseline ? null : right?.CpuMedianMs)
            { CaseCount = caseCount };
        }).OrderBy(row => row.PolicyVerdict == "fail" ? 0 : row.IoOutcome == "worse" ? 1 : 2).ToArray();
    }

    public static OutcomeCounts Count(IReadOnlyList<CaseOutcomeDetail> rows) => new(
        rows.Where(r => r.ResultsEquivalent == true).Sum(r => r.CaseCount), rows.Where(r => r.ResultsEquivalent == false).Sum(r => r.CaseCount), rows.Where(r => r.ResultsEquivalent is null).Sum(r => r.CaseCount),
        rows.Where(r => r.IoOutcome == "better").Sum(r => r.CaseCount), rows.Where(r => r.IoOutcome == "same").Sum(r => r.CaseCount), rows.Where(r => r.IoOutcome == "worse").Sum(r => r.CaseCount), rows.Where(r => r.IoOutcome == "unavailable").Sum(r => r.CaseCount),
        rows.Where(r => r.PolicyVerdict == "pass").Sum(r => r.CaseCount), rows.Where(r => r.PolicyVerdict == "fail").Sum(r => r.CaseCount), rows.Where(r => r.PolicyVerdict is null).Sum(r => r.CaseCount));

}