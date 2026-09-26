using System.Text.Json;
using System.Diagnostics;
using Xunit.Abstractions;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class AgentOutputTests
{
    private readonly ITestOutputHelper _testOutput;

    public AgentOutputTests(ITestOutputHelper testOutput) => _testOutput = testOutput;
    [Fact]
    public async Task Agent_success_uses_a_versioned_single_document_envelope()
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new { value = 7 }, null));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["gain", "--output", "agent"]);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("gain", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("success", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("result").GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("error").ValueKind);
        Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Agent_rejects_unknown_options_as_safe_json_before_dispatch()
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["gain", "--output", "agent", "--unknown", "secret-value"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("error", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("safety_rejected", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret-value", output.ToString(), StringComparison.Ordinal);
        Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Agent_parse_error_writes_only_one_json_document_to_stdout()
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("gain");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add("agent");
        start.ArgumentList.Add("--unknown");
        start.ArgumentList.Add("secret-value");
        using var process = Process.Start(start);

        Assert.NotNull(process);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal((int)SqlHarnessExitCode.Safety, process.ExitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain("secret-value", stdout, StringComparison.Ordinal);
        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("safety_rejected", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Machine_json_error_is_structured_and_has_stable_code()
    {
        var output = new StringWriter();
        var error = new SqlHarnessOutcome(SqlHarnessExitCode.Authentication, null, "Credentials were rejected.");
        var exit = await SqlHarnessCli.Create(new FakeModule(error), output).RunAsync(["gain", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Authentication, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("authentication_failed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Credentials were rejected.", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Missing_input_file_is_reported_as_a_structured_input_error()
    {
        var output = new StringWriter();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.sql");
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["query", "dev", "--file", missing, "--output", "agent"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("input_file_unavailable", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("input", json.RootElement.GetProperty("error").GetProperty("phase").GetString());
    }

    [Theory]
    [InlineData(SqlHarnessExitCode.Safety, "safety_rejected")]
    [InlineData(SqlHarnessExitCode.Authentication, "authentication_failed")]
    [InlineData(SqlHarnessExitCode.TargetMismatch, "target_mismatch")]
    [InlineData(SqlHarnessExitCode.SqlExecution, "sql_execution_failed")]
    [InlineData(SqlHarnessExitCode.LocalStorage, "local_storage_failed")]
    public async Task Agent_errors_keep_numeric_exit_and_stable_cause_code(SqlHarnessExitCode exitCode, string expectedCode)
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(exitCode, null, "Safe failure message."));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["gain", "--output", "agent"]);

        Assert.Equal((int)exitCode, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Agent_renderer_keeps_matrix_report_and_error_together()
    {
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", []);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, matrix, "Cell 2 failed.");
        var output = new StringWriter();
        new Renderer().Render(outcome, OutputMode.Agent, new OutputCaptureWriter(output), "compare");

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("partial", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Cell 2 failed.", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("batch", json.RootElement.GetProperty("result").GetProperty("parameterName").GetString());
    }

    [Fact]
    public void Agent_projection_bounds_large_table_reports_and_counts_omissions_with_newline()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var report = new SqlHarnessCountsReport(target,
            Enumerable.Range(0, 1000).Select(i => new SqlHarnessCountReport("dbo", $"表{i}", i, "estimate")).ToArray(), 0);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
        var output = new StringWriter();

        new Renderer().RenderAgent(outcome, "counts", new OutputCaptureWriter(output), new AgentOutputOptions(4096, 32));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"1000 table projection: {bytes} UTF-8 bytes including newline (budget 4096)");
        Assert.InRange(bytes, 1, 4096);
        Assert.EndsWith("\n", output.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
        Assert.True(json.RootElement.GetProperty("result").GetProperty("tables").GetArrayLength() < 1000);
    }

    [Fact]
    public void Agent_projection_clips_huge_unicode_cells_and_keeps_raw_hash()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var text = string.Concat(Enumerable.Repeat("語😀", 10000));
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(100000, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 64));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"Unicode cell projection: {bytes} UTF-8 bytes including newline (budget 4096)");
        Assert.InRange(bytes, 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        var projected = json.RootElement.GetProperty("result");
        Assert.Equal("raw-hash", projected.GetProperty("resultHash").GetString());
        Assert.True(projected.GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetString()!.Length <= 64);
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Agent_projection_bounds_matrix_cells_long_warnings_and_artifact_paths()
    {
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int",
            Enumerable.Range(0, 100).Select(i => new CompareMatrixCellReport(i, i.ToString(), BuildCompare(new string('w', 100_000), new string('a', 20_000)))).ToArray());
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), "compare",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 128));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"100 cell matrix with long warning/path: {bytes} UTF-8 bytes including newline (budget 4096)");
        Assert.InRange(bytes, 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("cells").GetArrayLength() < 100);
        Assert.Equal((int)ResultComparisonMode.Multiset, result.GetProperty("cells")[0].GetProperty("compare").GetProperty("equivalence").GetProperty("mode").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public async Task Agent_output_options_are_accepted_and_constrain_the_whole_envelope()
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new { value = "😀" }, null));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["gain", "--output", "agent", "--max-output-bytes", "4096", "--max-cell-chars", "0"]);

        Assert.Equal(0, exit);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("success", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Agent_validation_errors_respect_configured_budget_and_cell_limit()
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        var largeKey = new string('a', 10000);
        var exit = await SqlHarnessCli.Create(module, output).RunAsync([
            "schema", "prod", "--var", $"{largeKey}=one", "--var", $"{largeKey}=two",
            "--output", "agent", "--max-output-bytes", "4096", "--max-cell-chars", "128"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("error").GetProperty("message").GetString()!.Length <= 128);
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Agent_plan_projection_bounds_operator_trees_before_serialization()
    {
        var root = new PlanNode("Nested Loops", "Join", null, null, 100, 10, 1, 0.5, null, [],
            Enumerable.Range(0, 10000).Select(i => new PlanNode($"Operator{i}", null, new string('x', 10000), null, 1, 1, 1, 0, null, [], [])).ToArray());
        var plan = new DistilledPlan([new PlanStatement(new string('q', 10000), root, [])]);
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, plan, null), "plan",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 64));

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("error").ValueKind);
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Output_footprint_counts_utf8_bytes_and_lines_incrementally()
    {
        var writer = new OutputCaptureWriter(new StringWriter());
        var mark = writer.Mark();
        writer.Write("語😀\nlast");

        var footprint = writer.GetAnsiFreeFootprint(mark);

        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("語😀\nlast"), footprint.Bytes);
        Assert.Equal(2, footprint.Lines);
    }

    [Fact]
    public void Output_footprint_counts_surrogate_pairs_split_across_writes()
    {
        var writer = new OutputCaptureWriter(new StringWriter());
        var mark = writer.Mark();
        writer.Write('\ud83d');
        writer.Write('\ude00');

        Assert.Equal(4, writer.GetAnsiFreeFootprint(mark).Bytes);
    }

    private sealed class FakeModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(outcome);
        }
    }

    private static SqlHarnessCompareReport BuildCompare(string warning, string artifactPath)
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var variant = new CompareVariantReport("variant", new CompareDistribution(1, 2, 3), new CompareDistribution(4, 5, 6),
            new CompareDistribution(7, 8, 9), new Dictionary<string, long>(), [], [warning]);
        return new SqlHarnessCompareReport(target, 1, 2, false, variant, variant, artifactPath)
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Multiset, true, null, 0, 0),
            Classification = new CompareClassificationReport("none", "read-only", "read-only"),
        };
    }
}
