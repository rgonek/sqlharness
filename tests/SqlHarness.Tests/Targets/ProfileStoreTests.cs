using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

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

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"targets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
