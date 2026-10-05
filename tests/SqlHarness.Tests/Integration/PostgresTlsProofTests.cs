using System.Net;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Integration;

public sealed class PostgresTlsProofTests
{
    [PostgresTlsProofFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Bad_host_is_rejected()
    {
        var proof = PostgresTlsProof.Load();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Connect(
            proof, proof.BadHost, false, new PostgresTransport(PostgresSslMode.VerifyFull, proof.TrustedRootCertificate)));
        AssertNotMissingPassword(error);
        AssertNoPassword(error, proof);
    }

    [PostgresTlsProofFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Untrusted_ca_is_rejected()
    {
        var proof = PostgresTlsProof.Load();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Connect(
            proof, proof.Host, false, new PostgresTransport(PostgresSslMode.VerifyFull, proof.UntrustedRootCertificate)));
        AssertNotMissingPassword(error);
        AssertNoPassword(error, proof);
    }

    [PostgresTlsProofFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Trusted_ca_connects_and_reports_explicit_policy()
    {
        var proof = PostgresTlsProof.Load();
        await using var session = await Connect(
            proof, proof.Host, false, new PostgresTransport(PostgresSslMode.VerifyFull, proof.TrustedRootCertificate));
        Assert.Equal(proof.Database, session.Identity.ActualDatabase, StringComparer.Ordinal);
        Assert.Equal(PostgresTransportPolicy.Explicit, session.Identity.TransportPolicy);
    }

    [PostgresTlsProofFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Disabled_tls_connects_to_the_plain_address()
    {
        var proof = PostgresTlsProof.Load();
        await using var session = await Connect(
            proof, proof.PlainHost, true, new PostgresTransport(PostgresSslMode.Disable, null));
        Assert.Equal(proof.Database, session.Identity.ActualDatabase, StringComparer.Ordinal);
        Assert.Equal(PostgresTransportPolicy.Explicit, session.Identity.TransportPolicy);
    }

    private static Task<ISqlSession> Connect(
        PostgresTlsProof proof, string host, bool trust, PostgresTransport transport)
    {
        var server = proof.Port == 5432 ? host : $"{host},{proof.Port}";
        var target = new ResolvedTarget(
            server,
            proof.Database,
            AuthSpec.Parse("sql", proof.User, proof.PasswordEnvVar, trust),
            "direct",
            SqlEngine.Postgres,
            transport);
        return new NpgsqlSessionFactory(5).ConnectAsync(target, CancellationToken.None);
    }

    private static void AssertNotMissingPassword(Exception exception) =>
        Assert.DoesNotContain("is missing or empty", exception.Message, StringComparison.Ordinal);

    private static void AssertNoPassword(Exception exception, PostgresTlsProof proof)
    {
        var password = Environment.GetEnvironmentVariable(proof.PasswordEnvVar);
        if (!string.IsNullOrEmpty(password))
            Assert.DoesNotContain(password, exception.ToString(), StringComparison.Ordinal);
    }
}

internal sealed class PostgresTlsProofFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_PG_TLS_PROOF";

    public PostgresTlsProofFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not configured.";
    }
}

internal sealed class PostgresTlsProof
{
    internal string Host { get; }
    internal int Port { get; }
    internal string Database { get; }
    internal string User { get; }
    internal string PasswordEnvVar { get; }
    internal string TrustedRootCertificate { get; }
    internal string UntrustedRootCertificate { get; }
    internal string BadHost { get; }
    internal string PlainHost { get; }

    private PostgresTlsProof(
        string host, int port, string database, string user, string passwordEnvVar,
        string trustedRootCertificate, string untrustedRootCertificate, string badHost, string plainHost)
    {
        Host = host;
        Port = port;
        Database = database;
        User = user;
        PasswordEnvVar = passwordEnvVar;
        TrustedRootCertificate = trustedRootCertificate;
        UntrustedRootCertificate = untrustedRootCertificate;
        BadHost = badHost;
        PlainHost = plainHost;
    }

    internal static PostgresTlsProof Load()
    {
        var configured = Environment.GetEnvironmentVariable(PostgresTlsProofFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("SQLHARNESS_PG_TLS_PROOF is not configured.");

        var full = Path.GetFullPath(configured);
        if (string.Equals(full, Path.GetFullPath(SqlHarnessPaths.TargetsFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLHARNESS_PG_TLS_PROOF must not be the user targets file.");

        PostgresTlsProofDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PostgresTlsProofDocument>(
                File.ReadAllText(full),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("TLS proof file is invalid.");
        }

        if (document is null ||
            string.IsNullOrWhiteSpace(document.Host) ||
            string.IsNullOrWhiteSpace(document.Database) ||
            string.IsNullOrWhiteSpace(document.User) ||
            string.IsNullOrWhiteSpace(document.PasswordEnvVar) ||
            string.IsNullOrWhiteSpace(document.TrustedRootCertificate) ||
            string.IsNullOrWhiteSpace(document.UntrustedRootCertificate) ||
            string.IsNullOrWhiteSpace(document.BadHost) ||
            string.IsNullOrWhiteSpace(document.PlainHost) ||
            document.Port is < 1 or > 65535 ||
            !IPAddress.TryParse(document.PlainHost, out _))
            throw new InvalidOperationException("TLS proof file is invalid.");

        if (string.Equals(document.PasswordEnvVar, "CIVICLENS_POSTGRES_PASSWORD", StringComparison.Ordinal))
            throw new InvalidOperationException("TLS proof must not use CIVICLENS_POSTGRES_PASSWORD.");

        return new PostgresTlsProof(
            document.Host, document.Port, document.Database, document.User, document.PasswordEnvVar,
            document.TrustedRootCertificate, document.UntrustedRootCertificate, document.BadHost, document.PlainHost);
    }

    private sealed class PostgresTlsProofDocument
    {
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? Database { get; set; }
        public string? User { get; set; }
        public string? PasswordEnvVar { get; set; }
        public string? TrustedRootCertificate { get; set; }
        public string? UntrustedRootCertificate { get; set; }
        public string? BadHost { get; set; }
        public string? PlainHost { get; set; }
    }
}