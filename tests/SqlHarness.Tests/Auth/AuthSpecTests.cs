using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;

namespace SqlHarness.Tests.Auth;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class AuthSpecTests
{
    [Theory]
    [InlineData("azure-cli", AuthStrategy.AzureCli)]
    [InlineData("ad-default", AuthStrategy.AdDefault)]
    [InlineData("integrated", AuthStrategy.Integrated)]
    public void BuildConnectionString_UsesExpectedAuthentication(
        string name,
        AuthStrategy expectedStrategy)
    {
        var spec = AuthSpec.Parse(name, null, null, false);

        var connectionString = spec.BuildConnectionString("sql.example.test", "AppDb", 17);
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal(expectedStrategy, spec.Strategy);
        Assert.Equal("sql.example.test", builder.DataSource);
        Assert.Equal("AppDb", builder.InitialCatalog);
        Assert.True(builder.Encrypt);
        Assert.Equal(17, builder.ConnectTimeout);
        Assert.Equal(
            name == "ad-default" ? SqlAuthenticationMethod.ActiveDirectoryDefault : SqlAuthenticationMethod.NotSpecified,
            builder.Authentication);
        Assert.Equal(name == "integrated", builder.IntegratedSecurity);
        Assert.Equal(name == "azure-cli", spec.RequiresAccessToken);
        Assert.False(builder.TrustServerCertificate);
        Assert.DoesNotContain("Trust Server Certificate", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildConnectionString_SqlReadsPasswordFromEnvironmentAtBuildTime()
    {
        const string variable = "SQLHARNESS_TEST_PASSWORD";
        const string password = "fresh-build-time-secret";
        var original = Environment.GetEnvironmentVariable(variable);

        try
        {
            var spec = AuthSpec.Parse("sql", "app-user", variable, true);
            Environment.SetEnvironmentVariable(variable, password);

            var connectionString = spec.BuildConnectionString("localhost", "AppDb", 5);
            var builder = new SqlConnectionStringBuilder(connectionString);

            Assert.Equal("app-user", builder.UserID);
            Assert.Equal(password, builder.Password);
            Assert.Equal("localhost", builder.DataSource);
            Assert.Equal("AppDb", builder.InitialCatalog);
            Assert.True(builder.Encrypt);
            Assert.Equal(5, builder.ConnectTimeout);
            Assert.Equal(SqlAuthenticationMethod.NotSpecified, builder.Authentication);
            Assert.False(builder.IntegratedSecurity);
            Assert.True(builder.TrustServerCertificate);
            Assert.Contains("Trust Server Certificate=True", connectionString, StringComparison.OrdinalIgnoreCase);
            Assert.False(spec.RequiresAccessToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void BuildConnectionString_MissingSqlPasswordNamesVariableWithoutLeakingPassword()
    {
        const string variable = "SQLHARNESS_TEST_MISSING_PASSWORD";
        const string secret = "must-never-appear";
        var original = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            var spec = AuthSpec.Parse("sql", "app-user", variable, false);

            var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
                spec.BuildConnectionString("localhost", "AppDb", 5));

            Assert.Contains(variable, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void BuildConnectionString_EmptySqlPasswordThrows()
    {
        const string variable = "SQLHARNESS_TEST_EMPTY_PASSWORD";
        var original = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "");
            var spec = AuthSpec.Parse("sql", "app-user", variable, false);

            var exception = Assert.Throws<SqlHarnessSafetyException>(() =>
                spec.BuildConnectionString("localhost", "AppDb", 5));

            Assert.Contains(variable, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void Parse_UnknownStrategyThrows()
    {
        Assert.Throws<SqlHarnessSafetyException>(() =>
            AuthSpec.Parse("device-code", null, null, false));
    }

    [Fact]
    public void Parse_SqlWithoutCredentialsThrows()
    {
        Assert.Throws<SqlHarnessSafetyException>(() =>
            AuthSpec.Parse("sql", null, null, false));
    }
}