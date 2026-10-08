using System.Text.Json;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardProfilesTests
{
    private static string TargetsPath(TempHome home) => Path.Combine(home.Path, "targets.json");

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
