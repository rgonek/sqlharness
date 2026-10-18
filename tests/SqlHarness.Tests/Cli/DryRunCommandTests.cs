using System.Text.Json;
using System.Text.Json.Nodes;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Cli;

/// <summary>
/// CLI <c>--dry-run</c> on query, measure and compare (plan 060 task 2). Every test runs the real
/// Core module behind the CLI with a session factory that fails the test on any connection attempt,
/// so a passing dry run also proves it stayed offline. The classifier cases are ported from the
/// retired <c>validate</c> command tests.
/// </summary>
[Collection(SqlHarnessHomeCollection.Name)]
public sealed class DryRunCommandTests : IDisposable
{
    private const string SqlServer = "ss";
    private const string Postgres = "pg";
    private const string PasswordEnvVar = "SQLHARNESS_TEST_DRYRUN_CLI_PASSWORD";
    private const string DryRunMissing = "Invalid command line arguments.";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sqlharness-dryrun-" + Guid.NewGuid().ToString("N"));
    private readonly NoConnectionFactory _factory = new();

    public DryRunCommandTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Directory.Delete(_directory, true);
        GC.SuppressFinalize(this);
    }

    // ---- shared plumbing -------------------------------------------------------------------

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() => new Dictionary<string, TargetProfile>
    {
        [SqlServer] = new("server-unused", "ssdb", new Dictionary<string, string>(), "integrated"),
        [Postgres] = new("server-unused", "pgdb", new Dictionary<string, string>(), "sql", "user-unused", PasswordEnvVar, Engine: "postgres"),
    };

    private string Sql(string text, string name = "q.sql")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<(int Exit, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        var module = new SqlHarnessModule(_factory, Profiles);
        var exit = await SqlHarnessCli.Create(module, output).RunAsync(args);
        return (exit, output.ToString());
    }

    private Task<(int Exit, string Output)> Query(string profile, string sql, params string[] extra) =>
        Run(["query", profile, "--file", Sql(sql), "--dry-run", "--json", .. extra]);

    private Task<(int Exit, string Output)> Measure(string profile, string query, string? setup, params string[] extra) =>
        Run(["measure", profile, "--query", Sql(query, "m.sql"), .. (setup is null ? Array.Empty<string>() : ["--setup", Sql(setup, "setup.sql")]), "--dry-run", "--json", .. extra]);

    private Task<(int Exit, string Output)> Compare(string profile, string baseline, string candidate, string? setup, params string[] extra) =>
        Run(["compare", profile, "--baseline", Sql(baseline, "b.sql"), "--candidate", Sql(candidate, "c.sql"), "--subject", "test-query", "--scope", "whole", .. (setup is null ? Array.Empty<string>() : ["--setup", Sql(setup, "setup.sql")]), "--dry-run", "--json", .. extra]);

    private static JsonElement Report(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private void AssertOffline() => Assert.Equal(0, _factory.ConnectAttempts);

    private void AssertAllowed((int Exit, string Output) run, string command)
    {
        Assert.True(run.Exit == 0, run.Output);
        var report = Report(run.Output);
        Assert.Equal(command, report.GetProperty("command").GetString());
        AssertOffline();
    }

    private string AssertRejected((int Exit, string Output) run, params string[] expectedFragments)
    {
        Assert.True(run.Exit == (int)SqlHarnessExitCode.Safety, run.Output);
        var message = Report(run.Output).GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
        Assert.NotEqual(DryRunMissing, message);
        foreach (var fragment in expectedFragments)
            Assert.Contains(fragment, message, StringComparison.Ordinal);
        AssertOffline();
        return message;
    }

    // ---- option wiring and report -----------------------------------------------------------

    [Fact]
    public async Task Query_dry_run_returns_the_dry_run_report_with_exit_0_and_never_connects()
    {
        var run = await Query(SqlServer, "SELECT @customerId;", "--param", "customerId:int=42");

        AssertAllowed(run, "query");
        var report = Report(run.Output);
        Assert.Equal("read-only", report.GetProperty("classification").GetString());
        Assert.Equal("read-only", report.GetProperty("tier").GetString());
        Assert.Equal(1, report.GetProperty("parameterSets").GetInt32());
        var parameter = Assert.Single(report.GetProperty("parameters").EnumerateArray());
        Assert.Equal("@customerId", parameter.GetProperty("name").GetString());
        Assert.Equal("int", parameter.GetProperty("type").GetString());
        Assert.DoesNotContain("42", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE TABLE #t (Id int); SELECT 1 AS A;", "session")]
    [InlineData("SELECT 1 AS A", "read-only")]
    public async Task Query_dry_run_reports_the_highest_risk_tier(string sql, string expectedTier)
    {
        var run = await Query(SqlServer, sql);

        AssertAllowed(run, "query");
        Assert.Equal(expectedTier, Report(run.Output).GetProperty("tier").GetString());
    }

    [Fact]
    public async Task Measure_and_compare_dry_runs_report_the_maximum_tier_from_setup_and_measured_sql()
    {
        var measure = await Measure(SqlServer, "SELECT 1 AS A", "CREATE TABLE #t (Id int);");
        var compare = await Compare(SqlServer, "SELECT 1 AS A", "SELECT 2 AS A", "CREATE TABLE #t (Id int);");

        AssertAllowed(measure, "measure");
        Assert.Equal("session", Report(measure.Output).GetProperty("tier").GetString());
        AssertAllowed(compare, "compare");
        Assert.Equal("session", Report(compare.Output).GetProperty("tier").GetString());
    }

    [Theory]
    [InlineData("CREATE TABLE #baseline (Id int);", "SELECT 2 AS A")]
    [InlineData("SELECT 1 AS A", "CREATE TABLE #candidate (Id int);")]
    public async Task Compare_dry_run_includes_session_tier_from_each_measured_side(string baseline, string candidate)
    {
        var run = await Compare(SqlServer, baseline, candidate, null);

        AssertAllowed(run, "compare");
        Assert.Equal("session", Report(run.Output).GetProperty("tier").GetString());
    }

    [Theory]
    [InlineData("CREATE TABLE #baseline (Id int); SELECT @id;", "SELECT @id;")]
    [InlineData("SELECT @id;", "CREATE TABLE #candidate (Id int); SELECT @id;")]
    public async Task Compare_matrix_dry_run_includes_each_measured_side_tier(string baseline, string candidate)
    {
        var run = await Compare(
            SqlServer,
            baseline,
            candidate,
            null,
            "--matrix",
            "id:int=1,2");

        AssertAllowed(run, "compare");
        Assert.Equal("session", Report(run.Output).GetProperty("tier").GetString());
    }

    [Fact]
    public async Task Measure_dry_run_includes_the_measured_query_tier()
    {
        var run = await Measure(SqlServer, "CREATE TABLE #t (Id int); SELECT 1 AS A;", "SELECT 1 AS SetupOnly;");

        AssertAllowed(run, "measure");
        Assert.Equal("session", Report(run.Output).GetProperty("tier").GetString());
    }

    [Fact]
    public async Task Query_dry_run_renders_a_text_summary_in_text_mode()
    {
        var run = await Run("query", SqlServer, "--file", Sql("SELECT 1;"), "--dry-run");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Dry run", run.Output, StringComparison.Ordinal);
        Assert.Contains("query", run.Output, StringComparison.Ordinal);
        Assert.Contains("read-only", run.Output, StringComparison.Ordinal);
        Assert.Contains("tier: read-only", run.Output, StringComparison.Ordinal);
        AssertOffline();
    }

    [Fact]
    public async Task Query_dry_run_agent_output_carries_the_report_in_the_envelope()
    {
        var run = await Run("query", SqlServer, "--file", Sql("SELECT 1;"), "--dry-run", "--output", "agent");

        Assert.Equal(0, run.Exit);
        var envelope = Report(run.Output);
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.Equal("query", envelope.GetProperty("result").GetProperty("command").GetString());
        AssertOffline();
    }

    [Fact]
    public async Task Query_dry_run_reads_redirected_stdin_like_a_real_query()
    {
        var output = new StringWriter();
        var module = new SqlHarnessModule(_factory, Profiles);

        var exit = await SqlHarnessCli.Create(module, output, new StringReader("SELECT 1;"), stdinRedirected: true)
            .RunAsync(["query", SqlServer, "--dry-run", "--json"]);

        Assert.Equal(0, exit);
        Assert.Equal("query", Report(output.ToString()).GetProperty("command").GetString());
        AssertOffline();
    }

    [Fact]
    public async Task Without_the_switch_the_same_arguments_still_dispatch_to_the_module()
    {
        var recorder = new RecordingModule();
        var output = new StringWriter();

        var exit = await SqlHarnessCli.Create(recorder, output)
            .RunAsync(["query", SqlServer, "--file", Sql("SELECT 1;"), "--json"]);

        Assert.Equal(0, exit);
        Assert.False(Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(recorder.Operations)).DryRun);
    }

    [Fact]
    public async Task Dry_run_switch_sets_DryRun_on_the_operation_each_command_builds()
    {
        var recorder = new RecordingModule();
        var query = Sql("SELECT 1;");

        foreach (var args in new[]
        {
            new[] { "query", SqlServer, "--file", query, "--dry-run", "--json" },
            ["measure", SqlServer, "--query", query, "--dry-run", "--json"],
            ["compare", SqlServer, "--baseline", query, "--candidate", query, "--subject", "test-query", "--scope", "whole", "--dry-run", "--json"],
            ["compare", SqlServer, "--baseline", query, "--candidate", query, "--subject", "test-query", "--scope", "whole", "--matrix", "id:int=1,2", "--dry-run", "--json"],
        })
        {
            var exit = await SqlHarnessCli.Create(recorder, new StringWriter()).RunAsync(args);
            Assert.Equal(0, exit);
        }

        Assert.Equal(4, recorder.Operations.Count);
        Assert.True(Assert.IsType<SqlHarnessQueryOperation>(recorder.Operations[0]).DryRun);
        Assert.True(Assert.IsType<SqlHarnessMeasureOperation>(recorder.Operations[1]).DryRun);
        Assert.True(Assert.IsType<SqlHarnessCompareOperation>(recorder.Operations[2]).DryRun);
        Assert.True(Assert.IsType<SqlHarnessCompareMatrixOperation>(recorder.Operations[3]).DryRun);
    }

    [Fact]
    public async Task Dry_run_never_writes_local_state()
    {
        var original = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        var home = Path.Combine(_directory, "home");
        Directory.CreateDirectory(home);
        try
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", home);
            var query = Sql("SELECT @id;");
            var before = Files(_directory);

            Assert.Equal(0, (await Run("query", SqlServer, "--file", query, "--param", "id:int=42", "--dry-run", "--json")).Exit);
            Assert.Equal(0, (await Run("measure", SqlServer, "--query", query, "--param", "id:int=42", "--dry-run", "--json")).Exit);
            Assert.Equal(0, (await Run("compare", SqlServer, "--baseline", query, "--candidate", query, "--subject", "test-query", "--scope", "whole", "--param", "id:int=42", "--dry-run", "--json")).Exit);

            Assert.Equal(before, Files(_directory));
            AssertOffline();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", original);
        }
    }

    private static string[] Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    // ---- CLI report is the Core report MCP receives ----------------------------------------

    [Fact]
    public async Task Query_dry_run_json_equals_the_core_report_for_the_operation_mcp_maps()
    {
        var run = await Query(SqlServer, "SELECT @a, @b;", "--param", "a:nvarchar=x", "--param", "b:int=7");

        // MCP maps typed parameters to SqlHarnessParameterInput and sets DryRun on the Core operation.
        var mcpShaped = new SqlHarnessQueryOperation(
            new SqlTargetRequest(SqlServer, new Dictionary<string, string>()), "SELECT @a, @b;", [], 30, 50, false, null)
        {
            TypedParameters = [new("a", "nvarchar", "x"), new("b", "int", "7")],
            DryRun = true,
        };
        var core = await new SqlHarnessModule(_factory, Profiles).ExecuteAsync(mcpShaped);

        Assert.Equal(0, run.Exit);
        AssertSameJson(core.Report!, run.Output);
    }

    [Fact]
    public async Task Query_dry_run_classifies_a_TVP_source_and_reports_offline_shape_note()
    {
        var operation = new SqlHarnessQueryOperation(
            new SqlTargetRequest(SqlServer, new Dictionary<string, string>()),
            "SELECT * FROM @Ids",
            [], 30, 50, false, null)
        {
            TypedParameters =
            [
                new SqlHarnessParameterInput("Ids", "dbo.IdList", null)
                {
                    Kind = "tvp",
                    Rows = [["1"], ["2"]],
                },
            ],
            DryRun = true,
        };

        var outcome = await new SqlHarnessModule(_factory, Profiles).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessDryRunReport>(outcome.Report);
        Assert.Equal("read-only", report.Classification);
        Assert.Contains("Table-valued parameter '@Ids': table type shape not verified offline.", report.Notes);
        AssertOffline();
    }

    [Fact]
    public async Task Query_params_json_round_trips_typed_table_input_through_cli_dry_run()
    {
        var parameterPath = Path.Combine(_directory, "fixed.sqljson");
        await File.WriteAllTextAsync(parameterPath,
            "{\"parameters\":[{\"name\":\"Ids\",\"type\":\"dbo.IdList\",\"kind\":\"tvp\",\"rows\":[[\"1\"],[\"2\"]]}]}");
        var output = new StringWriter();
        var module = new SqlHarnessModule(_factory, Profiles);

        var exit = await SqlHarnessCli.Create(module, output).RunAsync(
            ["query", SqlServer, "--file", Sql("SELECT * FROM @Ids"), "--dry-run", "--json", "--params-json", parameterPath]);

        Assert.Equal(0, exit);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("read-only", report.RootElement.GetProperty("classification").GetString());
        Assert.Contains(
            "Table-valued parameter '@Ids': table type shape not verified offline.",
            report.RootElement.GetProperty("notes").EnumerateArray().Select(item => item.GetString()));
        AssertOffline();
    }

    [Fact]
    public async Task Query_dry_run_rejects_TVP_without_exactly_one_source()
    {
        var operation = new SqlHarnessQueryOperation(
            new SqlTargetRequest(SqlServer, new Dictionary<string, string>()),
            "SELECT * FROM @Ids",
            [], 30, 50, false, null)
        {
            TypedParameters = [new SqlHarnessParameterInput("Ids", "dbo.IdList", null) { Kind = "tvp" }],
            DryRun = true,
        };

        var outcome = await new SqlHarnessModule(_factory, Profiles).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("Table-valued SQL parameter '@Ids' requires exactly one of rows or rowsFrom.", outcome.SafeError);
        AssertOffline();
    }

    [Fact]
    public async Task Compare_dry_run_json_equals_the_core_report_for_the_operation_mcp_maps()
    {
        var run = await Compare(Postgres, "SELECT @a::int;", "SELECT @a::int;", "CREATE TEMP TABLE t (x int);", "--param", "a:int=1");

        var mcpShaped = new SqlHarnessCompareOperation(
            new SqlTargetRequest(Postgres, new Dictionary<string, string>()), "CREATE TEMP TABLE t (x int);",
            "SELECT @a::int;", "SELECT @a::int;", [], 30, 5)
        {
            TypedParameters = [new("a", "int", "1")],
            DryRun = true,
            Context = new CompareContext("test-query", "whole"),
        };
        var core = await new SqlHarnessModule(_factory, Profiles).ExecuteAsync(mcpShaped);

        Assert.Equal(0, run.Exit);
        AssertSameJson(core.Report!, run.Output);
    }

    private static void AssertSameJson(object coreReport, string cliJson)
    {
        var expected = JsonSerializer.SerializeToNode(coreReport, coreReport.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected!.ToJsonString(), JsonNode.Parse(cliJson)!.ToJsonString());
    }

    // ---- typed values bind whole ------------------------------------------------------------

    [Theory]
    [InlineData(SqlServer, "a,b")]
    [InlineData(SqlServer, "a=b:c")]
    [InlineData(SqlServer, "")]
    [InlineData(Postgres, "a,b")]
    [InlineData(Postgres, "a=b:c")]
    [InlineData(Postgres, "")]
    public async Task Query_dry_run_binds_a_text_value_whole(string profile, string value)
    {
        var sql = profile == Postgres ? "SELECT @v::text;" : "SELECT @v;";

        var run = await Query(profile, sql, "--param", "v:nvarchar=" + value);

        AssertAllowed(run, "query");
        var parameter = Assert.Single(Report(run.Output).GetProperty("parameters").EnumerateArray());
        Assert.Equal("@v", parameter.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(SqlServer)]
    [InlineData(Postgres)]
    public async Task Query_dry_run_accepts_a_typed_null(string profile)
    {
        var sql = profile == Postgres ? "SELECT @v::int;" : "SELECT @v;";

        var run = await Query(profile, sql, "--param", "v:int:null");

        AssertAllowed(run, "query");
        var parameter = Assert.Single(Report(run.Output).GetProperty("parameters").EnumerateArray());
        Assert.Equal("int", parameter.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Measure_and_compare_dry_run_bind_values_with_separators_whole()
    {
        var measure = await Measure(SqlServer, "SELECT @a, @b, @c;", null,
            "--param", "a:nvarchar=a,b", "--param", "b:nvarchar=a=b:c", "--param", "c:nvarchar=");
        var compare = await Compare(Postgres, "SELECT @a::text, @b::text;", "SELECT @a::text, @b::text;", null,
            "--param", "a:nvarchar=a,b", "--param", "b:nvarchar=a=b:c");

        AssertAllowed(measure, "measure");
        AssertAllowed(compare, "compare");
        Assert.Equal(3, Report(measure.Output).GetProperty("parameters").GetArrayLength());
        Assert.Equal(2, Report(compare.Output).GetProperty("parameters").GetArrayLength());
    }

    [Fact]
    public async Task A_comma_inside_an_int_value_is_one_invalid_value_not_two_values()
    {
        var run = await Query(SqlServer, "SELECT @id;", "--param", "id:int=1,2");

        AssertRejected(run, "Invalid value", "'id'");
    }

    // ---- query: classifier cases ported from validate ---------------------------------------

    [Fact]
    public async Task Sql_server_parameterized_select_is_allowed_without_echoing_values()
    {
        var run = await Query(SqlServer, "SELECT @id;", "--param", "id:nvarchar=secret-value");

        AssertAllowed(run, "query");
        Assert.DoesNotContain("secret-value", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_cast_syntax_is_allowed()
    {
        var run = await Query(Postgres, "SELECT @id::int LIMIT 1;", "--param", "id:int=42");

        AssertAllowed(run, "query");
    }

    [Theory]
    [InlineData(SqlServer, "DELETE FROM dbo.PrivateRecords;")]
    [InlineData(Postgres, "DELETE FROM public.private_records;")]
    [InlineData(SqlServer, "DELETE FROM dbo.T;")]
    [InlineData(Postgres, "INSERT INTO public.items (a) VALUES (1);")]
    public async Task Persistent_mutation_is_rejected_with_exit_2_without_the_allow_flag(string profile, string sql)
    {
        var run = await Query(profile, sql);

        var message = AssertRejected(run, "SQL safety rejection");
        Assert.DoesNotContain("PrivateRecords", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private_records", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persistent_mutation_dry_run_is_rejected_with_the_mutation_command_hint()
    {
        var run = await Query(SqlServer, "DELETE FROM dbo.T;");

        AssertRejected(run, "Persistent changes use `sqlharness mutation`.");
    }

    [Fact]
    public async Task Query_rejects_removed_mutation_options()
    {
        var run = await Query(SqlServer, "DELETE FROM dbo.T;", "--allow-mutation");

        Assert.Equal((int)SqlHarnessExitCode.Safety, run.Exit);
        Assert.Contains("Invalid command line arguments", run.Output, StringComparison.Ordinal);
        AssertOffline();
    }

    [Fact]
    public async Task Postgres_parse_error_is_rejected()
    {
        var run = await Query(Postgres, "SELECT FROM WHERE @id");

        AssertRejected(run, "SQL safety rejection");
    }

    [Fact]
    public async Task Rejected_mutation_with_a_supplied_parameter_reports_the_mutation_not_a_missing_parameter()
    {
        var run = await Query(SqlServer, "DELETE FROM dbo.PrivateRecords WHERE Id = @id;", "--param", "id:int=42");

        var message = AssertRejected(run, "SQL safety rejection");
        Assert.DoesNotContain("missing", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bad_parameter_name_is_named_in_the_rejection()
    {
        var run = await Query(SqlServer, "SELECT @Name AS N;", "--param", "1bad:int=1");

        AssertRejected(run, "'1bad'");
    }

    [Fact]
    public async Task Bad_parameter_value_matching_the_profile_password_is_redacted()
    {
        const string password = "profile-password-used-as-bad-int-value";
        var previous = Environment.GetEnvironmentVariable(PasswordEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PasswordEnvVar, password);

            var run = await Query(Postgres, "SELECT @Name::int AS N;", "--param", "Name:int=" + password);

            var message = AssertRejected(run, "Invalid value '[REDACTED]'", "'Name'", "'int'");
            Assert.DoesNotContain(password, run.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(password, message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PasswordEnvVar, previous);
        }
    }

    [Theory]
    [InlineData(SqlServer, "SELECT @id;", true, "id:int=42")]
    [InlineData(Postgres, "SELECT @id::int LIMIT 1;", true, "id:int=42")]
    [InlineData(SqlServer, "DELETE FROM dbo.PrivateRecords;", false, "")]
    [InlineData(Postgres, "DELETE FROM public.private_records;", false, "")]
    public async Task Dry_run_verdict_matches_the_engine_preflight_classification(string profile, string sql, bool allowed, string parameter)
    {
        var run = parameter.Length == 0 ? await Query(profile, sql) : await Query(profile, sql, "--param", parameter);

        if (allowed)
            AssertAllowed(run, "query");
        else
            AssertRejected(run, "SQL safety rejection");
    }

    [Theory]
    [InlineData(SqlServer, "SELECT @id;")]
    [InlineData(Postgres, "SELECT @id::int;")]
    public async Task Missing_parameter_is_rejected_by_name_without_values(string profile, string sql)
    {
        var run = await Query(profile, sql);

        var message = AssertRejected(run, "missing_parameters", "id");
        Assert.Equal("safety_rejected", Report(run.Output).GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("@id", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_parameters_are_listed_sorted_and_only_the_unsupplied_ones()
    {
        var run = await Query(SqlServer, "SELECT @b, @a, @c;", "--param", "c:int=1");

        var message = AssertRejected(run, "missing_parameters");
        Assert.EndsWith("a, b.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leading_at_sign_on_a_supplied_name_still_counts_as_supplied()
    {
        var run = await Query(SqlServer, "SELECT @id;", "--param", "@id:int=1");

        AssertAllowed(run, "query");
    }

    [Fact]
    public async Task Batch_local_declare_is_not_reported_missing()
    {
        var run = await Measure(SqlServer, "DECLARE @x int = 1; SELECT @x;", null);

        AssertAllowed(run, "measure");
    }

    [Fact]
    public async Task Measure_missing_parameter_is_rejected_and_setup_only_references_are_not_required()
    {
        var missing = await Measure(SqlServer, "SELECT @id;", "SELECT 1;");
        var setupOnly = await Measure(SqlServer, "SELECT 1;", "SELECT @setupOnly;");

        AssertRejected(missing, "missing_parameters", "id");
        AssertAllowed(setupOnly, "measure");
    }

    [Fact]
    public async Task Compare_missing_parameter_in_either_variant_is_rejected()
    {
        var baseline = await Compare(SqlServer, "SELECT @id;", "SELECT 1;", null);
        var candidate = await Compare(Postgres, "SELECT 1;", "SELECT @id::int;", null);

        AssertRejected(baseline, "missing_parameters", "id");
        AssertRejected(candidate, "missing_parameters", "id");
    }

    [Fact]
    public async Task Compare_dry_run_reports_each_classification_without_execution()
    {
        var run = await Compare(SqlServer,
            "SELECT 1;",
            "CREATE TABLE #candidate (x int);",
            "CREATE TABLE #setup (x int);");

        AssertAllowed(run, "compare");
        var report = Report(run.Output);
        Assert.Equal("passed", report.GetProperty("validation").GetString());
        Assert.Equal("not-run", report.GetProperty("execution").GetString());
        Assert.Equal("session", report.GetProperty("tier").GetString());
        var classification = report.GetProperty("compareClassification");
        Assert.Equal("session-local", classification.GetProperty("setup").GetString());
        Assert.Equal("read-only", classification.GetProperty("baseline").GetString());
        Assert.Equal("session-local", classification.GetProperty("candidate").GetString());
    }

    [Fact]
    public async Task Matrix_and_parameter_set_names_count_as_supplied()
    {
        var small = Path.Combine(_directory, "s1.json");
        var large = Path.Combine(_directory, "s2.json");
        File.WriteAllText(small, "{\"name\":\"small\",\"parameters\":[\"id:int=1\"]}");
        File.WriteAllText(large, "{\"name\":\"large\",\"parameters\":[\"id:int=2\"]}");

        var matrix = await Compare(SqlServer, "SELECT @id;", "SELECT @id;", null, "--matrix", "id:int=1,2");
        var sets = await Measure(SqlServer, "SELECT @id;", null, "--param-set", small, "--param-set", large);
        var setsMissing = await Measure(SqlServer, "SELECT @id, @other;", null, "--param-set", small, "--param-set", large);

        AssertAllowed(matrix, "compare");
        AssertAllowed(sets, "measure");
        AssertRejected(setsMissing, "missing_parameters", "other");
    }

    [Theory]
    [InlineData(SqlServer, "SELECT @note;", "note:nvarchar=synthetic-note-7x9", "synthetic-note-7x9")]
    [InlineData(Postgres, "SELECT @id::int;", "id:int=42137", "42137")]
    public async Task Dry_run_json_never_echoes_sql_or_values(string profile, string sql, string declaration, string value)
    {
        var run = await Query(profile, sql, "--param", declaration);

        AssertAllowed(run, "query");
        Assert.DoesNotContain(sql, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(value, run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Postgres, "SELECT set_config('search_path', 'public', false);")]
    [InlineData(Postgres, "SELECT pg_cancel_backend(123);")]
    [InlineData(Postgres, "SELECT pg_terminate_backend(123);")]
    [InlineData(Postgres, "SELECT pg_sleep(1);")]
    [InlineData(Postgres, "SELECT lo_import('/tmp/x');")]
    [InlineData(Postgres, "SELECT pg_read_file('pg_hba.conf');")]
    [InlineData(Postgres, "SELECT * FROM pg_ls_dir('.');")]
    [InlineData(Postgres, "SELECT * FROM dblink('c', 'SELECT 1') AS t(x int);")]
    [InlineData(Postgres, "SELECT pg_advisory_lock(1);")]
    [InlineData(Postgres, "SELECT pg_try_advisory_lock(1);")]
    [InlineData(Postgres, "SELECT SET_CONFIG('a', 'b', false);")]
    [InlineData(Postgres, "SELECT length(set_config('a', 'b', false));")]
    [InlineData(SqlServer, "EXEC sp_executesql N'SELECT 1';")]
    [InlineData(SqlServer, "EXEC xp_cmdshell 'dir';")]
    [InlineData(SqlServer, "SELECT * FROM OPENROWSET('SQLOLEDB', 's', 'SELECT 1') AS t(x int);")]
    public async Task Denylisted_functions_are_rejected_offline_without_echoing_the_sql(string profile, string sql)
    {
        var run = await Query(profile, sql);

        AssertRejected(run, "SQL safety rejection");
        Assert.DoesNotContain(sql, run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT nextval('seq');")]
    [InlineData("SELECT setval('seq', 1);")]
    [InlineData("SELECT pg_catalog.setval('s', 1);")]
    public async Task Postgres_sequence_functions_are_accepted(string sql)
    {
        var run = await Query(Postgres, sql);

        AssertAllowed(run, "query");
    }

    [Fact]
    public async Task Two_nonempty_go_batches_are_rejected_with_the_classifier_sentence_and_no_sql()
    {
        const string sql = "SELECT 1\nGO\nSELECT 2";

        var run = await Query(SqlServer, sql);

        AssertRejected(run, "Batch separators (GO) are not supported; send a single batch.");
        Assert.DoesNotContain("SELECT 2", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_multi_statement_batch_is_allowed_for_query()
    {
        var run = await Query(Postgres, "SELECT 1; SELECT 2;");

        AssertAllowed(run, "query");
    }

    [Fact]
    public async Task Postgres_temp_setup_is_needed_for_a_query_that_touches_the_temp_table()
    {
        var run = await Query(Postgres, "CREATE INDEX ix ON meas (x);");

        AssertRejected(run, "SQL safety rejection");
    }

    [Fact]
    public async Task Sql_server_inline_temp_table_batch_is_session_local_and_allowed()
    {
        var run = await Query(SqlServer, "CREATE TABLE #m (x int); SELECT x FROM #m;");

        AssertAllowed(run, "query");
        Assert.Equal("session-local", Report(run.Output).GetProperty("classification").GetString());
    }

    // ---- query: input handling --------------------------------------------------------------

    [Fact]
    public async Task Missing_sql_file_is_input_file_unavailable_with_exit_2()
    {
        var run = await Run("query", SqlServer, "--file", Path.Combine(_directory, "absent.sql"), "--dry-run", "--json");

        Assert.Equal((int)SqlHarnessExitCode.Safety, run.Exit);
        Assert.Equal("input_file_unavailable", Report(run.Output).GetProperty("error").GetProperty("code").GetString());
        AssertOffline();
    }

    [Fact]
    public async Task Sql_file_over_the_input_limit_is_input_too_large_with_exit_2()
    {
        var path = Sql(new string('x', (int)SqlInputReader.MaxSqlInputUtf8Bytes + 1));

        var run = await Run("query", SqlServer, "--file", path, "--dry-run", "--json");

        Assert.Equal((int)SqlHarnessExitCode.Safety, run.Exit);
        Assert.Equal("input_too_large", Report(run.Output).GetProperty("error").GetProperty("code").GetString());
        AssertOffline();
    }

    [Fact]
    public async Task Sql_file_at_the_exact_input_limit_reaches_the_classifier()
    {
        var path = Sql(new string('x', (int)SqlInputReader.MaxSqlInputUtf8Bytes));

        var run = await Run("query", SqlServer, "--file", path, "--dry-run", "--json");

        // The bound let the input through: the classifier judged the content, not the size.
        Assert.Equal((int)SqlHarnessExitCode.Safety, run.Exit);
        Assert.NotEqual("input_too_large", Report(run.Output).GetProperty("error").GetProperty("code").GetString());
        Assert.NotEqual(DryRunMissing, Report(run.Output).GetProperty("error").GetProperty("message").GetString());
        AssertOffline();
    }

    // ---- measure: setup and benchmark rules ported from validate ----------------------------

    [Fact]
    public async Task Measure_dry_run_rejects_a_persistent_mutation_setup_with_exit_2()
    {
        var run = await Measure(SqlServer, "SELECT 1;", "DELETE FROM dbo.T;");

        AssertRejected(run, "SQL safety rejection");
    }

    [Theory]
    [InlineData(SqlServer, "DELETE FROM dbo.T;")]
    [InlineData(Postgres, "DELETE FROM public.items;")]
    public async Task Invalid_setup_rejects_the_measure_and_compare_with_the_same_message(string profile, string setup)
    {
        var measure = await Measure(profile, "SELECT 1;", setup);
        var compare = await Compare(profile, "SELECT 1;", "SELECT 1;", setup);

        var measureMessage = AssertRejected(measure, "SQL safety rejection");
        var compareMessage = AssertRejected(compare, "SQL safety rejection");
        Assert.Equal(measureMessage, compareMessage);
        Assert.DoesNotContain("SELECT 1", measure.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_temp_setup_flows_into_the_measured_query()
    {
        var withSetup = await Measure(Postgres, "SELECT x FROM meas;", "CREATE TEMP TABLE meas (x int);");
        var setupAlone = await Measure(Postgres, "SELECT 1;", "CREATE TEMP TABLE meas (x int);");

        AssertAllowed(withSetup, "measure");
        AssertAllowed(setupAlone, "measure");
    }

    [Fact]
    public async Task Sql_server_temp_setup_flows_into_the_measured_query()
    {
        var run = await Measure(SqlServer, "SELECT x FROM #m;", "CREATE TABLE #m (x int);");

        AssertAllowed(run, "measure");
    }

    [Fact]
    public async Task Postgres_multi_statement_batch_is_rejected_for_measure_and_compare_but_allowed_for_query()
    {
        const string batch = "SELECT 1; SELECT 2;";

        var query = await Query(Postgres, batch);
        var measure = await Measure(Postgres, batch, null);
        var compare = await Compare(Postgres, batch, "SELECT 1;", null);

        AssertAllowed(query, "query");
        AssertRejected(measure);
        AssertRejected(compare);
    }

    [Fact]
    public async Task Sql_server_multi_statement_batch_stays_allowed_for_measure()
    {
        var run = await Measure(SqlServer, "SELECT 1; SELECT 2;", null);

        AssertAllowed(run, "measure");
    }

    [Fact]
    public async Task Sql_server_setup_shape_rejection_keeps_the_actionable_detail_without_leaking_sql()
    {
        const string setup = "SELECT @id; CREATE TABLE #t (Id int);";

        var run = await Measure(SqlServer, "SELECT Id FROM #t;", setup, "--param", "id:int=42");

        var message = AssertRejected(run, "session-local temp table", "before any statement that references a parameter");
        Assert.DoesNotContain(setup, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("#t", message, StringComparison.Ordinal);
        Assert.DoesNotContain("@id", message, StringComparison.Ordinal);
        Assert.DoesNotContain("42", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_only_parameter_reference_is_allowed_when_the_batch_needs_no_parameter()
    {
        var run = await Measure(SqlServer, "SELECT 1;", "SELECT @setupOnly;");

        AssertAllowed(run, "measure");
    }

    [Fact]
    public async Task Setup_declared_scalar_referenced_by_the_batch_is_rejected_without_echoing_names_or_values()
    {
        var run = await Measure(
            SqlServer, "SELECT @privateSetupValue;", "DECLARE @privateSetupValue int = 73195; SELECT @privateSetupValue;");

        var message = AssertRejected(
            run, "A variable declared in setup is referenced by a benchmark batch. Setup variables do not cross into benchmark batches.");
        Assert.DoesNotContain("73195", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("privateSetupValue", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_declared_scalar_is_allowed_when_the_batch_binds_a_typed_parameter()
    {
        var run = await Measure(
            SqlServer, "SELECT @inputValue;", "DECLARE @inputValue int = 2; SELECT @inputValue;", "--param", "inputValue:int=19");

        AssertAllowed(run, "measure");
    }

    [Fact]
    public async Task Batch_local_declaration_shadows_a_setup_declared_name()
    {
        var run = await Measure(
            SqlServer, "DECLARE @localValue int = 2; SELECT @localValue;", "DECLARE @localValue int = 1; SELECT @localValue;");

        AssertAllowed(run, "measure");
    }

    [Fact]
    public async Task Setup_declared_scalar_referenced_before_a_late_batch_declaration_is_rejected()
    {
        var run = await Measure(
            SqlServer, "SELECT @sharedValue; DECLARE @sharedValue int = 2;", "DECLARE @sharedValue int = 1; SELECT @sharedValue;");

        AssertRejected(
            run, "A variable declared in setup is referenced by a benchmark batch. Setup variables do not cross into benchmark batches.");
    }

    [Theory]
    [InlineData("DECLARE @v int; SET @v = 2;", false, true)]
    [InlineData("CREATE TABLE #t (Id int); DECLARE @v int = @id; SET @v = @v + 2; INSERT #t VALUES (@v);", true, true)]
    [InlineData("DECLARE @v int; CREATE TABLE #t (Id int); SET @v = @id; INSERT #t VALUES (@v);", true, false)]
    [InlineData("SET @v = 2;", false, false)]
    [InlineData("DECLARE @v int; SET @v += 2;", false, false)]
    public async Task Setup_scalar_SET_agrees_with_execution_preparation(string setup, bool parameterized, bool expectedAllowed)
    {
        var run = parameterized
            ? await Measure(SqlServer, "SELECT @id;", setup, "--param", "id:int=42")
            : await Measure(SqlServer, "SELECT 1;", setup);

        if (expectedAllowed)
            AssertAllowed(run, "measure");
        else
            AssertRejected(run);
    }

    [Fact]
    public async Task Missing_setup_file_is_input_file_unavailable_with_exit_2()
    {
        var run = await Run("measure", SqlServer, "--query", Sql("SELECT 1;"), "--setup", Path.Combine(_directory, "no-such-setup.sql"), "--dry-run", "--json");

        Assert.Equal((int)SqlHarnessExitCode.Safety, run.Exit);
        Assert.Equal("input_file_unavailable", Report(run.Output).GetProperty("error").GetProperty("code").GetString());
        AssertOffline();
    }

    [Fact]
    public async Task Measure_dry_run_binds_every_parameter_set_and_reports_the_count()
    {
        var small = Path.Combine(_directory, "small.json");
        var large = Path.Combine(_directory, "large.json");
        File.WriteAllText(small, "{\"name\":\"small\",\"parameters\":[\"id:int=1\"]}");
        File.WriteAllText(large, "{\"name\":\"large\",\"parameters\":[\"id:int=2\"]}");

        var run = await Measure(SqlServer, "SELECT @id;", null, "--param-set", small, "--param-set", large);

        AssertAllowed(run, "measure");
        Assert.Equal(2, Report(run.Output).GetProperty("parameterSets").GetInt32());
    }

    [Fact]
    public async Task Measure_dry_run_json_summary_carries_the_dry_run_report()
    {
        var run = await Run("measure", SqlServer, "--query", Sql("SELECT 1;"), "--dry-run", "--json-summary");

        AssertAllowed(run, "measure");
    }

    // ---- compare ----------------------------------------------------------------------------

    [Fact]
    public async Task Compare_dry_run_classifies_baseline_and_candidate_without_connecting()
    {
        var allowed = await Compare(SqlServer, "SELECT 1;", "SELECT 2;", null);
        var badBaseline = await Compare(SqlServer, "DELETE FROM dbo.T;", "SELECT 2;", null);
        var badCandidate = await Compare(SqlServer, "SELECT 1;", "DELETE FROM dbo.T;", null);

        AssertAllowed(allowed, "compare");
        AssertRejected(badBaseline, "SQL safety rejection");
        AssertRejected(badCandidate, "SQL safety rejection");
    }

    [Fact]
    public async Task Compare_dry_run_matrix_binds_every_value_without_connecting()
    {
        var allowed = await Compare(SqlServer, "SELECT @id;", "SELECT @id;", null, "--matrix", "id:int=1,2,3");
        var badValue = await Compare(SqlServer, "SELECT @id;", "SELECT @id;", null, "--matrix", "id:int=1,abc");

        AssertAllowed(allowed, "compare");
        AssertRejected(badValue, "--matrix");
    }

    [Fact]
    public async Task Compare_dry_run_json_summary_and_agent_output_carry_the_dry_run_report()
    {
        var summary = await Run("compare", SqlServer, "--baseline", Sql("SELECT 1;", "b.sql"), "--candidate", Sql("SELECT 2;", "c.sql"), "--subject", "test-query", "--scope", "whole", "--dry-run", "--json-summary");
        var agent = await Run("compare", SqlServer, "--baseline", Sql("SELECT 1;", "b.sql"), "--candidate", Sql("SELECT 2;", "c.sql"), "--subject", "test-query", "--scope", "whole", "--dry-run", "--output", "agent");

        AssertAllowed(summary, "compare");
        Assert.Equal(0, agent.Exit);
        Assert.Equal("compare", Report(agent.Output).GetProperty("result").GetProperty("command").GetString());
    }

    // ---- helpers ----------------------------------------------------------------------------

    private sealed class NoConnectionFactory : ISqlSessionFactory
    {
        public int ConnectAttempts { get; private set; }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectAttempts++;
            throw new InvalidOperationException("A dry run must never open a connection.");
        }
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
