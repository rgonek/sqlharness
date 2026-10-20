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

    // A query needs SQL text; the module is a fake, so the content never reaches a database.
    private static readonly string SqlFile = CreateSqlFile();

    private static string CreateSqlFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlharness-agent-output-{Guid.NewGuid():N}.sql");
        File.WriteAllText(path, "SELECT 1");
        return path;
    }

    [Fact]
    public void Cli_envelope_is_not_changed_for_mcp_parity()
    {
        var output = new StringWriter();
        new Renderer().RenderAgent(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0), null),
            "query",
            new OutputCaptureWriter(output),
            new AgentOutputOptions());

        using var json = JsonDocument.Parse(output.ToString());
        var envelope = json.RootElement;
        Assert.Equal(AgentEnvelopeSchema.Version, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("query", envelope.GetProperty("command").GetString());
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.Equal(0, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("truncation").ValueKind);
        Assert.Equal("read-only", envelope.GetProperty("result").GetProperty("classification").GetString());
        Assert.Equal("read-only", envelope.GetProperty("result").GetProperty("tier").GetString());

        var compareOutput = new StringWriter();
        new Renderer().RenderAgent(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, BuildCompare("marker", "/artifacts/cell") with
            {
                Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Multiset, true, null, 0, 0),
            }, null),
            "compare",
            new OutputCaptureWriter(compareOutput),
            new AgentOutputOptions());
        using var compareJson = JsonDocument.Parse(compareOutput.ToString());
        Assert.Equal(2, compareJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("compare", compareJson.RootElement.GetProperty("command").GetString());
        Assert.Equal(0, compareJson.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, compareJson.RootElement.GetProperty("result").GetProperty("equivalence").GetProperty("mode").GetInt32());
        Assert.Equal(JsonValueKind.Null, compareJson.RootElement.GetProperty("error").ValueKind);
        Assert.Equal("/artifacts/cell", compareJson.RootElement.GetProperty("result").GetProperty("artifactDirectory").GetString());
        Assert.False(compareJson.RootElement.GetProperty("result").TryGetProperty("artifactId", out _));
        Assert.Equal("server", compareJson.RootElement.GetProperty("result").GetProperty("target").GetProperty("actualServer").GetString());
        Assert.Equal(5, compareJson.RootElement.GetProperty("result").GetProperty("baseline").GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetInt64());
    }

    [Fact]
    public void Agent_output_default_budget_is_shared_by_cli_options_and_capabilities()
    {
        const int expectedBytes = 65536;

        Assert.Equal(expectedBytes, new AgentOutputOptions().MaximumBytes);
        Assert.Equal(expectedBytes, new QueryCommand.Settings().MaxOutputBytes);
        Assert.Equal(expectedBytes, new PlanCommand.Settings().MaxOutputBytes);
        Assert.Equal(expectedBytes, new ArtifactCommand.Settings().MaxOutputBytes);

        var output = new OutputContext(new StringWriter());
        output.Configure(["query", "--output", "agent"]);
        Assert.Equal(expectedBytes, output.AgentOptions.MaximumBytes);

        var advertised = Assert.IsType<int>(SqlHarnessCapabilitiesProvider.Get().Limits["agentOutputBytes"]
            .GetType().GetProperty("defaultValue")?.GetValue(SqlHarnessCapabilitiesProvider.Get().Limits["agentOutputBytes"]));
        Assert.Equal(expectedBytes, advertised);
    }

    [Fact]
    public void Default_agent_cell_limit_is_2048()
    {
        Assert.Equal(2048, new AgentOutputOptions().MaximumCellCharacters);
        Assert.Equal(AgentOutputLimits.DefaultCellCharacters, new AgentOutputOptions().MaximumCellCharacters);
    }

    [Fact]
    public void Query_cell_of_1500_characters_is_not_clipped_by_default()
    {
        var report = QueryShapeFixtures.Report(QueryShapeFixtures.Set(1, 1, width: 1500));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions());

        Assert.DoesNotContain("clippedCells", output.ToString());
    }

    [Fact]
    public async Task Agent_success_uses_a_versioned_single_document_envelope()
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new { value = 7 }, null));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["query", "dev", "--file", SqlFile, "--output", "agent"]);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(AgentEnvelopeSchema.Version, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("query", json.RootElement.GetProperty("command").GetString());
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
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["query", "dev", "--output", "agent", "--unknown", "secret-value"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("error", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("safety_rejected", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret-value", output.ToString(), StringComparison.Ordinal);
        Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("query", "-1")]
    [InlineData("query", "4097")]
    [InlineData("plan", "-1")]
    [InlineData("plan", "4097")]
    [InlineData("artifact", "-1")]
    [InlineData("artifact", "4097")]
    public async Task Invalid_cli_cell_limit_uses_the_mcp_diagnostic(string command, string cellLimit)
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        string[] args = command switch
        {
            "query" => ["query", "dev", "--file", SqlFile, "--output", "agent", "--max-cell-chars", cellLimit],
            "plan" => ["plan", Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"),
                "--output", "agent", "--max-cell-chars", cellLimit],
            "artifact" => ["artifact", "sample", "--section", "summary", "--output", "agent",
                "--max-cell-chars", cellLimit],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown CLI command."),
        };

        var exit = await SqlHarnessCli.Create(module, output).RunAsync(args);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Empty(module.Operations);
        using var envelope = JsonDocument.Parse(output.ToString());
        Assert.Equal("Cell limit must be 0..4096 characters.",
            envelope.RootElement.GetProperty("error").GetProperty("message").GetString());
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
        start.ArgumentList.Add("query");
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
        var exit = await SqlHarnessCli.Create(new FakeModule(error), output).RunAsync(["query", "dev", "--file", SqlFile, "--json"]);

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
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["query", "dev", "--file", SqlFile, "--output", "agent"]);

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
    public void Agent_plan_projection_reports_once_only_clipping_in_cli_envelope()
    {
        var nodes = Enumerable.Range(0, 32).Select(index => new ArtifactPlanNode(
            index == 0 ? "0" : "0/" + (index - 1), index == 0 ? null : "0",
            new PlanNode("Scan", null, null, null, 1, 1, 1, 0.1, null, [], []))).ToArray();
        var section = new ArtifactPlanSection("artifact", "baseline", null, 0, 1, 1,
            "last-measured", [1], null, nodes.Length, nodes, null)
        { StatementText = new string('s', 600) };
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, section, null), "artifact",
            new OutputCaptureWriter(output), new AgentOutputOptions(65536, 512));

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32());
        Assert.Equal(512, document.RootElement.GetProperty("result").GetProperty("statementText").GetString()!.Length);
    }

    [Fact]
    public void Agent_plan_output_keeps_overlap_within_the_existing_take_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlharness-agent-overlap-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string id = "agent-overlap";
            var directory = Path.Combine(root, id);
            Directory.CreateDirectory(Path.Combine(directory, "plans"));
            File.WriteAllText(Path.Combine(directory, "manifest.json"),
                "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
            const string planName = "baseline-001-000-000.plan.json";
            var hint = """{"table":"Orders","schema":"dbo","equalityColumns":["CustomerId"],"inequalityColumns":[],"includeColumns":["Total"],"impact":87.5}""";
            File.WriteAllText(Path.Combine(directory, "plans", planName),
                "{\"statements\":[{\"root\":{\"physicalOp\":\"Scan\"},\"missingIndexes\":[" + hint + "," + hint + "," + hint + "]}]}");
            File.WriteAllText(Path.Combine(directory, "runs.jsonl"),
                "{\"variant\":\"baseline\",\"repetition\":1,\"planJsonFiles\":[\"" + planName + "\"]}");
            File.WriteAllText(Path.Combine(directory, "index-inventory.json"), JsonSerializer.Serialize(new IndexInventory(
                "AdventureWorks",
                DateTimeOffset.UnixEpoch,
                [new InventoryTable("dbo", "Orders", true, [new InventoryIndex(2, "IX_C", "NONCLUSTERED", ["CustomerId"], ["Status"], false, false, false, false, false)], false)],
                []), ArtifactDirectoryPublisher.JsonOptions));

            var section = Assert.IsType<ArtifactPlanSection>(ArtifactReader.ReadSection(root, id, "plan",
                planSelector: new ArtifactPlanSelector("baseline")));
            var limited = Assert.IsType<ArtifactPlanSection>(AgentOutputProjection.Project(section, 512, 2, out var omitted, 65536));
            var limitedJson = JsonSerializer.Serialize(limited, ArtifactDirectoryPublisher.JsonOptions);
            using var limitedDocument = JsonDocument.Parse(limitedJson);
            var limitedHints = limitedDocument.RootElement.GetProperty("missingIndexes");

            Assert.Equal(3, section.MissingIndexes.Count);
            Assert.Equal(2, limitedHints.GetArrayLength());
            Assert.Equal(1, omitted);
            Assert.All(limitedHints.EnumerateArray(), item =>
            {
                Assert.Equal("include-gap", item.GetProperty("overlap").GetProperty("verdict").GetString());
                Assert.Equal("IX_C", item.GetProperty("overlap").GetProperty("index").GetProperty("name").GetString());
            });

            var output = new StringWriter();
            new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, section, null), "artifact",
                new OutputCaptureWriter(output), new AgentOutputOptions(65536, 512));
            using var envelope = JsonDocument.Parse(output.ToString());
            var rendered = envelope.RootElement.GetProperty("result").GetProperty("missingIndexes");
            Assert.Equal(3, rendered.GetArrayLength());
            Assert.All(rendered.EnumerateArray(), item =>
                Assert.Equal("include-gap", item.GetProperty("overlap").GetProperty("verdict").GetString()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Agent_projection_clips_unicode_cells_and_reports_safe_prefix_metadata()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var text = string.Concat(Enumerable.Repeat("語😀", 10000));
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(100000, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 65));

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output.ToString());
        _testOutput.WriteLine($"Unicode cell projection: {bytes} UTF-8 bytes including newline (budget 4096)");
        Assert.InRange(bytes, 1, 4096);
        using var json = JsonDocument.Parse(output.ToString());
        var projected = json.RootElement.GetProperty("result");
        Assert.False(projected.TryGetProperty("resultHash", out _));
        Assert.False(projected.TryGetProperty("rawFootprint", out _));
        Assert.False(projected.TryGetProperty("target", out _));
        Assert.True(projected.GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetString()!.Length <= 65);
        var clippedCell = Assert.Single(projected.GetProperty("resultSets")[0].GetProperty("clippedCells").EnumerateArray());
        Assert.Equal(0, clippedCell.GetProperty("row").GetInt32());
        Assert.Equal(0, clippedCell.GetProperty("column").GetInt32());
        Assert.Equal(text.Length, clippedCell.GetProperty("originalLength").GetInt64());
        Assert.Equal(projected.GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetString()!.Length,
            clippedCell.GetProperty("returnedLength").GetInt32());
        Assert.False(char.IsHighSurrogate(projected.GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetString()![^1]));
        Assert.Equal("utf16-code-units", clippedCell.GetProperty("unit").GetString());
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Agent_query_projection_uses_clr_aliases_and_omits_only_default_fields()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var columns = new[]
        {
            new SqlHarnessColumnReport(0, "date", "System.DateTime", true),
            new SqlHarnessColumnReport(1, "alsoDate", "System.DateTime", false),
            new SqlHarnessColumnReport(2, "offset", "System.DateTimeOffset", true),
            new SqlHarnessColumnReport(3, "text", "System.String", true),
            new SqlHarnessColumnReport(4, "short", "System.Int16", false),
            new SqlHarnessColumnReport(5, "int", "System.Int32", false),
            new SqlHarnessColumnReport(6, "long", "System.Int64", false),
            new SqlHarnessColumnReport(7, "bool", "System.Boolean", false),
            new SqlHarnessColumnReport(8, "decimal", "System.Decimal", false),
            new SqlHarnessColumnReport(9, "bytes", "System.Byte[]", true),
            new SqlHarnessColumnReport(10, "custom", "Example.CustomType", true),
        };
        var empty = new SqlHarnessResultSetReport(columns, [], 0, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [empty], [], 0, 1,
            "canonical-hash", new OutputFootprint(0, 0));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 64));

        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("db", result.GetProperty("database").GetString());
        Assert.False(result.TryGetProperty("target", out _));
        Assert.False(result.TryGetProperty("resultHash", out _));
        Assert.False(result.TryGetProperty("rawFootprint", out _));
        Assert.False(result.TryGetProperty("messages", out _));
        Assert.False(result.TryGetProperty("omittedResultSetCount", out _));
        Assert.Equal(0, result.GetProperty("recordsAffected").GetInt32());
        var set = result.GetProperty("resultSets")[0];
        Assert.Equal(0, set.GetProperty("rowCount").GetInt64());
        Assert.Equal(columns.Length, set.GetProperty("columns").GetArrayLength());
        Assert.False(set.TryGetProperty("clippedCells", out _));
        Assert.False(set.TryGetProperty("omittedRowCount", out _));
        Assert.False(set.TryGetProperty("omittedColumnCount", out _));
        Assert.Equal(new[] { "dateTime", "dateTime", "dateTimeOffset", "string", "int16", "int32", "int64", "boolean", "decimal", "bytes", "Example.CustomType" },
            set.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("valueType").GetString()).ToArray());
        Assert.Equal("System.DateTime", report.ResultSets[0].Columns[0].DataType);
        Assert.Equal("canonical-hash", report.ResultHash);
    }

    [Fact]
    public void Agent_query_projection_keeps_compact_empty_result_and_zero_records_affected_when_it_fits()
    {
        var target = new SqlHarnessTargetIdentityReport(new string('s', 5000), new string('d', 5000), "actual", "database", "profile");
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "System.String", true)], [["kept"]], 1, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set], [], 0, 1, new string('h', 5000), new OutputFootprint(50000, 1));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 64));

        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("truncation").ValueKind);
        Assert.Equal(1, result.GetProperty("resultSets")[0].GetProperty("rows").GetArrayLength());
        Assert.Equal(0, result.GetProperty("recordsAffected").GetInt32());
        Assert.False(result.GetProperty("resultSets")[0].TryGetProperty("clippedCells", out _));
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
        Assert.False(result.TryGetProperty("resultHash", out _));
        var projectedCell = result.GetProperty("resultSets")[0].GetProperty("rows")[0][0];
        Assert.Equal(bytes.LongLength, projectedCell.GetProperty("byteLength").GetInt64());
        Assert.True(projectedCell.GetProperty("truncated").GetBoolean());
        Assert.True(projectedCell.GetProperty("base64Prefix").GetString()!.Length <= 64);
        var clippedCell = Assert.Single(result.GetProperty("resultSets")[0].GetProperty("clippedCells").EnumerateArray());
        Assert.Equal(bytes.LongLength, clippedCell.GetProperty("originalLength").GetInt64());
        Assert.Equal(48, clippedCell.GetProperty("returnedLength").GetInt32());
        Assert.Equal("bytes", clippedCell.GetProperty("unit").GetString());
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Agent_query_clipping_metadata_is_local_and_excludes_omitted_rows()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var columns = new[]
        {
            new SqlHarnessColumnReport(0, "left", "System.String", true),
            new SqlHarnessColumnReport(1, "right", "System.String", true),
        };
        var rows = Enumerable.Range(0, 200)
            .Select(i => (IReadOnlyList<object?>)[$"left-{i:D4}-long", $"right-{i:D4}-long"])
            .ToArray();
        var set = new SqlHarnessResultSetReport(columns, rows, rows.Length, 0);
        var report = new SqlHarnessQueryReport(target, "read-only", [set, set], [], -1, 1,
            "hash", new OutputFootprint(10000, 2));
        var projected = AgentOutputProjection.Project(report, 8, 1, out _, 4096);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var result = json.RootElement;
        Assert.False(result.TryGetProperty("recordsAffected", out _));
        var sets = result.GetProperty("resultSets");
        Assert.Equal(2, sets.GetArrayLength());
        foreach (var projectedSet in sets.EnumerateArray())
        {
            Assert.Equal(1, projectedSet.GetProperty("rows").GetArrayLength());
            var clips = projectedSet.GetProperty("clippedCells").EnumerateArray().ToArray();
            Assert.Equal(2, clips.Length);
            Assert.All(clips, clip => Assert.Equal(0, clip.GetProperty("row").GetInt32()));
            Assert.Equal(new[] { 0, 1 }, clips.Select(clip => clip.GetProperty("column").GetInt32()).ToArray());
        }
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
        Assert.True(result.GetProperty("cases").GetArrayLength() < 100);
        Assert.Equal("compared", result.GetProperty("cases")[0].GetProperty("equivalenceAvailability").GetString());
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public async Task Agent_output_options_are_accepted_and_constrain_the_whole_envelope()
    {
        var output = new StringWriter();
        var module = new FakeModule(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new { value = "😀" }, null));
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(["query", "dev", "--file", SqlFile, "--output", "agent", "--max-output-bytes", "4096", "--max-cell-chars", "0"]);

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
            "query", "prod", "--var", $"{largeKey}=one", "--var", $"{largeKey}=two",
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
        Assert.False(projectedSet.TryGetProperty("omittedRowCount", out _));
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
            Assert.False(resultSets[i].TryGetProperty("omittedRowCount", out _));
        }
    }

    [Fact]
    public void Agent_projection_small_matrix_returns_all_case_outcomes_and_details()
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
        var result = json.RootElement.GetProperty("result");
        var resultCases = result.GetProperty("cases");
        Assert.Equal(3, resultCases.GetArrayLength());
        Assert.All(resultCases.EnumerateArray(), item =>
            Assert.False(string.IsNullOrEmpty(item.GetProperty("artifactId").GetString())));
        Assert.Equal(3, result.GetProperty("details").GetArrayLength());
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
        var projectedCases = result.GetProperty("cases");
        Assert.True(projectedCases.GetArrayLength() < 20 || result.GetProperty("continuation").ValueKind == JsonValueKind.Number,
            "An oversized matrix should page its outcome inventory when all cases do not fit.");
        Assert.True(json.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
        Assert.All(projectedCases.EnumerateArray(), item =>
            Assert.False(string.IsNullOrEmpty(item.GetProperty("artifactId").GetString())));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Agent_matrix_keeps_completed_case_inventory_ahead_of_optional_detail(int budget)
    {
        var cells = Enumerable.Range(0, 6).Select(i => new CompareMatrixCellReport(
            i, i.ToString(), BuildInventoryCompare(i, i == 0 ? ResultComparisonMode.Off : ResultComparisonMode.Multiset,
                i == 1 ? false : true, i == 2))).ToArray();
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells);
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), "compare",
            new OutputCaptureWriter(output), new AgentOutputOptions(budget, 128));

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, budget);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        var cases = result.GetProperty("cases");
        Assert.Equal(6, result.GetProperty("caseCount").GetInt32());
        Assert.InRange(cases.GetArrayLength(), 1, 6);
        Assert.Equal(6 - cases.GetArrayLength(), result.GetProperty("omittedCaseCount").GetInt32());
        if (budget >= 65536)
        {
            Assert.Equal(6, cases.GetArrayLength());
            Assert.Equal(0, result.GetProperty("omittedCaseCount").GetInt32());
        }
        Assert.Equal(Enumerable.Range(0, cases.GetArrayLength()), cases.EnumerateArray().Select(item => item.GetProperty("index").GetInt32()));
        Assert.All(cases.EnumerateArray(), item =>
        {
            Assert.True(item.TryGetProperty("baseline", out var baseline));
            Assert.True(baseline.TryGetProperty("elapsedMinimum", out _));
            Assert.True(baseline.TryGetProperty("elapsedMaximum", out _));
        });
        Assert.Equal("case-0", cases[0].GetProperty("artifactId").GetString());
        Assert.Equal(40m, cases[0].GetProperty("baseline").GetProperty("elapsedMilliseconds").GetDecimal());
        Assert.Equal(50m, cases[0].GetProperty("candidate").GetProperty("elapsedMilliseconds").GetDecimal());
        Assert.Equal(10m, cases[0].GetProperty("delta").GetProperty("elapsedMilliseconds").GetDecimal());
        Assert.Equal(JsonValueKind.Null, cases[2].GetProperty("candidate").GetProperty("cpuMilliseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, cases[2].GetProperty("delta").GetProperty("cpuMilliseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, cases[2].GetProperty("baseline").GetProperty("cpuMilliseconds").ValueKind);
        Assert.Equal("not-requested", cases[0].GetProperty("equivalenceAvailability").GetString());
        Assert.Equal(JsonValueKind.Null, cases[0].GetProperty("equivalent").ValueKind);
        Assert.Equal("compared", cases[1].GetProperty("equivalenceAvailability").GetString());
        Assert.False(cases[1].GetProperty("equivalent").GetBoolean());
        Assert.All(result.GetProperty("details").EnumerateArray(), detail =>
        {
            Assert.False(detail.GetProperty("compare").TryGetProperty("target", out _));
            Assert.False(detail.GetProperty("compare").TryGetProperty("classification", out _));
            Assert.False(detail.GetProperty("compare").TryGetProperty("parameters", out _));
            Assert.False(detail.GetProperty("compare").TryGetProperty("repetitions", out _));
            Assert.True(detail.GetProperty("compare").TryGetProperty("regression", out _));
        });
    }

    [Fact]
    public void Agent_matrix_partial_run_keeps_status_error_and_completed_outcomes_only()
    {
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int",
            Enumerable.Range(0, 3).Select(i => new CompareMatrixCellReport(i, i.ToString(), BuildInventoryCompare(i, ResultComparisonMode.Multiset, true, false))).ToArray());
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, matrix, "Cell 3 failed."), "compare",
            new OutputCaptureWriter(output), new AgentOutputOptions(4096, 128));

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("partial", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Cell 3 failed.", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal(3, result.GetProperty("caseCount").GetInt32());
        Assert.Equal(new[] { 0, 1, 2 }, result.GetProperty("cases").EnumerateArray().Select(item => item.GetProperty("index").GetInt32()).ToArray());
    }

    private static SqlHarnessCompareReport BuildInventoryCompare(int index, ResultComparisonMode mode, bool? equivalent, bool unavailableCpu)
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var baseline = new CompareVariantReport("baseline", new CompareDistribution(10, 20, 30), new CompareDistribution(30, 40, 50),
            new CompareDistribution(5, 6, 7), new Dictionary<string, long>(),
            Enumerable.Range(0, 30).Select(i => new CompareOperatorReport(i, $"baseline-op-{i}", $"dbo.t{i}", true, false, false)).ToArray(),
            [$"matrix-warning-{index}-" + new string('w', 20_000)])
        {
            LogicalReadsByTable = Enumerable.Range(0, 30).ToDictionary(i => $"table-{i}", _ => new CompareDistribution(1, 2, 3)),
            MetricReport = unavailableCpu ? new BenchmarkMetricReport(BenchmarkMetricReport.Unavailable, BenchmarkMetricReport.Measured,
                null, true, null, null, BenchmarkMetricReport.Measured, null, null, null, BenchmarkMetricReport.ResultStatement,
                BenchmarkMetricText.StatementRows, []) : null,
        };
        var candidate = baseline with
        {
            Name = "candidate",
            CpuTimeMilliseconds = new CompareDistribution(20, 30, 40),
            ElapsedTimeMilliseconds = new CompareDistribution(40, 50, 60),
            LogicalReads = new CompareDistribution(7, 8, 9),
        };
        return new SqlHarnessCompareReport(target, 5, 5, equivalent, baseline, candidate, $"/artifacts/case-{index}")
        {
            Equivalence = new ResultEquivalenceReport(mode, equivalent, null, null, null),
            Classification = new CompareClassificationReport("setup", "baseline", "candidate"),
            Regression = new RegressionReport("inconclusive", [], 1,
                new RegressionDiagnostic("regressed", 40m, 50m, 10m, 25m),
                new RegressionDiagnostic("not-regressed", 6m, 8m, 2m, 33m),
                new RegressionDiagnostic("unavailable", null, null, null, null)),
        };
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
    public void Every_report_uses_one_descending_ladder()
    {
        Assert.Equal(
            [int.MaxValue, 128, 64, 32, 16, 8, 4, 2, 1, 0],
            AgentOutputProjection.CandidateDetailLimits);
    }

    [Fact]
    public void Per_table_reads_order_by_median_then_max_then_name()
    {
        var reads = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
        {
            ["Worktable"] = new(0, 0, 0),
            ["[dbo].[B]"] = new(28, 28, 28),
            ["[dbo].[A]"] = new(280, 282, 290),
            ["[dbo].[C]"] = new(20, 28, 40),
            ["[dbo].[D]"] = new(2, 280, 290),
            ["[dbo].[E]"] = new(1, 280, 290),
        };

        var ordered = AgentOutputProjection.BySignificance(reads).Select(pair => pair.Key).ToArray();

        Assert.Equal(["[dbo].[A]", "[dbo].[D]", "[dbo].[E]", "[dbo].[C]", "[dbo].[B]", "Worktable"], ordered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(2048)]
    public void Agent_measure_map_preserves_complete_table_identity(int maximumCellCharacters)
    {
        var (firstName, secondName, reads) = CollidingTableReads(maximumCellCharacters);
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var baseline = VariantWithTableReads("baseline", reads);
        var measureJson = RenderAgentResult(new SqlHarnessMeasureReport(target, 3, 3, true, baseline, null), "measure",
            new AgentOutputOptions(65536, maximumCellCharacters));
        AssertTableReadValues(measureJson.RootElement.GetProperty("result").GetProperty("query").GetProperty("logicalReadsByTable"),
            firstName, secondName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(2048)]
    public void Agent_compare_maps_preserve_complete_table_identity_on_both_sides(int maximumCellCharacters)
    {
        var (firstName, secondName, reads) = CollidingTableReads(maximumCellCharacters);
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var baseline = VariantWithTableReads("baseline", reads);
        var candidate = VariantWithTableReads("candidate", reads);
        var compare = new SqlHarnessCompareReport(target, 3, 3, true, baseline, candidate, null);

        var compareJson = RenderAgentResult(compare, "compare", new AgentOutputOptions(65536, maximumCellCharacters));
        var result = compareJson.RootElement.GetProperty("result");
        AssertTableReadValues(result.GetProperty("baseline").GetProperty("logicalReadsByTable"), firstName, secondName);
        AssertTableReadValues(result.GetProperty("candidate").GetProperty("logicalReadsByTable"), firstName, secondName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(2048)]
    public void Agent_artifact_metric_map_preserves_complete_table_identity(int maximumCellCharacters)
    {
        var (firstName, secondName, reads) = CollidingTableReads(maximumCellCharacters);
        var artifactMetrics = new ArtifactMetricsSection("artifact", "measure",
            [new ArtifactNamedMetrics("query", new CompareDistribution(1, 2, 3), new CompareDistribution(4, 5, 6),
                new CompareDistribution(7, 8, 9), reads, ["ordinary display text"])]);
        var artifactJson = RenderAgentResult(artifactMetrics, "artifact", new AgentOutputOptions(65536, maximumCellCharacters));
        var variant = artifactJson.RootElement.GetProperty("result").GetProperty("variants")[0];
        AssertTableReadValues(variant.GetProperty("logicalReadsByTable"), firstName, secondName);
        if (maximumCellCharacters == 0)
            Assert.Equal("", variant.GetProperty("name").GetString());
    }

    [Fact]
    public void Agent_normal_metric_map_keeps_existing_envelope_schema_and_fields()
    {
        var reads = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
        {
            ["dbo.Orders"] = new(1, 2, 3),
        };
        var report = new SqlHarnessMeasureReport(
            new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile"),
            3, 3, true, VariantWithTableReads("query", reads), null);

        using var json = RenderAgentResult(report, "measure", new AgentOutputOptions(65536, 2048));

        Assert.Equal(AgentEnvelopeSchema.Version, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ["schemaVersion", "command", "status", "exitCode", "result", "error", "truncation"],
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("truncation").ValueKind);
        var readsJson = json.RootElement.GetProperty("result").GetProperty("query").GetProperty("logicalReadsByTable");
        Assert.Equal(["dbo.Orders"], readsJson.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void Measure_set_summary_retains_each_requested_set()
    {
        var target = new SqlHarnessTargetIdentityReport("server", "db", "server", "db", "profile");
        var metrics = VariantWithTableReads("query", new Dictionary<string, CompareDistribution>
        {
            ["dbo.Shared_first"] = new(1, 11, 21),
            ["dbo.Shared_second"] = new(2, 29, 39),
        });
        var set = new SqlHarnessMeasureSetReport(target, 3, 6, 0, ["small", "large"], "user order", "cache stable",
            [new MeasureParameterSetReport("small", [], null, 3, true, null, metrics, []),
             new MeasureParameterSetReport("large", [], null, 3, true, null, metrics, [])],
            new MeasureCrossSetSummary("small", 1, "large", 2, "small", 1, "large", 2, "small", 1, "large", 2), null);

        var json = RenderAgentResult(set, "measure-set", new AgentOutputOptions(65536, 4));
        var sets = json.RootElement.GetProperty("result").GetProperty("sets");
        Assert.Equal(2, sets.GetArrayLength());
        Assert.Equal(new[] { "smal", "larg" }, sets.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()).ToArray());
        Assert.Equal(8, sets[0].GetProperty("medianLogicalReads").GetInt64());
        Assert.Equal(8, sets[1].GetProperty("medianLogicalReads").GetInt64());
    }

    private static (string First, string Second, IReadOnlyDictionary<string, CompareDistribution> Reads)
        CollidingTableReads(int maximumCellCharacters)
    {
        var (firstName, secondName) = CollidingTableNames(maximumCellCharacters);
        return (firstName, secondName, new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
        {
            [firstName] = new(1, 11, 21),
            [secondName] = new(2, 29, 39),
        });
    }

    private static (string First, string Second) CollidingTableNames(int maximumCellCharacters)
    {
        var shared = new string('x', maximumCellCharacters);
        return ($"{shared}first", $"{shared}second");
    }

    private static CompareVariantReport VariantWithTableReads(string name,
        IReadOnlyDictionary<string, CompareDistribution> reads) =>
        new(name, new CompareDistribution(1, 2, 3), new CompareDistribution(4, 5, 6),
            new CompareDistribution(7, 8, 9), new Dictionary<string, long>(), [], [])
        {
            LogicalReadsByTable = reads,
        };

    private JsonDocument RenderAgentResult(object report, string command, AgentOutputOptions options)
    {
        var output = new StringWriter();
        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), command,
            new OutputCaptureWriter(output), options);
        var rendered = output.ToString();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(rendered);
        _testOutput.WriteLine($"Agent {command} response: {bytes} UTF-8 bytes (budget {options.MaximumBytes}).");
        Assert.InRange(bytes, 1, options.MaximumBytes);
        return JsonDocument.Parse(rendered);
    }

    private static void AssertTableReadValues(JsonElement reads, string firstName, string secondName)
    {
        Assert.Equal(2, reads.EnumerateObject().Count());
        Assert.Equal(11, reads.GetProperty(firstName).GetProperty("median").GetInt64());
        Assert.Equal(29, reads.GetProperty(secondName).GetProperty("median").GetInt64());
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
        Assert.False(result.TryGetProperty("omittedResultSetCount", out _));
        var sets = result.GetProperty("resultSets");
        Assert.Equal(3, sets.GetArrayLength());
        for (var i = 0; i < 3; i++)
        {
            Assert.False(sets[i].TryGetProperty("omittedColumnCount", out _));
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

        foreach (var limit in AgentOutputProjection.CandidateDetailLimits)
        {
            var projected = AgentOutputProjection.Project(report, 64, limit, out _, 4096);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.False(json.RootElement.TryGetProperty("resultHash", out _));
            Assert.False(json.RootElement.TryGetProperty("target", out _));
            Assert.False(json.RootElement.TryGetProperty("rawFootprint", out _));
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
        // Enough rows that the set's complete rows exceed the whole-set reserve,
        // so the row sample applies instead of the cheap-set top-up.
        var rows = Enumerable.Range(0, 500)
            .Select(i => (IReadOnlyList<object?>)new object?[] { i == 0 ? big : new byte[] { 1, 2 }, null, i, new string('x', 40) })
            .ToArray();
        var report = QueryShapeFixtures.Report(new SqlHarnessResultSetReport(columns, rows, 500, 0));

        var result = ProjectQueryJson(report, 16, 2);

        var set = result.GetProperty("resultSets")[0];
        Assert.Equal(4, set.GetProperty("columns").GetArrayLength());
        var emitted = set.GetProperty("rows");
        Assert.Equal(2, emitted.GetArrayLength());
        Assert.Equal(498, set.GetProperty("omittedRowCount").GetInt64());
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
        Assert.False(result.TryGetProperty("omittedResultSetCount", out _));
        var sets = result.GetProperty("resultSets");
        Assert.Equal(5, sets.GetArrayLength());
        Assert.Equal(0, sets[1].GetProperty("columns").GetArrayLength());
        Assert.False(sets[1].TryGetProperty("omittedColumnCount", out _));
        Assert.Equal(2, sets[2].GetProperty("columns").GetArrayLength());
        Assert.Equal(0, sets[2].GetProperty("rows").GetArrayLength());
        Assert.False(sets[2].TryGetProperty("omittedRowCount", out _));
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
        Assert.False(result.TryGetProperty("omittedResultSetCount", out _));
        var sets = result.GetProperty("resultSets");
        Assert.Equal(2, sets.GetArrayLength());
        // Cheapest complete schema first, then complete rows, then column
        // prefixes: the summary set keeps its whole schema and rows, and the
        // wide first schema gets the leftover columns, explicitly counted.
        Assert.True(sets[0].GetProperty("omittedColumnCount").GetInt32() > 0);
        Assert.Equal(0, sets[0].GetProperty("rows").GetArrayLength());
        Assert.Equal(3, sets[0].GetProperty("omittedRowCount").GetInt64());
        Assert.Equal(2, sets[1].GetProperty("columns").GetArrayLength());
        Assert.False(sets[1].TryGetProperty("omittedColumnCount", out _));
        Assert.Equal(2, sets[1].GetProperty("rowCount").GetInt64());
        Assert.Equal(2, sets[1].GetProperty("rows").GetArrayLength());
        Assert.False(sets[1].TryGetProperty("omittedRowCount", out _));
    }

    public static TheoryData<int, int, int> WideDetailShapes => new()
    {
        { 100, 200, 30 }, // SELECT * over ~100 columns: one row cannot fit
        { 12, 5000, 20 }, // ~12 nvarchar(max) columns, clipped to the cell limit
    };

    [Theory]
    [MemberData(nameof(WideDetailShapes))]
    public void Query_wide_one_row_detail_keeps_both_summaries_complete_at_default_budget(int columns, int width, int nameLength)
    {
        var report = QueryShapeFixtures.Report(
            QueryShapeFixtures.Set(columns, 1, width: width, nameLength: nameLength),
            QueryShapeFixtures.Set(1, 1),
            QueryShapeFixtures.Set(2, 5));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions());

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, 65536);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(result, report);
        QueryShapeFixtures.AssertSummariesComplete(result, report, 1, 2);
    }

    [Fact]
    public void Query_wide_one_row_detail_that_cannot_fit_keeps_summaries_at_level_zero()
    {
        var report = QueryShapeFixtures.Report(
            QueryShapeFixtures.Set(100, 1, width: 200, nameLength: 30),
            QueryShapeFixtures.Set(1, 1),
            QueryShapeFixtures.Set(2, 5));

        var result = ProjectQueryJson(report, 512, 0);

        QueryShapeFixtures.AssertAccounted(result, report);
        QueryShapeFixtures.AssertSummariesComplete(result, report, 1, 2);
        var detail = result.GetProperty("resultSets")[0];
        Assert.True(detail.GetProperty("omittedColumnCount").GetInt32() > 0);
        Assert.Equal(0, detail.GetProperty("rows").GetArrayLength());
        Assert.Equal(1, detail.GetProperty("omittedRowCount").GetInt64());
    }

    [Fact]
    public void Query_row_sample_keeps_cheap_summary_whole_while_detail_is_sampled()
    {
        // The detail set forces a small row sample; the 50-row GROUP BY summary
        // is cheap enough for the whole-set reserve and is not cut to the sample.
        var report = QueryShapeFixtures.Report(
            QueryShapeFixtures.Set(4, 200, width: 500),
            QueryShapeFixtures.Set(2, 50));
        var output = new StringWriter();

        new Renderer().RenderAgent(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query",
            new OutputCaptureWriter(output), new AgentOutputOptions());

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(output.ToString()), 1, 65536);
        using var json = JsonDocument.Parse(output.ToString());
        var truncation = json.RootElement.GetProperty("truncation");
        Assert.InRange(truncation.GetProperty("detailLimit").GetInt32(), 1, 49);
        var result = json.RootElement.GetProperty("result");
        QueryShapeFixtures.AssertAccounted(result, report);
        QueryShapeFixtures.AssertSummariesComplete(result, report, 1);
        var detailRows = result.GetProperty("resultSets")[0].GetProperty("rows").GetArrayLength();
        Assert.Equal(truncation.GetProperty("detailLimit").GetInt32(), detailRows);
        Assert.Equal(200 - detailRows, truncation.GetProperty("omittedItems").GetInt32());
    }

    [Fact]
    public void Offline_plan_projection_marks_omitted_descendants_at_the_retained_node()
    {
        var root = new PlanNode("Root", null, null, null, null, null, null, null, null, [],
        [
            new PlanNode("Kept", null, null, null, null, null, null, null, null, [], []),
            new PlanNode("Omitted", null, null, null, null, null, null, null, null, [],
            [new PlanNode("Grandchild", null, null, null, null, null, null, null, null, [], [])]),
        ]);
        var report = new DistilledPlan([new PlanStatement(null, root, [])]);

        var projected = AgentOutputProjection.Project(report, 64, 1, out var omitted, 4096);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var projectedRoot = json.RootElement.GetProperty("statements")[0].GetProperty("root");

        Assert.Equal(1, projectedRoot.GetProperty("omittedChildCount").GetInt32());
        Assert.Equal(2, projectedRoot.GetProperty("omittedSubtreeCount").GetInt32());
        Assert.Equal("Kept", projectedRoot.GetProperty("children")[0].GetProperty("physicalOp").GetString());
        Assert.True(omitted > 0);
    }

    [Fact]
    public void Offline_plan_projection_marks_deep_truncation_instead_of_emitting_a_complete_leaf()
    {
        var node = new PlanNode("Leaf", null, null, null, null, null, null, null, null, [], []);
        node = new PlanNode("Boundary", null, null, null, null, null, null, null, null, [], [node]);
        for (var depth = 5; depth >= 0; depth--)
            node = new PlanNode("Level" + depth, null, null, null, null, null, null, null, null, [], [node]);
        var report = new DistilledPlan([new PlanStatement(null, node, [])]);

        var projected = AgentOutputProjection.Project(report, 64, 1, out _, 65536);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var boundary = json.RootElement.GetProperty("statements")[0].GetProperty("root");
        for (var depth = 0; depth < 6; depth++)
            boundary = boundary.GetProperty("children")[0];

        Assert.Equal(1, boundary.GetProperty("omittedChildCount").GetInt32());
        Assert.Equal(1, boundary.GetProperty("omittedSubtreeCount").GetInt64());
        Assert.False(boundary.TryGetProperty("children", out _));
    }

    [Fact]
    public void Dry_run_projection_states_validation_and_non_execution_and_preserves_variant_classes()
    {
        var report = new SqlHarnessDryRunReport("compare", "read-only", "schema", [], 1)
        {
            CompareClassification = new CompareClassificationReport("session-local", "read-only", "schema"),
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            AgentOutputProjection.Project(report, 2048, 128, out _, 4096),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var result = json.RootElement;

        Assert.Equal("passed", result.GetProperty("validation").GetString());
        Assert.Equal("not-run", result.GetProperty("execution").GetString());
        Assert.Equal("schema", result.GetProperty("tier").GetString());
        Assert.Equal("session-local", result.GetProperty("compareClassification").GetProperty("setup").GetString());
        Assert.Equal("read-only", result.GetProperty("compareClassification").GetProperty("baseline").GetString());
        Assert.Equal("schema", result.GetProperty("compareClassification").GetProperty("candidate").GetString());
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