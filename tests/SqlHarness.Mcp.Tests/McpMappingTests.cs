using System.Text.Json;

using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T3 mapping contract: MCP arguments become the same Core operations the CLI
/// builds, parameters travel as Core declaration strings (null, decimal,
/// Unicode, '=' preserved; duplicates and unknown types left to Core), PG
/// gates fire before any connection, Core safety stays authoritative offline,
/// and plan results lose statement text and literal predicates. Only synthetic
/// HOME directories and an unreachable profile target are used; no test opens
/// a real database connection.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpMappingTests : IDisposable
{
    private const string ProfileName = "mcp-t3-map";
    private const string PgProfileName = "mcp-t3-map-pg";
    private const string CrossDatabaseSql = "SELECT * FROM [otherdb].dbo.T";
    private const string UnicodeValue = "zażółć gęślą jaźń €";
    private const string SecretValue = "t0p-s3cret-value";

    private readonly string _home;
    private readonly string? _savedHome;
    private readonly string _targetsFile;

    public McpMappingTests()
    {
        _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-t3-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
        _targetsFile = Path.Combine(_home, "targets.json");
        File.WriteAllText(
            _targetsFile,
            "{\""
            + ProfileName
            + "\": {\"server\": \"mcp-unreachable.invalid\", \"database\": \"reportdb\", "
            + "\"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}, \""
            + PgProfileName
            + "\": {\"server\": \"mcp-unreachable.invalid\", \"database\": \"pgdb\", "
            + "\"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"sql\", "
            + "\"sqlUser\": \"mcp\", \"passwordEnvVar\": \"SQLHARNESS_MCP_TEST_PW\", \"engine\": \"postgres\"}}");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private McpScope Scope(string? profile = null) => McpScope.Create(
        new McpServerOptions
        {
            Profile = profile ?? ProfileName,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
        },
        ProfileStore.Load(_targetsFile));

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }

    private static McpParameterArgument P(string name, string? type = null, string? value = null) =>
        new() { Name = name, Type = type, Value = value };

    private static void AssertQueryOperation(
        SqlHarnessQueryOperation actual,
        SqlTargetRequest target,
        string sql,
        string[] parameters,
        int timeout,
        int maxRows)
    {
        Assert.Same(target, actual.Target);
        Assert.Equal(sql, actual.Sql);
        Assert.Equal(parameters, actual.Parameters.ToArray());
        Assert.Equal(timeout, actual.TimeoutSeconds);
        Assert.Equal(maxRows, actual.MaxRows);
        Assert.False(actual.AllowMutation);
        Assert.Null(actual.ConfirmDatabase);
    }

    [Fact]
    public async Task Query_mapping_matches_cli_operation_and_never_mutates()
    {
        var scope = Scope();
        var operation = await McpOperationMapper.MapQueryAsync(
            scope, "SELECT 1", null, [P("customerId", "int", "42")], 30, 50, CancellationToken.None);
        AssertQueryOperation(
            operation, scope.TargetRequest, "SELECT 1", ["customerId:int=42"], 30, 50);
    }

    [Fact]
    public async Task Query_handler_records_the_same_operation_on_a_recording_module()
    {
        var scope = Scope();
        var recording = new RecordingModule();
        var handlers = new McpToolHandlers(scope, recording);
        var result = await handlers.QueryAsync(null!, "SELECT 1", null, [P("customerId", "int", "42")], 30, 50);
        Assert.False(result.IsError == true);
        var operation = Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(recording.Operations));
        AssertQueryOperation(
            operation, scope.TargetRequest, "SELECT 1", ["customerId:int=42"], 30, 50);
    }

    [Fact]
    public void Inspect_mapping_matches_cli_operations_per_kind()
    {
        var scope = Scope();
        Assert.Equal(
            new SqlHarnessPingOperation(scope.TargetRequest, 5),
            McpOperationMapper.MapInspect(scope, "ping", null, null, null, null, null, false, null, null));
        Assert.Equal(
            new SqlHarnessSchemaOperation(scope.TargetRequest, null, 30, 50, "dbo.Contracts"),
            McpOperationMapper.MapInspect(scope, "schema", "dbo.Contracts", null, null, null, null, false, null, null));
        var counts = Assert.IsType<SqlHarnessCountsOperation>(
            McpOperationMapper.MapInspect(scope, "counts", null, null, ["dbo.A"], null, 10, true, null, 30));
        Assert.Equal(["dbo.A"], counts.Tables.ToArray());
        Assert.Equal(10, counts.Top);
        Assert.True(counts.Exact);
        var space = Assert.IsType<SqlHarnessSpaceOperation>(
            McpOperationMapper.MapInspect(scope, "space", "dbo.A", null, null, null, null, false, null, null));
        Assert.Equal("dbo.A", space.Object);
        Assert.Equal(25, space.Top);
        var qstop = Assert.IsType<SqlHarnessQueryStoreTopOperation>(
            McpOperationMapper.MapInspect(scope, "qstop", null, null, null, null, null, false, "24h", null));
        Assert.Equal(1440, qstop.WindowMinutes);
        Assert.Equal(20, qstop.Top);
        var indexes = Assert.IsType<SqlHarnessIndexesOperation>(
            McpOperationMapper.MapInspect(scope, "indexes", null, null, null, null, 7, false, null, null));
        Assert.Equal(7, indexes.Top);
    }

    [Fact]
    public void Inspect_rejects_misplaced_fields_and_bad_combinations()
    {
        var scope = Scope();
        Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(scope, "ping", null, null, null, null, 5, false, null, null));
        Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(scope, "bogus", null, null, null, null, null, false, null, null));
        Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(scope, "schema", "dbo.A", "dbo.%", null, null, null, false, null, null));
        Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(scope, "counts", null, null, ["dbo.A"], "%A%", null, false, null, null));
        Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(scope, "space", "a.b.c", null, null, null, null, false, null, null));
        Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(scope, "qstop", null, null, null, null, null, false, "never", null));
    }


    [Theory]
    [InlineData("1m", 1L, 'm', 1)]
    [InlineData("24h", 24L, 'h', 1440)]
    [InlineData("31d", 31L, 'd', 44640)]
    [InlineData("24H", 24L, 'h', 1440)]
    public void Inspect_window_parsing_matches_shared_core_conversion(
        string window, long magnitude, char unit, int minutes)
    {
        // The MCP adapter keeps its own format policy (case folding, trim);
        // the pure magnitude-to-minutes conversion and 1..44640 range live in
        // Core. Both must agree on every shared input.
        Assert.Equal(minutes, McpOperationMapper.ParseQueryStoreWindow(window));
        Assert.True(OperationLimits.TryConvertQueryStoreWindow(magnitude, unit, out var converted));
        Assert.Equal(minutes, converted);
    }

    [Fact]
    public void Inspect_window_rejection_keeps_adapter_message()
    {
        var exception = Assert.Throws<McpMappingException>(
            () => McpOperationMapper.ParseQueryStoreWindow("32d"));
        Assert.Equal(
            "The inspect window must be a positive integer with an m, h, or d suffix, totaling 1..44640 minutes.",
            exception.Message);
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("90m", 5400)]
    [InlineData("24h", 86400)]
    public void Watch_duration_parsing_matches_shared_core_cap(string text, long seconds)
    {
        var duration = McpOperationMapper.ParseWatchDuration(text, TimeSpan.FromSeconds(30), "interval");
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
        Assert.True(OperationLimits.IsWatchDuration(duration));
    }

    [Fact]
    public void Watch_duration_default_stays_in_adapter()
    {
        var fallback = TimeSpan.FromSeconds(30);
        Assert.Equal(fallback, McpOperationMapper.ParseWatchDuration(null, fallback, "interval"));
        Assert.Equal(fallback, McpOperationMapper.ParseWatchDuration("  ", fallback, "maxDuration"));
    }

    [Theory]
    [InlineData("25h", "maxDuration", "must not exceed 24 hours")]
    [InlineData("0", "interval", "positive integral value")]
    [InlineData("10d", "interval", "positive integral value")]
    public void Watch_duration_rejections_keep_adapter_messages(string text, string label, string fragment)
    {
        var exception = Assert.Throws<McpMappingException>(
            () => McpOperationMapper.ParseWatchDuration(text, TimeSpan.FromSeconds(30), label));
        Assert.Contains(fragment, exception.Message, StringComparison.Ordinal);
        Assert.Contains(label, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measure_mapping_matches_cli_operation_with_setup_and_param_sets()
    {
        var scope = Scope();
        var root = Path.Combine(_home, "inputs");
        Directory.CreateDirectory(root);
        var queryFile = Path.Combine(root, "q.sql");
        var setupFile = Path.Combine(root, "s.sql");
        File.WriteAllText(queryFile, "SELECT @id;");
        File.WriteAllText(setupFile, "SELECT 1;");
        var setA = Path.Combine(root, "a.sqljson");
        var setB = Path.Combine(root, "b.sqljson");
        File.WriteAllText(setA, "{\"name\":\"a\",\"parameters\":[\"id:int=1\"]}");
        File.WriteAllText(setB, "{\"name\":\"b\",\"parameters\":[\"id:int=2\"]}");
        var rooted = McpScope.Create(
            new McpServerOptions
            {
                Profile = ProfileName,
                Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
                InputRoots = [root],
            },
            ProfileStore.Load(_targetsFile));
        var operation = await McpOperationMapper.MapMeasureAsync(
            rooted,
            new McpSqlSourceArgument { File = queryFile },
            new McpSqlSourceArgument { File = setupFile },
            [P("tenant", "nvarchar", "frozen")],
            [setA, setB],
            5,
            null,
            CancellationToken.None);
        Assert.Same(rooted.TargetRequest, operation.Target);
        Assert.Equal("SELECT @id;", operation.QuerySql);
        Assert.Equal("SELECT 1;", operation.SetupSql);
        Assert.Equal(["tenant:nvarchar=frozen"], operation.Parameters.ToArray());
        Assert.Equal(5, operation.Repeat);
        Assert.Equal(30, operation.TimeoutSeconds);
        Assert.NotNull(operation.ParameterSets);
        Assert.Equal(["a", "b"], operation.ParameterSets.Select(set => set.Name).ToArray());
    }

    [Fact]
    public async Task Compare_mapping_builds_plain_and_matrix_variants_like_cli()
    {
        var scope = Scope();
        var plain = await McpOperationMapper.MapCompareAsync(
            scope,
            new McpSqlSourceArgument { Sql = "SELECT 1" },
            new McpSqlSourceArgument { Sql = "SELECT 2" },
            null, null, 5, 30, "multiset", null, CancellationToken.None);
        var compare = Assert.IsType<SqlHarnessCompareOperation>(plain);
        Assert.Equal("SELECT 1", compare.BaselineSql);
        Assert.Equal("SELECT 2", compare.CandidateSql);
        Assert.Equal(ResultComparisonMode.Multiset, compare.CompareResults);

        var matrix = await McpOperationMapper.MapCompareAsync(
            scope,
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            null, null, 5, 30, "ordered",
            new McpMatrixArgument { Name = "BatchSize", Type = "int", Values = ["1", "20", "100"] },
            CancellationToken.None);
        var matrixOperation = Assert.IsType<SqlHarnessCompareMatrixOperation>(matrix);
        Assert.Equal("BatchSize:int=1,20,100", matrixOperation.Matrix);

        await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapCompareAsync(
            scope,
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            null, null, 5, 30, "ordered",
            new McpMatrixArgument { Name = "BatchSize", Type = "int", Values = ["1,2", "3"] },
            CancellationToken.None));
        await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapCompareAsync(
            scope,
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            null, null, 5, 30, "bogus", null, CancellationToken.None));
    }

    // 012/T2 regression: the old text bridge rejected a comma and an empty
    // string in the mapper and could not carry a JSON null at all. The values
    // must now reach Core as structure, in caller order, with no text matrix.
    [Theory]
    [InlineData("[\"a,b\",\"c\"]")]
    [InlineData("[\"\",\"c\"]")]
    [InlineData("[null,\"c\"]")]
    [InlineData("[\"a,b\",\"\",null,\"null\"]")]
    public async Task Matrix_values_with_comma_empty_and_null_reach_core_as_structure(string valuesJson)
    {
        var scope = Scope();
        var recording = new RecordingModule();
        var handlers = new McpToolHandlers(scope, recording);
        var matrix = JsonSerializer.Deserialize<McpMatrixArgument>(
            "{\"name\":\"Label\",\"type\":\"nvarchar(20)\",\"values\":" + valuesJson + "}")!;

        var result = await handlers.CompareAsync(
            null!,
            new McpSqlSourceArgument { Sql = "SELECT @Label" },
            new McpSqlSourceArgument { Sql = "SELECT @Label" },
            matrix: matrix);

        Assert.False(result.IsError == true);
        var operation = Assert.IsType<SqlHarnessCompareMatrixOperation>(Assert.Single(recording.Operations));
        Assert.Equal(string.Empty, operation.Matrix);
        Assert.NotNull(operation.TypedMatrix);
        Assert.Equal("Label", operation.TypedMatrix.Name);
        Assert.Equal("nvarchar(20)", operation.TypedMatrix.Type);
        Assert.Equal(JsonSerializer.Deserialize<string?[]>(valuesJson), operation.TypedMatrix.Values.ToArray());
    }

    // The fixed-name clash is the last matrix check in Core and runs only
    // after every value is bound, so reaching it offline proves the Core
    // binder accepted the comma, the empty string, the typed NULL and the
    // text "null" as four distinct values.
    [Fact]
    public async Task Matrix_comma_empty_null_and_null_text_bind_in_core_as_distinct_values()
    {
        var scope = Scope();
        var operation = await McpOperationMapper.MapCompareAsync(
            scope,
            new McpSqlSourceArgument { Sql = "SELECT @Label" },
            new McpSqlSourceArgument { Sql = "SELECT @Label" },
            null, [P("label", "int", "7")], 5, 30, "ordered",
            JsonSerializer.Deserialize<McpMatrixArgument>(
                "{\"name\":\"Label\",\"type\":\"nvarchar(20)\",\"values\":[\"a,b\",\"\",null,\"null\"]}"),
            CancellationToken.None);
        var outcome = await scope.CreateModule().ExecuteAsync(
            operation, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("The --matrix option for SQL parameter '@Label' duplicates a fixed parameter.", outcome.SafeError);
    }

    [Theory]
    [InlineData("nvarchar(20)", "[null,null]", "The --matrix option for SQL parameter '@Label' contains a duplicate value.")]
    [InlineData("nvarchar(20)", "[\"\",\"\"]", "The --matrix option for SQL parameter '@Label' contains a duplicate value.")]
    [InlineData("int", "[\"1\",\"\"]", "The --matrix option for SQL parameter '@Label' of type 'int' is invalid.")]
    [InlineData("int", "[\"1\",\"2,3\"]", "The --matrix option for SQL parameter '@Label' of type 'int' is invalid.")]
    public async Task Matrix_value_rejections_come_from_the_core_binder(string type, string valuesJson, string expected)
    {
        var scope = Scope();
        var operation = await McpOperationMapper.MapCompareAsync(
            scope,
            new McpSqlSourceArgument { Sql = "SELECT @Label" },
            new McpSqlSourceArgument { Sql = "SELECT @Label" },
            null, null, 5, 30, "ordered",
            JsonSerializer.Deserialize<McpMatrixArgument>(
                "{\"name\":\"Label\",\"type\":\"" + type + "\",\"values\":" + valuesJson + "}"),
            CancellationToken.None);
        var outcome = await scope.CreateModule().ExecuteAsync(
            operation, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(expected, outcome.SafeError);
    }

    [Fact]
    public async Task Watch_mapping_applies_cli_defaults_and_exclusive_stop()
    {
        var scope = Scope();
        var operation = await McpOperationMapper.MapWatchAsync(
            scope, "SELECT 1", null, null, 30, 50, null, null, null, null, CancellationToken.None);
        Assert.Null(operation.Until);
        Assert.Equal(3, operation.UntilUnchanged);
        Assert.Equal(TimeSpan.FromSeconds(30), operation.Interval);
        Assert.Equal(TimeSpan.FromMinutes(15), operation.MaxDuration);

        var until = await McpOperationMapper.MapWatchAsync(
            scope, "SELECT 1", null, null, 30, 50, "Imported >= 1000", null, "10s", "5m", CancellationToken.None);
        Assert.Equal("Imported >= 1000", until.Until);
        Assert.Null(until.UntilUnchanged);

        await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapWatchAsync(
            scope, "SELECT 1", null, null, 30, 50, "x > 1", 2, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapWatchAsync(
            scope, "SELECT 1", null, null, 30, 50, null, null, "25h", null, CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_mapping_never_sets_force()
    {
        var scope = Scope();
        var capture = await McpOperationMapper.MapSnapshotAsync(
            scope, "capture", "before-import", "SELECT 1", null, null, 30, 50, CancellationToken.None);
        Assert.False(capture.Diff);
        Assert.False(capture.Force);
        Assert.Equal("before-import", capture.Name);
        var diff = await McpOperationMapper.MapSnapshotAsync(
            scope, "diff", "before-import", "SELECT 1", null, null, 30, 50, CancellationToken.None);
        Assert.True(diff.Diff);
        Assert.False(diff.Force);
        await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapSnapshotAsync(
            scope, "capture", "not a name!", "SELECT 1", null, null, 30, 50, CancellationToken.None));
        await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapSnapshotAsync(
            scope, "overwrite", "before-import", "SELECT 1", null, null, 30, 50, CancellationToken.None));
    }

    [Fact]
    public async Task Plan_mapping_carries_text_and_footprint()
    {
        var scope = Scope();
        var operation = await McpOperationMapper.MapPlanAsync(scope, "<a/>", null, CancellationToken.None);
        Assert.Equal("<a/>", operation.ShowplanXml);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("<a/>"), operation.RawFootprint.Bytes);
        Assert.Equal(1, operation.RawFootprint.Lines);
    }

    [Fact]
    public void Parameter_formatting_preserves_null_decimal_unicode_and_equals()
    {
        Assert.Equal(["a:null"], McpOperationMapper.FormatParameters([P("a")]).ToArray());
        Assert.Equal(["a:int:null"], McpOperationMapper.FormatParameters([P("a", "int")]).ToArray());
        Assert.Equal(
            ["amount:decimal(19,4)=1234.5600"],
            McpOperationMapper.FormatParameters([P("amount", "decimal(19,4)", "1234.5600")]).ToArray());
        Assert.Equal(
            ["note:nvarchar=" + UnicodeValue],
            McpOperationMapper.FormatParameters([P("note", "nvarchar", UnicodeValue)]).ToArray());
        Assert.Equal(
            ["note:nvarchar=a=b"],
            McpOperationMapper.FormatParameters([P("note", "nvarchar", "a=b")]).ToArray());
        Assert.Throws<McpMappingException>(() => McpOperationMapper.FormatParameters([P("  ")]));
    }

    [Theory]
    [InlineData("query")]
    [InlineData("setup")]
    [InlineData("benchmark")]
    [InlineData("QUERY")]
    public async Task Validate_accepts_every_usage_offline_through_core(string usage)
    {
        var scope = Scope();
        var report = await McpOperationMapper.MapValidateAsync(
            scope, "SELECT @id;", null, usage, [P("id", "int", "42")], CancellationToken.None);
        Assert.True(report.Allowed);
        Assert.False(report.Executed);
    }

    [Theory]
    [InlineData("query", "SELECT @id;", "id:int=42")]
    [InlineData("setup", "SELECT @id;", "id:int=42")]
    [InlineData("benchmark", "SELECT @id;", "id:int=42")]
    [InlineData("benchmark", "SELECT 1; SELECT 2;", null)]
    public async Task Validate_mcp_and_core_agree_on_decision_for_same_operation(
        string usage, string sql, string? parameter)
    {
        // 007/T4: CLI and MCP share one Core classifier, so the same
        // SQL+usage+engine must yield the same decision on both surfaces.
        // The multi-statement benchmark row is allowed on SQL Server (shape
        // check is a Postgres rule) and rejected on Postgres; both surfaces
        // must still agree per engine.
        foreach (var profile in new[] { ProfileName, PgProfileName })
        {
            var scope = Scope(profile);
            var mcp = await McpOperationMapper.MapValidateAsync(
                scope, sql, null, usage,
                parameter is null ? null : [P("id", "int", "42")],
                CancellationToken.None);
            var core = SqlValidation.Validate(
                scope.TargetRequest, sql,
                parameter is null ? [] : [parameter],
                scope.Profiles,
                new ValidationOptions(usage.ToLowerInvariant() switch
                {
                    "setup" => ValidationUsage.Setup,
                    "benchmark" => ValidationUsage.Benchmark,
                    _ => ValidationUsage.Query,
                }));
            Assert.Equal(core.Allowed, mcp.Allowed);
            Assert.Equal(core.Reason, mcp.Reason);
            Assert.Equal(core.CheckedConditions, mcp.CheckedConditions);
            Assert.Equal(core.ObjectAndPermissionStatus, mcp.ObjectAndPermissionStatus);
            Assert.False(mcp.Executed);
        }
    }
    [Fact]
    public async Task Validate_reports_core_rejections_offline_without_echo()
    {
        var scope = Scope();
        var report = await McpOperationMapper.MapValidateAsync(
            scope, "DELETE FROM dbo.T WHERE Id = @id;", null, "query", [P("id", "int",  "42")], CancellationToken.None);
        Assert.False(report.Allowed);
        Assert.False(report.Executed);
        await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapValidateAsync(
            scope, "SELECT 1", null, "execute", null, CancellationToken.None));
    }

    [Fact]
    public async Task Validate_reports_missing_parameters_offline_without_echo()
    {
        var scope = Scope();
        var report = await McpOperationMapper.MapValidateAsync(
            scope, "SELECT @id;", null, "query", null, CancellationToken.None);
        Assert.False(report.Allowed);
        Assert.Equal("missing_parameters", report.Reason);
        Assert.Equal(["id"], report.MissingParameters);
        Assert.False(report.Executed);

        var pg = Scope(PgProfileName);
        var pgReport = await McpOperationMapper.MapValidateAsync(
            pg, "SELECT @id::int;", null, "query", null, CancellationToken.None);
        Assert.False(pgReport.Allowed);
        Assert.Equal("missing_parameters", pgReport.Reason);
        Assert.Equal(["id"], pgReport.MissingParameters);
        Assert.False(pgReport.Executed);
    }

    [Fact]
    public async Task Validate_never_authorizes_persistent_mutation_on_postgres()
    {
        var pg = Scope(PgProfileName);
        var report = await McpOperationMapper.MapValidateAsync(
            pg, "INSERT INTO public.items (a) VALUES (1);", null, "query", null, CancellationToken.None);
        Assert.False(report.Allowed);
        Assert.Equal("mutation_not_allowed", report.Reason);
        Assert.False(report.Executed);
    }

    [Fact]
    public async Task Validate_report_json_carries_no_sql_or_values()
    {
        var scope = Scope();
        var report = await McpOperationMapper.MapValidateAsync(
            scope, "SELECT @note;", null, "query", [P("note", "nvarchar", "synthetic-note-7x9")], CancellationToken.None);
        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        var json = JsonSerializer.Serialize(report);
        Assert.False(report.Executed);
        Assert.DoesNotContain("synthetic-note-7x9", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT @note;", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_proves_equals_value_survives_core_binding()
    {
        var scope = Scope();
        var report = await McpOperationMapper.MapValidateAsync(
            scope, "SELECT @note;", null, "query", [P("note", "nvarchar", "a=b")], CancellationToken.None);
        Assert.True(report.Allowed, JsonSerializer.Serialize(report));
        Assert.DoesNotContain("a=b", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_proves_decimal_unicode_and_null_shapes()
    {
        var scope = Scope();
        var decimalReport = await McpOperationMapper.MapValidateAsync(
            scope, "SELECT @amount;", null, "query", [P("amount", "decimal(19,4)", "1234.5600")], CancellationToken.None);
        Assert.True(decimalReport.Allowed, JsonSerializer.Serialize(decimalReport));
        var unicodeReport = await McpOperationMapper.MapValidateAsync(
            scope, "SELECT @note;", null, "query", [P("note", "nvarchar", UnicodeValue)], CancellationToken.None);
        Assert.True(unicodeReport.Allowed, JsonSerializer.Serialize(unicodeReport));
        Assert.DoesNotContain(UnicodeValue, JsonSerializer.Serialize(unicodeReport), StringComparison.Ordinal);
        var nullReport = await McpOperationMapper.MapValidateAsync(
            scope, "SELECT @note;", null, "query", [P("note", "nvarchar", null)], CancellationToken.None);
        Assert.True(nullReport.Allowed, JsonSerializer.Serialize(nullReport));
    }

    [Fact]
    public async Task Duplicate_and_unknown_type_parameters_fail_in_core_without_value_echo()
    {
        var scope = Scope();
        var module = scope.CreateModule();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var duplicate = await McpOperationMapper.MapQueryAsync(
            scope, "SELECT @a;", null, [P("a", "int", "1"), P("a", "int", "2")], 30, 50, CancellationToken.None);
        Assert.Equal(["a:int=1", "a:int=2"], duplicate.Parameters.ToArray());
        var duplicateOutcome = await module.ExecuteAsync(duplicate, cts.Token);
        Assert.Equal(SqlHarnessExitCode.Safety, duplicateOutcome.ExitCode);

        var unknown = await McpOperationMapper.MapQueryAsync(
            scope, "SELECT @a;", null, [P("a", "frobnicate", SecretValue)], 30, 50, CancellationToken.None);
        var unknownOutcome = await module.ExecuteAsync(unknown, cts.Token);
        Assert.Equal(SqlHarnessExitCode.Safety, unknownOutcome.ExitCode);
        Assert.DoesNotContain(SecretValue, unknownOutcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disallowed_sql_never_opens_a_session()
    {
        var scope = Scope();
        var operation = await McpOperationMapper.MapQueryAsync(
            scope, CrossDatabaseSql, null, null, 5, 50, CancellationToken.None);
        Assert.Equal(CrossDatabaseSql, operation.Sql);
        var outcome = await scope.CreateModule().ExecuteAsync(
            operation, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Contains("safety rejection", outcome.SafeError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_matrix_values_fail_in_core_without_value_echo()
    {
        var scope = Scope();
        var operation = await McpOperationMapper.MapCompareAsync(
            scope,
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            new McpSqlSourceArgument { Sql = "SELECT @n" },
            null, null, 5, 30, "ordered",
            new McpMatrixArgument { Name = "n", Type = "int", Values = ["1", SecretValue] },
            CancellationToken.None);
        var outcome = await scope.CreateModule().ExecuteAsync(
            operation, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.DoesNotContain(SecretValue, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_gates_fire_before_any_connection()
    {
        var pg = Scope(PgProfileName);
        Assert.Equal(SqlEngine.Postgres, pg.ResolvedTarget.Engine);
        var qstop = Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(pg, "qstop", null, null, null, null, null, false, null, null));
        Assert.Contains("SQL Server", qstop.Message, StringComparison.Ordinal);
        var indexes = Assert.Throws<McpMappingException>(() =>
            McpOperationMapper.MapInspect(pg, "indexes", null, null, null, null, null, false, null, null));
        Assert.Contains("SQL Server", indexes.Message, StringComparison.Ordinal);

        var capabilities = McpOperationMapper.BuildCapabilities(pg, includeDiagnostics: false);
        Assert.Equal("postgres", capabilities.Engine);
        Assert.False(capabilities.QueryStoreAvailable);
        Assert.False(capabilities.IndexesAvailable);

        var sqlserver = McpOperationMapper.BuildCapabilities(Scope(), includeDiagnostics: false);
        Assert.Equal("sqlserver", sqlserver.Engine);
        Assert.True(sqlserver.QueryStoreAvailable);
        Assert.True(sqlserver.IndexesAvailable);
        Assert.Equal(McpToolCatalog.ToolNames, sqlserver.Tools);
    }

    [Fact]
    public async Task Postgres_rejected_parameter_types_stay_core_owned()
    {
        var pg = Scope(PgProfileName);
        var report = await McpOperationMapper.MapValidateAsync(
            pg, "SELECT @amount;", null, "query", [P("amount", "money", "5")], CancellationToken.None);
        Assert.False(report.Allowed);
        var sqlserver = Scope();
        var allowed = await McpOperationMapper.MapValidateAsync(
            sqlserver, "SELECT @amount;", null, "query", [P("amount", "money", "5")], CancellationToken.None);
        Assert.True(allowed.Allowed, JsonSerializer.Serialize(allowed));
    }

    [Fact]
    public void Sanitizer_strips_statement_text_and_literal_predicates()
    {
        var child = new PlanNode("Index Seek", "Index Seek", "dbo.T", "IX_T", 1, 1, 1, 0.1, "[Id]=@id", [], []);
        var root = new PlanNode("Nested Loops", "Inner Join", null, null, 2, 2, 1, 0.9, "OuterRefs", [], [child]);
        var plan = new DistilledPlan([new PlanStatement("SELECT * FROM dbo.T WHERE Id = 1", root, [])]);
        var sanitized = Assert.IsType<DistilledPlan>(McpResultSanitizer.Sanitize(plan));
        Assert.Null(sanitized.Statements[0].StatementText);
        Assert.Null(sanitized.Statements[0].Root.Predicate);
        Assert.Null(sanitized.Statements[0].Root.Children[0].Predicate);
        Assert.Equal("Index Seek", sanitized.Statements[0].Root.Children[0].PhysicalOp);
        Assert.Equal("dbo.T", sanitized.Statements[0].Root.Children[0].ObjectName);
        Assert.Null(McpResultSanitizer.Sanitize(null));
        var passthrough = new object();
        Assert.Same(passthrough, McpResultSanitizer.Sanitize(passthrough));
    }

    [Fact]
    public async Task Snapshot_handler_records_no_force_and_rejects_before_execution()
    {
        var scope = Scope();
        var recording = new RecordingModule();
        var handlers = new McpToolHandlers(scope, recording);
        var capture = await handlers.SnapshotAsync(null!, "capture", "n1", "SELECT 1");
        Assert.False(capture.IsError == true);
        var operation = Assert.IsType<SqlHarnessSnapshotOperation>(Assert.Single(recording.Operations));
        Assert.False(operation.Force);
        Assert.False(operation.Diff);

        var rejected = await handlers.SnapshotAsync(null!, "capture", "bad name!");
        Assert.True(rejected.IsError == true);
        Assert.Single(recording.Operations);
    }

    [Fact]
    public async Task Artifact_handler_rejects_unsafe_sections_before_any_read()
    {
        var scope = Scope();
        var recording = new RecordingModule();
        var handlers = new McpToolHandlers(scope, recording);
        var result = await handlers.ArtifactAsync(null!, "anything", "raw");
        Assert.True(result.IsError == true);
        Assert.Empty(recording.Operations);
    }

    // 003/T2 RED: the artifact tool envelope must refuse foreign and legacy
    // artifacts with isError=true before any projection. The old path serves
    // them with isError=false, so refusal tests fail until T3 enforces the
    // manifest owner from the frozen scope in Core.

    private const string ArtifactScopeProfile = "mcp-artifact-own";
    private const string ArtifactForeignProfile = "mcp-artifact-foreign";

    private void WriteArtifactTargetsFile() => File.WriteAllText(
        _targetsFile,
        "{\""
        + ArtifactScopeProfile + "\": {\"server\": \"artifact-own.invalid\", \"database\": \"artifactdb\", \"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}, \""
        + ArtifactForeignProfile + "\": {\"server\": \"artifact-foreign.invalid\", \"database\": \"artifactdb\", \"vars\": {\"tenant\": \"^frozen$\"}, \"auth\": \"integrated\"}}");

    private McpScope ArtifactScope(string profile) => McpScope.Create(
        new McpServerOptions
        {
            Profile = profile,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
        },
        ProfileStore.Load(_targetsFile));

    private static string ArtifactOwnerJson(McpScope scope) =>
        "{\"profile\": " + JsonSerializer.Serialize(scope.TargetRequest.Profile)
        + ", \"vars\": {\"tenant\": \"frozen\"}"
        + ", \"engine\": \"sqlserver\""
        + ", \"server\": " + JsonSerializer.Serialize(scope.ResolvedTarget.Server)
        + ", \"database\": " + JsonSerializer.Serialize(scope.ResolvedTarget.Database) + "}";

    private static string WriteMappingArtifact(string id, string? ownerJson)
    {
        var root = SqlHarnessPaths.CompareDir;
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\": 1, \"artifactKind\": \"compare\", \"reportFile\": \"report.json\", \"sections\": [\"summary\", \"metrics\", \"operators\"]"
            + (ownerJson is null ? string.Empty : ", \"owner\": " + ownerJson) + "}");
        var report = new SqlHarnessCompareReport(
            new SqlHarnessTargetIdentityReport("artifact-own.invalid", "artifactdb", "artifact-own.invalid", "artifactdb", "profile"),
            5, 10, true,
            new CompareVariantReport(
                "baseline",
                new CompareDistribution(1, 2, 3),
                new CompareDistribution(10, 20, 30),
                new CompareDistribution(3, 4, 5),
                new Dictionary<string, long>(),
                [new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false)],
                []),
            new CompareVariantReport(
                "candidate",
                new CompareDistribution(1, 2, 3),
                new CompareDistribution(4, 5, 6),
                new CompareDistribution(3, 4, 5),
                new Dictionary<string, long>(),
                [new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false)],
                []),
            null);
        File.WriteAllText(
            Path.Combine(directory, "report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return id;
    }

    [Fact]
    public async Task Artifact_handler_returns_projection_for_own_scope()
    {
        WriteArtifactTargetsFile();
        var scope = ArtifactScope(ArtifactScopeProfile);
        var id = WriteMappingArtifact("handler-own-artifact", ArtifactOwnerJson(scope));
        var handlers = new McpToolHandlers(scope, new RecordingModule());

        var result = await handlers.ArtifactAsync(null!, id, "summary");

        Assert.False(result.IsError == true);
    }

    [Fact]
    public async Task Artifact_handler_reports_error_for_foreign_scope()
    {
        WriteArtifactTargetsFile();
        var owner = ArtifactScope(ArtifactScopeProfile);
        var foreign = ArtifactScope(ArtifactForeignProfile);
        var id = WriteMappingArtifact("handler-foreign-artifact", ArtifactOwnerJson(owner));
        var handlers = new McpToolHandlers(foreign, new RecordingModule());

        var result = await handlers.ArtifactAsync(null!, id, "summary");

        Assert.True(result.IsError == true);
        var foreignPayload = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.DoesNotContain("Index Seek", foreignPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifact_handler_reports_error_for_legacy_artifact_while_cli_reads_it()
    {
        WriteArtifactTargetsFile();
        var scope = ArtifactScope(ArtifactScopeProfile);
        var id = WriteMappingArtifact("handler-legacy-artifact", ownerJson: null);
        var handlers = new McpToolHandlers(scope, new RecordingModule());

        var result = await handlers.ArtifactAsync(null!, id, "metrics");

        Assert.True(result.IsError == true);
        var legacyPayload = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.DoesNotContain("Index Seek", legacyPayload, StringComparison.Ordinal);
        Assert.NotNull(ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id, "metrics"));
    }

    [Fact]
    public async Task Validate_usage_benchmark_rejects_pg_multi_statement_batch_while_query_allows()
    {
        // 007/T3: MapValidateAsync must carry the validated usage string into
        // the shared Core model instead of always validating as query.
        var pg = Scope(PgProfileName);

        var query = await McpOperationMapper.MapValidateAsync(
            pg, "SELECT 1; SELECT 2;", null, "query", null, CancellationToken.None);
        var benchmark = await McpOperationMapper.MapValidateAsync(
            pg, "SELECT 1; SELECT 2;", null, "benchmark", null, CancellationToken.None);

        Assert.True(query.Allowed);
        Assert.False(query.Executed);
        Assert.False(benchmark.Allowed);
        Assert.Equal("benchmark_batch_not_supported", benchmark.Reason);
        Assert.False(benchmark.Executed);
    }

    [Theory]
    [InlineData("BENCHMARK", false)]
    [InlineData("Setup", true)]
    public async Task Validate_usage_mapping_is_case_insensitive(string usage, bool expectedAllowed)
    {
        var pg = Scope(PgProfileName);

        var report = await McpOperationMapper.MapValidateAsync(
            pg, "SELECT 1; SELECT 2;", null, usage, null, CancellationToken.None);

        Assert.Equal(expectedAllowed, report.Allowed);
        Assert.False(report.Executed);
    }
}
