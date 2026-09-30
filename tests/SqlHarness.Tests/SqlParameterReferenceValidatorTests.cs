using SqlHarness.Core;
using SqlHarness.Core.Dialect;

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
    public void T2_Validate_SET_target_is_not_a_required_parameter()
    {
        var required = SqlParameterReferences.Collect(
            SqlEngine.SqlServer,
            "DECLARE @n int; SET @n = 5; SELECT @n");

        Assert.Empty(required);
    }

    [Fact]
    public void Sql_server_dialect_still_rejects_postgres_only_syntax()
    {
        var parameters = SqlParameterParser.Parse(["n:int=1"]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
            SqlDialects.For(SqlEngine.SqlServer).ValidateParameterReferences(parameters, "SELECT @n::int"));

        Assert.Equal("SQL parameter references could not be parsed.", exception.Message);
    }
}