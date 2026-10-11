using System.Diagnostics;
using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.TestFixtures;

using Xunit.Abstractions;

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
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["ping", "dev", "--output", "agent"]);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ping", json.RootElement.GetProperty("command").GetString());
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
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["ping", "dev", "--output", "agent", "--unknown", "secret-value"]);

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
        start.ArgumentList.Add("ping");
        start.ArgumentList.Add("dev");
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
        var exit = await SqlHarnessCli.Create(new FakeModule(error), output).RunAsync(["ping", "dev", "--json"]);

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
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["ping", "dev", "--output", "agent"]);

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
    public void Agent_projection_bounds_statement_entries_and_operator_details()
    {
        var operation = new StatementOperatorSummary(1, new string('p', 40), new string('o', 40), new string('i', 40), 2, 4, 1);
        var section = new ArtifactStatementsSection("id", "compare",
        [
            new ArtifactStatementMetric("baseline", null, null, 0, new string('a', 64), 10, 15, 2, [operation, operation]),
            new ArtifactStatementMetric("baseline", null, null, 1, new string('b', 64), 20, 25, 1, [operation]),
        ], 0);

        var projected = Assert.IsType<ArtifactStatementsSection>(
            AgentOutputProjection.Project(section, 16, 1, out var omittedItems));

        Assert.Single(projected.Statements);
        Assert.Single(projected.Statements[0].TopOperators);
        Assert.Equal(1, projected.OmittedStatements);
        Assert.True(omittedItems >= 2);
        Assert.Equal(16, projected.Statements[0].TopOperators[0].PhysicalOp.Length);
        Assert.Equal(16, projected.Statements[0].TopOperators[0].Object!.Length);
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
    public void Agent_projection_summarizes_huge_binary_cells_before_json_serialization()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var bytes = new byte[8 * 1024 * 1024];
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "payload", "bytea", true)], [[bytes]], 1, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "binary-hash", new OutputFootprint(bytes.Length, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 64));

        var emittedBytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"8 MiB binary cell projection: {emittedBytes} UTF-8 bytes including newline (budget 4096)");
        Assert.InRange(emittedBytes, 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("binary-hash", result.GetProperty("resultHash").GetString());
        var projectedCell = result.GetProperty("resultSets")[0].GetProperty("rows")[0][0];
        Assert.Equal(bytes.LongLength, projectedCell.GetProperty("byteLength").GetInt64());
        Assert.True(projectedCell.GetProperty("truncated").GetBoolean());
        Assert.True(projectedCell.GetProperty("base64Prefix").GetString()!.Length <= 64);
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Agent_projection_bounds_matrix_cells_and_long_warnings_without_emitting_artifact_paths()
    {
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int",
            Enumerable.Range(0, 100).Select(i => new CompareMatrixCellReport(i, i.ToString(), BuildCompare(new string('w', 100_000), $"/workspace/artifacts/cell-{i:D3}"))).ToArray());
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), "compare",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 128));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"100 cell matrix with long warnings: {bytes} UTF-8 bytes including newline (budget 4096)");
        Assert.InRange(bytes, 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.DoesNotContain("/workspace/artifacts", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(result.GetProperty("cells").GetArrayLength() < 100);
        Assert.Equal((int)ResultComparisonMode.Multiset, result.GetProperty("cells")[0].GetProperty("compare").GetProperty("equivalence").GetProperty("mode").GetInt32());
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public async Task Agent_output_options_are_accepted_and_constrain_the_whole_envelope()
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new { value = "😀" }, null));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["ping", "dev", "--output", "agent", "--max-output-bytes", "4096", "--max-cell-chars", "0"]);

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

    [Fact]
    public void Agent_projection_three_by_three_query_returns_all_cells_with_null_truncation()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var columns = new[]
        {
            new SqlHarnessColumnReport(0, "a", "int", true),
            new SqlHarnessColumnReport(1, "b", "int", true),
            new SqlHarnessColumnReport(2, "c", "int", true),
        };
        var rows = Enumerable.Range(1, 3)
            .Select(i => new object?[] { i, i * 10, i * 100 })
            .ToArray();
        var set = new SqlHarnessResultSetReport(columns, rows, rows.Length, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(2151, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(16384, 512));

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("truncation").ValueKind);
        var resultSets = json.RootElement.GetProperty("result").GetProperty("resultSets");
        Assert.Equal(1, resultSets.GetArrayLength());
        var projectedSet = resultSets[0];
        Assert.Equal(3, projectedSet.GetProperty("columns").GetArrayLength());
        Assert.Equal(3, projectedSet.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, projectedSet.GetProperty("omittedRowCount").GetInt64());
    }

    [Fact]
    public void Agent_projection_twenty_kb_query_truncates_and_reports_omitted_items()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var columns = new[]
        {
            new SqlHarnessColumnReport(0, "a", "int", true),
            new SqlHarnessColumnReport(1, "b", "int", true),
            new SqlHarnessColumnReport(2, "c", "int", true),
        };
        var rows = Enumerable.Range(1, 1000)
            .Select(i => new object?[] { i, i * 10, i * 100 })
            .ToArray();
        var set = new SqlHarnessResultSetReport(columns, rows, rows.Length, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(50000, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(16384, 512));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"1000 row projection: {bytes} UTF-8 bytes including newline (budget 16384)");
        Assert.InRange(bytes, 1, 16384);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("truncation").ValueKind);
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Agent_projection_three_small_result_sets_return_all_with_null_truncation()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var sets = Enumerable.Range(1, 3)
            .Select(i =>
            {
                var columns = new[] { new SqlHarnessColumnReport(0, $"col{i}", "int", true) };
                var rows = Enumerable.Range(1, 3)
                    .Select(j => new object?[] { j + (i - 1) * 10 })
                    .ToArray();
                return new SqlHarnessResultSetReport(columns, rows, rows.Length, 0);
            })
            .ToArray();
        var report = new SqlHarnessQueryReport(target, "read-only", sets, [], 0, 1, "raw-hash", new OutputFootprint(1754, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(16384, 512));

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("truncation").ValueKind);
        var resultSets = json.RootElement.GetProperty("result").GetProperty("resultSets");
        Assert.Equal(3, resultSets.GetArrayLength());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, resultSets[i].GetProperty("columns").GetArrayLength());
            Assert.Equal(3, resultSets[i].GetProperty("rows").GetArrayLength());
            Assert.Equal(0, resultSets[i].GetProperty("omittedRowCount").GetInt64());
        }
    }

    [Fact]
    public void Agent_projection_small_matrix_returns_all_cells_and_references()
    {
        var cells = Enumerable.Range(1, 3)
            .Select(i => new CompareMatrixCellReport(i - 1, i.ToString(), BuildCompare($"warning{i}", $"/artifacts/cell{i}")))
            .ToArray();
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells);
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), "compare",
            new OutputCaptureWriter(output), new AgentOutputOptions(16384, 512));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"3 cell matrix projection: {bytes} UTF-8 bytes including newline (budget 16384)");
        Assert.InRange(bytes, 1, 16384);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("truncation").ValueKind);
        var resultCells = json.RootElement.GetProperty("result").GetProperty("cells");
        Assert.Equal(3, resultCells.GetArrayLength());
        Assert.All(resultCells.EnumerateArray(), cell =>
            Assert.False(string.IsNullOrEmpty(cell.GetProperty("compare").GetProperty("artifactDirectory").GetString())));
    }

    [Fact]
    public void Agent_projection_oversized_matrix_emits_retrievable_cell_references()
    {
        var cells = Enumerable.Range(0, 20)
            .Select(i => new CompareMatrixCellReport(i, i.ToString(), BuildCompare(new string('w', 5000), $"/artifacts/matrix-cell-{i:D2}")))
            .ToArray();
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells);
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), "compare",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 128));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"20 cell oversized matrix projection: {bytes} UTF-8 bytes including newline (budget 4096)");
        Assert.InRange(bytes, 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        var projectedCells = result.GetProperty("cells");
        var references = result.GetProperty("omittedCellReferences");
        Assert.True(projectedCells.GetArrayLength() < 20 || references.GetArrayLength() > 0,
            "An oversized matrix should either project fewer than 20 full cells or emit references for omitted cells.");
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
        if (references.GetArrayLength() > 0)
        {
            Assert.All(references.EnumerateArray(), reference =>
                Assert.False(string.IsNullOrEmpty(reference.GetProperty("artifactDirectory").GetString())));
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void Agent_projection_all_envelopes_fit_the_configured_byte_budget(int budget)
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var text = string.Concat(Enumerable.Repeat("x", budget));
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(budget, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(budget, 512));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"Single huge cell projection: {bytes} UTF-8 bytes including newline (budget {budget})");
        Assert.InRange(bytes, 1, budget);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public void Query_candidate_ladder_samples_rows_before_dropping_shape()
    {
        var query = QueryShapeFixtures.Report(QueryShapeFixtures.Set(3, 3));
        var counts = new SqlHarnessCountsReport(new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "p"), [], 0);

        Assert.Equal(
            [int.MaxValue, 128, 64, 32, 16, 8, 4, 2, 1, 0],
            AgentOutputProjection.GetCandidateDetailLimits(query, 16384, 512).ToArray());
        var expected = new List<int> { int.MaxValue };
        for (var level = AgentOutputProjection.CalculateDetailLimit(16384, 512); level > 0; level = level == 1 ? 0 : Math.Max(1, level / 2))
            expected.Add(level);
        expected.Add(0);
        Assert.Equal(expected, AgentOutputProjection.GetCandidateDetailLimits(counts, 16384, 512).ToArray());
        Assert.Equal(expected, AgentOutputProjection.GetCandidateDetailLimits(null, 16384, 512).ToArray());
    }

    [Fact]
    public void Query_small_three_result_sets_render_complete_with_ordinals()
    {
        var report = QueryShapeFixtures.Report(QueryShapeFixtures.Set(2, 3), QueryShapeFixtures.Set(1, 1), QueryShapeFixtures.Set(3, 2));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(16384, 512));

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, 16384);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("truncation").ValueKind);
        var result = json.RootElement.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(result, report);
        Assert.Equal(0, result.GetProperty("omittedResultSetCount").GetInt32());
        var sets = result.GetProperty("resultSets");
        Assert.Equal(3, sets.GetArrayLength());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0, sets[i].GetProperty("omittedColumnCount").GetInt32());
            Assert.Equal(report.ResultSets[i].Rows.Count, sets[i].GetProperty("rows").GetArrayLength());
        }
    }

    [Fact]
    public void Query_large_detail_set_keeps_summary_sets_and_aligned_detail_sample()
    {
        var report = QueryShapeFixtures.Report(QueryShapeFixtures.Set(4, 1000, width: 40), QueryShapeFixtures.Set(2, 3), QueryShapeFixtures.Set(3, 2));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(16384, 512));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"Detail + summaries projection: {bytes} UTF-8 bytes including newline (budget 16384)");
        Assert.InRange(bytes, 1, 16384);
        using var json = JsonDocument.Parse(output.ToString());
        var truncation = json.RootElement.GetProperty("truncation");
        Assert.Equal(JsonValueKind.Object, truncation.ValueKind);
        Assert.True(truncation.GetProperty("detailLimit").GetInt32() > 1);
        var result = json.RootElement.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(result, report);
        var sets = result.GetProperty("resultSets");
        Assert.Equal(3, sets.GetArrayLength());
        var detailRows = sets[0].GetProperty("rows").GetArrayLength();
        Assert.InRange(detailRows, 2, 999);
        Assert.Equal(1000 - detailRows, sets[0].GetProperty("omittedRowCount").GetInt64());
        Assert.Equal(4, sets[0].GetProperty("columns").GetArrayLength());
        Assert.Equal(3, sets[1].GetProperty("rows").GetArrayLength());
        Assert.Equal(2, sets[2].GetProperty("rows").GetArrayLength());
        Assert.Equal(1000 - detailRows, truncation.GetProperty("omittedItems").GetInt32());
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void Query_wide_schema_with_oversized_cells_falls_back_explicitly(int budget)
    {
        var report = QueryShapeFixtures.Report(
            QueryShapeFixtures.Set(400, 3, width: 5000, nameLength: 60),
            QueryShapeFixtures.Set(2, 2));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(budget, 512));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"Wide schema projection: {bytes} UTF-8 bytes including newline (budget {budget})");
        Assert.InRange(bytes, 1, budget);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("success", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("truncation").GetProperty("detailLimit").GetInt32());
        var result = json.RootElement.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(result, report);
        var sets = result.GetProperty("resultSets");
        Assert.True(sets.GetArrayLength() >= 1);
        Assert.True(sets[0].GetProperty("omittedColumnCount").GetInt32() > 0);
        Assert.Equal(0, sets[0].GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Query_result_set_inventory_overflow_reports_omitted_result_sets()
    {
        var report = QueryShapeFixtures.Report(Enumerable.Range(0, 400).Select(_ => QueryShapeFixtures.Set(3, 50, width: 20)).ToArray());
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 512));

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(result, report);
        Assert.True(result.GetProperty("omittedResultSetCount").GetInt32() > 0);
        Assert.True(result.GetProperty("resultSets").GetArrayLength() > 0);
    }

    [Fact]
    public void Query_projection_does_not_change_the_source_report()
    {
        var report = QueryShapeFixtures.Report(QueryShapeFixtures.Set(400, 3, width: 5000, nameLength: 60), QueryShapeFixtures.Set(4, 1000, width: 40));
        var before = JsonSerializer.Serialize(report);

        foreach (var limit in AgentOutputProjection.GetCandidateDetailLimits(report, 4096, 64))
        {
            var projected = AgentOutputProjection.Project(report, 64, limit, out _, 4096);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(report.ResultHash, json.RootElement.GetProperty("resultHash").GetString());
            Assert.Equal(report.Target.ActualDatabase, json.RootElement.GetProperty("target").GetProperty("actualDatabase").GetString());
            Assert.Equal(report.RawFootprint.Bytes, json.RootElement.GetProperty("rawFootprint").GetProperty("bytes").GetInt64());
            QueryShapeFixtures.AssertAccounted(json.RootElement, report);
        }

        Assert.Equal(before, JsonSerializer.Serialize(report));
    }

    private static JsonElement ProjectQueryJson(SqlHarnessQueryReport report, int cellChars, int limit, int budget = 16384)
    {
        var projected = AgentOutputProjection.Project(report, cellChars, limit, out _, budget);
        return JsonDocument.Parse(JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
    }

    [Fact]
    public void Query_sampled_rows_keep_binary_null_and_non_string_cells_aligned()
    {
        var columns = new[]
        {
            new SqlHarnessColumnReport(0, "blob", "varbinary", true),
            new SqlHarnessColumnReport(1, "nothing", "nvarchar", true),
            new SqlHarnessColumnReport(2, "n", "int", false),
            new SqlHarnessColumnReport(3, "text", "nvarchar", true),
        };
        var big = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var rows = Enumerable.Range(0, 5)
            .Select(i => (IReadOnlyList<object?>)new object?[] { i == 0 ? big : new byte[] { 1, 2 }, null, i, new string('x', 40) })
            .ToArray();
        var report = QueryShapeFixtures.Report(new SqlHarnessResultSetReport(columns, rows, 5, 0));

        var result = ProjectQueryJson(report, 16, 2);

        var set = result.GetProperty("resultSets")[0];
        Assert.Equal(4, set.GetProperty("columns").GetArrayLength());
        var emitted = set.GetProperty("rows");
        Assert.Equal(2, emitted.GetArrayLength());
        Assert.Equal(3, set.GetProperty("omittedRowCount").GetInt64());
        for (var r = 0; r < 2; r++)
            Assert.Equal(4, emitted[r].GetArrayLength());
        var clippedBlob = emitted[0][0];
        Assert.Equal(JsonValueKind.Object, clippedBlob.ValueKind);
        Assert.Equal(64, clippedBlob.GetProperty("byteLength").GetInt64());
        Assert.True(clippedBlob.GetProperty("truncated").GetBoolean());
        Assert.True(clippedBlob.GetProperty("base64Prefix").GetString()!.Length <= 16);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2 }), emitted[1][0].GetString());
        Assert.Equal(JsonValueKind.Null, emitted[0][1].ValueKind);
        Assert.Equal(1, emitted[1][2].GetInt32());
        Assert.Equal(16, emitted[0][3].GetString()!.Length);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(2)]
    [InlineData(0)]
    public void Query_zero_column_and_zero_row_sets_keep_ordinals_and_counts(int limit)
    {
        var report = QueryShapeFixtures.Report(
            QueryShapeFixtures.Set(3, 2),
            QueryShapeFixtures.Set(0, 0),
            QueryShapeFixtures.Set(2, 0),
            QueryShapeFixtures.Set(0, 2),
            QueryShapeFixtures.Set(1, 1));

        var result = ProjectQueryJson(report, 64, limit);

        QueryShapeFixtures.AssertAccounted(result, report);
        Assert.Equal(0, result.GetProperty("omittedResultSetCount").GetInt32());
        var sets = result.GetProperty("resultSets");
        Assert.Equal(5, sets.GetArrayLength());
        Assert.Equal(0, sets[1].GetProperty("columns").GetArrayLength());
        Assert.Equal(0, sets[1].GetProperty("omittedColumnCount").GetInt32());
        Assert.Equal(2, sets[2].GetProperty("columns").GetArrayLength());
        Assert.Equal(0, sets[2].GetProperty("rows").GetArrayLength());
        Assert.Equal(0, sets[2].GetProperty("omittedRowCount").GetInt64());
        Assert.Equal(2, sets[3].GetProperty("rowCount").GetInt64());
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void Query_wide_first_set_then_summary_set_at_level_zero_never_implies_completeness(int budget)
    {
        var report = QueryShapeFixtures.Report(
            QueryShapeFixtures.Set(400, 3, width: 5000, nameLength: 60),
            QueryShapeFixtures.Set(2, 2));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(budget, 512));

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, budget);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, json.RootElement.GetProperty("truncation").GetProperty("detailLimit").GetInt32());
        var result = json.RootElement.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(result, report);
        Assert.Equal(0, result.GetProperty("omittedResultSetCount").GetInt32());
        var sets = result.GetProperty("resultSets");
        Assert.Equal(2, sets.GetArrayLength());
        // Greedy in order: the wide first schema takes the column budget and is
        // explicitly cut. The summary set keeps its inventory, gets only what is
        // left for columns, and counts every column it could not emit. Rows are
        // never emitted at level 0, and their count is carried in omittedRowCount.
        Assert.True(sets[0].GetProperty("omittedColumnCount").GetInt32() > 0);
        Assert.Equal(0, sets[0].GetProperty("rows").GetArrayLength());
        Assert.Equal(2, sets[1].GetProperty("columns").GetArrayLength() + sets[1].GetProperty("omittedColumnCount").GetInt32());
        Assert.Equal(2, sets[1].GetProperty("rowCount").GetInt64());
        Assert.Equal(2, sets[1].GetProperty("omittedRowCount").GetInt64());
        Assert.Equal(0, sets[1].GetProperty("rows").GetArrayLength());
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