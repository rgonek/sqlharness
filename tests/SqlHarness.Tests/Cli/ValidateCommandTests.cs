using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Cli;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class ValidateCommandTests
{
    [Fact]
    public void Sql_server_validation_uses_the_preflight_classifier_and_reports_ast_locations_without_values()
    {
        var profile = Profile("sqlserver");
        var result = SqlValidation.Validate(
            new SqlTargetRequest("test", new Dictionary<string, string>()),
            "SELECT @id;",
            ["id:nvarchar=secret-value"],
            new Dictionary<string, TargetProfile> { ["test"] = profile });

        Assert.True(result.Allowed, JsonSerializer.Serialize(result));
        Assert.Equal("sqlserver", result.Engine);
        Assert.Equal(["id"], result.RequiredParameters);
        Assert.Empty(result.MissingParameters);
        Assert.NotEmpty(result.AstLocations);
        Assert.True(result.AstLocationsAvailable);
        Assert.False(result.Executed);
        Assert.DoesNotContain("secret-value", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_validation_accepts_cast_syntax_and_reports_no_connection_or_object_claims()
    {
        var profile = Profile("postgres");
        var result = SqlValidation.Validate(
            new SqlTargetRequest("test", new Dictionary<string, string>()),
            "SELECT @id::int LIMIT 1;",
            ["id:int=42"],
            new Dictionary<string, TargetProfile> { ["test"] = profile });

        Assert.True(result.Allowed);
        Assert.Equal("postgres", result.Engine);
        Assert.Equal("unknown", result.ObjectAndPermissionStatus);
        Assert.False(result.Executed);
    }

    [Fact]
    public void Sql_server_mutation_is_a_successful_offline_classification_with_a_safe_rejection_reason()
    {
        var profile = Profile("sqlserver");
        var result = SqlValidation.Validate(
            new SqlTargetRequest("test", new Dictionary<string, string>()),
            "DELETE FROM dbo.PrivateRecords;",
            [],
            new Dictionary<string, TargetProfile> { ["test"] = profile });

        Assert.False(result.Allowed);
        Assert.Equal("mutation_not_allowed", result.Reason);
        Assert.Equal("rejected", result.Classification);
        Assert.DoesNotContain("PrivateRecords", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.False(result.Executed);
    }

    [Fact]
    public void Postgres_parse_rejection_returns_a_structured_validation_report()
    {
        var result = SqlValidation.Validate(
            new SqlTargetRequest("test", new Dictionary<string, string>()),
            "SELECT FROM WHERE @id",
            [],
            new Dictionary<string, TargetProfile> { ["test"] = Profile("postgres") });

        Assert.False(result.Allowed);
        Assert.Equal("sql_parse_error", result.Reason);
        Assert.Empty(result.RequiredParameters);
        Assert.Empty(result.MissingParameters);
        Assert.False(result.Executed);
    }

    [Fact]
    public void Rejected_mutation_does_not_claim_supplied_parameters_are_missing()
    {
        var result = SqlValidation.Validate(
            new SqlTargetRequest("test", new Dictionary<string, string>()),
            "DELETE FROM dbo.PrivateRecords WHERE Id = @id;",
            ["id:int=42"],
            new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") });

        Assert.False(result.Allowed);
        Assert.Equal("mutation_not_allowed", result.Reason);
        Assert.Equal(["id"], result.RequiredParameters);
        Assert.Empty(result.MissingParameters);
    }

    [Theory]
    [InlineData("sqlserver", "SELECT @id;", "id:int=42", true)]
    [InlineData("postgres", "SELECT @id::int LIMIT 1;", "id:int=42", true)]
    [InlineData("sqlserver", "DELETE FROM dbo.PrivateRecords;", "", false)]
    [InlineData("postgres", "DELETE FROM public.private_records;", "", false)]
    public void Offline_validation_matches_engine_preflight_classification(string engine, string sql, string parameter, bool expectedAllowed)
    {
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var resolved = TargetResolver.Resolve(request, profiles);
        var dialect = SqlDialects.For(resolved.Engine);
        var preflight = dialect.Classify(sql, SqlUsage.Query, resolved.Database, allowMutation: false, confirmDatabase: null, new HashSet<string>(StringComparer.Ordinal));
        if (preflight.Allowed)
        {
            var parameters = dialect.ParseParameters(parameter.Length == 0 ? [] : [parameter]);
            dialect.ValidateParameterReferences(parameters, sql);
        }

        var validation = SqlValidation.Validate(request, sql, parameter.Length == 0 ? [] : [parameter], profiles);

        Assert.Equal(expectedAllowed, preflight.Allowed);
        Assert.Equal(preflight.Allowed, validation.Allowed);
    }

    [Fact]
    public async Task Validate_missing_file_returns_json_without_dispatch()
    {
        var module = new RecordingModule();
        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(module, output).RunAsync(["validate", "missing-profile", "--file", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sql"), "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Empty(module.Operations);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("input_file_unavailable", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Validate_file_over_sql_input_limit_returns_input_too_large_without_validation()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, new string('x', (int)SqlInputReader.MaxSqlInputUtf8Bytes + 1));
        try
        {
            var module = new RecordingModule();
            var output = new StringWriter();
            var exitCode = await SqlHarnessCli.Create(module, output).RunAsync(["validate", "test", "--file", path, "--json"]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
            Assert.Empty(module.Operations);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("input_too_large", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Validate_file_at_exact_sql_input_limit_passes_the_bound_to_validation()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, new string('x', (int)SqlInputReader.MaxSqlInputUtf8Bytes));
        try
        {
            var module = new RecordingModule();
            var output = new StringWriter();
            var exitCode = await SqlHarnessCli.Create(module, output).RunAsync(["validate", "test", "--file", path, "--json"]);

            // The bound let the input through: validation ran and reported on the
            // content, not on the size.
            Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.NotEqual("input_too_large", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Postgres_multi_statement_batch_is_rejected_for_benchmark_but_allowed_for_query()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("postgres") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        // 007/T1: PostgresBenchmark.ValidateMeasuredBatch rejects
        // multi-statement batches at execution, so benchmark-mode validation
        // must reject them while query mode keeps allowing them.
        var query = SqlValidation.Validate(request, "SELECT 1; SELECT 2;", [], profiles);
        var benchmark = SqlValidation.Validate(
            request, "SELECT 1; SELECT 2;", [], profiles,
            new ValidationOptions(ValidationUsage.Benchmark));

        Assert.True(query.Allowed);
        Assert.False(benchmark.Allowed);
        Assert.Equal("benchmark_batch_not_supported", benchmark.Reason);
        Assert.Equal("rejected", benchmark.Classification);
        Assert.False(benchmark.Executed);
    }

    [Fact]
    public void Sql_server_multi_statement_batch_stays_allowed_for_benchmark()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        // The SQL Server dialect has no measured-batch shape check (its
        // ValidateMeasuredBatch is a no-op, like the execution path).
        var benchmark = SqlValidation.Validate(
            request, "SELECT 1; SELECT 2;", [], profiles,
            new ValidationOptions(ValidationUsage.Benchmark));

        Assert.True(benchmark.Allowed);
    }

    [Fact]
    public void Postgres_setup_temp_tables_flow_into_query_validation()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("postgres") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        const string setup = "CREATE TEMP TABLE meas (x int);";
        const string query = "CREATE INDEX ix ON meas (x);";

        var setupReport = SqlValidation.Validate(
            request, setup, [], profiles, new ValidationOptions(ValidationUsage.Setup));
        var withoutSetup = SqlValidation.Validate(request, query, [], profiles);
        var withSetup = SqlValidation.Validate(
            request, query, [], profiles,
            new ValidationOptions(ValidationUsage.Query, SetupSql: setup));

        Assert.True(setupReport.Allowed);
        Assert.False(withoutSetup.Allowed);
        Assert.True(withSetup.Allowed);
        Assert.Equal("session-local", withSetup.Classification);
    }

    [Fact]
    public void Sql_server_setup_temp_tables_flow_into_query_validation()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        const string setup = "CREATE TABLE #m (x int);";
        const string query = "SELECT x FROM #m;";

        var setupReport = SqlValidation.Validate(
            request, setup, [], profiles, new ValidationOptions(ValidationUsage.Setup));
        var withSetup = SqlValidation.Validate(
            request, query, [], profiles,
            new ValidationOptions(ValidationUsage.Query, SetupSql: setup));
        // Engine rules: SQL Server accepts #temp syntactically, so the same
        // shape written directly in one query batch is session-local too.
        var inline = SqlValidation.Validate(
            request, setup + " " + query, [], profiles);

        Assert.True(setupReport.Allowed, JsonSerializer.Serialize(setupReport));
        Assert.True(withSetup.Allowed, JsonSerializer.Serialize(withSetup));
        Assert.True(inline.Allowed, JsonSerializer.Serialize(inline));
        Assert.False(withSetup.Executed);
    }

    [Theory]
    [InlineData("sqlserver", "DELETE FROM dbo.T;")]
    [InlineData("postgres", "DELETE FROM public.items;")]
    public void Invalid_setup_context_rejects_query_with_setup_reason(string engine, string setup)
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(
            request, "SELECT 1;", [], profiles,
            new ValidationOptions(ValidationUsage.Query, SetupSql: setup));

        // Dynamic expectation: the same setup batch classified on its own
        // (Setup usage) must yield the reason the query report propagates.
        var setupOnly = SqlValidation.Validate(
            request, setup, [], profiles, new ValidationOptions(ValidationUsage.Setup));

        Assert.False(setupOnly.Allowed, JsonSerializer.Serialize(setupOnly));
        Assert.False(report.Allowed);
        Assert.False(report.Executed);
        Assert.Equal("rejected", report.Classification);
        Assert.NotNull(report.Reason);
        Assert.Equal(setupOnly.Reason, report.Reason);
        Assert.DoesNotContain("SELECT 1", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlserver", "SELECT @id;")]
    [InlineData("postgres", "SELECT @id::int;")]
    public void Missing_parameters_report_names_without_values(string engine, string sql)
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(request, sql, [], profiles);

        Assert.False(report.Allowed);
        Assert.Equal("missing_parameters", report.Reason);
        Assert.Equal(["id"], report.RequiredParameters);
        Assert.Equal(["id"], report.MissingParameters);
        Assert.False(report.Executed);
    }

    [Theory]
    [InlineData("sqlserver", "DELETE FROM dbo.T;")]
    [InlineData("postgres", "INSERT INTO public.items (a) VALUES (1);")]
    public void Persistent_mutation_is_never_authorized_by_validation(string engine, string sql)
    {
        // Validate takes no allow-mutation input by construction: the default
        // query path must refuse persistent writes on both engines.
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(request, sql, [], profiles);

        Assert.False(report.Allowed);
        Assert.Equal("mutation_not_allowed", report.Reason);
        Assert.Equal("rejected", report.Classification);
        Assert.False(report.Executed);
    }

    [Theory]
    [InlineData("sqlserver", "SELECT @note;", "note:nvarchar=synthetic-note-7x9")]
    [InlineData("postgres", "SELECT @id::int;", "id:int=42137")]
    public void Validation_report_json_never_echoes_sql_or_values(string engine, string sql, string declaration)
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(request, sql, [declaration], profiles);

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.False(report.Executed);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(sql, json, StringComparison.Ordinal);
        Assert.DoesNotContain(declaration.Split('=', 2)[1], json, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_only_parameter_reference_is_not_reported_as_required_or_missing()
    {
        // Ledger M1 (007): Required/MissingParameters are collected from the
        // main batch only, so a parameter referenced solely inside SetupSql is
        // reported by neither check. Pinned explicitly instead of faking
        // full-context coverage.
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(
            request, "SELECT 1;", [], profiles,
            new ValidationOptions(ValidationUsage.Query, SetupSql: "SELECT @setupOnly;"));

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.Empty(report.RequiredParameters);
        Assert.Empty(report.MissingParameters);
    }

    private static TargetProfile Profile(string engine) => new(
        "server-unused", "database-unused", new Dictionary<string, string>(), "sql", "user-unused", "MUST_NOT_BE_READ", Engine: engine);

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }

    [Fact]
    public async Task Validate_usage_benchmark_rejects_pg_multi_statement_batch_offline()
    {
        // 007/T3: --usage reaches the shared Core model through the runner.
        using var home = new TempHome();
        var query = home.WriteSql("batch.sql", "SELECT 1; SELECT 2;");
        var module = new RecordingModule();

        var benchmarkOutput = new StringWriter();
        var benchmarkExit = await SqlHarnessCli.Create(module, benchmarkOutput)
            .RunAsync(["validate", TempHome.PgProfile, "--file", query, "--usage", "benchmark", "--json"]);

        var queryOutput = new StringWriter();
        var queryExit = await SqlHarnessCli.Create(module, queryOutput)
            .RunAsync(["validate", TempHome.PgProfile, "--file", query, "--usage", "query", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, benchmarkExit);
        Assert.Equal((int)SqlHarnessExitCode.Success, queryExit);
        Assert.Empty(module.Operations);
        Assert.False(Result(benchmarkOutput.ToString()).GetProperty("allowed").GetBoolean());
        Assert.Equal("benchmark_batch_not_supported", Result(benchmarkOutput.ToString()).GetProperty("reason").GetString());
        Assert.False(Result(benchmarkOutput.ToString()).GetProperty("executed").GetBoolean());
        Assert.True(Result(queryOutput.ToString()).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Validate_default_usage_is_query()
    {
        using var home = new TempHome();
        var query = home.WriteSql("batch.sql", "SELECT 1; SELECT 2;");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.PgProfile, "--file", query, "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        Assert.True(Result(output.ToString()).GetProperty("allowed").GetBoolean());
    }

    [Theory]
    [InlineData("BENCHMARK", false)]
    [InlineData("Setup", true)]
    public async Task Validate_usage_is_case_insensitive(string usage, bool expectedAllowed)
    {
        using var home = new TempHome();
        var query = home.WriteSql("batch.sql", "SELECT 1; SELECT 2;");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.PgProfile, "--file", query, "--usage", usage, "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        Assert.Equal(expectedAllowed, Result(output.ToString()).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Validate_unknown_usage_is_rejected_without_dispatch()
    {
        using var home = new TempHome();
        var query = home.WriteSql("q.sql", "SELECT 1;");
        var module = new RecordingModule();
        var output = new StringWriter();

        // Ledger minor M2 (007): unknown --usage fails like other invalid
        // inputs with exit 2, never reaching validation.
        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.Profile, "--file", query, "--usage", "execute", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Empty(module.Operations);
    }

    [Fact]
    public async Task Validate_setup_file_provides_session_temp_context()
    {
        using var home = new TempHome();
        var setup = home.WriteSql("setup.sql", "CREATE TEMP TABLE meas (x int);");
        var query = home.WriteSql("q.sql", "CREATE INDEX ix ON meas (x);");
        var module = new RecordingModule();

        var withSetupOutput = new StringWriter();
        var withSetupExit = await SqlHarnessCli.Create(module, withSetupOutput)
            .RunAsync(["validate", TempHome.PgProfile, "--file", query, "--setup", setup, "--json"]);

        var withoutSetupOutput = new StringWriter();
        var withoutSetupExit = await SqlHarnessCli.Create(module, withoutSetupOutput)
            .RunAsync(["validate", TempHome.PgProfile, "--file", query, "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, withSetupExit);
        Assert.Equal((int)SqlHarnessExitCode.Success, withoutSetupExit);
        Assert.Empty(module.Operations);
        Assert.True(Result(withSetupOutput.ToString()).GetProperty("allowed").GetBoolean());
        Assert.False(Result(withoutSetupOutput.ToString()).GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Validate_setup_usage_classifies_the_batch_as_setup()
    {
        using var home = new TempHome();
        var setup = home.WriteSql("setup.sql", "CREATE TEMP TABLE meas (x int);");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.PgProfile, "--file", setup, "--usage", "setup", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        var result = Result(output.ToString());
        Assert.True(result.GetProperty("allowed").GetBoolean());
        Assert.Equal("session-local", result.GetProperty("classification").GetString());
    }

    [Fact]
    public async Task Validate_missing_setup_file_returns_json_without_dispatch()
    {
        using var home = new TempHome();
        var query = home.WriteSql("q.sql", "SELECT 1;");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.Profile, "--file", query, "--setup", Path.Combine(home.Path, "no-such-setup.sql"), "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Empty(module.Operations);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("input_file_unavailable", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static JsonElement Result(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TempHome : IDisposable
    {
        public const string Profile = "t3-cli";
        public const string PgProfile = "t3-cli-pg";

        private readonly string? _original = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-validate-t3-" + Guid.NewGuid().ToString("N"));

        public TempHome()
        {
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", Path);
            File.WriteAllText(
                System.IO.Path.Combine(Path, "targets.json"),
                "{\"t3-cli\": {\"server\": \"t3.invalid\", \"database\": \"t3db\", \"vars\": {}, \"auth\": \"integrated\"}, " +
                "\"t3-cli-pg\": {\"server\": \"t3.invalid\", \"database\": \"t3pgdb\", \"vars\": {}, \"auth\": \"sql\", " +
                "\"sqlUser\": \"t3\", \"passwordEnvVar\": \"SQLHARNESS_T3_TEST_PW\", \"engine\": \"postgres\"}}");
        }

        public string WriteSql(string name, string sql)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, sql);
            return path;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _original);
            Directory.Delete(Path, true);
        }
    }
}
