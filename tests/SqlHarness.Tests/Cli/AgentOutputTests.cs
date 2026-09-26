using System.Text.Json;
using System.Diagnostics;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class AgentOutputTests
{
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

    private sealed class FakeModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(outcome);
        }
    }
}
