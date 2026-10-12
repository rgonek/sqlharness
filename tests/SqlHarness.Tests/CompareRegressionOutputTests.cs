using System.Text.Json;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

/// <summary>Plan 051 task 2: the regression verdict reaches every compare output.</summary>
public class CompareRegressionOutputTests
{
    private static readonly SqlHarnessTargetIdentityReport Identity =
        new("safe-server", "safe-db", "safe-server", "safe-db", "profile");

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static RegressionDiagnostic Unavailable => new(RegressionPolicy.StatusUnavailable, null, null, null, null);

    private static RegressionReport Fail() => new(
        RegressionPolicy.Fail,
        [RegressionPolicy.ReasonElapsedRegressed],
        RegressionPolicy.PolicyVersion,
        new RegressionDiagnostic(RegressionPolicy.StatusRegressed, 100m, 1000m, 900m, 900m),
        new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 10m, 10m, 0m, 0m),
        Unavailable);

    private static RegressionReport Pass() => new(
        RegressionPolicy.Pass,
        [],
        RegressionPolicy.PolicyVersion,
        new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 100m, 98.5m, -1.5m, -1.5m),
        Unavailable,
        Unavailable);

    private static RegressionReport Inconclusive() => new(
        RegressionPolicy.Inconclusive,
        [RegressionPolicy.ReasonTooFewRuns, RegressionPolicy.ReasonResultsNotCompared],
        RegressionPolicy.PolicyVersion,
        Unavailable,
        Unavailable,
        Unavailable);

    private static CompareVariantReport Variant(string name) => new(
        name,
        new CompareDistribution(1, 2, 3),
        new CompareDistribution(2, 3, 4),
        new CompareDistribution(3, 4, 5),
        new Dictionary<string, long> { ["Clients"] = 4 },
        [],
        []);

    private static SqlHarnessCompareReport Report(RegressionReport? regression) =>
        new(Identity, 5, 10, true, Variant("baseline"), Variant("candidate"), null)
        {
            Regression = regression,
        };

    private static CompareCellRequest Request(ResultComparisonMode mode) =>
        new(
            new ResolvedTarget("test-server", "testdb-a", new AuthSpec(AuthStrategy.Integrated), "profile"),
            "SELECT Id INTO #ids FROM dbo.Clients",
            "SELECT Value FROM dbo.Clients",
            "SELECT Value FROM dbo.Clients -- candidate",
            [],
            30,
            2,
            mode);

    [Fact]
    public async Task CompareCellRunner_report_carries_the_regression_verdict()
    {
        var session = SqlHarnessCompareTests.FakeCompareSession.Create();
        var runner = new CompareCellRunner(session, new MemoryWriter());

        var cell = await runner.RunAsync(Request(ResultComparisonMode.Ordered), CancellationToken.None);

        var regression = Assert.IsType<RegressionReport>(cell.Report.Regression);
        Assert.Equal(RegressionPolicy.PolicyVersion, regression.PolicyVersion);
        // Two measured runs per variant (warm-up excluded) is below the minimum.
        Assert.Equal(RegressionPolicy.Inconclusive, regression.Verdict);
        Assert.Contains(RegressionPolicy.ReasonTooFewRuns, regression.Reasons);
        Assert.DoesNotContain(RegressionPolicy.ReasonResultsNotCompared, regression.Reasons);
    }

    [Fact]
    public async Task CompareCellRunner_stable_equal_samples_with_compared_results_pass()
    {
        var session = SqlHarnessCompareTests.FakeCompareSession.Create();
        session.StatisticsMessages =
        [
            "Table 'Clients'. Scan count 1, logical reads 5, physical reads 0, lob logical reads 0." + "\n" +
            "SQL Server Execution Times: CPU time = 10 ms, elapsed time = 20 ms.",
        ];
        var runner = new CompareCellRunner(session, new MemoryWriter());

        var cell = await runner.RunAsync(Request(ResultComparisonMode.Ordered) with { Repeat = 5 }, CancellationToken.None);

        var regression = Assert.IsType<RegressionReport>(cell.Report.Regression);
        Assert.Equal(RegressionPolicy.Pass, regression.Verdict);
        Assert.Empty(regression.Reasons);
        Assert.Equal(RegressionPolicy.StatusNotRegressed, regression.Elapsed.Status);
        Assert.Equal(20m, regression.Elapsed.BaselineMedian);
        Assert.Equal(20m, regression.Elapsed.CandidateMedian);
    }

    [Fact]
    public async Task CompareCellRunner_unrecognized_statistics_make_elapsed_unavailable_and_inconclusive()
    {
        var session = SqlHarnessCompareTests.FakeCompareSession.Create();
        session.CandidateStatisticsMessages = ["unrecognized statistics text"];
        var runner = new CompareCellRunner(session, new MemoryWriter());

        var cell = await runner.RunAsync(Request(ResultComparisonMode.Ordered) with { Repeat = 5 }, CancellationToken.None);

        var regression = Assert.IsType<RegressionReport>(cell.Report.Regression);
        Assert.Equal(RegressionPolicy.Inconclusive, regression.Verdict);
        Assert.Contains(RegressionPolicy.ReasonElapsedUnavailable, regression.Reasons);
        Assert.Equal(RegressionPolicy.StatusUnavailable, regression.Elapsed.Status);
        Assert.Equal(RegressionPolicy.StatusUnavailable, regression.LogicalReads.Status);
        Assert.Equal(RegressionPolicy.StatusUnavailable, regression.Cpu.Status);
    }

    [Fact]
    public async Task CompareCellRunner_off_mode_reports_results_not_compared()
    {
        var session = SqlHarnessCompareTests.FakeCompareSession.Create();
        var runner = new CompareCellRunner(session, new MemoryWriter());

        var cell = await runner.RunAsync(Request(ResultComparisonMode.Off), CancellationToken.None);

        Assert.Contains(RegressionPolicy.ReasonResultsNotCompared, cell.Report.Regression!.Reasons);
    }

    [Fact]
    public void CompareReport_serializes_regression_as_camelCase_and_omits_it_when_null()
    {
        using var present = JsonDocument.Parse(JsonSerializer.Serialize(Report(Fail()), Web));
        var regression = present.RootElement.GetProperty("regression");
        Assert.Equal(
            ["verdict", "reasons", "policyVersion", "elapsed", "logicalReads", "cpu"],
            regression.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["status", "baselineMedian", "candidateMedian", "delta", "deltaPercent"],
            regression.GetProperty("elapsed").EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("fail", regression.GetProperty("verdict").GetString());

        using var absent = JsonDocument.Parse(JsonSerializer.Serialize(Report(null), Web));
        Assert.False(absent.RootElement.TryGetProperty("regression", out _));
    }

    [Fact]
    public void BenchmarkSummary_projection_carries_the_regression_and_omits_it_when_absent()
    {
        AssertSameVerdict(Fail(), BenchmarkSummaryProjector.Project(Report(Fail())).Regression);
        Assert.Null(BenchmarkSummaryProjector.Project(Report(null)).Regression);

        using var present = JsonDocument.Parse(JsonSerializer.Serialize(BenchmarkSummaryProjector.Project(Report(Pass())), Web));
        Assert.Equal("pass", present.RootElement.GetProperty("regression").GetProperty("verdict").GetString());
        using var absent = JsonDocument.Parse(JsonSerializer.Serialize(BenchmarkSummaryProjector.Project(Report(null)), Web));
        Assert.False(absent.RootElement.TryGetProperty("regression", out _));
    }

    [Fact]
    public void BenchmarkSummary_agent_projection_keeps_the_regression()
    {
        var projected = AgentOutputProjection.Project(Report(Fail()), 512, 32, out _);

        AssertSameVerdict(Fail(), Assert.IsType<CompareBenchmarkSummary>(projected).Regression);
    }

    [Fact]
    public void BenchmarkSummary_matrix_cells_carry_their_own_verdicts()
    {
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int",
        [
            new CompareMatrixCellReport(0, "1", Report(Pass())),
            new CompareMatrixCellReport(1, "20", Report(Fail())),
        ]);

        var summary = BenchmarkSummaryProjector.Project(matrix);
        Assert.Equal(
            ["pass", "fail"],
            summary.Cells.Select(cell => cell.Compare.Regression!.Verdict).ToArray());

        var agent = Assert.IsType<CompareMatrixBenchmarkSummary>(AgentOutputProjection.Project(matrix, 512, 32, out _));
        Assert.Equal(
            ["pass", "fail"],
            agent.Cells.Select(cell => cell.Compare.Regression!.Verdict).ToArray());
    }

    [Fact]
    public void ArtifactReader_summary_copies_the_regression_from_report_json()
    {
        using var temp = new TempDirectory();
        var id = WriteCompareArtifact(temp.Path, Report(Fail()));

        var summary = Assert.IsType<CompareBenchmarkSummary>(ArtifactReader.ReadSection(temp.Path, id, "summary"));

        AssertSameVerdict(Fail(), summary.Regression);
    }

    [Fact]
    public void ArtifactReader_legacy_report_without_regression_yields_no_verdict()
    {
        using var temp = new TempDirectory();
        var id = WriteCompareArtifact(temp.Path, Report(null));
        var reportPath = Path.Combine(temp.Path, id, "report.json");
        Assert.DoesNotContain("regression", File.ReadAllText(reportPath), StringComparison.Ordinal);

        var summary = Assert.IsType<CompareBenchmarkSummary>(ArtifactReader.ReadSection(temp.Path, id, "summary"));

        Assert.Null(summary.Regression);
        Assert.DoesNotContain("regression", JsonSerializer.Serialize(summary, Web), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fail", "Regression: fail (elapsed +900 ms, +900 %)")]
    [InlineData("pass", "Regression: pass (elapsed -1.5 ms, -1.5 %)")]
    [InlineData("inconclusive", "Regression: inconclusive (too-few-runs, results-not-compared)")]
    public void CompareText_renders_exactly_one_regression_line(string verdict, string expected)
    {
        var regression = verdict switch { "fail" => Fail(), "pass" => Pass(), _ => Inconclusive() };

        var lines = RenderText(Report(regression));

        Assert.Equal(expected, Assert.Single(lines, line => line.StartsWith("Regression:", StringComparison.Ordinal)));
    }

    [Fact]
    public void CompareText_omits_the_regression_line_when_the_report_has_no_verdict()
    {
        Assert.DoesNotContain(RenderText(Report(null)), line => line.StartsWith("Regression:", StringComparison.Ordinal));
    }

    [Fact]
    public void CompareText_without_a_percent_prints_only_the_millisecond_delta()
    {
        var regression = Fail() with
        {
            Elapsed = new RegressionDiagnostic(RegressionPolicy.StatusRegressed, 0m, 12m, 12m, null),
        };

        Assert.Contains("Regression: fail (elapsed +12 ms)", RenderText(Report(regression)));
    }

    // Records hold IReadOnlyList members, so compare the wire shape instead of references.
    private static void AssertSameVerdict(RegressionReport expected, RegressionReport? actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected, Web), JsonSerializer.Serialize(actual, Web));

    private static string[] RenderText(SqlHarnessCompareReport report)
    {
        var output = new StringWriter();
        new Renderer().Render(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), OutputMode.Text, new OutputCaptureWriter(output), "compare");
        return output.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string WriteCompareArtifact(string root, SqlHarnessCompareReport report)
    {
        var runs = new[]
        {
            new CompareRunArtifact("baseline", 1, 1, 2, 3, new Dictionary<string, long> { ["Clients"] = 3 }, "HASH", [], 0),
            new CompareRunArtifact("candidate", 1, 4, 5, 6, new Dictionary<string, long> { ["Clients"] = 6 }, "HASH", [], 0),
        };
        var directory = new CompareArtifactWriter(root, () => DateTimeOffset.UnixEpoch).Write(report, runs, "wind");
        return Path.GetFileName(directory);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-verdict-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class MemoryWriter : ICompareArtifactWriter
    {
        public void CheckStorage() { /* In-memory test writer. */ }

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target) => "cell-artifacts";
    }
}