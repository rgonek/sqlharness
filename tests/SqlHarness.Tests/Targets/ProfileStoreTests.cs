using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;
using SqlHarness.Tests.Postgres;

namespace SqlHarness.Tests.Targets;

public sealed class ProfileStoreTests
{
    [Fact]
    public void Loads_example_profile()
    {
        var path = WriteTemp("""
            {
              "prod-eu": {
                "server": "contoso.database.windows.net",
                "database": "contoso-{tenant}-{env}",
                "vars": { "tenant": "^[a-z0-9]{3,20}$", "env": "^(dev|test|uat)$" },
                "auth": "ad-default"
              }
            }
            """);
        try
        {
            var profiles = ProfileStore.Load(path);

            var profile = Assert.Single(profiles).Value;
            Assert.Equal("contoso.database.windows.net", profile.Server);
            Assert.Equal("contoso-{tenant}-{env}", profile.Database);
            Assert.Equal("^[a-z0-9]{3,20}$", profile.Vars["tenant"]);
            Assert.Equal(AuthStrategy.AdDefault, AuthSpec.Parse(profile.Auth, profile.SqlUser, profile.PasswordEnvVar, profile.TrustServerCertificate).Strategy);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_returns_empty_set() =>
        Assert.Empty(ProfileStore.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));

    [Fact]
    public void Malformed_json_reports_path_without_content()
    {
        const string secret = "SUPER_SECRET_CONTENT";
        var path = WriteTemp("{" + secret);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Contains(path, error.Message);
            Assert.DoesNotContain(secret, error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unknown_field_is_rejected_without_content_echo()
    {
        const string secret = "SUPER_SECRET_FIELD";
        var path = WriteTemp("""
            { "prod": { "server": "s", "database": "d", "vars": {}, "auth": "integrated", "SUPER_SECRET_FIELD": true } }
            """);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Contains(path, error.Message);
            Assert.DoesNotContain(secret, error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{ \"prod\": { \"server\": \"s\", \"database\": \"d\", \"vars\": {}, \"auth\": \"integrated\" }, \"prod\": { \"server\": \"other\", \"database\": \"d\", \"vars\": {}, \"auth\": \"integrated\" } }")]
    [InlineData("{ \"prod\": { \"server\": \"s\", \"server\": \"other\", \"database\": \"d\", \"vars\": {}, \"auth\": \"integrated\" } }")]
    [InlineData("{ \"prod\": { \"server\": \"s\", \"database\": \"d\", \"vars\": { \"tenant\": \"x\", \"tenant\": \"y\" }, \"auth\": \"integrated\" } }")]
    public void Duplicate_names_and_members_are_rejected(string json)
    {
        var path = WriteTemp(json);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Contains(path, error.Message);
            Assert.DoesNotContain("other", error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Loads_postgres_engine_and_defaults_omitted_engine_to_null()
    {
        var path = WriteTemp("""
            {
              "pg": {
                "engine": "postgres",
                "server": "localhost,5432",
                "database": "appdb",
                "vars": {},
                "auth": "sql",
                "sqlUser": "sqlharness",
                "passwordEnvVar": "SQLHARNESS_PG_PASSWORD",
                "trustServerCertificate": true
              },
              "mssql": {
                "server": "s",
                "database": "d",
                "vars": {},
                "auth": "integrated"
              }
            }
            """);
        try
        {
            var profiles = ProfileStore.Load(path);
            Assert.Equal("postgres", profiles["pg"].Engine);
            Assert.Null(profiles["mssql"].Engine);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Null_regex_value_is_rejected_as_safety_error()
    {
        var path = WriteTemp("""
            { "prod": { "server": "s", "database": "d-{tenant}", "vars": { "tenant": null }, "auth": "integrated" } }
            """);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Contains(path, error.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("sslMode", "verify-full")]
    [InlineData("rootCertificate", "ca.pem")]
    public void Sql_server_profile_rejects_postgres_transport_fields(string field, string value)
    {
        var path = WriteTemp($$"""
            { "prod": { "server": "s", "database": "d", "vars": {}, "auth": "integrated", "{{field}}": {{JsonSerializer.Serialize(value)}} } }
            """);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Equal(PostgresTransportPolicy.SqlServerFields, error.Message);
            Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Postgres_profile_rejects_a_mode_that_contradicts_trust()
    {
        var path = WriteTemp("""
            {
              "pg": {
                "engine": "postgres", "server": "db.example.test", "database": "appdb", "vars": {},
                "auth": "sql", "sqlUser": "u", "passwordEnvVar": "P",
                "trustServerCertificate": true, "sslMode": "verify-full"
              }
            }
            """);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Equal(PostgresTransportPolicy.ContradictsTrust, error.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(" verify-full")]
    [InlineData("Verify-Full")]
    [InlineData("")]
    public void Postgres_profile_rejects_ssl_mode_that_is_not_an_exact_token(string sslMode)
    {
        var path = WriteTemp($$"""
            {
              "pg": {
                "engine": "postgres", "server": "db.example.test", "database": "appdb", "vars": {},
                "auth": "sql", "sqlUser": "u", "passwordEnvVar": "P",
                "sslMode": {{JsonSerializer.Serialize(sslMode)}}
              }
            }
            """);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Equal(PostgresTransportPolicy.InvalidSslMode, error.Message);
            if (sslMode.Length > 0)
                Assert.DoesNotContain(sslMode, error.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Postgres_profile_rejects_an_unknown_ssl_mode_without_echoing_it()
    {
        const string secret = "ssl-mode-must-not-leak";
        var path = WriteTemp($$"""
            {
              "pg": {
                "engine": "postgres", "server": "db.example.test", "database": "appdb", "vars": {},
                "auth": "sql", "sqlUser": "u", "passwordEnvVar": "P",
                "sslMode": {{JsonSerializer.Serialize(secret)}}
              }
            }
            """);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Equal(PostgresTransportPolicy.InvalidSslMode, error.Message);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Postgres_profile_rejects_a_root_without_a_verifying_mode()
    {
        const string secret = "root-path-must-not-leak";
        var path = WriteTemp($$"""
            {
              "pg": {
                "engine": "postgres", "server": "db.example.test", "database": "appdb", "vars": {},
                "auth": "sql", "sqlUser": "u", "passwordEnvVar": "P",
                "rootCertificate": {{JsonSerializer.Serialize(secret)}}
              }
            }
            """);
        try
        {
            var error = Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
            Assert.Equal(PostgresTransportPolicy.RootRequiresVerifying, error.Message);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Postgres_profile_loads_verify_full_and_a_readable_root()
    {
        var pem = TestCertificates.WritePem();
        var path = WriteTemp($$"""
            {
              "pg": {
                "engine": "postgres", "server": "db.example.test", "database": "appdb", "vars": {},
                "auth": "sql", "sqlUser": "u", "passwordEnvVar": "P",
                "trustServerCertificate": false,
                "sslMode": "verify-full",
                "rootCertificate": {{JsonSerializer.Serialize(pem)}}
              }
            }
            """);
        try
        {
            var profile = ProfileStore.Load(path)["pg"];
            Assert.Equal("verify-full", profile.SslMode);
            Assert.Equal(pem, profile.RootCertificate);
        }
        finally
        {
            File.Delete(path);
            File.Delete(pem);
        }
    }

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"targets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}