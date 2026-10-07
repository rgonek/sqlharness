using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class DashboardCommandTests
{
    [Theory]
    [InlineData("--bogus")]
    [InlineData("--json")]
    [InlineData("extra")]
    public async Task Invalid_dashboard_arguments_exit_with_safety(string argument)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output, planStdin: new MemoryStream(), mcpError: error)
            .RunAsync(["dashboard", argument]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal("sqlharness: Invalid command line arguments.", error.ToString().Trim());
    }

    private sealed class NoopModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
    }
}