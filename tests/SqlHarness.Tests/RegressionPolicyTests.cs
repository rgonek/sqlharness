using SqlHarness.Core;

namespace SqlHarness.Tests;

public class RegressionPolicyTests
{
    private const string Measured = BenchmarkMetricReport.Measured;
    private const string Unavailable = BenchmarkMetricReport.Unavailable;

    private static readonly ResultEquivalenceReport Equivalent =
        new(ResultComparisonMode.Ordered, true, 0, 0, 0);

    private static RegressionVariantInput Variant(
        decimal[] elapsed,
        long[]? reads = null,
        long[]? cpu = null,
        string elapsedAvailability = Measured,
        string readsAvailability = Measured,
        string cpuAvailability = Measured) =>
        new(elapsed,
            reads ?? Enumerable.Repeat(1000L, elapsed.Length).ToArray(),
            cpu ?? Enumerable.Repeat(50L, elapsed.Length).ToArray(),
            elapsedAvailability, readsAvailability, cpuAvailability);

    private static decimal[] Flat(decimal value, int count = 5) =>
        Enumerable.Repeat(value, count).ToArray();

    private static RegressionReport Run(
        RegressionVariantInput baseline,
        RegressionVariantInput candidate,
        ResultEquivalenceReport? equivalence = null,
        SqlEngine engine = SqlEngine.SqlServer) =>
        RegressionPolicy.Evaluate(new RegressionInput(engine, baseline, candidate, equivalence ?? Equivalent));

    public static TheoryData<string, decimal, decimal, string, string[]> ElapsedThresholdRows => new()
    {
        { "100 to 1000 fails", 100m, 1000m, RegressionPolicy.Fail, [RegressionPolicy.ReasonElapsedRegressed] },
        { "100 to 105 passes", 100m, 105m, RegressionPolicy.Pass, [] },
        { "0 to 5 passes", 0m, 5m, RegressionPolicy.Pass, [] },
        { "0 to 6 fails", 0m, 6m, RegressionPolicy.Fail, [RegressionPolicy.ReasonElapsedRegressed] },
        { "exact 1.10x and exact 5 ms fails", 50m, 55m, RegressionPolicy.Fail, [RegressionPolicy.ReasonElapsedRegressed] },
        { "ratio met but delta below 5 ms passes", 10m, 11m, RegressionPolicy.Pass, [] },
        { "delta met but ratio below 1.10 passes", 1000m, 1009m, RegressionPolicy.Pass, [] },
        { "faster candidate passes", 100m, 50m, RegressionPolicy.Pass, [] },
    };

    [Theory]
    [MemberData(nameof(ElapsedThresholdRows))]
    public void Elapsed_threshold_decides_pass_or_fail(
        string _, decimal baseline, decimal candidate, string verdict, string[] reasons)
    {
        var report = Run(Variant(Flat(baseline)), Variant(Flat(candidate)));

        Assert.Equal(verdict, report.Verdict);
        Assert.Equal(reasons, report.Reasons);
        Assert.Equal(RegressionPolicy.PolicyVersion, report.PolicyVersion);
    }

    [Fact]
    public void Elapsed_diagnostic_reports_medians_delta_and_rounded_percent()
    {
        var report = Run(Variant(Flat(30m)), Variant(Flat(40m)));

        Assert.Equal(RegressionPolicy.StatusRegressed, report.Elapsed.Status);
        Assert.Equal(30m, report.Elapsed.BaselineMedian);
        Assert.Equal(40m, report.Elapsed.CandidateMedian);
        Assert.Equal(10m, report.Elapsed.Delta);
        Assert.Equal(33.33m, report.Elapsed.DeltaPercent);
    }

    [Fact]
    public void Zero_baseline_has_no_delta_percent()
    {
        var report = Run(Variant(Flat(0m)), Variant(Flat(3m)));

        Assert.Equal(3m, report.Elapsed.Delta);
        Assert.Null(report.Elapsed.DeltaPercent);
    }

