using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresConnectionStringTests
{
    [Fact]
    public void Builds_disable_ssl_for_trust_server_certificate()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_PG_PASSWORD", "secret");
        try
        {
            var target = new ResolvedTarget(
                "localhost,5432", "appdb",
                AuthSpec.Parse("sql", "sqlharness", "SQLHARNESS_PG_PASSWORD", true),
                "profile", SqlEngine.Postgres);
            var cs = PostgresConnectionString.Build(target, 15);
            Assert.Contains("Host=localhost", cs, StringComparison.Ordinal);
            Assert.Contains("Port=5432", cs, StringComparison.Ordinal);
            Assert.Contains("Database=appdb", cs, StringComparison.Ordinal);
            Assert.Contains("Username=sqlharness", cs, StringComparison.Ordinal);
            Assert.Contains("SSL Mode=Disable", cs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", cs.Replace("Password=secret", "Password=***", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("SQLHARNESS_PG_PASSWORD", null); }
    }

    [Fact]
    public void Default_port_is_5432_and_require_ssl_without_trust()
    {
        Environment.SetEnvironmentVariable("P", "x");
        try
        {
            var target = new ResolvedTarget(
                "db.example.com", "appdb",
                AuthSpec.Parse("sql", "u", "P", false),
                "direct", SqlEngine.Postgres);
            var cs = PostgresConnectionString.Build(target, 15);
            Assert.Contains("Host=db.example.com", cs, StringComparison.Ordinal);
            Assert.Contains("Port=5432", cs, StringComparison.Ordinal);
            Assert.Contains("SSL Mode=Require", cs, StringComparison.OrdinalIgnoreCase);
        }
        finally { Environment.SetEnvironmentVariable("P", null); }
    }

    [Fact]
    public void Missing_password_env_fails_without_echoing_name_value_pair()
    {
        Environment.SetEnvironmentVariable("MISSING_PG_PASSWORD", null);
        var target = new ResolvedTarget(
            "localhost,5432", "appdb",
            AuthSpec.Parse("sql", "u", "MISSING_PG_PASSWORD", true),
            "profile", SqlEngine.Postgres);
        var error = Assert.Throws<SqlHarnessSafetyException>(() => PostgresConnectionString.Build(target, 15));
        Assert.Contains("MISSING_PG_PASSWORD", error.Message, StringComparison.Ordinal);
    }
}
