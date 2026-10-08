using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests;

public class SetupSqlExecutionTests
{
    [Fact]
    public void No_parameters_runs_setup_unchanged_without_bindings()
    {
        var sql = "CREATE TABLE #t (Id int); INSERT #t VALUES (1);";
        var parameters = Parameters(("x", "int", "7"));

        var commands = SetupSqlExecution.PrepareCommands(sql, parameters, 30);

        var single = Assert.Single(commands);
        Assert.Equal(sql, single.Sql);
        Assert.Empty(single.Parameters);
    }

    [Fact]
    public void Parameters_without_temp_creation_runs_unchanged_with_referenced_params()
    {
        var sql = "SELECT @x AS n;";
        var parameters = Parameters(("x", "int", "7"));

        var commands = SetupSqlExecution.PrepareCommands(sql, parameters, 30);

        var single = Assert.Single(commands);
        Assert.Equal(sql, single.Sql);
        Assert.Equal("@x", Assert.Single(single.Parameters).Name);
    }

    [Fact]
    public void Parameter_free_temp_declaration_followed_by_parameterized_population_splits()
    {
        var sql = "CREATE TABLE #t (Id int); INSERT #t VALUES (@x);";
        var parameters = Parameters(("x", "int", "7"));

        var commands = SetupSqlExecution.PrepareCommands(sql, parameters, 30);

        Assert.Equal(2, commands.Count);
        Assert.Equal("CREATE TABLE #t (Id int);", commands[0].Sql);
        Assert.Empty(commands[0].Parameters);
        Assert.Equal("INSERT #t VALUES (@x);", commands[1].Sql);
        Assert.Equal("@x", Assert.Single(commands[1].Parameters).Name);
    }

    [Fact]
    public void Parameter_free_select_into_followed_by_parameterized_population_splits()
    {
        var sql = "SELECT Id INTO #t FROM dbo.T; UPDATE #t SET Id = @x;";
        var parameters = Parameters(("x", "int", "7"));

        var commands = SetupSqlExecution.PrepareCommands(sql, parameters, 30);

        Assert.Equal(2, commands.Count);
        Assert.Contains("SELECT Id INTO #t", commands[0].Sql);
        Assert.Empty(commands[0].Parameters);
        Assert.Contains("UPDATE #t SET Id = @x", commands[1].Sql);
        Assert.Equal("@x", Assert.Single(commands[1].Parameters).Name);
    }

    [Fact]
    public void Create_table_with_parameter_reference_rejects_parameterized_temp_creation()
    {
        var sql = "CREATE TABLE #t (Id int DEFAULT @x); INSERT #t VALUES (1);";
        var parameters = Parameters(("x", "int", "7"));

        var exception = Assert.Throws<SetupSqlShapeException>(() =>
            SetupSqlExecution.PrepareCommands(sql, parameters, 30));

        Assert.Contains("session-local temp table", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_into_with_parameter_reference_rejects_parameterized_temp_creation()
    {
        var sql = "SELECT Id INTO #t FROM dbo.T WHERE Id = @x;";
        var parameters = Parameters(("x", "int", "7"));

        var exception = Assert.Throws<SetupSqlShapeException>(() =>
            SetupSqlExecution.PrepareCommands(sql, parameters, 30));

        Assert.Contains("session-local temp table", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_temp_statements_before_temp_creation_stay_in_prefix()
    {
        var sql = "SET NOCOUNT ON; CREATE TABLE #t (Id int); INSERT #t VALUES (@x);";
        var parameters = Parameters(("x", "int", "7"));

        var commands = SetupSqlExecution.PrepareCommands(sql, parameters, 30);

        Assert.Equal(2, commands.Count);
        Assert.Contains("SET NOCOUNT ON", commands[0].Sql);
        Assert.Contains("CREATE TABLE #t", commands[0].Sql);
        Assert.Empty(commands[0].Parameters);
        Assert.Contains("INSERT #t VALUES (@x)", commands[1].Sql);
        Assert.Equal("@x", Assert.Single(commands[1].Parameters).Name);
    }

    [Fact]
    public void Interleaved_non_temp_statements_keep_original_order()
    {
        var sql = "CREATE TABLE #t (Id int); SET NOCOUNT ON; INSERT #t VALUES (@x); SELECT * FROM #t;";
        var parameters = Parameters(("x", "int", "7"));

        var commands = SetupSqlExecution.PrepareCommands(sql, parameters, 30);

        Assert.Equal(2, commands.Count);
        Assert.Contains("CREATE TABLE #t", commands[0].Sql);
        Assert.Contains("SET NOCOUNT ON", commands[0].Sql);
        Assert.DoesNotContain("INSERT", commands[0].Sql, StringComparison.Ordinal);
        Assert.Contains("INSERT #t VALUES (@x)", commands[1].Sql);
        Assert.Contains("SELECT * FROM #t", commands[1].Sql);
    }

    [Fact]
    public void Parameter_free_setup_preserves_drop_then_create_order()
    {
        var sql = "DROP TABLE #t; CREATE TABLE #t (Id int);";
        var parameters = Parameters(("x", "int", "7"));

        var commands = SetupSqlExecution.PrepareCommands(sql, parameters, 30);

        var single = Assert.Single(commands);
        Assert.Equal(sql, single.Sql);
        Assert.Empty(single.Parameters);
    }

    [Fact]
    public void Temp_creation_after_leading_parameter_reference_is_rejected()
    {
        var sql = "SELECT @x; CREATE TABLE #t (Id int);";
        var parameters = Parameters(("x", "int", "7"));

        var exception = Assert.Throws<SetupSqlShapeException>(() =>
            SetupSqlExecution.PrepareCommands(sql, parameters, 30));

        Assert.Contains("session-local temp table", exception.Message, StringComparison.Ordinal);
        Assert.Contains("before any statement that references a parameter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_is_no_op_for_empty_setup_or_no_parameters()
    {
        var parameters = Parameters(("x", "int", "7"));

        SetupSqlExecution.Validate(SqlEngine.SqlServer, null, parameters);
        SetupSqlExecution.Validate(SqlEngine.SqlServer, "   ", parameters);
        SetupSqlExecution.Validate(SqlEngine.SqlServer, "SELECT @x;", Array.Empty<SqlHarnessParameter>());
        SetupSqlExecution.Validate(SqlEngine.Postgres, "SELECT @x;", parameters);
    }

    private static SqlHarnessParameter[] Parameters(params (string Name, string Type, string Value)[] inputs) =>
        inputs.Select(input => SqlParameterParser.Bind(new SqlHarnessParameterInput(input.Name, input.Type, input.Value)))
            .ToArray();
}