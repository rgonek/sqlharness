using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Npgsql;

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

    [Fact]
    public void Explicit_verify_full_is_authoritative_without_a_root()
    {
        var built = Build(false, new PostgresTransport(PostgresSslMode.VerifyFull, null));
        Assert.Equal(SslMode.VerifyFull, built.SslMode);
        Assert.Equal(GssEncryptionMode.Disable, built.GssEncryptionMode);
        Assert.True(string.IsNullOrEmpty(built.RootCertificate));
    }

    [Fact]
    public void Explicit_verify_full_uses_the_root_certificate()
    {
        var pem = TestCertificates.WritePem();
        try
        {
            var built = Build(false, new PostgresTransport(PostgresSslMode.VerifyFull, pem));
            Assert.Equal(SslMode.VerifyFull, built.SslMode);
            Assert.Equal(GssEncryptionMode.Disable, built.GssEncryptionMode);
            Assert.Equal(pem, built.RootCertificate);
        }
        finally { File.Delete(pem); }
    }

    [Fact]
    public void Explicit_verify_ca_uses_the_root_certificate()
    {
        var pem = TestCertificates.WritePem();
        try
        {
            var built = Build(false, new PostgresTransport(PostgresSslMode.VerifyCa, pem));
            Assert.Equal(SslMode.VerifyCA, built.SslMode);
            Assert.Equal(GssEncryptionMode.Disable, built.GssEncryptionMode);
            Assert.Equal(pem, built.RootCertificate);
        }
        finally { File.Delete(pem); }
    }

    [Fact]
    public void Explicit_require_and_disable_map_to_those_ssl_modes()
    {
        var require = Build(false, new PostgresTransport(PostgresSslMode.Require, null));
        var disable = Build(true, new PostgresTransport(PostgresSslMode.Disable, null));
        Assert.Equal(SslMode.Require, require.SslMode);
        Assert.Equal(GssEncryptionMode.Disable, require.GssEncryptionMode);
        Assert.Equal(SslMode.Disable, disable.SslMode);
        Assert.Equal(GssEncryptionMode.Disable, disable.GssEncryptionMode);
    }

    [Fact]
    public void Missing_root_certificate_fails_without_the_password_or_the_path()
    {
        const string password = "pg-password-must-not-leak";
        var path = Path.Combine(Path.GetTempPath(), password + ".pem");
        File.Delete(path);
        var error = Reject(false, new PostgresTransport(PostgresSslMode.VerifyFull, path), password);
        Assert.Equal(PostgresTransportPolicy.RootUnreadable, error.Message);
        Assert.DoesNotContain(password, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_root_certificate_fails_without_the_password()
    {
        const string password = "pg-password-must-not-leak";
        var path = Path.Combine(Path.GetTempPath(), $"sqlharness-bad-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, "-----BEGIN CERTIFICATE-----\nnot-a-certificate\n-----END CERTIFICATE-----");
        try
        {
            var error = Reject(false, new PostgresTransport(PostgresSslMode.VerifyFull, path), password);
            Assert.Equal(PostgresTransportPolicy.RootUnreadable, error.Message);
            Assert.DoesNotContain(password, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("not-a-certificate", error.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true, PostgresSslMode.VerifyFull)]
    [InlineData(true, PostgresSslMode.Require)]
    [InlineData(false, PostgresSslMode.Disable)]
    public void Contradictory_trust_and_ssl_mode_are_rejected_without_the_password(bool trust, PostgresSslMode mode)
    {
        const string password = "pg-password-must-not-leak";
        var error = Reject(trust, new PostgresTransport(mode, null), password);
        Assert.Equal(PostgresTransportPolicy.ContradictsTrust, error.Message);
        Assert.DoesNotContain(password, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Root_certificate_without_a_verifying_mode_is_rejected_without_the_path()
    {
        const string password = "pg-password-must-not-leak";
        var error = Reject(false, new PostgresTransport(PostgresSslMode.Require, password), password);
        Assert.Equal(PostgresTransportPolicy.RootRequiresVerifying, error.Message);
        Assert.DoesNotContain(password, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_and_explicit_policies_are_named_on_the_identity_report()
    {
        var legacy = Target(true, null);
        var explicitMode = Target(false, new PostgresTransport(PostgresSslMode.VerifyFull, null));
        var legacyReport = PostgresEndpointIdentity.CreateReport(legacy, "192.0.2.1/32", "appdb");
        var explicitReport = PostgresEndpointIdentity.CreateReport(explicitMode, "192.0.2.1/32", "appdb");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal(PostgresTransportPolicy.Legacy, legacyReport.TransportPolicy);
        Assert.Equal("192.0.2.1/32", legacyReport.ActualServer);
        Assert.Contains("\"transportPolicy\":\"legacy\"", JsonSerializer.Serialize(legacyReport, options), StringComparison.Ordinal);
        Assert.Equal(PostgresTransportPolicy.Explicit, explicitReport.TransportPolicy);
        Assert.Contains("\"transportPolicy\":\"explicit\"", JsonSerializer.Serialize(explicitReport, options), StringComparison.Ordinal);

        var sqlServer = new SqlHarnessTargetIdentityReport("s", "d", "s", "d", "profile");
        Assert.DoesNotContain("transportPolicy", JsonSerializer.Serialize(sqlServer, options), StringComparison.Ordinal);
    }

    private static NpgsqlConnectionStringBuilder Build(bool trust, PostgresTransport? transport)
    {
        const string variable = "SQLHARNESS_PG_BUILDER_PASSWORD";
        Environment.SetEnvironmentVariable(variable, "connection-secret");
        try
        {
            return new NpgsqlConnectionStringBuilder(PostgresConnectionString.Build(Target(trust, transport, variable), 15));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private static SqlHarnessSafetyException Reject(bool trust, PostgresTransport transport, string password)
    {
        const string variable = "SQLHARNESS_PG_BUILDER_PASSWORD";
        Environment.SetEnvironmentVariable(variable, password);
        try
        {
            return Assert.Throws<SqlHarnessSafetyException>(() => PostgresConnectionString.Build(Target(trust, transport, variable), 15));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private static ResolvedTarget Target(bool trust, PostgresTransport? transport, string passwordVariable = "SQLHARNESS_PG_BUILDER_PASSWORD") =>
        new(
            "db.example.test",
            "appdb",
            AuthSpec.Parse("sql", "sqlharness", passwordVariable, trust),
            "profile",
            SqlEngine.Postgres,
            transport);
}

internal static class TestCertificates
{
    internal static string WritePem()
    {
        using var certificate = Create("sqlharness-test");
        var path = Path.Combine(Path.GetTempPath(), $"sqlharness-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, certificate.ExportCertificatePem());
        return path;
    }

    internal static X509Certificate2 Create(string commonName, params string[] dnsNames)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (dnsNames.Length > 0)
        {
            var alternative = new SubjectAlternativeNameBuilder();
            foreach (var dnsName in dnsNames)
                alternative.AddDnsName(dnsName);
            request.CertificateExtensions.Add(alternative.Build());
        }

        using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509Certificate2.CreateFromPem(signed.ExportCertificatePem());
    }
}