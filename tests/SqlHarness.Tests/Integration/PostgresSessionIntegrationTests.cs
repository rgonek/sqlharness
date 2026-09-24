using Npgsql;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Integration;

public sealed class PostgresSessionIntegrationTests
{
    [PostgresIntegrationFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Connect_via_NpgsqlSessionFactory_reads_identity()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.Variable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.Host));
        Assert.False(string.IsNullOrWhiteSpace(builder.Database));
        Assert.False(string.IsNullOrWhiteSpace(builder.Username));
        Assert.False(string.IsNullOrEmpty(builder.Password));

        var passwordEnvVar = "SQLHARNESS_PG_INTEGRATION_PASSWORD_" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable(passwordEnvVar);
        Environment.SetEnvironmentVariable(passwordEnvVar, builder.Password);
        try
        {
            var server = builder.Port is > 0 and not 5432
                ? $"{builder.Host},{builder.Port}"
                : builder.Host!;
            var trust = builder.SslMode is SslMode.Disable or SslMode.Prefer or SslMode.Allow;
            var target = new ResolvedTarget(
                server,
                builder.Database!,
                AuthSpec.Parse("sql", builder.Username, passwordEnvVar, trust),
                "direct",
                SqlEngine.Postgres);

            var factory = new NpgsqlSessionFactory();
            await using var session = await factory.ConnectAsync(target, CancellationToken.None);

            Assert.Equal(builder.Database, session.Identity.ActualDatabase, StringComparer.Ordinal);
            Assert.Equal(SqlEngineNames.Postgres, session.Identity.Engine);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordEnvVar, previous);
        }
    }
}