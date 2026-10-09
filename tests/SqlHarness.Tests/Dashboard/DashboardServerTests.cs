using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardServerTests
{
    /// <summary>A started server and a client holding its session cookie; disposing stops both.</summary>
    internal sealed class AuthenticatedDashboard(RunningDashboard server, HttpClient client) : IAsyncDisposable
    {
        public void Deconstruct(out RunningDashboard runningServer, out HttpClient authenticatedClient) =>
            (runningServer, authenticatedClient) = (server, client);

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await server.DisposeAsync();
        }
    }

    internal static async Task<AuthenticatedDashboard> StartAuthenticated(TempHome home)
    {
        var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses())
            {
                ConfigPath = Path.Combine(home.Path, "config.json"),
                TargetsPath = Path.Combine(home.Path, "targets.json"),
            },
            CancellationToken.None);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = server.BaseUri };
        var dashboard = new AuthenticatedDashboard(server, client);
        try
        {
            using var exchange = await client.GetAsync($"/?t={server.Token}");
            Assert.Equal(HttpStatusCode.Redirect, exchange.StatusCode);
            return dashboard;
        }
        catch
        {
            await dashboard.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task Token_exchange_sets_a_strict_http_only_cookie_and_redirects_without_the_token()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseUri };

        var response = await client.GetAsync($"/sessions?t={server.Token}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sessions", response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains(DashboardSecurity.CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Requests_without_cookie_or_with_foreign_host_are_rejected()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/?t=wrong")).StatusCode);

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        rebinding.Headers.Host = $"attacker.example:{server.Port}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(rebinding)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
        using var viaLocalhost = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        viaLocalhost.Headers.Host = $"localhost:{server.Port}";
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(viaLocalhost)).StatusCode);
    }

    [Fact]
    public async Task Only_get_endpoints_and_the_settings_put_exist()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/api/operations/1")).StatusCode);
        var writes = server.Endpoints.Where(endpoint => !endpoint.Methods.SequenceEqual(["GET", "HEAD"])).ToArray();
        var settingsPut = Assert.Single(writes);
        Assert.Equal("/api/settings", settingsPut.Route);
        Assert.Equal(["PUT"], settingsPut.Methods);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsync("/api/sessions", null)).StatusCode);
        Assert.Contains(server.Endpoints, endpoint => endpoint.Route == "/api/live");
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Api_serves_journal_data_and_validates_input()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        var handle = seed.Operation(JournalSeed.Session("cli:a"), operation: "measure", benchmark: JournalSeed.Benchmark());
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        using var sessions = JsonDocument.Parse(await client.GetStringAsync("/api/sessions?limit=10"));
        Assert.Equal("claude", sessions.RootElement.GetProperty("items")[0].GetProperty("agentKind").GetString());

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{handle.OperationId}"));
        Assert.Equal("measure", detail.RootElement.GetProperty("operation").GetProperty("operation").GetString());

        // The server's process reader sees no live process, so the journaled host is gone.
        var orphan = seed.Operation(JournalSeed.Session("cli:gone", hostPid: 6161), complete: false);
        using var abandoned = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{orphan.OperationId}"));
        Assert.Equal("abandoned", abandoned.RootElement.GetProperty("operation").GetProperty("status").GetString());

        var raw = await client.GetAsync($"/api/plans/{new string('A', 64)}");
        Assert.Equal("application/xml", raw.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", raw.Content.Headers.ContentDisposition!.DispositionType);

        using var distilled = JsonDocument.Parse(await client.GetStringAsync($"/api/plans/{new string('A', 64)}?view=distilled"));
        Assert.True(distilled.RootElement.TryGetProperty("statements", out _));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/operations/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plans/{new string('B', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/plans/not-a-hash")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stats?from=yesterday")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operations?limit=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stats?from=2026-10-01T00:00:00Z")).StatusCode);
    }

    [Fact]
    public async Task Stats_and_operation_list_share_profile_and_dimension_filter_contract()
    {
        using var home = new TempHome();
        File.WriteAllText(Path.Combine(home.Path, "targets.json"), """
            {"app":{"server":"srv","database":"db-{region}","vars":{"region":".*"},"auth":"sql"}}
            """);
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:matching"), profile: "app",
            variables: new Dictionary<string, string> { ["region"] = "eu:west" });
        seed.Operation(JournalSeed.Session("cli:other"), profile: "app",
            variables: new Dictionary<string, string> { ["region"] = "us" });
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;
        var encodedDimensions = Uri.EscapeDataString("""{"region":"eu:west"}""");

        using var stats = JsonDocument.Parse(await client.GetStringAsync("/api/stats?profile=app"));
        using var operations = JsonDocument.Parse(await client.GetStringAsync($"/api/operations?profile=app&dimensions={encodedDimensions}"));

        Assert.Equal(2, stats.RootElement.GetProperty("profileDimensions").GetProperty("operations").GetInt32());
        Assert.Equal("region", stats.RootElement.GetProperty("profileDimensions").GetProperty("dimensions")[0].GetProperty("name").GetString());
        Assert.Single(operations.RootElement.GetProperty("items").EnumerateArray());
        var operation = operations.RootElement.GetProperty("items")[0];
        Assert.Equal("db", operation.GetProperty("database").GetString());
        var operationId = operation.GetProperty("id").GetInt64();
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{operationId}"));
        var dimension = detail.RootElement.GetProperty("dimensions").GetProperty("values")[0];
        Assert.Equal("region", dimension.GetProperty("name").GetString());
        Assert.Equal("eu:west", dimension.GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/operations?dimensions=%7B%22region%22%3A42%7D")).StatusCode);
    }

    [Fact]
    public async Task Unknown_non_api_paths_serve_the_spa_entry_and_unknown_api_paths_are_404()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal("text/html", (await client.GetAsync("/sessions/42")).Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nope")).StatusCode);
    }

    [Fact]
    public async Task Busy_preferred_port_falls_back_to_the_next_free_port()
    {
        using var home = new TempHome();
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, busy, new FakeProcesses()), CancellationToken.None);

        Assert.NotEqual(busy, server.Port);
        Assert.InRange(server.Port, busy + 1, busy + DashboardServer.PortAttempts);
    }

    [Fact]
    public async Task Exhausted_candidate_ports_fall_back_to_an_ephemeral_loopback_port()
    {
        using var home = new TempHome();
        // The top port is the only candidate; if another process already holds it, it is busy either way.
        using var blocker = new TcpListener(IPAddress.Loopback, IPEndPoint.MaxPort);
        try
        {
            blocker.Start();
        }
        catch (SocketException)
        {
        }

        await using var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, IPEndPoint.MaxPort, new FakeProcesses()), CancellationToken.None);

        Assert.NotEqual(IPEndPoint.MaxPort, server.Port);
        Assert.InRange(server.Port, 1, IPEndPoint.MaxPort - 1);
        Assert.Equal(new Uri($"http://127.0.0.1:{server.Port}"), new Uri(Assert.Single(server.Addresses)));
    }

    [Fact]
    public async Task Head_requests_are_served_like_get()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        using var head = new HttpRequestMessage(HttpMethod.Head, "/api/sessions");
        var response = await client.SendAsync(head);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Token_query_key_is_removed_regardless_of_case()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseUri };

        var response = await client.GetAsync($"/sessions?T={server.Token}&view=x");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/sessions?view=x", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("///evil.example/x")]
    [InlineData("/%5Cevil.example/x")]
    [InlineData("/%5C/evil.example/x")]
    public async Task Token_exchange_redirect_stays_on_the_dashboard(string path)
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        var response = await client.GetAsync(new Uri($"http://127.0.0.1:{server.Port}{path}?t={server.Token}"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/", location);
        Assert.False(location.Length > 1 && location[1] is '/' or '\\', location);
        Assert.DoesNotContain("%5C", location[..Math.Min(4, location.Length)], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Corrupt_stored_plan_is_unprocessable()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        seed.Operation(JournalSeed.Session("cli:a"));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO plans (hash, format, raw_size, gz, first_seen) VALUES ($h, 'showplan-xml', 3, x'00010203', '2026-10-07T09:00:00Z');";
            insert.Parameters.AddWithValue("$h", new string('C', 64));
            insert.ExecuteNonQuery();
        }

        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/api/plans/{new string('C', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/api/plans/{new string('C', 64)}?view=distilled")).StatusCode);
    }

    [Fact]
    public async Task Server_errors_are_empty_and_keep_security_headers()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.GetDirectoryName(home.DatabasePath)!);
        await File.WriteAllTextAsync(home.DatabasePath, "this is not a sqlite database, it is long enough to have a header");
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Preferred_port_outside_the_tcp_range_is_rejected(int port)
    {
        using var home = new TempHome();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, port, new FakeProcesses()), CancellationToken.None));
    }
}