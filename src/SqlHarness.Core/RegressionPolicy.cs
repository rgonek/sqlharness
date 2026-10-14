namespace SqlHarness.Core;

/// <summary>Raw per-run samples and metric availability for one compare variant. Warm-ups are never included.</summary>
public sealed record RegressionVariantInput(
    IReadOnlyList<decimal> ElapsedMilliseconds,
    IReadOnlyList<long> LogicalReads,
    IReadOnlyList<long> CpuMilliseconds,
    string ElapsedAvailability,
    string LogicalReadsAvailability,
    string CpuAvailability);

/// <summary>
/// One compare pair. <paramref name="Equivalence"/> is null (or has mode Off, or an undetermined
/// <see cref="ResultEquivalenceReport.Equivalent"/>) when results were not compared.
/// </summary>
public sealed record RegressionInput(
    SqlEngine Engine,
    RegressionVariantInput Baseline,
    RegressionVariantInput Candidate,
    ResultEquivalenceReport? Equivalence);

/// <summary>Median comparison for one metric. Numbers are null when the status is unavailable.</summary>
public sealed record RegressionDiagnostic(
    string Status,
    decimal? BaselineMedian,
    decimal? CandidateMedian,
    decimal? Delta,
    decimal? DeltaPercent);

public sealed record RegressionReport(
    string Verdict,
    IReadOnlyList<string> Reasons,
    int PolicyVersion,
    RegressionDiagnostic Elapsed,
    RegressionDiagnostic LogicalReads,
    RegressionDiagnostic Cpu);

/// <summary>
/// Latency regression verdict for one compare pair. Pure: no I/O, clock or logging.
/// Gates (runs, elapsed availability, result equivalence, spread) are all evaluated and every failing
/// gate is reported in that order; any failure makes the verdict inconclusive. The elapsed threshold
/// decides pass/fail only when no gate failed. A variant's elapsed spread is unstable only when its range
/// exceeds both the 2 ms floor and a quarter of its median. Reads and CPU are diagnostics and never change the verdict.
/// </summary>
public static class RegressionPolicy
{
    public const int PolicyVersion = 2;

    public const int MinimumRunsPerVariant = 5;

    /// <summary>(max - min) / median above this value makes a variant unstable.</summary>
    public const decimal MaxElapsedSpread = 0.25m;

    /// <summary>
    /// An elapsed range (max - min) at or below this many ms never makes a variant unstable:
    /// whole-millisecond samples jitter by a tick or two regardless of the median.
    /// </summary>
    public const decimal ElapsedSpreadFloorMilliseconds = 2m;

    public const decimal ElapsedRatio = 1.10m;
    public const decimal ElapsedMinimumDeltaMilliseconds = 5m;

    /// <summary>With a zero baseline median, a candidate median above this many ms fails.</summary>
    public const decimal ZeroBaselineCeilingMilliseconds = 5m;

    public const decimal LogicalReadsRatio = 1.10m;
    public const decimal LogicalReadsMinimumDelta = 100m;

    public const decimal CpuRatio = 1.15m;
    public const decimal CpuMinimumDeltaMilliseconds = 5m;

    /// <summary>DeltaPercent is rounded to this many decimals (away from zero); nothing else is rounded.</summary>
    public const int DeltaPercentDecimals = 2;

    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Inconclusive = "inconclusive";

    public const string ReasonTooFewRuns = "too-few-runs";
    public const string ReasonElapsedUnavailable = "elapsed-unavailable";
    public const string ReasonResultsNotCompared = "results-not-compared";
    public const string ReasonResultsNotEquivalent = "results-not-equivalent";
    public const string ReasonUnstableElapsed = "unstable-elapsed";
    public const string ReasonElapsedRegressed = "elapsed-regressed";

    public const string StatusRegressed = "regressed";
    public const string StatusNotRegressed = "not-regressed";
    public const string StatusUnavailable = "unavailable";
    public const string StatusInconclusive = "inconclusive";

