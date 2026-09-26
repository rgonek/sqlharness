using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

using Npgsql;

using SqlHarness.Core.Targets;

namespace SqlHarness.Core.Postgres;

internal interface IPostgresHostResolver
{
    IReadOnlyList<IPAddress> Resolve(string host);
}

internal readonly record struct PostgresEndpointObservation(
    IPAddress? ConnectedAddress,
    IReadOnlyList<string> AuthenticatedNames);

internal static class PostgresEndpointIdentity
{
    internal static bool Matches(
        ResolvedTarget expected,
        string actualDatabase,
        PostgresEndpointObservation observation,
        IPostgresHostResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(resolver);

        if (!string.Equals(expected.Database, actualDatabase, StringComparison.Ordinal))
            return false;

        var host = HostWithoutPort(expected.Server);
        if (IsLoopback(host))
            return true;

        var bare = BareHost(host);
        var mode = PostgresTransportPolicy.EffectiveMode(expected);
        if (mode is PostgresSslMode.Require or PostgresSslMode.Disable)
        {
            if (!IPAddress.TryParse(bare, out var requested))
                return false;
            return observation.ConnectedAddress is not null && observation.ConnectedAddress.Equals(requested);
        }

        var names = observation.AuthenticatedNames ?? [];
        if (!HostIsAuthenticated(bare, names))
            return false;
        if (observation.ConnectedAddress is null)
            return false;

        IReadOnlyList<IPAddress> answers;
        try
        {
            answers = resolver.Resolve(bare);
        }
        catch (Exception)
        {
            return false;
        }

        if (answers is null)
            return false;

        foreach (var answer in answers)
        {
            if (answer is not null && answer.Equals(observation.ConnectedAddress))
                return true;
        }

        return false;
    }

    internal static PostgresEndpointObservation WithVerifyFullFallback(
        ResolvedTarget target,
        PostgresEndpointObservation observation)
    {
        var names = observation.AuthenticatedNames ?? [];
        if (PostgresTransportPolicy.EffectiveMode(target) != PostgresSslMode.VerifyFull || names.Count > 0)
            return new PostgresEndpointObservation(observation.ConnectedAddress, names);

        return new PostgresEndpointObservation(
            observation.ConnectedAddress,
            [AuthenticationHost(target.Server)]);
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

    internal static string AuthenticationHost(string server) => BareHost(HostWithoutPort(server));

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "::1" or "[::1]";

    private static string HostWithoutPort(string server)
    {
        var trimmed = server.Trim();
        var comma = trimmed.IndexOf(',');
        return comma < 0 ? trimmed : trimmed[..comma].Trim();
    }

    private static string BareHost(string host) =>
        host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    private static bool HostIsAuthenticated(string bareHost, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (string.Equals(name, bareHost, StringComparison.OrdinalIgnoreCase))
                return true;
            if (IsSingleLabelWildcard(name, bareHost))
                return true;
            if (IPAddress.TryParse(bareHost, out var requested) &&
                IPAddress.TryParse(BareHost(name), out var listed) &&
                requested.Equals(listed))
                return true;
        }

        return false;
    }

    private static bool IsSingleLabelWildcard(string pattern, string host)
    {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal) || pattern.IndexOf('*', 1) >= 0)
            return false;

        var suffix = pattern[1..];
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var label = host[..^suffix.Length];
        return label.Length > 0 && label.IndexOf('.') < 0;
    }
}

internal sealed class SystemPostgresHostResolver : IPostgresHostResolver
{
    internal static readonly SystemPostgresHostResolver Instance = new();

    public IReadOnlyList<IPAddress> Resolve(string host)
    {
        var bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (IPAddress.TryParse(bare, out var address))
            return [address];

        return Dns.GetHostAddresses(bare);
    }
}

internal static class NpgsqlEndpointObservation
{
    internal static PostgresEndpointObservation Read(NpgsqlConnection connection)
    {
        try
        {
            var connector = typeof(NpgsqlConnection)
                .GetProperty("Connector", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(connection);
            if (connector is null)
                return new PostgresEndpointObservation(null, []);

            var endpoint = connector.GetType()
                .GetProperty("ConnectedEndPoint", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(connector);
            var address = endpoint is IPEndPoint ip ? ip.Address : null;
            var names = new List<string>();
            if (connector.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(connector) is SslStream ssl &&
                ssl.RemoteCertificate is { } remote)
            {
                using var certificate = new X509Certificate2(remote);
                names.AddRange(CertificateNames(certificate));
            }

            return new PostgresEndpointObservation(address, names);
        }
        catch (Exception)
        {
            return new PostgresEndpointObservation(null, []);
        }
    }

    internal static IReadOnlyList<string> CertificateNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension alternative)
                continue;

            var names = new List<string>();
            foreach (var dns in alternative.EnumerateDnsNames())
                names.Add(dns);
            foreach (var address in alternative.EnumerateIPAddresses())
                names.Add(address.ToString());
            if (names.Count > 0)
                return names;
        }

        var commonName = certificate.GetNameInfo(X509NameType.DnsName, false);
        return string.IsNullOrWhiteSpace(commonName) ? [] : [commonName];
    }
}
