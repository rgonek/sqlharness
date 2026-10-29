using System.Net;
using System.Text;
using System.Text.Json;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardProfilesTests
{
    private static string TargetsPath(TempHome home) => Path.Combine(home.Path, "targets.json");

    [Fact]
    public async Task Put_requires_guard_and_rejects_duplicate_members_and_stale_revision()
    {
        using var home = new TempHome();
        var path = TargetsPath(home);
        File.WriteAllText(path, "{\"p\":{\"server\":\"s\",\"database\":\"d\",\"auth\":\"integrated\",\"vars\":{}}}");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        var revision = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement.GetProperty("revision").GetString();
        using var noHeader = new HttpRequestMessage(HttpMethod.Put, "/api/profiles") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noHeader)).StatusCode);
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        using var noCookie = Request("{}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(noCookie)).StatusCode);
        using var foreign = Request("{}");
        foreign.Headers.Add("Origin", "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        using var badHost = Request("{}");
        badHost.Headers.Host = $"attacker.example:{server.Port}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(badHost)).StatusCode);
        using var oversized = Request(new string('x', 1024 * 1024 + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(oversized)).StatusCode);
        using var unknown = Request("{\"name\":\"p\",\"expectedRevision\":\"x\",\"profile\":{},\"path\":\"elsewhere\"}");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(unknown)).StatusCode);
        using var nestedDuplicate = Request("{\"name\":\"p\",\"expectedRevision\":\"x\",\"profile\":{\"server\":\"s\",\"SERVER\":\"t\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(nestedDuplicate)).StatusCode);
        using var duplicate = Request("{\"name\":\"p\",\"name\":\"p\"}");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(duplicate)).StatusCode);
        var body = JsonSerializer.Serialize(new { name = "p", expectedRevision = revision, profile = new { server = "new", database = "d", auth = "integrated", vars = new Dictionary<string, string>() } });
        var invalidAuth = JsonSerializer.Serialize(new { name = "p", expectedRevision = revision, profile = new { server = "s", database = "d", auth = "sql", vars = new Dictionary<string, string>() } });
        using var invalid = Request(invalidAuth);
        var invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal("auth", JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("errors")[0].GetProperty("field").GetString());
        using var valid = Request(body);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(valid)).StatusCode);
        using var stale = Request(body);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(stale)).StatusCode);
        Assert.Contains("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task Guard_rejects_before_attempting_to_read_locked_file()
    {
        using var home = new TempHome();
        var path = TargetsPath(home);
        File.WriteAllText(path, "secret marker");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        using var rejected = Request("{}");
        var response = await anonymous.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("secret marker", await response.Content.ReadAsStringAsync());
        using var guarded = Request("{\"name\":\"p\",\"expectedRevision\":\"x\",\"profile\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
        var failure = await client.SendAsync(guarded);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.DoesNotContain("secret marker", await failure.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Request(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/profiles") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-SqlHarness-Dashboard", "1");
        return request;
    }

    [Fact]
    public async Task Profiles_report_inherited_policy_and_follow_global_changes()
    {
        using var home = new TempHome();
        File.WriteAllText(TargetsPath(home), """
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {}, "environment": "staging",
              "mutations": { "writer": { "sameAsReader": true } } } }
            """);
        var config = Path.Combine(home.Path, "config.json");
        File.WriteAllText(config, """{ "mutations": { "defaultMode": "operator" } }""");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;
        var profile = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement.GetProperty("profiles")[0];
        Assert.Equal("operator", profile.GetProperty("mutationMode").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("configuredMode").ValueKind);
        Assert.Equal("global", profile.GetProperty("modeSource").GetString());
        Assert.True(profile.GetProperty("writerConfigured").GetBoolean());
        File.WriteAllText(config, """{ "mutations": { "defaultMode": "yolo" } }""");
        profile = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement.GetProperty("profiles")[0];
        Assert.Equal("yolo", profile.GetProperty("effectiveMode").GetString());
        Assert.True(profile.GetProperty("productionAcknowledgementRequired").GetBoolean());
    }

    [Fact]
    public async Task Profiles_list_names_templates_and_auth_metadata()
    {
        using var home = new TempHome();
        File.WriteAllText(TargetsPath(home), """
            {
              "zeta": { "server": "localhost,1433", "database": "db", "auth": "integrated", "vars": {} },
              "pg": { "engine": "postgres", "server": "pg.example", "database": "app_{env}", "auth": "sql",
                      "sqlUser": "reader", "passwordEnvVar": "SQLH_TEST_PG_PASSWORD", "sslMode": "verify-full",
                      "vars": { "env": "uat|test" } }
            }
            """);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var body = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement;

        Assert.Equal("valid", body.GetProperty("status").GetString());
        var profiles = body.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal(["pg", "zeta"], profiles.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal("postgres", profiles[0].GetProperty("engine").GetString());
        Assert.Equal("app_{env}", profiles[0].GetProperty("database").GetString());
        Assert.Equal("SQLH_TEST_PG_PASSWORD", profiles[0].GetProperty("passwordEnvVar").GetString());
        Assert.Equal("env", profiles[0].GetProperty("vars")[0].GetProperty("name").GetString());
        Assert.Equal("uat|test", profiles[0].GetProperty("vars")[0].GetProperty("rule").GetString());
        Assert.Equal("sqlserver", profiles[1].GetProperty("engine").GetString());
    }

    [Theory]
    [InlineData("\"engine\": \"postgres\", \"sslMode\": \"verify-full\"", "verify-full")]
    [InlineData("\"engine\": \"postgres\", \"trustServerCertificate\": false", "require (legacy)")]
    [InlineData("\"engine\": \"postgres\", \"trustServerCertificate\": true", "disable (legacy)")]
    [InlineData("\"trustServerCertificate\": true", "trust server certificate")]
    [InlineData("\"trustServerCertificate\": false", "verify")]
    public async Task Profiles_report_the_effective_tls_label(string fields, string expected)
    {
        using var home = new TempHome();
        File.WriteAllText(TargetsPath(home), $$"""
            { "p": { {{fields}}, "server": "s", "database": "d", "auth": "sql", "sqlUser": "u", "passwordEnvVar": "SQLH_TEST_TLS_PW", "vars": {} } }
            """);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var body = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement;

        Assert.Equal("valid", body.GetProperty("status").GetString());
        Assert.Equal(expected, body.GetProperty("profiles")[0].GetProperty("tls").GetString());
    }

    [Fact]
    public async Task Profiles_expose_mutation_policy_without_writer_environment_values()
    {
        using var home = new TempHome();
        File.WriteAllText(TargetsPath(home), """
            { "p": { "server": "s", "database": "d", "auth": "integrated", "vars": {}, "environment": "staging",
                     "mutations": { "mode": "yolo", "acknowledgeProductionYolo": true,
                                    "writer": { "sqlUser": "writer", "passwordEnvVar": "SQLH_WRITER_PASSWORD" } } } }
            """);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var text = await client.GetStringAsync("/api/profiles");
        var profile = JsonDocument.Parse(text).RootElement.GetProperty("profiles")[0];

        Assert.Equal("staging", profile.GetProperty("environment").GetString());
        Assert.Equal("yolo", profile.GetProperty("mutationMode").GetString());
        Assert.Equal("separate", profile.GetProperty("mutationWriterKind").GetString());
        Assert.True(profile.GetProperty("acknowledgeProductionYolo").GetBoolean());
        Assert.Equal("SQLH_WRITER_PASSWORD", profile.GetProperty("editableProfile").GetProperty("mutations").GetProperty("writer").GetProperty("passwordEnvVar").GetString());
    }

    [Fact]
    public async Task Profiles_never_contain_password_values()
    {
        using var home = new TempHome();
        var variable = "SQLH_TEST_PW_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "SQLH_PASSWORD_VALUE_MARKER");
        try
        {
            File.WriteAllText(TargetsPath(home), $$"""
                { "p": { "server": "s", "database": "d", "auth": "sql", "sqlUser": "u", "passwordEnvVar": "{{variable}}", "vars": {} } }
                """);
            await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
            var (_, client) = dashboard;

            var text = await client.GetStringAsync("/api/profiles");

            Assert.Contains(variable, text);
            Assert.DoesNotContain("SQLH_PASSWORD_VALUE_MARKER", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Missing_and_invalid_files_return_empty_lists_without_file_content()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var missing = JsonDocument.Parse(await client.GetStringAsync("/api/profiles")).RootElement;
        Assert.Equal("missing", missing.GetProperty("status").GetString());
        Assert.Empty(missing.GetProperty("profiles").EnumerateArray());

        File.WriteAllText(TargetsPath(home), "{ \"SQLH_TARGET_MARKER\": ");
        var text = await client.GetStringAsync("/api/profiles");
        Assert.Contains("\"status\":\"invalid\"", text);
        Assert.DoesNotContain("SQLH_TARGET_MARKER", text);
        Assert.DoesNotContain(home.Path.Replace("\\", "\\\\"), text);
    }
}