    [Fact]
    public void Even_run_counts_use_the_mean_of_the_two_middle_samples()
    {
        // Sorted: 100 100 100 110 110 110 -> median 105; candidate flat 100.
        var report = Run(
            Variant([100m, 100m, 100m, 110m, 110m, 110m]),
            Variant(Flat(100m, 6)));

        Assert.Equal(105m, report.Elapsed.BaselineMedian);
    }

    [Fact]
    public void Four_runs_per_variant_is_inconclusive()
    {
        var report = Run(Variant(Flat(100m, 4)), Variant(Flat(1000m, 4)));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal([RegressionPolicy.ReasonTooFewRuns], report.Reasons);
    }

    [Fact]
    public void One_variant_with_too_few_runs_is_inconclusive()
    {
        var report = Run(Variant(Flat(100m, 5)), Variant(Flat(100m, 4)));

        Assert.Equal([RegressionPolicy.ReasonTooFewRuns], report.Reasons);
    }

    [Fact]
    public void Spread_above_a_quarter_is_unstable()
    {
        // median 100, range 26 -> 0.26
        var report = Run(Variant([100m, 100m, 100m, 100m, 126m]), Variant(Flat(100m)));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal([RegressionPolicy.ReasonUnstableElapsed], report.Reasons);
    }

    [Fact]
    public void Spread_of_exactly_a_quarter_passes_the_gate()
    {
        // median 100, range 25 -> 0.25
        var report = Run(Variant([100m, 100m, 100m, 100m, 125m]), Variant(Flat(100m)));

        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
        Assert.Empty(report.Reasons);
    }

    [Fact]
    public void Candidate_spread_is_gated_too()
    {
        var report = Run(Variant(Flat(100m)), Variant([100m, 100m, 100m, 100m, 130m]));

        Assert.Equal([RegressionPolicy.ReasonUnstableElapsed], report.Reasons);
    }

    [Fact]
    public void Zero_median_with_a_positive_maximum_is_unstable()
    {
        var report = Run(Variant([0m, 0m, 0m, 0m, 4m]), Variant(Flat(0m)));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal([RegressionPolicy.ReasonUnstableElapsed], report.Reasons);
    }

    [Fact]
    public void All_zero_samples_pass_the_spread_gate()
    {
        var report = Run(Variant(Flat(0m)), Variant(Flat(0m)));

        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
    }

    [Fact]
    public void Elapsed_unavailable_on_one_variant_is_inconclusive_and_has_no_elapsed_diagnostic()
    {
        var report = Run(
            Variant(Flat(100m)),
            Variant(Flat(1000m), elapsedAvailability: Unavailable));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal([RegressionPolicy.ReasonElapsedUnavailable], report.Reasons);
        Assert.Equal(RegressionPolicy.StatusUnavailable, report.Elapsed.Status);
        Assert.Null(report.Elapsed.BaselineMedian);
        Assert.Null(report.Elapsed.CandidateMedian);
        Assert.Null(report.Elapsed.Delta);
        Assert.Null(report.Elapsed.DeltaPercent);
    }

    [Fact]
    public void Elapsed_availability_is_compared_ordinally()
    {
        var report = Run(Variant(Flat(100m)), Variant(Flat(100m), elapsedAvailability: "Measured"));

        Assert.Equal([RegressionPolicy.ReasonElapsedUnavailable], report.Reasons);
    }

    public static TheoryData<string, ResultEquivalenceReport?, string> EquivalenceRows => new()
    {
        { "compare-results off", new(ResultComparisonMode.Off, null, null, null, null), RegressionPolicy.ReasonResultsNotCompared },
        { "missing equivalence", null, RegressionPolicy.ReasonResultsNotCompared },
        { "undetermined equivalence", new(ResultComparisonMode.Ordered, null, null, null, null), RegressionPolicy.ReasonResultsNotCompared },
        { "not equivalent", new(ResultComparisonMode.Ordered, false, 3, null, null), RegressionPolicy.ReasonResultsNotEquivalent },
        {
            "strict nullability mismatch",
            new(ResultComparisonMode.Ordered, false, 0, null, null,
                CompareNullability: true, NullabilityEquivalent: false,
                MismatchReason: ResultMismatchReasons.Nullability),
            RegressionPolicy.ReasonResultsNotEquivalent
        },
    };

