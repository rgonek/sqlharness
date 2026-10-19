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
        new[] { "ping", "missing-profile", "--json" },
        new[] { "query", "missing-profile", "--json", "--file", "does-not-exist.sql" },
    };

    [Fact]
    public void Journal_v19_pins_statement_plan_occurrence_provenance()
    {
        var path = Path.Combine(_temp.Path, "data", "activity.db");
        _ = ActivityJournal.Open(path, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));

        Assert.Equal(18, JournalSchema.FirstRetainedVersion);
        Assert.Equal(19, JournalSchema.CurrentVersion);
        Assert.Equal((long)JournalSchema.CurrentVersion, JournalDb.Rows(path, "PRAGMA user_version").Single()["user_version"]);
        var operationColumns = JournalDb.Rows(path, "SELECT name FROM pragma_table_info('operations')")
            .Select(row => (string)row["name"]!).ToArray();
        Assert.Contains("conditions_json", operationColumns);
        Assert.Contains("conditions_fingerprint", operationColumns);
        Assert.Contains("warnings_json", operationColumns);
        Assert.Contains("measurement_fingerprint", operationColumns);
        Assert.Contains("case_id", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('operation_metrics')")
            .Select(row => (string)row["name"]!));
        Assert.Contains("case_label", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('operation_statements')")
            .Select(row => (string)row["name"]!));
        var statementColumns = JournalDb.Rows(path, "SELECT name FROM pragma_table_info('operation_statements')")
            .Select(row => (string)row["name"]!).ToArray();
        Assert.Contains("statement_key", statementColumns);
        Assert.Contains("occurrence", statementColumns);
        Assert.Contains("executions", statementColumns);
        Assert.Contains("plan_hash", statementColumns);
        Assert.Contains("status", statementColumns);
        Assert.Contains("udf_elapsed_ms", statementColumns);
        Assert.Contains("hidden_work_json", statementColumns);
        Assert.Contains("statement_text", statementColumns);
        Assert.Contains("plan_repetition", statementColumns);
        Assert.Contains("plan_document_ordinal", statementColumns);
        Assert.Contains("plan_statement_index", statementColumns);
        Assert.Contains("omitted_statements", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('operation_metrics')")
            .Select(row => (string)row["name"]!));
        Assert.Equal(6, JournalDb.Rows(path, "SELECT name FROM pragma_table_info('operation_statement_objects')").Count);
        Assert.Contains("shape_hash", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('plan_documents')").Select(row => (string)row["name"]!));
        Assert.Contains("sha256", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('plan_documents')").Select(row => (string)row["name"]!));
        Assert.Contains("span_start", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('plan_occurrences')").Select(row => (string)row["name"]!));
        Assert.Contains("span_length", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('plan_occurrences')").Select(row => (string)row["name"]!));
        Assert.Contains("match_kind", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('plan_occurrences')").Select(row => (string)row["name"]!));
        Assert.Contains("statement_id", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('plan_occurrences')").Select(row => (string)row["name"]!));
        Assert.Contains("statement_comp_id", JournalDb.Rows(path, "SELECT name FROM pragma_table_info('plan_occurrences')").Select(row => (string)row["name"]!));
        var salt = Assert.IsType<byte[]>(JournalDb.Rows(path,
            "SELECT value FROM journal_meta WHERE key = 'fingerprint_salt'").Single()["value"]);
        Assert.Equal(32, salt.Length);
    }

    [Fact]
    public void Journal_v18_migrates_in_place_to_v19_statement_plan_provenance()
    {
        var path = Path.Combine(_temp.Path, "data", "activity.db");
        _ = ActivityJournal.Open(path, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "ALTER TABLE operation_statements DROP COLUMN plan_statement_index; ALTER TABLE operation_statements DROP COLUMN plan_document_ordinal; ALTER TABLE operation_statements DROP COLUMN plan_repetition; PRAGMA user_version = 18;";
            downgrade.ExecuteNonQuery();
        }

        _ = ActivityJournal.Open(path, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));

        Assert.Equal(19L, JournalDb.Rows(path, "PRAGMA user_version").Single()["user_version"]);
        var columns = JournalDb.Rows(path, "SELECT name FROM pragma_table_info('operation_statements')")
            .Select(row => (string)row["name"]!).ToArray();
        Assert.Contains("plan_repetition", columns);
        Assert.Contains("plan_document_ordinal", columns);
        Assert.Contains("plan_statement_index", columns);
    }

    [Fact]
    public void Journal_v16_is_archived_without_migrating_old_rows()
    {
        var path = Path.Combine(_temp.Path, "data", "activity.db");
        _ = ActivityJournal.Open(path, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "INSERT INTO sessions (session_key, agent_kind, transport, source, host_pid, first_seen, last_seen) VALUES ('old', 'codex', 'cli', 'test', 1, 'now', 'now'); PRAGMA user_version = 16;";
            downgrade.ExecuteNonQuery();
        }

        _ = ActivityJournal.Open(path, new JournalConfig(), TextWriter.Null, new FixedTimeProvider(JournalTestData.T0));

        Assert.Equal(19L, JournalDb.Rows(path, "PRAGMA user_version").Single()["user_version"]);
        Assert.Empty(JournalDb.Rows(path, "SELECT id FROM sessions"));
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "activity.db.v16-*.bak"));
        Assert.Single(JournalDb.Rows(backup, "SELECT session_key FROM sessions WHERE session_key = 'old'"));
    }

    [Fact]
    public void Journal_v15_is_archived_without_migrating_old_rows()
    {
        var path = Path.Combine(_temp.Path, "data", "activity.db");
        _ = ActivityJournal.Open(path, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 15;";
            command.ExecuteNonQuery();
        }

        _ = ActivityJournal.Open(path, new JournalConfig(), TextWriter.Null,
            new FixedTimeProvider(JournalTestData.T0));

        Assert.Equal(19L, JournalDb.Rows(path, "PRAGMA user_version").Single()["user_version"]);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "activity.db.v15-*.bak"));
    }

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
            : ["compare", "test", "--var", "env=a", "--baseline", baseline, "--candidate", candidate, "--subject", "test-query", "--scope", "whole", "--repeat", "3", format];
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
        // The producer identity is deliberately unique per invocation; compare artifact payloads
        // while normalizing that one manifest field just like the random artifact directory.
        Assert.Equal(plain.Files, journaled.Files);
        // The journal really took the benchmark path: one metrics row per variant.
        var variants = JournalDb.Rows(databasePath, "SELECT variant FROM operation_metrics ORDER BY ordinal")
            .Select(row => (string)row["variant"]!).ToArray();
        Assert.Equal(command == "measure" ? ["measure"] : ["baseline", "candidate"], variants);
        Assert.NotEmpty(JournalDb.Rows(databasePath, "SELECT document_sha FROM plan_occurrences"));
    }

    private static async Task<(int Exit, string Output, Dictionary<string, string> Files)> RunBenchmark(
        string command, string[] args, IActivityJournal? journal, string artifactRoot)
    {
        var fixedNow = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        ISqlSessionFactory sessions = command == "measure"
            ? SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: PlanMetricsExtractorTests.ActualPlan)
            : SqlHarnessCompareTests.FakeCompareSession.Create(plan: PlanMetricsExtractorTests.ActualPlan);
        ISqlHarnessModule module = new SqlHarnessModule(
            sessions, new CompareArtifactWriter(artifactRoot, () => fixedNow), BenchmarkProfiles);
        if (journal is not null)
            module = new JournalingModule(module, () => journal, () => JournalTestData.Session());

        var (exit, output) = await Run(module, args);
        Assert.True(exit == 0, output);

        // One publish per run: the artifact directory and the id referenced from planRefs
        // both contain a fresh suffix, so normalize those identifiers while retaining the
        // structure and cross-references in the payloads.
        var directory = Assert.Single(Directory.GetDirectories(artifactRoot));
        var artifactId = ArtifactIds.FromDirectory(directory)!;
        string Normalize(string text) => text
            .Replace(System.Text.Json.JsonSerializer.Serialize(directory)[1..^1], "<artifact>", StringComparison.Ordinal)
            .Replace(directory, "<artifact>", StringComparison.Ordinal)
            .Replace(artifactId, "<artifact-id>", StringComparison.Ordinal);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
            path => NormalizeArtifactFile(path, Normalize),
            StringComparer.Ordinal);
        return (exit, Normalize(output), files);
    }

    private static string NormalizeArtifactFile(string path, Func<string, string> normalize)
    {
        var text = normalize(File.ReadAllText(path));
        if (!string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.Ordinal))
            return text;

        using var manifest = System.Text.Json.JsonDocument.Parse(text);
        var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        if (manifest.RootElement.TryGetProperty("producerOperationRef", out _))
            node["producerOperationRef"] = "<operation>";
        return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyDictionary<string, TargetProfile> BenchmarkProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static async Task<(int Exit, string Output)> Run(ISqlHarnessModule module, string[] args)
    {
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output, new StringReader(string.Empty), stdinRedirected: false,
            planStdin: new MemoryStream(), mcpError: TextWriter.Null).RunAsync(args);
        return (exit, output.ToString());
    }
}