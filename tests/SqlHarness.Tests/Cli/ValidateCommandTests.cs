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

    [Fact]
    public void Sql_server_setup_shape_rejection_preserves_actionable_detail_without_leaking_sql_or_identifiers()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        var setupSql = "SELECT @id; CREATE TABLE #t (Id int);";
        var querySql = "SELECT Id FROM #t;";

        var report = SqlValidation.Validate(
            request,
            querySql,
            ["id:int=42"],
            profiles,
            new ValidationOptions(ValidationUsage.Benchmark, setupSql));

        Assert.False(report.Allowed);
        Assert.Equal("parameter_validation_failed", report.Reason);
        Assert.Equal("rejected", report.Classification);
        Assert.NotNull(report.Detail);
        Assert.Contains("session-local temp table", report.Detail, StringComparison.Ordinal);
        Assert.Contains("before any statement that references a parameter", report.Detail, StringComparison.Ordinal);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(setupSql, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("#t", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("@id", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("42", serialized, StringComparison.Ordinal);
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

    [Fact]
    public void Setup_only_scalar_reference_is_rejected_offline_without_echoing_names_or_values()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        const string setup = "DECLARE @privateSetupValue int = 73195; SELECT @privateSetupValue;";
        const string query = "SELECT @privateSetupValue;";

        var report = SqlValidation.Validate(
            request, query, [], profiles,
            new ValidationOptions(ValidationUsage.Benchmark, SetupSql: setup));

        Assert.False(report.Allowed);
        Assert.Equal("parameter_validation_failed", report.Reason);
        Assert.Equal(
            "A variable declared in setup is referenced by a benchmark batch. Setup variables do not cross into benchmark batches.",
            report.Detail);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("73195", json, StringComparison.Ordinal);
        Assert.DoesNotContain("setup =", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Setup_only_scalar_reference_is_allowed_when_variant_binds_typed_parameter()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(
            request, "SELECT @inputValue;", ["inputValue:int=19"], profiles,
            new ValidationOptions(ValidationUsage.Benchmark, SetupSql: "DECLARE @inputValue int = 2; SELECT @inputValue;"));

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void Variant_local_declaration_shadows_setup_only_name()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(
            request, "DECLARE @localValue int = 2; SELECT @localValue;", [], profiles,
            new ValidationOptions(ValidationUsage.Benchmark, SetupSql: "DECLARE @localValue int = 1; SELECT @localValue;"));

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void Setup_only_reference_before_late_variant_declaration_is_rejected_offline()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        const string setup = "DECLARE @sharedValue int = 1; SELECT @sharedValue;";
        const string query = "SELECT @sharedValue; DECLARE @sharedValue int = 2;";

        var report = SqlValidation.Validate(
            request, query, [], profiles,
            new ValidationOptions(ValidationUsage.Benchmark, SetupSql: setup));

        Assert.False(report.Allowed);
        Assert.Equal("parameter_validation_failed", report.Reason);
        Assert.Equal(
            "A variable declared in setup is referenced by a benchmark batch. Setup variables do not cross into benchmark batches.",
            report.Detail);
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

    [Fact]
    public void Validation_report_lists_checked_conditions_for_query_usage()
    {
        // 007/T4: the report carries the explicit scope of checked conditions;
        // catalog/permission checks never run offline.
        var report = SqlValidation.Validate(
            new SqlTargetRequest("test", new Dictionary<string, string>()),
            "SELECT @id;",
            ["id:int=42"],
            new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") });

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.Equal(
            ["query_safety_classification", "parameter_validation", "missing_parameter_check"],
            report.CheckedConditions!.ToArray());
        Assert.Equal("unknown", report.ObjectAndPermissionStatus);
        Assert.False(report.Executed);
    }

    [Fact]
    public void Validation_report_lists_checked_conditions_for_setup_usage_and_ignores_setup_context()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        var report = SqlValidation.Validate(
            request, "SELECT @id;", ["id:int=42"], profiles,
            new ValidationOptions(ValidationUsage.Setup, SetupSql: "SELECT @setupOnly;"));

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.Equal(
            ["setup_classification", "parameter_validation", "missing_parameter_check"],
            report.CheckedConditions!.ToArray());
    }

    [Fact]
    public void Validation_report_lists_setup_context_and_measured_batch_shape_for_benchmark()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("sqlserver") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        var report = SqlValidation.Validate(
            request, "SELECT @id;", ["id:int=42"], profiles,
            new ValidationOptions(ValidationUsage.Benchmark, SetupSql: "SELECT @setupOnly;"));

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.Equal(
            ["setup_context_classification", "query_safety_classification", "parameter_validation", "missing_parameter_check", "measured_batch_shape"],
            report.CheckedConditions!.ToArray());
    }

    [Fact]
    public void Validation_report_lists_measured_batch_shape_for_postgres_benchmark()
    {
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile("postgres") };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());
        var report = SqlValidation.Validate(
            request, "SELECT @id::int;", ["id:int=42"], profiles,
            new ValidationOptions(ValidationUsage.Benchmark));

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.Equal(
            ["query_safety_classification", "parameter_validation", "missing_parameter_check", "measured_batch_shape"],
            report.CheckedConditions!.ToArray());
        Assert.False(report.AstLocationsAvailable);
    }

    [Fact]
    public async Task Validate_cli_json_reports_checked_conditions_without_sql_echo()
    {
        // 007/T4: the CLI surface reports the same scope indicator; reasons
        // stay safe codes and the JSON carries no SQL or parameter values.
        using var home = new TempHome();
        var query = home.WriteSql("q.sql", "SELECT @id;");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.Profile, "--file", query, "--usage", "benchmark", "--param", "id:int=42", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        var result = Result(output.ToString());
        Assert.Equal(
            ["query_safety_classification", "parameter_validation", "missing_parameter_check", "measured_batch_shape"],
            result.GetProperty("checkedConditions").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray());
        Assert.Equal("unknown", result.GetProperty("objectAndPermissionStatus").GetString());
        Assert.DoesNotContain("SELECT @id;", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlserver", "SELECT @id;")]
    [InlineData("postgres", "SELECT @id::int;")]
    public void Validation_report_carries_versioned_static_analysis_boundary(string engine, string sql)
    {
        // 009/T1 red witness: the offline verdict must disclose its static
        // boundary (static-visible-effects analysis, unknown
        // catalog/permission state, hidden effects not verified) without
        // changing classification or exit codes.
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(request, sql, ["id:int=42"], profiles);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report));
        var root = document.RootElement;

        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.Equal("static-visible-effects", root.GetProperty("AnalysisKind").GetString());
        Assert.Equal(1, root.GetProperty("AnalysisContractVersion").GetInt32());
        Assert.False(root.GetProperty("HiddenEffectsVerified").GetBoolean());
        Assert.Equal("unknown", root.GetProperty("ObjectAndPermissionStatus").GetString());
    }

    [Fact]
    public async Task Validate_cli_json_reports_static_analysis_boundary()
    {
        // 009/T1 red witness: the CLI JSON surface carries the same additive
        // boundary fields in camelCase.
        using var home = new TempHome();
        var query = home.WriteSql("q.sql", "SELECT @id;");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.Profile, "--file", query, "--param", "id:int=42", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        var result = Result(output.ToString());
        Assert.Equal("static-visible-effects", result.GetProperty("analysisKind").GetString());
        Assert.Equal(1, result.GetProperty("analysisContractVersion").GetInt32());
        Assert.False(result.GetProperty("hiddenEffectsVerified").GetBoolean());
    }

    private static JsonElement Result(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("sqlserver", "SELECT @id;", "id:int=42")]
    [InlineData("postgres", "SELECT @id::int;", "id:int=42")]
    [InlineData("sqlserver", "DELETE FROM dbo.PrivateRecords;", "")]
    [InlineData("postgres", "DELETE FROM public.private_records;", "")]
    [InlineData("sqlserver", "SELECT FROM WHERE @id", "")]
    [InlineData("postgres", "SELECT FROM WHERE @id", "")]
    public void Validation_boundary_fields_are_stable_across_engines_and_verdicts(string engine, string sql, string parameter)
    {
        // 009/T3: the versioned static-analysis boundary is the same
        // contract for every call — both engines, allowed and rejected
        // verdicts alike — and the preflight never executes.
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(request, sql, parameter.Length == 0 ? [] : [parameter], profiles);

        Assert.Equal(SqlSafetyAnalysis.AnalysisKind, report.AnalysisKind);
        Assert.Equal(SqlSafetyAnalysis.ContractVersion, report.AnalysisContractVersion);
        Assert.Equal(SqlSafetyAnalysis.HiddenEffectsVerified, report.HiddenEffectsVerified);
        Assert.Equal(SqlSafetyAnalysis.ObjectAndPermissionStatus, report.ObjectAndPermissionStatus);
        Assert.False(report.Executed);
    }

    [Theory]
    [InlineData("postgres", "SELECT set_config('search_path', 'public', false);")]
    [InlineData("postgres", "SELECT pg_cancel_backend(123);")]
    [InlineData("postgres", "SELECT pg_terminate_backend(123);")]
    [InlineData("postgres", "SELECT pg_sleep(1);")]
    [InlineData("postgres", "SELECT nextval('seq');")]
    [InlineData("postgres", "SELECT setval('seq', 1);")]
    [InlineData("postgres", "SELECT lo_import('/tmp/x');")]
    [InlineData("postgres", "SELECT pg_read_file('pg_hba.conf');")]
    [InlineData("postgres", "SELECT * FROM pg_ls_dir('.');")]
    [InlineData("postgres", "SELECT * FROM dblink('c', 'SELECT 1') AS t(x int);")]
    [InlineData("postgres", "SELECT pg_advisory_lock(1);")]
    [InlineData("postgres", "SELECT pg_try_advisory_lock(1);")]
    [InlineData("postgres", "SELECT pg_catalog.setval('s', 1);")]
    [InlineData("postgres", "SELECT SET_CONFIG('a', 'b', false);")]
    [InlineData("postgres", "SELECT length(set_config('a', 'b', false));")]
    [InlineData("sqlserver", "EXEC sp_executesql N'SELECT 1';")]
    [InlineData("sqlserver", "EXEC xp_cmdshell 'dir';")]
    [InlineData("sqlserver", "SELECT * FROM OPENROWSET('SQLOLEDB', 's', 'SELECT 1') AS t(x int);")]
    public void Preflight_rejects_denylisted_functions_without_execution(string engine, string sql)
    {
        // 009/T3: the preflight denylist keeps rejecting stateful and
        // external functions without executing anything — the same verdict
        // as before T1/T2, with no catalog or permission claims.
        var profiles = new Dictionary<string, TargetProfile> { ["test"] = Profile(engine) };
        var request = new SqlTargetRequest("test", new Dictionary<string, string>());

        var report = SqlValidation.Validate(request, sql, [], profiles);

        Assert.False(report.Allowed, JsonSerializer.Serialize(report));
        Assert.False(report.Executed);
        Assert.Equal(SqlSafetyAnalysis.ObjectAndPermissionStatus, report.ObjectAndPermissionStatus);
        Assert.NotNull(report.Reason);
    }

    [Fact]
    public async Task Validate_cli_json_rejects_denylisted_function_without_dispatch_or_echo()
    {
        // 009/T3: the CLI preflight path rejects the denylist offline —
        // exit 0 with allowed=false, no module dispatch, no SQL echo.
        using var home = new TempHome();
        const string sql = "SELECT set_config('search_path', 'public', false);";
        var path = home.WriteSql("denied.sql", sql);
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.PgProfile, "--file", path, "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        var result = Result(output.ToString());
        Assert.False(result.GetProperty("allowed").GetBoolean());
        Assert.False(result.GetProperty("executed").GetBoolean());
        Assert.DoesNotContain(sql, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_cli_json_rejects_two_nonempty_go_batches()
    {
        // 023: two non-empty batches are UnsupportedStatement. The JSON code is
        // the same safe reason the other validate tests read.
        using var home = new TempHome();
        var path = home.WriteSql("batches.sql", "SELECT 1\nGO\nSELECT 2");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.Profile, "--file", path, "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        var result = Result(output.ToString());
        Assert.False(result.GetProperty("allowed").GetBoolean());
        Assert.Equal("unsupported_statement", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Validate_cli_json_carries_object_and_permission_status_for_query_usage()
    {
        // 009/T3 closes the T1/T2 deferred minor: query-usage CLI JSON
        // pins objectAndPermissionStatus like the benchmark surface does.
        using var home = new TempHome();
        var query = home.WriteSql("q.sql", "SELECT @id;");
        var module = new RecordingModule();
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(module, output)
            .RunAsync(["validate", TempHome.Profile, "--file", query, "--param", "id:int=42", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(module.Operations);
        Assert.Equal(
            SqlSafetyAnalysis.ObjectAndPermissionStatus,
            Result(output.ToString()).GetProperty("objectAndPermissionStatus").GetString());
    }

    [Fact]
    public async Task Preflight_succeeds_without_opening_a_connection()
    {
        // 009/T3: validate and capabilities never open a DB connection —
        // the module throws on any dispatch attempt and both profiles
        // point at an unreachable host, yet every call succeeds offline.
        using var home = new TempHome();
        var module = new ThrowingModule();

        var allowedPath = home.WriteSql("ok.sql", "SELECT @id;");
        var allowedOutput = new StringWriter();
        var allowedExit = await SqlHarnessCli.Create(module, allowedOutput)
            .RunAsync(["validate", TempHome.Profile, "--file", allowedPath, "--param", "id:int=42", "--json"]);
        Assert.Equal((int)SqlHarnessExitCode.Success, allowedExit);
        Assert.True(Result(allowedOutput.ToString()).GetProperty("allowed").GetBoolean());

        var deniedPath = home.WriteSql("denied.sql", "DELETE FROM dbo.PrivateRecords;");
        var deniedOutput = new StringWriter();
        var deniedExit = await SqlHarnessCli.Create(module, deniedOutput)
            .RunAsync(["validate", TempHome.Profile, "--file", deniedPath, "--json"]);
        Assert.Equal((int)SqlHarnessExitCode.Success, deniedExit);
        Assert.False(Result(deniedOutput.ToString()).GetProperty("allowed").GetBoolean());
        Assert.False(Result(deniedOutput.ToString()).GetProperty("executed").GetBoolean());

        var capabilitiesOutput = new StringWriter();
        var capabilitiesExit = await SqlHarnessCli.Create(module, capabilitiesOutput)
            .RunAsync(["capabilities", "--json"]);
        Assert.Equal(0, capabilitiesExit);
    }

    [Fact]
    public async Task Preflight_leaves_no_side_effects()
    {
        // 009/T3: running validate/capabilities mutates no state — the
        // synthetic HOME holds exactly the files the test itself wrote.
        using var home = new TempHome();
        var before = OrderedFiles(home.Path);
        var module = new RecordingModule();
        var query = home.WriteSql("q.sql", "SELECT @id;");

        await SqlHarnessCli.Create(module, new StringWriter())
            .RunAsync(["validate", TempHome.Profile, "--file", query, "--param", "id:int=42", "--json"]);
        await SqlHarnessCli.Create(module, new StringWriter())
            .RunAsync(["capabilities", "--json"]);

        Assert.Empty(module.Operations);
        Assert.Equal(before.Concat([query]).Order(StringComparer.Ordinal).ToArray(), OrderedFiles(home.Path));
    }

    private static string[] OrderedFiles(string home) =>
        Directory.GetFiles(home, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    private sealed class ThrowingModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            throw new InvalidOperationException("Preflight must never dispatch a database operation.");
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