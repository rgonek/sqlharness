using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Cli;

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
}
