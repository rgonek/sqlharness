using System.Globalization;

using Npgsql;

using SqlHarness.Core.Targets;

namespace SqlHarness.Core.Postgres;

internal static class PostgresConnectionString
{
    internal static string Build(ResolvedTarget target, int connectTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(target);
        var (host, port) = ParseEndpoint(target.Server);
        // Cert and mode are checked before the password is read so a rejection cannot echo it.
        // Legacy trustServerCertificate stays a Disable/Require switch; it is not Npgsql TrustServerCertificate.
        PostgresTransportPolicy.EnsureAccepted(target);
        var mode = PostgresTransportPolicy.EffectiveMode(target);
        var passwordEnvVar = target.Auth.PasswordEnvVar;
        if (string.IsNullOrWhiteSpace(passwordEnvVar) || string.IsNullOrWhiteSpace(target.Auth.SqlUser))
        {
            throw new SqlHarnessSafetyException(
                "SQL authentication requires both a user and a password environment variable.");
        }

        var password = Environment.GetEnvironmentVariable(passwordEnvVar);
        if (string.IsNullOrEmpty(password))
        {
            throw new SqlHarnessSafetyException(
                $"SQL password environment variable '{passwordEnvVar}' is missing or empty.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = target.Database,
            Username = target.Auth.SqlUser,
            Password = password,
            Timeout = connectTimeoutSeconds,
            SslMode = ToNpgsqlSslMode(mode),
        };
        if (target.Transport?.RootCertificate is { } rootCertificate)
            builder.RootCertificate = rootCertificate;
        return builder.ConnectionString;
    }

    private static SslMode ToNpgsqlSslMode(PostgresSslMode mode) => mode switch
    {
        PostgresSslMode.VerifyFull => SslMode.VerifyFull,
        PostgresSslMode.VerifyCa => SslMode.VerifyCA,
        PostgresSslMode.Require => SslMode.Require,
        PostgresSslMode.Disable => SslMode.Disable,
        _ => throw new SqlHarnessSafetyException(PostgresTransportPolicy.InvalidSslMode),
    };

    private static (string Host, int Port) ParseEndpoint(string server)
    {
        var trimmed = server.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma < 0)
            return (trimmed, 5432);

        var host = trimmed[..comma].Trim();
        var portText = trimmed[(comma + 1)..].Trim();
        if (host.Length == 0 ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new SqlHarnessSafetyException("Postgres server must be host or host,port.");
        }

        return (host, port);
    }
}