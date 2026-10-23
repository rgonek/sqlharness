using System.Text.Json;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Cli;

public sealed class AgentOutputTestsErrorDiagnosticFloor
{
    [Fact]
    public void Agent_minimum_budget_bounds_multibyte_error_with_null_hint_and_keeps_identity()
    {
        var error = new SqlHarnessError("sql_execution_failed", "sql", string.Concat(Enumerable.Repeat("語😀", 6_000)));
        var output = new StringWriter();

        new Renderer().RenderAgent(
            new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, error.Message, Error: error),
            "query", new OutputCaptureWriter(output), new AgentOutputOptions(4096, 0));

        var rendered = output.ToString();
        using var document = JsonDocument.Parse(rendered);
        var envelope = document.RootElement;
        var reportedError = envelope.GetProperty("error");

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(rendered) <= 4096);
        Assert.Equal("sql_execution_failed", reportedError.GetProperty("code").GetString());
        Assert.Equal("sql", reportedError.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, reportedError.GetProperty("hint").ValueKind);
        Assert.True(reportedError.GetProperty("message").GetString()!.Length > 0);
        Assert.True(envelope.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Agent_minimum_byte_budget_preserves_error_identity_when_diagnostics_must_shrink_below_floor()
    {
        var error = new SqlHarnessError(
            "sql_execution_failed", "sql", new string('m', 3_000), new string('h', 2_048),
            new SqlHarnessErrorLocation(Path: new string('p', 50)));
        var output = new StringWriter();

        new Renderer().RenderAgent(
            new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, error.Message, Error: error),
            "query", new OutputCaptureWriter(output), new AgentOutputOptions(4096, 0));

        using var document = JsonDocument.Parse(output.ToString());
        var envelope = document.RootElement;
        var reportedError = envelope.GetProperty("error");
        Assert.EndsWith(output.NewLine, output.ToString(), StringComparison.Ordinal);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(output.ToString()) <= 4096,
            $"CLI output, including the writer's line ending, is {System.Text.Encoding.UTF8.GetByteCount(output.ToString())} bytes.");
        Assert.Equal("sql_execution_failed", reportedError.GetProperty("code").GetString());
        Assert.Equal("sql", reportedError.GetProperty("phase").GetString());
        Assert.True(reportedError.GetProperty("message").GetString()!.Length < AgentOutputLimits.DefaultCellCharacters);
        Assert.True(reportedError.GetProperty("hint").GetString()!.Length < AgentOutputLimits.DefaultCellCharacters);
        Assert.Equal(4, envelope.GetProperty("truncation").GetProperty("omittedItems").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(AgentOutputLimits.DefaultCellCharacters)]
    [InlineData(AgentOutputLimits.MaximumCellCharacters)]
    public void Agent_errors_keep_actionable_diagnostics_without_changing_result_cell_limits(int maximumCellCharacters)
    {
        foreach (var (exitCode, code, phase) in new[]
        {
            (SqlHarnessExitCode.Authentication, "connection_failed", "connect"),
            (SqlHarnessExitCode.Authentication, "authentication_failed", "authentication"),
            (SqlHarnessExitCode.SqlExecution, "sql_execution_failed", "sql"),
        })
        {
            var message = new string('m', 195) + "diagnostic" + new string('m', 400) +
                "Actionable tail: check the configured database endpoint.";
            var hint = new string('h', 500) + "Actionable hint: verify the login and database permissions.";
            var error = new SqlHarnessError(code, phase, message, hint,
                new SqlHarnessErrorLocation(Path: "db/session.sql"));
            var resultSet = new SqlHarnessResultSetReport(
                [new SqlHarnessColumnReport(0, "value", "text", true)],
                [[new string('c', 512)]], 1, 0);
            var report = new SqlHarnessQueryReport(
                new SqlHarnessTargetIdentityReport("req-srv", "req-db", "srv", "db", "profile"),
                "read-only", [resultSet], [], 1, 1, "raw-hash", new OutputFootprint(1, 0));
            var output = new StringWriter();

            new Renderer().RenderAgent(
                new SqlHarnessOutcome(exitCode, report, message, Error: error),
                "query", new OutputCaptureWriter(output), new AgentOutputOptions(4096, maximumCellCharacters));

            using var document = JsonDocument.Parse(output.ToString());
            var envelope = document.RootElement;
            var reportedError = envelope.GetProperty("error");
            var cell = envelope.GetProperty("result").GetProperty("resultSets")[0]
                .GetProperty("rows")[0][0].GetString()!;

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(output.ToString()) <= 4096);
            Assert.Contains("Actionable tail", reportedError.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("Actionable hint", reportedError.GetProperty("hint").GetString(), StringComparison.Ordinal);
            Assert.True(reportedError.GetProperty("message").GetString()!.Length <= Math.Max(maximumCellCharacters, AgentOutputLimits.DefaultCellCharacters));
            Assert.True(reportedError.GetProperty("hint").GetString()!.Length <= Math.Max(maximumCellCharacters, AgentOutputLimits.DefaultCellCharacters));
            Assert.True(cell.Length <= maximumCellCharacters);
            Assert.Equal(code, reportedError.GetProperty("code").GetString());
            Assert.Equal(phase, reportedError.GetProperty("phase").GetString());
            Assert.True(reportedError.GetProperty("location").GetProperty("path").GetString()!.Length <= maximumCellCharacters);
        }
    }
}