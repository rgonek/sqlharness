using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Core.Targets;

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

    public static TheoryData<string, string> BenchmarkCommands => new()
    {
        { "measure", "--json" },
        { "measure", "--json-summary" },
        { "compare", "--json" },
        { "compare", "--json-summary" },
    };

    [Theory]
    [MemberData(nameof(BenchmarkCommands))]
    public async Task Successful_benchmark_output_and_artifacts_are_identical_with_and_without_journal(string command, string format)
    {
        var sqlDirectory = Path.Combine(_temp.Path, "sql");
        Directory.CreateDirectory(sqlDirectory);
        var baseline = Path.Combine(sqlDirectory, "baseline.sql");
        var candidate = Path.Combine(sqlDirectory, "candidate.sql");
        File.WriteAllText(baseline, "SELECT Value FROM dbo.Clients");
        File.WriteAllText(candidate, "SELECT Value FROM dbo.Clients -- candidate");
        string[] args = command == "measure"
            ? ["measure", "test", "--var", "env=a", "--query", baseline, "--repeat", "3", format]
            : ["compare", "test", "--var", "env=a", "--baseline", baseline, "--candidate", candidate, "--repeat", "3", format];
        var databasePath = Path.Combine(_temp.Path, "journal", "activity.db");

        var plain = await RunBenchmark(command, args, journal: null, Path.Combine(_temp.Path, "off"));
        var journal = ActivityJournal.Open(databasePath, new JournalConfig { StoreSensitive = true }, TextWriter.Null, TimeProvider.System);
        var journaled = await RunBenchmark(command, args, journal, Path.Combine(_temp.Path, "on"));

        Assert.Equal(0, plain.Exit);
        Assert.Equal(plain.Exit, journaled.Exit);
        Assert.Equal(plain.Output, journaled.Output);
        Assert.Contains("report.json", plain.Files.Keys);
        Assert.Contains("runs.jsonl", plain.Files.Keys);
        Assert.Contains("manifest.json", plain.Files.Keys);
        Assert.Equal(plain.Files, journaled.Files);
        // The journal really took the benchmark path: one metrics row per variant.
        var variants = JournalDb.Rows(databasePath, "SELECT variant FROM operation_metrics ORDER BY ordinal")
            .Select(row => (string)row["variant"]!).ToArray();
        Assert.Equal(command == "measure" ? ["measure"] : ["baseline", "candidate"], variants);
        Assert.NotEmpty(JournalDb.Rows(databasePath, "SELECT plan_hash FROM operation_plans"));
    }

    private static async Task<(int Exit, string Output, Dictionary<string, string> Files)> RunBenchmark(
        string command, string[] args, IActivityJournal? journal, string artifactRoot)
    {
        var fixedNow = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        ISqlSessionFactory sessions = command == "measure"
            ? SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: PlanMetricsExtractorTests.ActualPlan)
            : SqlHarnessCompareTests.FakeCompareSession.Create(plan: PlanMetricsExtractorTests.ActualPlan);
        ISqlHarnessModule module = new SqlHarnessModule(
            sessions, new NullGainStore(), new CompareArtifactWriter(artifactRoot, () => fixedNow), BenchmarkProfiles);
        if (journal is not null)
            module = new JournalingModule(module, () => journal, () => JournalTestData.Session());

        var (exit, output) = await Run(module, args);
        Assert.True(exit == 0, output);

        // One publish per run: the only per-run difference is the directory (a GUID suffix
        // under a per-run root), so both its raw and JSON-escaped forms become one token.
        var directory = Assert.Single(Directory.GetDirectories(artifactRoot));
        string Normalize(string text) => text
            .Replace(System.Text.Json.JsonSerializer.Serialize(directory)[1..^1], "<artifact>", StringComparison.Ordinal)
            .Replace(directory, "<artifact>", StringComparison.Ordinal);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
            path => Normalize(File.ReadAllText(path)),
            StringComparer.Ordinal);
        return (exit, Normalize(output), files);
    }

    private static IReadOnlyDictionary<string, TargetProfile> BenchmarkProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private sealed class NullGainStore : IGainStore
    {
        public void Append(GainRecord record) { }
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private static async Task<(int Exit, string Output)> Run(ISqlHarnessModule module, string[] args)
    {
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output, new StringReader(string.Empty), stdinRedirected: false,
            planStdin: new MemoryStream(), mcpError: TextWriter.Null).RunAsync(args);
        return (exit, output.ToString());
    }
}