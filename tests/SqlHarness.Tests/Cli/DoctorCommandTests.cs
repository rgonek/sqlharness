using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class DoctorCommandTests
{
    [Fact]
    public async Task Doctor_is_offline_and_does_not_dispatch_or_report_environment_values()
    {
        var module = new RecordingModule();
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(module, output).RunAsync(["doctor", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(module.Operations);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.False(root.GetProperty("databaseCheckPerformed").GetBoolean());
        Assert.False(root.GetProperty("credentialsRead").GetBoolean());
        Assert.False(root.GetProperty("tokenRequested").GetBoolean());
        Assert.False(root.GetProperty("installationUpdated").GetBoolean());
        Assert.DoesNotContain("SQLHARNESS_HOME", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }
}
