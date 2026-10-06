using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class DoctorJournalConfigTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-doctor-" + Guid.NewGuid().ToString("N"));

    public DoctorJournalConfigTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        Directory.Delete(_home, true);
    }

    [Fact]
    public async Task Doctor_reports_invalid_config_as_fail_closed()
    {
        File.WriteAllText(Path.Combine(_home, "config.json"), """{ "journal": { "storeSensitive": true }, "bogus": 1 }""");
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("configFilePresent").GetBoolean());
        Assert.False(root.GetProperty("configValid").GetBoolean());
        Assert.True(root.GetProperty("journalEnabled").GetBoolean());
        Assert.False(root.GetProperty("journalStoreSensitive").GetBoolean());
        Assert.False(root.GetProperty("activityJournalPresent").GetBoolean());
    }

    [Fact]
    public async Task Doctor_reports_missing_config_as_valid_defaults()
    {
        var output = new StringWriter();

        await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("configFilePresent").GetBoolean());
        Assert.True(document.RootElement.GetProperty("configValid").GetBoolean());
    }

    private sealed class NoopModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
    }
}