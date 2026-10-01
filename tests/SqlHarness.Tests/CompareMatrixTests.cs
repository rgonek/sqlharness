using System.Globalization;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class CompareMatrixTests
{
    private const string SetupSql = "SELECT Id INTO #ids FROM dbo.Clients WHERE Tenant = @Tenant";
    private const string BaselineSql = "SELECT Value FROM dbo.Clients WHERE BatchSize = @BatchSize AND Tenant = @Tenant";
    private const string CandidateSql = "SELECT Value FROM dbo.Clients WHERE BatchSize = @BatchSize AND Tenant = @Tenant -- candidate";
    private const string Plan = "<ShowPlanXML><BatchSequence><RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Table=\"[Clients]\" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>";

    [Fact]
    public async Task Matrix_runs_each_value_on_a_distinct_session_in_order()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Equal(3, factory.ConnectCount);
        Assert.Equal(3, factory.Sessions.Count);
        Assert.NotSame(factory.Sessions[0], factory.Sessions[1]);
        Assert.NotSame(factory.Sessions[1], factory.Sessions[2]);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.SetupCount));
        Assert.Equal([1], factory.Sessions[0].BatchSizes);
        Assert.Equal([20], factory.Sessions[1].BatchSizes);
        Assert.Equal([100], factory.Sessions[2].BatchSizes);
        Assert.All(factory.Sessions, session => Assert.Equal([7], session.TenantValues));
        Assert.All(factory.Sessions, session =>
        {
            var userCommands = session.Commands.Where(command => !IsStatistics(command.Sql)).ToArray();
            Assert.NotEmpty(userCommands);
            Assert.All(userCommands, command =>
                Assert.Equal(["@Tenant", "@BatchSize"], command.Parameters.Select(parameter => parameter.Name)));
        });

        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal("@BatchSize", report.ParameterName);
        Assert.Equal("int", report.ParameterType);
        Assert.Equal([0, 1, 2], report.Cells.Select(cell => cell.Index));
        Assert.Equal(["1", "20", "100"], report.Cells.Select(cell => cell.ParameterValue));
        Assert.Equal(3, artifacts.Directories.Count);
        Assert.All(report.Cells, cell =>
        {
            Assert.True(cell.Compare.ResultsEquivalent);
            Assert.Contains(cell.Compare.ArtifactDirectory, artifacts.Directories);
            Assert.True(Directory.Exists(cell.Compare.ArtifactDirectory));
            Assert.Equal(["@Tenant", "@BatchSize"], cell.Compare.Parameters.Select(parameter => parameter.Name));
        });
    }

    [Fact]
    public async Task Matrix_rejects_invalid_sql_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(
            Matrix("BatchSize:int=1,20,100", candidate: "DELETE dbo.Clients"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Empty(artifacts.Directories);
    }

    [Fact]
    public async Task Matrix_rejects_invalid_final_value_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,nope"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.DoesNotContain("nope", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_rejects_fixed_parameter_conflict_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(
            Matrix("BatchSize:int=1,20", parameters: ["BatchSize:int=5"]));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("duplicates", outcome.SafeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("20", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("=5", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_rejects_unreferenced_parameter_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix(
            "BatchSize:int=1,20",
            setup: null,
            baseline: "SELECT Value FROM dbo.Clients",
            candidate: "SELECT Value FROM dbo.Clients",
            parameters: []));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("not referenced", outcome.SafeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Amount:money=1.00,2.00", "1.00")]
    [InlineData("Path:hierarchyid=/1/2/,/1/3/", "/1/2/")]
    [InlineData("Loc:geography=POINT(0 0),POINT(1 1)", "POINT(0 0)")]
    [InlineData("Shape:geometry=POINT(0 0),POINT(1 1)", "POINT(0 0)")]
    public async Task Postgres_rejects_unsupported_matrix_types_before_connect(string matrix, string secret)
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var name = matrix[..matrix.IndexOf(':')];
        var sql = $"SELECT @{name} AS {name}";

        var outcome = await Module(factory, artifacts, PostgresProfiles).ExecuteAsync(Matrix(
            matrix,
            setup: null,
            baseline: sql,
            candidate: sql,
            parameters: [],
            profile: "local-pg"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("not supported on Postgres", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_rejects_unsupported_fixed_parameter_before_connect()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        const string sql = "SELECT @BatchSize AS BatchSize, @Amount AS Amount";

        var outcome = await Module(factory, artifacts, PostgresProfiles).ExecuteAsync(Matrix(
            "BatchSize:int=1,20",
            setup: null,
            baseline: sql,
            candidate: sql,
            parameters: ["Amount:money=9.99"],
            profile: "local-pg"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("not supported on Postgres", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("9.99", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    // 012/T3: the same Postgres type rejection, reached through the typed
    // fixed-parameter path (no declaration text) on a plain compare, not the matrix.
    [Fact]
    public async Task Postgres_rejects_unsupported_typed_fixed_parameter_before_connect()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        const string sql = "SELECT @Amount AS Amount";
        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("local-pg", new Dictionary<string, string>()),
            null, sql, sql, [], 30, 1)
        {
            TypedParameters = [new SqlHarnessParameterInput("Amount", "money", "9.99")],
        };

        var outcome = await Module(factory, artifacts, PostgresProfiles).ExecuteAsync(operation);

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(0, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("not supported on Postgres", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("9.99", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_stops_on_sql_failure_and_keeps_the_earlier_artifact_directory()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failSqlAt: 1);

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

        Assert.Equal(5, (int)outcome.ExitCode);
        Assert.Equal(2, factory.ConnectCount);
        var partialReport = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal([0], partialReport.Cells.Select(cell => cell.Index));
        Assert.Equal("int", partialReport.ParameterType);
        var error = outcome.SafeError ?? string.Empty;
        Assert.Contains("cell 1", error, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", error, StringComparison.Ordinal);
        Assert.Contains("measured-run-failed", error, StringComparison.Ordinal);
        Assert.DoesNotContain("20", error, StringComparison.Ordinal);
        Assert.Equal([1], factory.Sessions[0].BatchSizes);
        Assert.Equal([20], factory.Sessions[1].BatchSizes);
        var kept = Assert.Single(artifacts.Directories);
        Assert.True(Directory.Exists(kept));
    }

    [Fact]
    public async Task Matrix_failure_redacts_a_longer_value_that_starts_with_an_earlier_value()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failSqlAt: 2);

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

        Assert.Equal(5, (int)outcome.ExitCode);
        var partialReport = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal([0, 1], partialReport.Cells.Select(cell => cell.Index));
        Assert.Equal("int", partialReport.ParameterType);
        var error = outcome.SafeError ?? string.Empty;
        Assert.Contains("cell 2", error, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", error, StringComparison.Ordinal);
        Assert.Contains("measured-run-failed", error, StringComparison.Ordinal);
        Assert.DoesNotContain("100", error, StringComparison.Ordinal);
        Assert.DoesNotContain("00", error, StringComparison.Ordinal);
        Assert.Equal([100], factory.Sessions[2].BatchSizes);
    }

    [Fact]
    public async Task Matrix_applies_the_row_cap_and_does_not_open_a_later_cell()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(resultRowCount: 3);

        var outcome = await Module(factory, artifacts, comparisonMaximumRows: 2)
            .ExecuteAsync(Matrix("BatchSize:int=1,20"));

        Assert.Equal(2, (int)outcome.ExitCode);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Null(outcome.Report);
        Assert.Contains("cell 0", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("20", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_preserves_authentication_failure_for_the_first_cell()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failConnect: true);

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20"));

        Assert.Equal(3, (int)outcome.ExitCode);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Empty(factory.Sessions);
        Assert.Null(outcome.Report);
        Assert.Contains("cell 0", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("20", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_failure_keeps_partial_cells_and_never_starts_later_values()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failSqlAt: 1);

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=1,20,100"));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, factory.Sessions.Count);
        var partialReport = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        var cell = Assert.Single(partialReport.Cells);
        Assert.Equal(0, cell.Index);
        Assert.Equal("1", cell.ParameterValue);
        Assert.True(cell.Compare.ResultsEquivalent);
        var kept = Assert.Single(artifacts.Directories);
        Assert.Equal(kept, cell.Compare.ArtifactDirectory);
        Assert.NotNull(outcome.EmissionReceipt);
        Assert.Equal(
            SqlHarnessExitCode.SqlExecution,
            await outcome.EmissionReceipt.CompleteAsync(new OutputFootprint(0, 0)));
    }

    // 012/T1 characterization: legacy --matrix text keeps its comma and empty-value meaning end to end.
    [Theory]
    [InlineData("BatchSize:int=1,,20", "The --matrix option for SQL parameter '@BatchSize' contains an empty value.")]
    [InlineData("BatchSize:int=1,20,", "The --matrix option for SQL parameter '@BatchSize' contains an empty value.")]
    [InlineData("BatchSize:int=1", "The --matrix option for SQL parameter '@BatchSize' requires at least two values.")]
    public async Task Legacy_matrix_text_rejects_empty_and_single_values_before_any_connection(string matrix, string expected)
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix(matrix));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(expected, outcome.SafeError);
        Assert.Null(outcome.Report);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task Legacy_matrix_text_serializes_each_cell_value_as_the_split_display_text()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(Matrix("BatchSize:int=01,20"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal(
            ["{\"Index\":0,\"ParameterValue\":\"01\"}", "{\"Index\":1,\"ParameterValue\":\"20\"}"],
            report.Cells.Select(cell => System.Text.Json.JsonSerializer.Serialize(new { cell.Index, cell.ParameterValue })));
        Assert.Equal([1], factory.Sessions[0].BatchSizes);
        Assert.Equal([20], factory.Sessions[1].BatchSizes);
    }

    // 012/T1: the typed model reaches the same binder without any declaration text.
    [Fact]
    public async Task Typed_matrix_runs_comma_empty_null_and_null_text_as_four_cells()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();

        var outcome = await Module(factory, artifacts).ExecuteAsync(TypedMatrix("a,b", "", null, "null"));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal(4, factory.ConnectCount);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.SetupCount));
        Assert.All(factory.Sessions, session => Assert.Equal([7], session.TenantValues));
        Assert.Equal<object>(["a,b", "", DBNull.Value, "null"], factory.Sessions.Select(session => session.MatrixValue));

        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal("@BatchSize", report.ParameterName);
        Assert.Equal("nvarchar(20)", report.ParameterType);
        Assert.Equal(["a,b", "", null, "null"], report.Cells.Select(cell => cell.ParameterValue));
        Assert.Equal(
            "{\"Index\":2,\"ParameterValue\":null}",
            System.Text.Json.JsonSerializer.Serialize(new { report.Cells[2].Index, report.Cells[2].ParameterValue }));
    }

    // 012/T4: the Task 2 review carried forward that no MCP-project test can
    // execute a matrix cell at all (no fake session is visible there), so the
    // runner invariants -- new session/setup per cell, caller order, first
    // failure stops the run, earlier artifacts/partial report are kept -- must
    // be proven here for typed input that carries a comma, an empty string
    // and a typed NULL among the *completed* cells, not just plain integers
    // (Matrix_stops_on_sql_failure_and_keeps_the_earlier_artifact_directory,
    // above) or a single comma cell that fails before any cell completes
    // (Typed_matrix_failure_redacts_a_value_that_contains_a_comma, below).
    [Fact]
    public async Task Typed_matrix_failure_stops_the_run_after_comma_and_empty_cells_and_keeps_only_their_artifacts()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failSqlAt: 2);

        var outcome = await Module(factory, artifacts).ExecuteAsync(TypedMatrix("se,cret", "", null, "x4", "x5"));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        // Cells 0 and 1 (comma, empty string) each connected and ran; cell 2
        // (typed NULL) connected and failed; cells 3 and 4 never connected.
        Assert.Equal(3, factory.ConnectCount);
        Assert.Equal(3, factory.Sessions.Count);
        Assert.NotSame(factory.Sessions[0], factory.Sessions[1]);
        Assert.NotSame(factory.Sessions[1], factory.Sessions[2]);
        Assert.All(factory.Sessions.Take(2), session => Assert.Equal(1, session.SetupCount));
        Assert.Equal<object>(["se,cret", ""], factory.Sessions.Take(2).Select(session => session.MatrixValue));
        Assert.All(factory.Sessions.Take(2), session => Assert.Equal([7], session.TenantValues));

        var partialReport = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal([0, 1], partialReport.Cells.Select(cell => cell.Index));
        Assert.Equal(["se,cret", ""], partialReport.Cells.Select(cell => cell.ParameterValue));
        Assert.Equal(2, artifacts.Directories.Count);
        Assert.All(artifacts.Directories, directory => Assert.True(Directory.Exists(directory)));
        Assert.Equal(
            [artifacts.Directories[0], artifacts.Directories[1]],
            partialReport.Cells.Select(cell => cell.Compare.ArtifactDirectory));

        // No DoesNotContain("se,cret"/"x4"/"x5") here (012/final F4): the failing cell
        // (index 2) is the typed NULL, whose value never reaches the message text, and
        // cells 3/4 never connect -- none of those three strings could ever appear in
        // this scenario's error, so the assertion could not fail. Redaction of a value
        // that genuinely would be echoed is covered by
        // Typed_matrix_failure_redacts_a_value_that_contains_a_comma below.
        var error = outcome.SafeError ?? string.Empty;
        Assert.Contains("cell 2", error, StringComparison.Ordinal);
        Assert.Contains("@BatchSize", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_matrix_failure_redacts_a_value_that_contains_a_comma()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failSqlAt: 0);

        var outcome = await Module(factory, artifacts).ExecuteAsync(TypedMatrix("se,cret", "other"));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var error = outcome.SafeError ?? string.Empty;
        Assert.Contains("cell 0", error, StringComparison.Ordinal);
        Assert.Contains("measured-run-failed", error, StringComparison.Ordinal);
        Assert.DoesNotContain("se,", error, StringComparison.Ordinal);
        Assert.DoesNotContain("cret", error, StringComparison.Ordinal);
    }

    // 012/final F6: the test above echoes the typed MATRIX value at execution phase; no
    // existing test does the same for a typed FIXED parameter. The setup command (the
    // first command on a cell with a non-null SetupSql) carries both the fixed and the
    // matrix parameter, so a comma in the fixed @Tenant value reaches the fake session's
    // failure message exactly as a comma in the matrix value does above.
    [Fact]
    public async Task Typed_fixed_parameter_failure_redacts_a_value_that_contains_a_comma()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory(failSqlAt: 0, echoParameterName: "@Tenant");
        var operation = TypedMatrix("1", "2") with
        {
            TypedParameters = [new SqlHarnessParameterInput("Tenant", "nvarchar", "se,cret")],
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        var error = outcome.SafeError ?? string.Empty;
        Assert.Contains("cell 0", error, StringComparison.Ordinal);
        Assert.Contains("measured-run-failed", error, StringComparison.Ordinal);
        Assert.DoesNotContain("se,", error, StringComparison.Ordinal);
        Assert.DoesNotContain("cret", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_matrix_rejects_a_bad_value_like_the_text_path_without_echoing_it()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var operation = TypedMatrix() with
        {
            TypedMatrix = new SqlHarnessParameterMatrixInput("BatchSize", "int", ["1", "private,audit"]),
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("The --matrix option for SQL parameter '@BatchSize' of type 'int' is invalid.", outcome.SafeError);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task Typed_matrix_name_that_duplicates_a_typed_fixed_parameter_is_rejected()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var operation = TypedMatrix("Q1", "Q2") with
        {
            TypedParameters =
            [
                new SqlHarnessParameterInput("Tenant", "int", "7"),
                new SqlHarnessParameterInput("batchsize", null, "Zz9"),
            ],
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("The --matrix option for SQL parameter '@BatchSize' duplicates a fixed parameter.", outcome.SafeError);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task Typed_matrix_without_a_value_list_is_a_safety_rejection_before_any_connection()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var operation = TypedMatrix("Q1", "Q2") with
        {
            TypedMatrix = new SqlHarnessParameterMatrixInput("BatchSize", "nvarchar(20)", null!),
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("The --matrix option for SQL parameter '@BatchSize' requires at least two values.", outcome.SafeError);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Theory]
    [InlineData(true, "SQL parameters must be supplied either as declarations or as typed inputs, not both.")]
    [InlineData(false, "The --matrix option must be supplied either as text or as a typed matrix, not both.")]
    public async Task Declaration_text_together_with_the_typed_model_is_rejected(bool parameters, string expected)
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var operation = parameters
            ? TypedMatrix("Q1", "Q2") with { Parameters = ["Other:int=1"] }
            : TypedMatrix("Q1", "Q2") with { Matrix = "BatchSize:nvarchar(20)=Q3,Q4" };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal(expected, outcome.SafeError);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("compare")]
    [InlineData("matrix")]
    [InlineData("measure")]
    [InlineData("measure-sets")]
    [InlineData("watch")]
    [InlineData("snapshot")]
    public async Task Typed_parameters_reach_the_binder_of_every_operation(string kind)
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var target = new SqlTargetRequest("test", new Dictionary<string, string> { ["env"] = "a" });
        IReadOnlyList<SqlHarnessParameterInput> typed = [new("Tenant", "int", "private,audit=value")];
        const string sql = "SELECT Value FROM dbo.Clients WHERE Tenant = @Tenant";
        const string setSql = "SELECT Value FROM dbo.Clients WHERE Tenant = @Tenant AND BatchSize = @BatchSize";
        SqlHarnessOperation operation = kind switch
        {
            "query" => new SqlHarnessQueryOperation(target, sql, [], 30, 10, false, null) { TypedParameters = typed },
            "compare" => new SqlHarnessCompareOperation(target, null, sql, sql, [], 30, 1) { TypedParameters = typed },
            "matrix" => TypedMatrix("Q1", "Q2") with { TypedParameters = typed },
            "measure" => new SqlHarnessMeasureOperation(target, null, sql, [], 30, 1) { TypedParameters = typed },
            "measure-sets" => new SqlHarnessMeasureOperation(
                target, null, setSql, [], 30, 1,
                [new("small", ["BatchSize:int=1"]), new("large", ["BatchSize:int=2"])]) { TypedParameters = typed },
            "watch" => new SqlHarnessWatchOperation(
                target, sql, [], 30, 10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), null, 3) { TypedParameters = typed },
            _ => new SqlHarnessSnapshotOperation(target, sql, [], 30, 10, "before", false, false) { TypedParameters = typed },
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Equal("Invalid value for SQL parameter 'Tenant' of type 'int'.", outcome.SafeError);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task Typed_query_parameter_is_bound_whole_with_its_separator_characters()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var operation = new SqlHarnessQueryOperation(
            new SqlTargetRequest("test", new Dictionary<string, string> { ["env"] = "a" }),
            "SELECT Value FROM dbo.Clients WHERE BatchSize = @BatchSize",
            [],
            30,
            10,
            false,
            null)
        {
            TypedParameters = [new SqlHarnessParameterInput("BatchSize", null, "a,b=c:d")],
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal("a,b=c:d", Assert.Single(factory.Sessions).MatrixValue);
    }

    // 012/T3: non-ASCII text and a surrogate pair (the emoji below is two UTF-16
    // code units) are bound as one opaque value, byte for byte.
    [Theory]
    [InlineData("zażółć gęślą jaźń")]
    [InlineData("𝕊urrogate pair 😀 inside")]
    public async Task Typed_parameter_preserves_unicode_values_including_a_surrogate_pair(string value)
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        var operation = new SqlHarnessQueryOperation(
            new SqlTargetRequest("test", new Dictionary<string, string> { ["env"] = "a" }),
            "SELECT Value FROM dbo.Clients WHERE BatchSize = @BatchSize",
            [],
            30,
            10,
            false,
            null)
        {
            TypedParameters = [new SqlHarnessParameterInput("BatchSize", null, value)],
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal(value, Assert.Single(factory.Sessions).MatrixValue);
    }

    // 012/T1 review gap: typed fixed parameters plus parameter sets must run
    // a measure-sets operation to completion, so ParseShaped(typed) is
    // actually executed past the bind call, not just invoked and thrown from.
    [Fact]
    public async Task Typed_fixed_parameters_run_to_completion_with_two_measure_parameter_sets()
    {
        using var artifacts = new DirectoryArtifactWriter();
        var factory = new MatrixSessionFactory();
        const string sql = "SELECT Value FROM dbo.Clients WHERE Tenant = @Tenant AND BatchSize = @BatchSize";
        var operation = new SqlHarnessMeasureOperation(
            new SqlTargetRequest("test", new Dictionary<string, string> { ["env"] = "a" }),
            null,
            sql,
            [],
            30,
            1,
            [new("small", ["BatchSize:int=1"]), new("large", ["BatchSize:int=2"])])
        {
            TypedParameters = [new SqlHarnessParameterInput("Tenant", "int", "7")],
        };

        var outcome = await Module(factory, artifacts).ExecuteAsync(operation);

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var report = Assert.IsType<SqlHarnessMeasureSetReport>(outcome.Report);
        Assert.Equal(["small", "large"], report.Sets.Select(set => set.Name));
        var session = Assert.Single(factory.Sessions);
        Assert.All(
            session.Commands.Where(command => !IsStatistics(command.Sql)),
            command => Assert.Equal(7, (int)command.Parameters.Single(parameter => parameter.Name == "@Tenant").Value));
    }

    private static SqlHarnessCompareMatrixOperation TypedMatrix(params string?[] values) =>
        Matrix(string.Empty, parameters: []) with
        {
            TypedParameters = [new SqlHarnessParameterInput("Tenant", "int", "7")],
            TypedMatrix = new SqlHarnessParameterMatrixInput("BatchSize", "nvarchar(20)", values),
        };

    private static SqlHarnessModule Module(
        MatrixSessionFactory sessions,
        ICompareArtifactWriter artifacts,
        Func<IReadOnlyDictionary<string, TargetProfile>>? profiles = null,
        int? comparisonMaximumRows = null) =>
        new(sessions, new FakeGainStore(), artifacts, profiles ?? SqlProfiles)
        {
            ComparisonMaximumRows = comparisonMaximumRows ?? CanonicalComparisonAccumulator.MaximumComparedRows,
        };

    private static SqlHarnessCompareMatrixOperation Matrix(
        string matrix,
        string? setup = SetupSql,
        string? baseline = BaselineSql,
        string? candidate = CandidateSql,
        IReadOnlyList<string>? parameters = null,
        int repeat = 1,
        string profile = "test") =>
        new(
            new SqlTargetRequest(profile, profile == "test"
                ? new Dictionary<string, string> { ["env"] = "a" }
                : new Dictionary<string, string>()),
            setup,
            baseline!,
            candidate!,
            parameters ?? ["Tenant:int=7"],
            30,
            repeat,
            matrix);

    private static IReadOnlyDictionary<string, TargetProfile> SqlProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static IReadOnlyDictionary<string, TargetProfile> PostgresProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["local-pg"] = new(
                "localhost,5432",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

    private static bool IsStatistics(string sql) =>
        sql.Contains("STATISTICS", StringComparison.Ordinal);

    private sealed class FakeGainStore : IGainStore
    {
        public void Append(GainRecord record) { }
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class DirectoryArtifactWriter : ICompareArtifactWriter, IDisposable
    {
        public List<string> Directories { get; } = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sqlharness-matrix-" + Guid.NewGuid().ToString("N"));

        public DirectoryArtifactWriter() => Directory.CreateDirectory(Root);

        public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
        {
            var directory = Path.Combine(Root, "cell-" + Directories.Count.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            Directories.Add(directory);
            return directory;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class MatrixSessionFactory : ISqlSessionFactory
    {
        private readonly int? _failSqlAt;
        private readonly bool _failConnect;
        private readonly int _resultRowCount;
        private readonly string _echoParameterName;

        public MatrixSessionFactory(
            int? failSqlAt = null,
            bool failConnect = false,
            int resultRowCount = 1,
            string echoParameterName = "@BatchSize")
        {
            _failSqlAt = failSqlAt;
            _failConnect = failConnect;
            _resultRowCount = resultRowCount;
            _echoParameterName = echoParameterName;
        }

        public int ConnectCount { get; private set; }
        public List<MatrixSession> Sessions { get; } = [];

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            var index = ConnectCount;
            ConnectCount++;
            if (_failConnect)
                return Task.FromException<ISqlSession>(new InvalidOperationException("login failed"));

            var session = new MatrixSession(failSql: _failSqlAt == index, _resultRowCount, _echoParameterName);
            Sessions.Add(session);
            return Task.FromResult<ISqlSession>(session);
        }
    }

    private sealed class MatrixSession : ISqlSession
    {
        private readonly bool _failSql;
        private readonly int _resultRowCount;
        private readonly string _echoParameterName;
        private readonly List<string> _messages = [];

        public MatrixSession(bool failSql, int resultRowCount, string echoParameterName = "@BatchSize")
        {
            _failSql = failSql;
            _resultRowCount = resultRowCount;
            _echoParameterName = echoParameterName;
        }

        public List<SqlExecutionCommand> Commands { get; } = [];
        public int SetupCount { get; private set; }
        public IReadOnlyList<string> Messages => _messages;
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("test-server", "testdb-a", "test-server", "testdb-a", "profile");

        public IReadOnlyList<int> BatchSizes => Values("@BatchSize");
        public IReadOnlyList<int> TenantValues => Values("@Tenant");

        /// <summary>The single @BatchSize value this session saw, as bound (DBNull for a typed NULL).</summary>
        public object MatrixValue =>
            Commands
                .Where(command => !IsStatistics(command.Sql))
                .Select(command => command.Parameters.Single(parameter => parameter.Name == "@BatchSize").Value)
                .Distinct()
                .Single();

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (IsStatistics(command.Sql))
                return Task.FromResult<ISqlReader>(MatrixReader.Empty());

            if (_failSql)
            {
                var echoed = Convert.ToString(
                    command.Parameters.Single(parameter => parameter.Name == _echoParameterName).Value,
                    CultureInfo.InvariantCulture);
                return Task.FromException<ISqlReader>(new TimeoutException($"measured-run-failed:{echoed}"));
            }

            if (command.Sql.Contains("INTO #ids", StringComparison.Ordinal))
            {
                SetupCount++;
                return Task.FromResult<ISqlReader>(MatrixReader.Empty());
            }

            _messages.Add(
                "Table 'Clients'. Scan count 1, logical reads 5, physical reads 0, lob logical reads 0.\nSQL Server Execution Times: CPU time = 10 ms, elapsed time = 12 ms.");
            var rows = Enumerable.Range(0, _resultRowCount)
                .Select(index => new object?[] { 42 + index })
                .ToArray();
            return Task.FromResult<ISqlReader>(MatrixReader.WithPlan(["Value"], rows, Plan));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private IReadOnlyList<int> Values(string name) =>
            Commands
                .Where(command => !IsStatistics(command.Sql))
                .Select(command => (int)command.Parameters.Single(parameter => parameter.Name == name).Value)
                .Distinct()
                .ToArray();
    }

    private sealed class MatrixReader : ISqlReader
    {
        private readonly IReadOnlyList<Result> _results;
        private int _result;
        private int _row = -1;
        private Result Current => _results[_result];

        private MatrixReader(IReadOnlyList<Result> results) => _results = results;

        public int FieldCount => _results.Count == 0 ? 0 : Current.Names.Length;
        public int RecordsAffected => -1;
        public static MatrixReader Empty() => new([]);
        public static MatrixReader WithPlan(string[] names, object?[][] rows, string plan) => new(
            [new(names, rows), new(["Microsoft SQL Server 2005 XML Showplan"], [[plan]])]);

        public string GetName(int ordinal) => Current.Names[ordinal];
        public Type GetFieldType(int ordinal) => Current.Rows[0][ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => false;
        public object GetValue(int ordinal) => Current.Rows[_row][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(_results.Count > 0 && ++_row < Current.Rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (_results.Count == 0 || ++_result >= _results.Count)
                return Task.FromResult(false);
            _row = -1;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed record Result(string[] Names, object?[][] Rows);
    }
}