    public static RegressionReport Evaluate(RegressionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var baseline = input.Baseline;
        var candidate = input.Candidate;

        var reasons = new List<string>();
        if (baseline.ElapsedMilliseconds.Count < MinimumRunsPerVariant ||
            candidate.ElapsedMilliseconds.Count < MinimumRunsPerVariant)
            reasons.Add(ReasonTooFewRuns);

        var elapsedAvailable = IsMeasured(baseline.ElapsedAvailability) &&
            IsMeasured(candidate.ElapsedAvailability) &&
            baseline.ElapsedMilliseconds.Count > 0 &&
            candidate.ElapsedMilliseconds.Count > 0;
        if (!IsMeasured(baseline.ElapsedAvailability) || !IsMeasured(candidate.ElapsedAvailability))
            reasons.Add(ReasonElapsedUnavailable);

        var equivalence = input.Equivalence;
        if (equivalence is null || equivalence.Mode == ResultComparisonMode.Off || equivalence.Equivalent is null)
            reasons.Add(ReasonResultsNotCompared);
        else if (equivalence.Equivalent == false)
            reasons.Add(ReasonResultsNotEquivalent);

        if (elapsedAvailable &&
            (IsUnstable(baseline.ElapsedMilliseconds) || IsUnstable(candidate.ElapsedMilliseconds)))
            reasons.Add(ReasonUnstableElapsed);

        var elapsedRegressed = false;
        decimal elapsedBaselineMedian = 0m;
        decimal elapsedCandidateMedian = 0m;
        if (elapsedAvailable)
        {
            elapsedBaselineMedian = Median(baseline.ElapsedMilliseconds);
            elapsedCandidateMedian = Median(candidate.ElapsedMilliseconds);
            elapsedRegressed = elapsedBaselineMedian > 0m
                ? ExceedsRatioAndDelta(
                    elapsedBaselineMedian, elapsedCandidateMedian, ElapsedRatio, ElapsedMinimumDeltaMilliseconds)
                : elapsedCandidateMedian > ZeroBaselineCeilingMilliseconds;
        }

        string verdict;
        if (reasons.Count > 0)
            verdict = Inconclusive;
        else if (elapsedRegressed)
        {
            verdict = Fail;
            reasons.Add(ReasonElapsedRegressed);
        }
        else
            verdict = Pass;

        // The elapsed status mirrors the verdict: a failed gate makes it inconclusive, medians stay visible.
        var elapsed = !elapsedAvailable
            ? Unavailable()
            : Diagnostic(
                verdict == Inconclusive ? StatusInconclusive : elapsedRegressed ? StatusRegressed : StatusNotRegressed,
                elapsedBaselineMedian, elapsedCandidateMedian);

        var logicalReads = CostDiagnostic(
            baseline.LogicalReadsAvailability, candidate.LogicalReadsAvailability,
            ToDecimals(baseline.LogicalReads),
            ToDecimals(candidate.LogicalReads),
            LogicalReadsRatio, LogicalReadsMinimumDelta);

        // PostgreSQL has no CPU time source: the metric is unavailable whatever the token says.
        var cpu = input.Engine == SqlEngine.Postgres
            ? Unavailable()
            : CostDiagnostic(
                baseline.CpuAvailability, candidate.CpuAvailability,
                ToDecimals(baseline.CpuMilliseconds),
                ToDecimals(candidate.CpuMilliseconds),
                CpuRatio, CpuMinimumDeltaMilliseconds);

        return new RegressionReport(verdict, reasons.ToArray(), PolicyVersion, elapsed, logicalReads, cpu);
    }

    private static RegressionDiagnostic CostDiagnostic(
        string baselineAvailability,
        string candidateAvailability,
        decimal[] baselineSamples,
        decimal[] candidateSamples,
        decimal ratio,
        decimal minimumDelta)
    {
        if (!IsMeasured(baselineAvailability) || !IsMeasured(candidateAvailability) ||
            baselineSamples.Length == 0 || candidateSamples.Length == 0)
            return Unavailable();

        var baselineMedian = Median(baselineSamples);
        var candidateMedian = Median(candidateSamples);
        var regressed = ExceedsRatioAndDelta(baselineMedian, candidateMedian, ratio, minimumDelta);
        return Diagnostic(regressed ? StatusRegressed : StatusNotRegressed, baselineMedian, candidateMedian);
    }

    private static bool ExceedsRatioAndDelta(
        decimal baselineMedian, decimal candidateMedian, decimal ratio, decimal minimumDelta) =>
        candidateMedian >= baselineMedian * ratio && candidateMedian - baselineMedian >= minimumDelta;

    private static decimal[] ToDecimals(IReadOnlyList<long> samples) =>
        samples.Select(static value => (decimal)value).ToArray();

    private static RegressionDiagnostic Diagnostic(string status, decimal baselineMedian, decimal candidateMedian)
    {
        var delta = candidateMedian - baselineMedian;
        decimal? percent = baselineMedian == 0m
            ? null
            : Math.Round(delta / baselineMedian * 100m, DeltaPercentDecimals, MidpointRounding.AwayFromZero);
        return new RegressionDiagnostic(
            status, baselineMedian, candidateMedian, delta, percent);
    }

    private static RegressionDiagnostic Unavailable() =>
        new(StatusUnavailable, null, null, null, null);

    private static bool IsMeasured(string availability) =>
        string.Equals(availability, BenchmarkMetricReport.Measured, StringComparison.Ordinal);

    private static bool IsUnstable(IReadOnlyList<decimal> samples)
    {
        var median = Median(samples);
        var range = samples.Max() - samples.Min();
        if (range <= ElapsedSpreadFloorMilliseconds)
            return false;
        return median == 0m || range / median > MaxElapsedSpread;
    }

    private static decimal Median(IReadOnlyList<decimal> samples)
    {
        var ordered = samples.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2m;
    }
}