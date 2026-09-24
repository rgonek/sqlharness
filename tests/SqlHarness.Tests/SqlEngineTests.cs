using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SqlEngineTests
{
    [Theory]
    [InlineData(null, SqlEngine.SqlServer)]
    [InlineData("", SqlEngine.SqlServer)]
    [InlineData("sqlserver", SqlEngine.SqlServer)]
    [InlineData("SQLServer", SqlEngine.SqlServer)]
    [InlineData("postgres", SqlEngine.Postgres)]
    [InlineData("Postgres", SqlEngine.Postgres)]
    public void Parse_accepts_omitted_sqlserver_and_postgres(string? value, SqlEngine expected) =>
        Assert.Equal(expected, SqlEngineNames.Parse(value));

    [Fact]
    public void Parse_rejects_unknown_without_echoing_value()
    {
        const string secret = "engine-secret-never-emit";
        var error = Assert.Throws<SqlHarnessSafetyException>(() => SqlEngineNames.Parse(secret));
        Assert.Contains("Unknown engine", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.Message);
    }
}