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
    public void Production_yolo_requires_acknowledgement()
    {
        var error = LoadError("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {},
                     "environment": "Prod", "mutations": { "mode": "yolo", "writer": { "sameAsReader": true } } } }
            """);

        Assert.Contains("yolo on a prod or staging profile needs mutations.acknowledgeProductionYolo: true.", error.Message);
    }

    [Fact]
    public void Production_yolo_loads_when_acknowledged_and_ack_is_inert_on_other_modes()
    {
        var production = LoadProfiles("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {},
                     "environment": "Prod", "mutations": { "mode": "yolo", "writer": { "sameAsReader": true }, "acknowledgeProductionYolo": true } } }
            """);
        var nonYolo = LoadProfiles("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {},
                     "mutations": { "mode": "never", "acknowledgeProductionYolo": true } } }
            """);

        Assert.Equal(true, ProfileProperty(ProfileProperty(production["p"], "Mutations")!, "AcknowledgeProductionYolo"));
        Assert.Equal(true, ProfileProperty(ProfileProperty(nonYolo["p"], "Mutations")!, "AcknowledgeProductionYolo"));
    }

    [Fact]
    public void Mutation_mode_requires_a_writer()
    {
        var error = LoadError("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {}, "mutations": { "mode": "operator" } } }
            """);

        Assert.Contains("A profile that allows mutations needs mutations.writer.", error.Message);
    }

    [Fact]
    public void Mutation_mode_rejects_numeric_enum_strings()
    {
        var error = LoadError("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {},
                     "mutations": { "mode": "2", "writer": { "sameAsReader": true } } } }
            """);

        Assert.Contains("A profile mutation mode is invalid.", error.Message);
    }

    [Fact]
    public void Mutation_writer_rejects_both_credential_forms()
    {
        var error = LoadError("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {},
                     "mutations": { "mode": "operator", "writer": { "sqlUser": "writer", "passwordEnvVar": "SQLH_WRITER_PASSWORD", "sameAsReader": true } } } }
            """);

        Assert.Contains("mutations.writer needs sqlUser and passwordEnvVar, or sameAsReader.", error.Message);
    }

    [Theory]
    [InlineData("{ \"sameAsReader\": true, \"sqlUser\": \"writer\" }")]
    [InlineData("{ \"sameAsReader\": true, \"passwordEnvVar\": \"SQLH_WRITER_PASSWORD\" }")]
    [InlineData("{ \"sqlUser\": \"writer\" }")]
    [InlineData("{ \"passwordEnvVar\": \"SQLH_WRITER_PASSWORD\" }")]
    public void Mutation_writer_rejects_mixed_or_partial_credential_forms(string writer)
    {
        var error = LoadError($$"""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {},
                     "mutations": { "mode": "operator", "writer": {{writer}} } } }
            """);

        Assert.Contains("mutations.writer needs sqlUser and passwordEnvVar, or sameAsReader.", error.Message);
    }

    [Fact]
    public void Valid_mutation_profile_loads_and_profiles_without_mutations_keep_defaults()
    {
        var valid = LoadProfiles("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {}, "environment": "dev",
                     "mutations": { "mode": "yolo", "writer": { "sameAsReader": true } } } }
            """);
        var legacy = LoadProfiles("""
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {} } }
            """);

        Assert.Equal("yolo", ProfileProperty(ProfileProperty(valid["p"], "Mutations")!, "Mode"));
        Assert.Equal("dev", ProfileProperty(valid["p"], "Environment"));
        Assert.Null(ProfileProperty(legacy["p"], "Mutations"));
        Assert.Null(ProfileProperty(legacy["p"], "Environment"));
    }

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

    [Fact]
    public void Example_targets_file_loads_and_remote_pg_omits_the_root()
    {
        var path = FindRepositoryFile("docs", "example-targets.json");
        var profiles = ProfileStore.Load(path);

        Assert.Contains("prod-eu", profiles.Keys);
        Assert.Contains("local-pg", profiles.Keys);
        var remote = profiles["remote-pg"];
        Assert.Equal("postgres", remote.Engine);
        Assert.Equal("verify-full", remote.SslMode);
        Assert.Null(remote.RootCertificate);
        Assert.False(remote.TrustServerCertificate);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate repository file: {string.Join('/', path)}");
    }

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"targets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static SqlHarnessSafetyException LoadError(string content)
    {
        var path = WriteTemp(content);
        try
        {
            return Assert.Throws<SqlHarnessSafetyException>(() => ProfileStore.Load(path));
        }
        finally { File.Delete(path); }
    }

    private static IReadOnlyDictionary<string, TargetProfile> LoadProfiles(string content)
    {
        var path = WriteTemp(content);
        try
        {
            return ProfileStore.Load(path);
        }
        finally { File.Delete(path); }
    }

    private static object? ProfileProperty(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);
}
