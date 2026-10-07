using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class GainRenderTests
{
    private sealed class GainModule(SqlHarnessGainReport report) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null));
    }

    private static SqlHarnessGainReport Report(bool enabled) =>
        new(SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty) { JournalEnabled = enabled };

    [Fact]
    public async Task Text_output_notes_a_disabled_journal()
    {
        var output = new StringWriter();

        await SqlHarnessCli.Create(new GainModule(Report(false)), output).RunAsync(["gain"]);

        Assert.Contains("activity journal is disabled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_output_has_no_note_when_the_journal_is_enabled()
    {
        var output = new StringWriter();

        await SqlHarnessCli.Create(new GainModule(Report(true)), output).RunAsync(["gain"]);

        Assert.DoesNotContain("activity journal is disabled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_output_carries_journal_enabled()
    {
        var output = new StringWriter();

        await SqlHarnessCli.Create(new GainModule(Report(true)), output).RunAsync(["gain", "--json"]);

        Assert.Contains("\"journalEnabled\": true", output.ToString(), StringComparison.Ordinal);
    }
}