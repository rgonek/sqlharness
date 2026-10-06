using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class JournalContractTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly JournalTempDirectory _temp = new();

    public JournalContractTests() => Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _temp.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        _temp.Dispose();
    }

    public static TheoryData<string[]> Commands => new()
    {
        new[] { "gain", "--json" },
        new[] { "ping", "missing-profile", "--json" },
        new[] { "query", "missing-profile", "--json", "--file", "does-not-exist.sql" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Cli_output_and_exit_code_are_identical_with_and_without_journal(string[] args)
    {
        var (plainExit, plainOut) = await Run(new SqlHarnessModule(), args);
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var (journalExit, journalOut) = await Run(
            new JournalingModule(new SqlHarnessModule(), () => journal, () => SessionIdentities.Cli(ProcessInfo.Current)), args);

        Assert.Equal(plainExit, journalExit);
        Assert.Equal(plainOut, journalOut);
    }

    [Fact]
    public async Task Plan_command_output_is_identical_and_is_journaled()
    {
        var plan = Path.Combine(AppContext.BaseDirectory, "Fixtures", "operators.sqlplan");
        var args = new[] { "plan", plan, "--json" };

        var (plainExit, plainOut) = await Run(new SqlHarnessModule(), args);
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var (journalExit, journalOut) = await Run(
            new JournalingModule(new SqlHarnessModule(), () => journal, () => SessionIdentities.Cli(ProcessInfo.Current)), args);

        Assert.Equal(plainExit, journalExit);
        Assert.Equal(plainOut, journalOut);
        var row = JournalDb.Rows(SqlHarnessPaths.ActivityDatabase, "SELECT operation, emitted_tokens FROM operations").Single();
        Assert.Equal("plan", row["operation"]);
        Assert.NotNull(row["emitted_tokens"]);
    }

    private static async Task<(int Exit, string Output)> Run(ISqlHarnessModule module, string[] args)
    {
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output, new StringReader(string.Empty), stdinRedirected: false,
            planStdin: new MemoryStream(), mcpError: TextWriter.Null).RunAsync(args);
        return (exit, output.ToString());
    }
}