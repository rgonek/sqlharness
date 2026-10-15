using System.Net;
using System.Net.Http.Json;
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

    internal static Task<AuthenticatedDashboard> StartAuthenticated(TempHome home) =>
        StartAuthenticated(home, options => options);

    internal static async Task<AuthenticatedDashboard> StartAuthenticated(
        TempHome home, Func<DashboardServerOptions, DashboardServerOptions> configure)
    {
        var server = await DashboardServer.StartAsync(
            configure(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses())
            {
                ConfigPath = Path.Combine(home.Path, "config.json"),
                TargetsPath = Path.Combine(home.Path, "targets.json"),
            }),
            CancellationToken.None);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = server.BaseUri };
        var dashboard = new AuthenticatedDashboard(server, client);
        try
        {
            using var exchange = await client.GetAsync($"/?t={server.Token}");
            Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
            return dashboard;
        }
        catch
        {
            await dashboard.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task Price_refresh_requires_the_write_protections()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        using var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noHeader)).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
        foreign.Headers.Add(DashboardSecurity.WriteHeader, "1");
        foreign.Headers.Add("Origin", "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);

        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        using var unauthenticated = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
        unauthenticated.Headers.Add(DashboardSecurity.WriteHeader, "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(unauthenticated)).StatusCode);
    }

    [Fact]
    public async Task Shutdown_requires_the_write_protections_and_stops_after_successful_response()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(DashboardSecurity.WriteHeader, "1");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.True(server.Stopping.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Rejected_shutdown_requests_never_stop_the_server()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;
        using var wrongCookie = ShutdownRequest();
        wrongCookie.Headers.Add("Cookie", $"{DashboardSecurity.CookieName}=wrong");
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(wrongCookie)).StatusCode);

        using var missingCookie = ShutdownRequest();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(missingCookie)).StatusCode);

        using var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown") { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noHeader)).StatusCode);

        using var foreignOrigin = ShutdownRequest();
        foreignOrigin.Headers.Add("Origin", "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreignOrigin)).StatusCode);

        using var queryToken = ShutdownRequest($"?t={server.Token}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(queryToken)).StatusCode);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/shutdown")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/api/shutdown")).StatusCode);
        Assert.False(server.Stopping.IsCancellationRequested);

        static HttpRequestMessage ShutdownRequest(string suffix = "")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown" + suffix) { Content = JsonContent.Create(new { }) };
            request.Headers.Add(DashboardSecurity.WriteHeader, "1");
            return request;
        }
    }

    [Fact]
    public async Task Price_refresh_writes_the_reduced_table_and_failure_keeps_it()
    {
        using var home = new TempHome();
        var pricesPath = Path.Combine(home.Path, "data", "model-prices.json");
        var answer = """{"openai":{"models":{"gpt-6-sol":{"name":"n","cost":{"input":1.5,"output":9}}}}}""";
        var fail = false;
        await using var dashboard = await StartAuthenticated(home, options => options with
        {
            PricesPath = pricesPath,
            PricesFetcher = _ => fail ? throw new HttpRequestException("down") : Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(answer))),
        });
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.OK, (await Refresh(client)).StatusCode);
        var written = File.ReadAllText(pricesPath);
        Assert.Contains("\"gpt-6-sol\"", written);
        Assert.DoesNotContain("\"name\"", written);

        fail = true;
        var failed = await Refresh(client);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(written, File.ReadAllText(pricesPath));
        Assert.DoesNotContain("down", await failed.Content.ReadAsStringAsync());

        static Task<HttpResponseMessage> Refresh(HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
            request.Headers.Add(DashboardSecurity.WriteHeader, "1");
            return client.SendAsync(request);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"openai":{"models":{"m":{"name":"n","cost":{"output":9}}}}}""")]
    [InlineData("""{"other":{"models":{"m":{"cost":{"input":1}}}}}""")]
    public async Task Price_refresh_rejects_a_table_without_usable_prices(string answer)
    {
        using var home = new TempHome();
        var pricesPath = Path.Combine(home.Path, "data", "model-prices.json");
        await using var dashboard = await StartAuthenticated(home, options => options with
        {
            PricesPath = pricesPath,
            PricesFetcher = _ => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(answer))),
        });
        var (_, client) = dashboard;

        var response = await Refresh(client);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(File.Exists(pricesPath));
    }

    [Fact]
    public async Task Price_refresh_keeps_the_existing_file_when_the_answer_has_no_usable_prices()
    {
        using var home = new TempHome();
        var pricesPath = Path.Combine(home.Path, "data", "model-prices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pricesPath)!);
        File.WriteAllText(pricesPath, "existing");
        await using var dashboard = await StartAuthenticated(home, options => options with
        {
            PricesPath = pricesPath,
            PricesFetcher = _ => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"))),
        });
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.BadGateway, (await Refresh(client)).StatusCode);
        Assert.Equal("existing", File.ReadAllText(pricesPath));
    }

    [Fact]
    public async Task Capped_read_fails_on_a_body_over_the_limit_without_a_content_length()
    {
        using var small = new MemoryStream(new byte[100]);
        using var ok = await DashboardPrices.ReadCappedAsync(small, 100, CancellationToken.None);
        Assert.Equal(100, ok.Length);

        using var large = new MemoryStream(new byte[101]);
        await Assert.ThrowsAsync<InvalidDataException>(() => DashboardPrices.ReadCappedAsync(large, 100, CancellationToken.None));
    }

    [Fact]
    public async Task Price_refresh_times_out_with_a_502_and_writes_nothing()
    {
        using var home = new TempHome();
        var pricesPath = Path.Combine(home.Path, "data", "model-prices.json");

        var result = await DashboardPrices.RefreshAsync(
            pricesPath,
            async token => { await Task.Delay(Timeout.Infinite, token); return Stream.Null; },
            new JsonSerializerOptions(),
            CancellationToken.None,
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(502, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(File.Exists(pricesPath));
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(DashboardSecurity.WriteHeader, "1");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Token_exchange_answers_a_same_origin_hand_off_page_with_a_long_lived_cookie()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseUri };

        var response = await client.GetAsync($"/sessions?t={server.Token}&x=1");

        // Not a redirect: a redirect would continue the browser's cross-site navigation from the
        // file:// launch page, and SameSite=Strict would withhold the cookie on it (401 today).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("http-equiv=\"refresh\"", body);
        Assert.Contains("url=/sessions?x=1", body);
        Assert.DoesNotContain(server.Token, body);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains(DashboardSecurity.CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=34560000", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Supplied_token_is_used_and_survives_a_restart()
    {
        using var home = new TempHome();
        var options = new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()) { Token = "fixed-token-value-123" };
        var cookies = new CookieContainer();
        await using (var server = await DashboardServer.StartAsync(options, CancellationToken.None))
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = server.BaseUri };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/?t=fixed-token-value-123")).StatusCode);
        }

        await using (var restarted = await DashboardServer.StartAsync(options, CancellationToken.None))
        {
            // The cookie from the first run authenticates against the restarted server (new port, same token).
            foreach (Cookie cookie in cookies.GetAllCookies())
                cookies.Add(new Cookie(cookie.Name, cookie.Value, "/", restarted.BaseUri.Host));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = restarted.BaseUri };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
        }
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
    public async Task Only_get_endpoints_and_the_four_guarded_writes_exist()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/api/operations/1")).StatusCode);
        var writes = server.Endpoints.Where(endpoint => !endpoint.Methods.SequenceEqual(["GET", "HEAD"])).ToArray();
        Assert.Equal(4, writes.Length);
        var settingsPut = Assert.Single(writes, endpoint => endpoint.Route == "/api/settings");
        Assert.Equal(["PUT"], settingsPut.Methods);
        var pricesRefresh = Assert.Single(writes, endpoint => endpoint.Route == "/api/prices/refresh");
        Assert.Equal(["POST"], pricesRefresh.Methods);
        var annotationPatch = Assert.Single(writes, endpoint => endpoint.Route == "/api/sessions/{id:long}/annotation");
        Assert.Equal(["PATCH"], annotationPatch.Methods);
        var shutdown = Assert.Single(writes, endpoint => endpoint.Route == "/api/shutdown");
        Assert.Equal(["POST"], shutdown.Methods);
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
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sessions?archived=maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions?archived=any")).StatusCode);
        using (var anyCase = JsonDocument.Parse(await client.GetStringAsync("/api/sessions?archived=Any")))
            Assert.NotEqual(0, anyCase.RootElement.GetProperty("items").GetArrayLength());
        using (var archivedOnly = JsonDocument.Parse(await client.GetStringAsync("/api/sessions?archived=TRUE")))
            Assert.Equal(0, archivedOnly.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stats?from=2026-10-01T00:00:00Z")).StatusCode);
    }

    [Fact]
    public async Task Compare_history_endpoint_pages_attempt_summaries()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:compare-page");
        seed.Compare(session, "orders", "whole", "base", "v1");
        seed.Compare(session, "orders", "whole", "v1", "v2");
        var id = Assert.Single(new JournalReader(home.DatabasePath, new FakeProcesses())
            .Sessions(new SessionQuery()).Items).Id;
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var first = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{id}/compares?limit=1"));
        var firstAttempt = first.RootElement.GetProperty("items")[0];
        var cursor = first.RootElement.GetProperty("nextCursor").GetInt64();
        using var second = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{id}/compares?cursor={cursor}&limit=1"));

        Assert.NotEqual(firstAttempt.GetProperty("operationId").GetInt64(),
            second.RootElement.GetProperty("items")[0].GetProperty("operationId").GetInt64());
        Assert.Equal("orders", second.RootElement.GetProperty("items")[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Agents_route_lists_kinds_as_key_count_pairs_and_detail_carries_agent()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var handle = seed.Operation(JournalSeed.Session("cli:a"));
        seed.Operation(JournalSeed.Session("cli:b", agent: "codex"));
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var agents = JsonDocument.Parse(await client.GetStringAsync("/api/agents"));
        var items = agents.RootElement.EnumerateArray().ToList();
        Assert.All(items, item => Assert.Equal(["key", "count"], item.EnumerateObject().Select(p => p.Name).ToArray()));
        Assert.Contains(items, item => item.GetProperty("key").GetString() == "claude" && item.GetProperty("count").GetInt32() == 1);
        Assert.Contains(items, item => item.GetProperty("key").GetString() == "codex" && item.GetProperty("count").GetInt32() == 1);

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{handle.OperationId}"));
        Assert.True(detail.RootElement.TryGetProperty("agent", out _));
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
            variables: new Dictionary<string, string> { ["region"] = "eu:west", ["component"] = "api" });
        seed.Operation(JournalSeed.Session("cli:other"), profile: "app",
            variables: new Dictionary<string, string> { ["region"] = "us", ["component"] = "portal" });
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;
        var encodedDimensions = Uri.EscapeDataString("""{"region":"eu:west"}""");

        using var stats = JsonDocument.Parse(await client.GetStringAsync("/api/stats?profile=app&rowDimension=region&columnDimension=component"));
        using var operations = JsonDocument.Parse(await client.GetStringAsync($"/api/operations?profile=app&dimensions={encodedDimensions}"));

        Assert.Equal(2, stats.RootElement.GetProperty("profileDimensions").GetProperty("operations").GetInt32());
        var matrix = stats.RootElement.GetProperty("profileDimensions").GetProperty("matrix");
        Assert.Equal("region", matrix.GetProperty("rowDimension").GetString());
        Assert.Equal(2, matrix.GetProperty("cells").GetArrayLength());
        Assert.Equal(2, matrix.GetProperty("totals").GetProperty("operations").GetInt32());
        Assert.Single(operations.RootElement.GetProperty("items").EnumerateArray());
        var operation = operations.RootElement.GetProperty("items")[0];
        Assert.Equal("db", operation.GetProperty("database").GetString());
        var operationId = operation.GetProperty("id").GetInt64();
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{operationId}"));
        var dimension = detail.RootElement.GetProperty("dimensions").GetProperty("values").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "region");
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("url=/sessions?view=x", body);
        Assert.DoesNotContain(server.Token, body);
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var marker = "url=";
        var start = body.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var location = System.Net.WebUtility.HtmlDecode(body[start..body.IndexOf('"', start)]);
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

    [Fact]
    public async Task Sessions_reject_an_overlong_search()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        var response = await client.GetAsync("/api/sessions?q=" + new string('a', 201));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}