using SqlHarness.Core.Targets;

namespace SqlHarness.Core.Postgres;

internal static class PostgresEndpointIdentity
{
    internal static bool Matches(ResolvedTarget expected, string actualServer, string actualDatabase)
    {
        _ = actualServer; // Retained for informational reporting; PostgreSQL hostname verification requires sslMode: verify-full.
        return string.Equals(expected.Database, actualDatabase, StringComparison.Ordinal);
    }

    internal static SqlHarnessTargetIdentityReport CreateReport(
        ResolvedTarget target,
        string actualServer,
        string actualDatabase) =>
        new(
            target.Server,
            target.Database,
            actualServer,
            actualDatabase,
            target.Mode,
            Engine: SqlEngineNames.Postgres,
            TransportPolicy: PostgresTransportPolicy.Label(target));
}