    [Theory]
    [MemberData(nameof(EquivalenceRows))]
    public void Result_equivalence_gate_is_inconclusive_never_a_performance_fail(
        string _, ResultEquivalenceReport? equivalence, string reason)
    {
        // A huge slowdown must not surface as a fail when results cannot be trusted.
        var report = RegressionPolicy.Evaluate(new RegressionInput(
            SqlEngine.SqlServer, Variant(Flat(100m)), Variant(Flat(1000m)), equivalence));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal([reason], report.Reasons);
    }

    [Fact]
    public void Strict_nullability_report_that_is_equivalent_does_not_gate()
    {
        var strictEquivalent = new ResultEquivalenceReport(
            ResultComparisonMode.Ordered, true, 0, null, null,
            CompareNullability: true, NullabilityEquivalent: true);

        var report = Run(Variant(Flat(100m)), Variant(Flat(100m)), strictEquivalent);

        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
    }

    [Fact]
    public void Postgres_cpu_is_unavailable_even_with_a_measured_token()
    {
        var report = Run(
            Variant(Flat(100m), cpu: Enumerable.Repeat(10L, 5).ToArray(), cpuAvailability: Measured),
            Variant(Flat(100m), cpu: Enumerable.Repeat(1000L, 5).ToArray(), cpuAvailability: Measured),
            engine: SqlEngine.Postgres);

        Assert.Equal(RegressionPolicy.StatusUnavailable, report.Cpu.Status);
        Assert.Null(report.Cpu.BaselineMedian);
        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
    }

    [Fact]
    public void Sql_server_cpu_is_diagnosed_when_measured()
    {
        var report = Run(
            Variant(Flat(100m), cpu: Enumerable.Repeat(100L, 5).ToArray()),
            Variant(Flat(100m), cpu: Enumerable.Repeat(115L, 5).ToArray()));

        Assert.Equal(RegressionPolicy.StatusRegressed, report.Cpu.Status);
        Assert.Equal(15m, report.Cpu.Delta);
        Assert.Equal(15m, report.Cpu.DeltaPercent);
        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
    }

    [Theory]
    [InlineData(100L, 115L, RegressionPolicy.StatusRegressed)]
    [InlineData(100L, 114L, RegressionPolicy.StatusNotRegressed)]
    [InlineData(10L, 12L, RegressionPolicy.StatusNotRegressed)] // ratio met, delta below 5 ms
    [InlineData(40L, 45L, RegressionPolicy.StatusNotRegressed)] // delta met, ratio below 1.15
    [InlineData(0L, 5L, RegressionPolicy.StatusRegressed)]
    public void Cpu_regression_needs_ratio_and_delta(long baseline, long candidate, string status)
    {
        var report = Run(
            Variant(Flat(100m), cpu: Enumerable.Repeat(baseline, 5).ToArray()),
            Variant(Flat(100m), cpu: Enumerable.Repeat(candidate, 5).ToArray()));

        Assert.Equal(status, report.Cpu.Status);
    }

    [Theory]
    [InlineData(1000L, 1100L, RegressionPolicy.StatusRegressed)] // exactly +10% and +100
    [InlineData(1000L, 1099L, RegressionPolicy.StatusNotRegressed)]
    [InlineData(500L, 550L, RegressionPolicy.StatusNotRegressed)] // +10% but delta 50 < 100
    [InlineData(0L, 100L, RegressionPolicy.StatusRegressed)]
    [InlineData(1000L, 500L, RegressionPolicy.StatusNotRegressed)]
    public void Logical_reads_regression_never_changes_the_verdict(long baseline, long candidate, string status)
    {
        var report = Run(
            Variant(Flat(100m), reads: Enumerable.Repeat(baseline, 5).ToArray()),
            Variant(Flat(100m), reads: Enumerable.Repeat(candidate, 5).ToArray()));

        Assert.Equal(status, report.LogicalReads.Status);
        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
        Assert.Empty(report.Reasons);
    }

