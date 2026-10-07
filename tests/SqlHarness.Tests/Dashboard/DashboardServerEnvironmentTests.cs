using System.Net;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

/// <summary>Mutates process environment and the test base directory, so it runs alone.</summary>
[Collection(SqlHarnessHomeCollection.Name)]
public sealed class DashboardServerEnvironmentTests
{
    private static readonly string[] Variables =
    [
        "ASPNETCORE_URLS",
        "ASPNETCORE_HTTP_PORTS",
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT",
        "Kestrel__Endpoints__Wide__Url",
        "ASPNETCORE_Kestrel__Endpoints__WidePrefixed__Url",
        "DOTNET_Kestrel__Endpoints__WideDotnet__Url",
    ];

    [Fact]
    public async Task Environment_and_appsettings_do_not_add_listeners_or_a_developer_error_page()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.GetDirectoryName(home.DatabasePath)!);
        await File.WriteAllTextAsync(home.DatabasePath, "this is not a sqlite database, it is long enough to have a header");
        var saved = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var development = Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json");
        Assert.False(File.Exists(settings));
        Assert.False(File.Exists(development));
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:5999");
        Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "5997");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Kestrel__Endpoints__Wide__Url", "http://0.0.0.0:5998");
        Environment.SetEnvironmentVariable("ASPNETCORE_Kestrel__Endpoints__WidePrefixed__Url", "http://0.0.0.0:5996");
        Environment.SetEnvironmentVariable("DOTNET_Kestrel__Endpoints__WideDotnet__Url", "http://0.0.0.0:5995");
        const string Wide = """{ "Kestrel": { "Endpoints": { "File": { "Url": "http://0.0.0.0:5994" } } } }""";
        await File.WriteAllTextAsync(settings, Wide);
        await File.WriteAllTextAsync(development, Wide.Replace("5994", "5993", StringComparison.Ordinal));
        try
        {
            var (server, client) = await DashboardServerTests.StartAuthenticated(home);
            await using var _ = server;

            Assert.Equal("127.0.0.1", server.BaseUri.Host);
            var address = Assert.Single(server.Addresses);
            Assert.Equal(new Uri($"http://127.0.0.1:{server.Port}"), new Uri(address));

            var error = await client.GetAsync("/api/sessions");
            Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
            Assert.Empty(await error.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
            File.Delete(settings);
            File.Delete(development);
        }
    }
}