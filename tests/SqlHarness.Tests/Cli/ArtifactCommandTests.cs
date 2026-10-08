using System.Text;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class ArtifactCommandTests
{
    [Fact]
    public async Task Requires_id_and_section()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output).RunAsync(["artifact", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("requires an artifact id", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_unknown_section()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output)
            .RunAsync(["artifact", "some-id", "--section", "plans", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("summary, metrics, operators", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_text_mode()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output)
            .RunAsync(["artifact", "some-id", "--section", "summary"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("requires --json or --output agent", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_json_and_output_together()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output)
            .RunAsync(["artifact", "some-id", "--section", "summary", "--json", "--output", "agent"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
    }

    [Fact]
    public async Task Unknown_id_reports_safety_without_paths()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output)
            .RunAsync(["artifact", "does-not-exist", "--section", "summary", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("safety_rejected", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Unknown artifact id.", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(".sqlharness", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Traversal_id_is_refused()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output)
            .RunAsync(["artifact", "..", "--section", "summary", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("Unknown artifact id.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoundTrip_summary_matches_saved_report_without_dispatch()
    {
        using var home = new TempHome();
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 10, true, Variant("baseline"), Variant("candidate"), null);
        var run = new CompareRunArtifact("baseline", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);
        var directory = new CompareArtifactWriter(SqlHarnessPaths.CompareDir, () => DateTimeOffset.UnixEpoch)
            .Write(report, [run], "wind");
        var id = Path.GetFileName(directory);
        var module = new ThrowingModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(module, output)
            .RunAsync(["artifact", id, "--section", "summary", "--json"]);

        Assert.Equal(0, exit);
        var expected = JsonSerializer.Serialize(
            BenchmarkSummaryProjector.Project(report with { ArtifactDirectory = directory }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Assert.Equal(expected.Trim(), output.ToString().Trim());
    }

    [Fact]
    public async Task RoundTrip_metrics_and_operators_omit_runs_and_plans()
    {
        using var home = new TempHome();
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 10, true, Variant("baseline"), Variant("candidate"), null);
        var run = new CompareRunArtifact("baseline", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);
        var id = Path.GetFileName(new CompareArtifactWriter(SqlHarnessPaths.CompareDir, () => DateTimeOffset.UnixEpoch)
            .Write(report, [run], "wind"));

        var metricsOutput = new StringWriter();
        var metricsExit = await SqlHarnessCli.Create(new ThrowingModule(), metricsOutput)
            .RunAsync(["artifact", id, "--section", "metrics", "--json"]);
        var operatorsOutput = new StringWriter();
        var operatorsExit = await SqlHarnessCli.Create(new ThrowingModule(), operatorsOutput)
            .RunAsync(["artifact", id, "--section", "operators", "--json"]);

        Assert.Equal(0, metricsExit);
        Assert.Equal(0, operatorsExit);
        using var metrics = JsonDocument.Parse(metricsOutput.ToString());
        Assert.Equal(2, metrics.RootElement.GetProperty("variants").GetArrayLength());
        Assert.Equal(
            3,
            metrics.RootElement.GetProperty("variants")[0].GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetInt32());
        Assert.Contains("Index Seek", operatorsOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("HASH", metricsOutput.ToString() + operatorsOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("planFiles", metricsOutput.ToString() + operatorsOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_envelope_respects_budget()
    {
        using var home = new TempHome();
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile"),
            5, 10, true, Variant("baseline"), Variant("candidate"), null);
        var run = new CompareRunArtifact("baseline", 1, 1, 2, 3,
            new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);
        var id = Path.GetFileName(new CompareArtifactWriter(SqlHarnessPaths.CompareDir, () => DateTimeOffset.UnixEpoch)
            .Write(report, [run], "wind"));
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output).RunAsync(
            ["artifact", id, "--section", "operators", "--output", "agent", "--max-output-bytes", "4096", "--max-cell-chars", "64"]);

        Assert.Equal(0, exit);
        Assert.InRange(Encoding.UTF8.GetByteCount(output.ToString()), 1, 4096);
        using var envelope = JsonDocument.Parse(output.ToString());
        Assert.Equal("success", envelope.RootElement.GetProperty("status").GetString());
        Assert.Equal("artifact", envelope.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Capabilities_lists_artifact_command_and_read_limits()
    {
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(new ThrowingModule(), output).RunAsync(["capabilities", "--json"]);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Contains(
            document.RootElement.GetProperty("commands").EnumerateArray(),
            command => command.GetProperty("name").GetString() == "artifact");
        var limits = document.RootElement.GetProperty("limits");
        Assert.Contains("sessionTempStatements", limits.EnumerateObject().Select(property => property.Name));
        var read = limits.GetProperty("artifactRead");
        Assert.Equal(
            ["summary", "metrics", "operators", "statements"],
            read.GetProperty("sections").EnumerateArray().Select(section => section.GetString()!).ToArray());
        Assert.Equal(1, read.GetProperty("manifestVersion").GetInt32());
    }

    private static CompareVariantReport Variant(string name) => new(
        name,
        new CompareDistribution(1, 2, 3),
        new CompareDistribution(2, 3, 4),
        new CompareDistribution(3, 4, 5),
        new Dictionary<string, long> { ["Clients"] = 4 },
        [new CompareOperatorReport(1, "Index Seek", "Clients", true, false, false)],
        ["plan-warning"]);

    private static string FixturePlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"));

    /// <summary>Proves the command never dispatches: any module use throws.</summary>
    private sealed class ThrowingModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            throw new InvalidOperationException("artifact must not dispatch to the module.");
    }

    private sealed class TempHome : IDisposable
    {
        private readonly string? _original = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-artifact-cli-" + Guid.NewGuid().ToString("N"));
        public TempHome()
        {
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", Path);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _original);
            Directory.Delete(Path, true);
        }
    }
}