    [Fact]
    public void Cost_metric_unavailable_on_either_variant_is_unavailable()
    {
        var report = Run(
            Variant(Flat(100m), readsAvailability: Measured, cpuAvailability: Measured),
            Variant(Flat(100m), readsAvailability: Unavailable, cpuAvailability: Unavailable));

        Assert.Equal(RegressionPolicy.StatusUnavailable, report.LogicalReads.Status);
        Assert.Equal(RegressionPolicy.StatusUnavailable, report.Cpu.Status);
        Assert.Null(report.LogicalReads.Delta);
        Assert.Equal(RegressionPolicy.Pass, report.Verdict);
    }

    [Fact]
    public void Cost_regression_does_not_rescue_or_fail_an_elapsed_verdict()
    {
        var report = Run(
            Variant(Flat(100m), reads: Enumerable.Repeat(1000L, 5).ToArray()),
            Variant(Flat(1000m), reads: Enumerable.Repeat(1L, 5).ToArray()));

        Assert.Equal(RegressionPolicy.Fail, report.Verdict);
        Assert.Equal(RegressionPolicy.StatusNotRegressed, report.LogicalReads.Status);
    }

    [Fact]
    public void Every_failing_gate_is_listed_in_rule_order()
    {
        var report = RegressionPolicy.Evaluate(new RegressionInput(
            SqlEngine.SqlServer,
            Variant(Flat(100m, 4)),
            Variant([100m, 100m, 100m, 100m], elapsedAvailability: Unavailable),
            new ResultEquivalenceReport(ResultComparisonMode.Ordered, false, 1, null, null)));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal(
            [
                RegressionPolicy.ReasonTooFewRuns,
                RegressionPolicy.ReasonElapsedUnavailable,
                RegressionPolicy.ReasonResultsNotEquivalent,
            ],
            report.Reasons);
    }

    [Fact]
    public void Spread_gate_joins_the_other_gates_when_elapsed_is_available()
    {
        var report = RegressionPolicy.Evaluate(new RegressionInput(
            SqlEngine.SqlServer,
            Variant([100m, 100m, 100m, 100m, 200m]),
            Variant(Flat(100m)),
            new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null)));

        Assert.Equal(
            [RegressionPolicy.ReasonResultsNotCompared, RegressionPolicy.ReasonUnstableElapsed],
            report.Reasons);
    }

    [Fact]
    public void Failed_gate_keeps_the_elapsed_diagnostic_but_not_the_verdict()
    {
        var report = RegressionPolicy.Evaluate(new RegressionInput(
            SqlEngine.SqlServer, Variant(Flat(100m)), Variant(Flat(1000m)), null));

        Assert.Equal(RegressionPolicy.Inconclusive, report.Verdict);
        Assert.Equal(RegressionPolicy.StatusInconclusive, report.Elapsed.Status);
        Assert.Equal(100m, report.Elapsed.BaselineMedian);
        Assert.Equal(1000m, report.Elapsed.CandidateMedian);
        Assert.Equal(900m, report.Elapsed.Delta);
        Assert.Equal(900m, report.Elapsed.DeltaPercent);
    }

    [Fact]
    public void Report_serializes_with_camel_case_names()
    {
        var report = Run(Variant(Flat(100m)), Variant(Flat(100m)));

        var json = System.Text.Json.JsonSerializer.Serialize(report, System.Text.Json.JsonSerializerOptions.Web);

        Assert.Contains("\"verdict\":\"pass\"", json);
        Assert.Contains("\"policyVersion\":1", json);
        Assert.Contains("\"logicalReads\":{", json);
    }
}