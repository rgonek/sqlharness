using System.Net;
using System.Text;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardSettingsTests
{
    private const string ValidBody = """
        { "journal": { "enabled": true, "storeSensitive": true,
                       "retention": { "enabled": true, "maxAgeDays": 7, "maxSizeMb": 100 } },
          "dashboard": { "autoStart": true, "port": 50000, "idleShutdownHours": 2, "safetyNotifications": true },
          "mutations": { "defaultMode": "operator", "verification": "click", "approvalTimeoutSeconds": 150 },
          "diagnostics": { "enabled": true } }
        """;

    private static string ConfigPath(TempHome home) => Path.Combine(home.Path, "config.json");

    private static HttpRequestMessage Put(RunningDashboard server, string body, string path = "/api/settings",
        bool header = true, string? origin = null, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (header)
            request.Headers.Add(DashboardSecurity.WriteHeader, "1");
        request.Headers.Add("Origin", origin ?? $"http://127.0.0.1:{server.Port}");
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Get_reports_missing_file_with_defaults()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var body = await Json(await client.GetAsync("/api/settings"));

        Assert.Equal("missing", body.GetProperty("status").GetString());
        Assert.Equal(ConfigPath(home), body.GetProperty("path").GetString());
        Assert.False(body.GetProperty("settings").GetProperty("journal").GetProperty("storeSensitive").GetBoolean());
        Assert.False(body.GetProperty("settings").GetProperty("journal").GetProperty("operationAnnotations").GetBoolean());
        Assert.Equal(47800, body.GetProperty("settings").GetProperty("dashboard").GetProperty("port").GetInt32());
        Assert.False(body.GetProperty("settings").GetProperty("diagnostics").GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("settings").GetProperty("dashboard").GetProperty("safetyNotifications").GetBoolean());
    }

    [Fact]
    public async Task Get_reports_invalid_file_without_its_contents()
    {
        using var home = new TempHome();
        File.WriteAllText(ConfigPath(home), "{ \"SQLH_SECRET_MARKER\": ");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var text = await client.GetStringAsync("/api/settings");

        Assert.Contains("\"status\":\"invalid\"", text);
        Assert.DoesNotContain("SQLH_SECRET_MARKER", text);
    }

    [Fact]
    public async Task Put_writes_the_file_and_keeps_the_current_port()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        var response = await client.SendAsync(Put(server, ValidBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var loaded = SqlHarnessConfigLoader.Load(ConfigPath(home));
        Assert.Equal(SqlHarnessConfigStatus.Valid, loaded.Status);
        Assert.True(loaded.Config.Journal.StoreSensitive);
        Assert.Equal(7, loaded.Config.Journal.Retention.MaxAgeDays);
        Assert.True(loaded.Config.Diagnostics.Enabled);
        Assert.Equal("operator", loaded.Config.Mutations.DefaultMode);
        Assert.Equal("click", loaded.Config.Mutations.Verification);
        Assert.Equal(150, loaded.Config.Mutations.ApprovalTimeoutSeconds);
        Assert.Equal(47800, loaded.Config.Dashboard.Port);
        Assert.True(loaded.Config.Dashboard.SafetyNotifications);
        Assert.Equal("valid", (await Json(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Put_persists_custom_execution_timeout_and_get_reads_it_back()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        var body = ValidBody.Replace("\"diagnostics\": { \"enabled\": true }", "\"diagnostics\": { \"enabled\": true }, \"execution\": { \"defaultTimeoutSeconds\": 75 }");

        var response = await client.SendAsync(Put(server, body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(75, SqlHarnessConfigLoader.Load(ConfigPath(home)).Config.Execution.DefaultTimeoutSeconds);
        Assert.Equal(75, (await Json(await client.GetAsync("/api/settings"))).GetProperty("settings")
            .GetProperty("execution").GetProperty("defaultTimeoutSeconds").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_persists_operation_annotations_and_get_reads_it_back(bool enabled)
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        var body = ValidBody.Replace("\"enabled\": true, \"storeSensitive\": true", $"\"enabled\": true, \"storeSensitive\": true, \"operationAnnotations\": {enabled.ToString().ToLowerInvariant()}");

        var response = await client.SendAsync(Put(server, body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = (await Json(await client.GetAsync("/api/settings"))).GetProperty("settings");
        Assert.Equal(enabled, settings.GetProperty("journal").GetProperty("operationAnnotations").GetBoolean());
        Assert.True(settings.GetProperty("dashboard").GetProperty("autoStart").GetBoolean());
        Assert.Equal("operator", settings.GetProperty("mutations").GetProperty("defaultMode").GetString());
    }

    [Fact]
    public async Task Put_with_operation_annotations_without_journal_preserves_the_existing_file()
    {
        using var home = new TempHome();
        const string original = """{ "journal": { "enabled": true, "operationAnnotations": false } }""";
        File.WriteAllText(ConfigPath(home), original);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        var invalid = ValidBody.Replace("\"enabled\": true, \"storeSensitive\": true", "\"enabled\": false, \"storeSensitive\": true, \"operationAnnotations\": true");

        var response = await client.SendAsync(Put(server, invalid));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("journal.operationAnnotations", error.GetProperty("field").GetString());
        Assert.Equal(original, File.ReadAllText(ConfigPath(home)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task Put_rejects_execution_timeout_outside_bounds(int timeoutSeconds)
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        var body = ValidBody.Replace("\"diagnostics\": { \"enabled\": true }", $"\"diagnostics\": {{ \"enabled\": true }}, \"execution\": {{ \"defaultTimeoutSeconds\": {timeoutSeconds} }}");

        var response = await client.SendAsync(Put(server, body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("execution.defaultTimeoutSeconds", error.GetProperty("field").GetString());
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_with_invalid_body_returns_field_errors_and_leaves_the_file()
    {
        using var home = new TempHome();
        File.WriteAllText(ConfigPath(home), "{}");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        var response = await client.SendAsync(Put(server, ValidBody.Replace("\"maxAgeDays\": 7", "\"maxAgeDays\": 0")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("journal.retention.maxAgeDays", error.GetProperty("field").GetString());
        Assert.Equal("{}", File.ReadAllText(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_with_unsupported_mutation_verification_preserves_the_existing_value()
    {
        using var home = new TempHome();
        const string original = """{ "mutations": { "verification": "click" } }""";
        File.WriteAllText(ConfigPath(home), original);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        const string unsupported = """{ "mutations": { "verification": "webauthn" } }""";

        var response = await client.SendAsync(Put(server, unsupported));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("mutations.verification", error.GetProperty("field").GetString());
        Assert.Equal(original, File.ReadAllText(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_with_numeric_mutation_verification_preserves_the_existing_value()
    {
        using var home = new TempHome();
        const string original = """{ "mutations": { "verification": "click" } }""";
        File.WriteAllText(ConfigPath(home), original);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        var response = await client.SendAsync(Put(server, """{ "mutations": { "verification": "0" } }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("mutations.verification", error.GetProperty("field").GetString());
        Assert.Equal(original, File.ReadAllText(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_over_an_invalid_file_requires_explicit_overwrite()
    {
        using var home = new TempHome();
        File.WriteAllText(ConfigPath(home), "{ broken");
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Put(server, ValidBody))).StatusCode);
        Assert.Equal("{ broken", File.ReadAllText(ConfigPath(home)));

        var forced = await client.SendAsync(Put(server, ValidBody, "/api/settings?overwriteInvalid=true"));
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        Assert.Equal(SqlHarnessConfigStatus.Valid, SqlHarnessConfigLoader.Load(ConfigPath(home)).Status);
    }

    [Fact]
    public async Task Put_from_foreign_origin_or_without_header_is_forbidden()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Put(server, ValidBody, header: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Put(server, ValidBody, origin: "http://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Put(server, ValidBody, contentType: "text/plain"))).StatusCode);
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_with_null_origin_is_forbidden()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Put(server, ValidBody, origin: "null"))).StatusCode);
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_from_localhost_origin_is_allowed()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(server, ValidBody, origin: $"http://localhost:{server.Port}"))).StatusCode);
    }

    [Fact]
    public async Task Put_without_cookie_is_unauthorized()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, _) = dashboard;
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Put(server, ValidBody))).StatusCode);
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_never_accepts_the_token_exchange()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, _) = dashboard;
        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseUri };

        var response = await anonymous.SendAsync(Put(server, ValidBody, $"/api/settings?t={server.Token}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(File.Exists(ConfigPath(home)));
    }

    [Fact]
    public async Task Put_without_origin_header_is_allowed()
    {
        using var home = new TempHome();
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;
        var request = Put(server, ValidBody);
        request.Headers.Remove("Origin");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }
}