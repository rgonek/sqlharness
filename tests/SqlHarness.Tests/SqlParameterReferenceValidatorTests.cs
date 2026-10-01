using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public class SqlParameterReferenceValidatorTests
{
    [Fact]
    public void Validate_accepts_case_insensitive_references_across_multiple_batches()
    {
        var parameters = SqlParameterParser.Parse(["customerId:int=42", "active:bit=true"]);

        SqlParameterReferenceValidator.Validate(
            parameters,
            "SELECT * FROM dbo.Customers WHERE Id = @CUSTOMERID",
            "SELECT * FROM dbo.Customers WHERE Active = @active");
    }

    [Fact]
    public void Validate_rejects_a_supplied_parameter_that_is_not_referenced()
    {
        var parameters = SqlParameterParser.Parse(["customerId:int=42", "unused=ignored"]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            SqlParameterReferenceValidator.Validate(
                parameters,
                "SELECT * FROM dbo.Customers WHERE Id = @customerId"));

        Assert.Contains("@unused", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_malformed_SQL_fail_closed()
    {
        var parameters = SqlParameterParser.Parse(["customerId:int=42"]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            SqlParameterReferenceValidator.Validate(parameters, "SELECT * FROM [unterminated WHERE Id = @customerId"));

        Assert.Equal("SQL parameter references could not be parsed.", exception.Message);
    }

    [Fact]
    public void Sql_server_dialect_delegates_case_insensitive_references()
    {
        var parameters = SqlParameterParser.Parse(["customerId:int=42", "active:bit=true"]);

        SqlDialects.For(SqlEngine.SqlServer).ValidateParameterReferences(
            parameters,
            "SELECT * FROM dbo.Customers WHERE Id = @CUSTOMERID",
            "SELECT * FROM dbo.Customers WHERE Active = @active");
    }

    [Fact]
    public void T3_Validate_table_variable_is_local_not_required()
    {
        var required = SqlParameterReferences.Collect(
            SqlEngine.SqlServer,
            "DECLARE @t TABLE (Id int); INSERT @t (Id) VALUES (1); SELECT Id FROM @t");

        Assert.Empty(required);
    }

    [Theory]
    [InlineData("INSERT @x (Id) VALUES (1)")]
    [InlineData("SELECT Id FROM @x")]
    [InlineData("DECLARE @t TABLE (Id int); DELETE @t OUTPUT deleted.Id INTO @x (Id)")]
    public void T3_Validate_undeclared_table_target_is_rejected(string sql)
    {
        // A table-position @name can never be satisfied by a scalar --param:
        // it is not offered as a required parameter, and the classifier denies it.
        var required = SqlParameterReferences.Collect(SqlEngine.SqlServer, sql);

        Assert.DoesNotContain("@x", required, StringComparer.OrdinalIgnoreCase);
        var decision = new SqlSafetyClassifier().Classify(sql, SqlUsage.Query, "db", allowMutation: false, confirmDatabase: null);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    [InlineData("INSERT @x (Id) VALUES (1)")]
    [InlineData("SELECT Id FROM @x")]
    [InlineData("DECLARE @t TABLE (Id int); DELETE @t OUTPUT deleted.Id INTO @x (Id)")]
    public void T3b_Validation_report_denies_undeclared_table_position_name_end_to_end(string sql)
    {
        // Ruling R6: "not listed as a required parameter" never means "allowed".
        var report = ValidateOffline(sql);

        Assert.False(report.Allowed);
        Assert.Equal("rejected", report.Classification);
        Assert.Equal("unsupported_statement", report.Reason);
        Assert.DoesNotContain("x", report.RequiredParameters, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(report.MissingParameters);
        Assert.False(report.Executed);
    }

    [Theory]
    [InlineData("INSERT @x (Id) VALUES (1)")]
    [InlineData("SELECT Id FROM @x")]
    public void T3b_Validation_report_stays_denied_when_a_parameter_with_that_name_is_supplied(string sql)
    {
        var report = ValidateOffline(sql, "x:int=1");

        Assert.False(report.Allowed);
        Assert.Equal("unsupported_statement", report.Reason);
        Assert.False(report.Executed);
    }

    [Fact]
    public void T3_Validate_scalar_reference_next_to_table_variable_is_still_required()
    {
        var required = SqlParameterReferences.Collect(
            SqlEngine.SqlServer,
            "DECLARE @t TABLE (Id int); INSERT @t (Id) VALUES (@seed); SELECT Id FROM @t WHERE Id = @seed");

        Assert.Equal(["@seed"], required);
    }

    [Theory]
    [InlineData("t:int=1", "@t", "DECLARE @t TABLE (Id int); INSERT @t (Id) VALUES (1); SELECT Id FROM @t")]
    [InlineData("t:int=1", "@t", "DECLARE @t TABLE (Id int); SELECT 1")]
    [InlineData("x:int=1", "@x", "SELECT Id FROM @x")]
    [InlineData("x:int=1", "@x", "INSERT @x (Id) VALUES (1)")]
    public void T3_Validate_rejects_supplied_parameter_used_only_as_table_name(string declaration, string name, string sql)
    {
        var parameters = SqlParameterParser.Parse([declaration]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            SqlParameterReferenceValidator.Validate(parameters, sql));

        Assert.Contains(name, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void T2_Validate_SET_target_is_not_a_required_parameter()
    {
        var required = SqlParameterReferences.Collect(
            SqlEngine.SqlServer,
            "DECLARE @n int; SET @n = 5; SELECT @n");

        Assert.Empty(required);
    }

    // Offline only: the profile is never connected to and its password variable is never read.
    private static SqlValidationReport ValidateOffline(string sql, params string[] parameters) =>
        SqlValidation.Validate(
            new SqlTargetRequest("test", new Dictionary<string, string>()),
            sql,
            parameters,
            new Dictionary<string, TargetProfile>
            {
                ["test"] = new(
                    "server-unused", "database-unused", new Dictionary<string, string>(), "sql", "user-unused", "MUST_NOT_BE_READ", Engine: "sqlserver"),
            });

    [Fact]
    public void Sql_server_dialect_still_rejects_postgres_only_syntax()
    {
        var parameters = SqlParameterParser.Parse(["n:int=1"]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            SqlDialects.For(SqlEngine.SqlServer).ValidateParameterReferences(parameters, "SELECT @n::int"));

        Assert.Equal("SQL parameter references could not be parsed.", exception.Message);
    }
}