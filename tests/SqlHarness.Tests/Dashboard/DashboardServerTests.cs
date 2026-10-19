using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using SqlHarness.Core;
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
    public async Task Repro_endpoint_returns_script()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        var handle = seed.Operation(JournalSeed.Session("cli:repro"), operation: "measure", sql: "SELECT 42");
        seed.RecordConditions(handle);
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync($"/api/operations/{handle.OperationId}/repro?side=query&case=0&form=as-executed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("SELECT 42", document.RootElement.GetProperty("text").GetString());
        Assert.Equal("Complete", document.RootElement.GetProperty("completeness").GetString());
        Assert.Equal("As executed (sp_executesql)", document.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Repro_endpoint_without_sensitive_storage_is_unavailable()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var handle = seed.Operation(JournalSeed.Session("cli:repro-no-sensitive"), operation: "measure", sql: "SELECT 42");
        seed.RecordConditions(handle);
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync($"/api/operations/{handle.OperationId}/repro?side=query&case=0&form=as-executed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("text").ValueKind);
        Assert.Equal("Unavailable", document.RootElement.GetProperty("completeness").GetString());
        Assert.Equal("Script not available — enable storeSensitive", document.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Repro_endpoint_validates_side_form_operation_and_case()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        var handle = seed.Operation(JournalSeed.Session("cli:repro-validation"), operation: "measure", sql: "SELECT 42");
        seed.RecordConditions(handle);
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/operations/{handle.OperationId}/repro?side=other&case=0&form=as-executed")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/operations/{handle.OperationId}/repro?side=query&case=0&form=other")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/operations/{handle.OperationId}/repro?side=query&form=as-executed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/operations/{handle.OperationId}/repro?side=query&case=missing&form=as-executed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/operations/987654/repro?side=query&case=0&form=as-executed")).StatusCode);
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
    public async Task Only_read_endpoints_and_guarded_writes_exist()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/api/operations/1")).StatusCode);
        var writes = server.Endpoints.Where(endpoint => endpoint.Methods.Any(method => method is not "GET" and not "HEAD")).ToArray();
        Assert.Equal(8, writes.Length);
        var settingsPut = Assert.Single(writes, endpoint => endpoint.Route == "/api/settings");
        Assert.Equal(["PUT"], settingsPut.Methods);
        var pricesRefresh = Assert.Single(writes, endpoint => endpoint.Route == "/api/prices/refresh");
        Assert.Equal(["POST"], pricesRefresh.Methods);
        var annotationPatch = Assert.Single(writes, endpoint => endpoint.Route == "/api/sessions/{id:long}/annotation");
        Assert.Equal(["PATCH"], annotationPatch.Methods);
        var shutdown = Assert.Single(writes, endpoint => endpoint.Route == "/api/shutdown");
        Assert.Equal(["POST"], shutdown.Methods);
        var transcriptsClear = Assert.Single(writes, endpoint => endpoint.Route == "/api/transcripts/clear");
        Assert.Equal(["POST"], transcriptsClear.Methods);
        var mutationSubmit = Assert.Single(writes, endpoint => endpoint.Route == "/api/mutations");
        Assert.Equal(["POST"], mutationSubmit.Methods);
        var mutationApprove = Assert.Single(writes, endpoint => endpoint.Route == "/api/mutations/{id}/approve");
        Assert.Equal(["POST"], mutationApprove.Methods);
        var mutationDeny = Assert.Single(writes, endpoint => endpoint.Route == "/api/mutations/{id}/deny");
        Assert.Equal(["POST"], mutationDeny.Methods);
        Assert.Contains(server.Endpoints, endpoint => endpoint.Route == "/api/mutations/events" && endpoint.Methods.SequenceEqual(["GET"]));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsync("/api/sessions", null)).StatusCode);
        Assert.Contains(server.Endpoints, endpoint => endpoint.Route == "/api/live");
    }

    [Fact]
    public async Task Transcript_status_reports_the_setting_and_clear_requires_the_write_protections()
    {
        using var home = new TempHome();
        var configPath = Path.Combine(home.Path, "config.json");
        SqlHarnessConfigWriter.Write(configPath, SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with
            {
                AgentTranscripts = new AgentTranscriptsConfig { Enabled = true },
            },
        });
        _ = new JournalSeed(home.DatabasePath);
        await using var dashboard = await StartAuthenticated(home, options => options with { ConfigPath = configPath });
        var (_, client) = dashboard;

        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/transcripts"));
        Assert.True(status.RootElement.GetProperty("enabled").GetBoolean());

        using var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/transcripts/clear") { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noHeader)).StatusCode);

        using var clear = new HttpRequestMessage(HttpMethod.Post, "/api/transcripts/clear") { Content = JsonContent.Create(new { }) };
        clear.Headers.Add(DashboardSecurity.WriteHeader, "1");
        using var response = await client.SendAsync(clear);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var cleared = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(cleared.RootElement.GetProperty("enabled").GetBoolean());
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
        await SetStoreSensitiveAsync(home, true);
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        var handle = seed.Operation(JournalSeed.Session("cli:a"), operation: "measure", benchmark: JournalSeed.Benchmark());
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var sessions = JsonDocument.Parse(await client.GetStringAsync("/api/sessions?limit=10"));
        Assert.Equal("claude", sessions.RootElement.GetProperty("items")[0].GetProperty("agentKind").GetString());

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{handle.OperationId}"));
        Assert.Equal("measure", detail.RootElement.GetProperty("operation").GetProperty("operation").GetString());

        // The server's process reader sees no live process, so the journaled host is gone.
        var orphan = seed.Operation(JournalSeed.Session("cli:gone", hostPid: 6161), complete: false);
        using var abandoned = JsonDocument.Parse(await client.GetStringAsync($"/api/operations/{orphan.OperationId}"));
        Assert.Equal("abandoned", abandoned.RootElement.GetProperty("operation").GetProperty("status").GetString());

        var raw = await client.GetAsync($"/api/plan-documents/{JournalSeed.PlanDocumentSha256}");
        Assert.Equal("application/xml", raw.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", raw.Content.Headers.ContentDisposition!.DispositionType);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/operations/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plan-documents/{new string('B', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/plan-documents/not-a-hash")).StatusCode);
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

        using var sessionDetail = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{id}"));
        var wholeVersions = sessionDetail.RootElement.GetProperty("compareSubjects")[0]
            .GetProperty("scopeVersions")[0].GetProperty("versions");
        Assert.Equal(["v0", "v1", "v2"], wholeVersions.EnumerateArray()
            .Select(version => version.GetProperty("version").GetString()!).ToArray());

        using var first = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{id}/compares?limit=1"));
        var firstAttempt = first.RootElement.GetProperty("items")[0];
        var cursor = first.RootElement.GetProperty("nextCursor").GetInt64();
        using var second = JsonDocument.Parse(await client.GetStringAsync($"/api/sessions/{id}/compares?cursor={cursor}&limit=1"));

        Assert.NotEqual(firstAttempt.GetProperty("operationId").GetInt64(),
            second.RootElement.GetProperty("items")[0].GetProperty("operationId").GetInt64());
        Assert.Equal("orders", second.RootElement.GetProperty("items")[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Optimization_endpoints_validate_selectors_and_return_scoped_comparison()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:optimization-api");
        var compare = seed.Compare(session, "orders", "whole", "base", "candidate");
        seed.CompareEvidence(compare, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}}]}""",
            OptimizationMetrics(100, 20));
        var fragment = seed.Compare(session, "orders", "fragment:1", "fragment-base", "fragment-candidate");
        seed.CompareEvidence(fragment, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}}]}""",
            OptimizationMetrics(300, 30));
        var otherSubject = seed.Compare(session, "customers", "whole", "base", "candidate");
        seed.CompareEvidence(otherSubject, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}}]}""",
            OptimizationMetrics(400, 200));
        var id = new JournalReader(home.DatabasePath, new FakeProcesses()).Sessions(new SessionQuery()).Items.Single().Id;
        var fingerprint = new JournalReader(home.DatabasePath, new FakeProcesses()).OptimizationEvidence(id)
            .First(item => item.OperationId == compare.OperationId).Fingerprint;
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var sectionResponse = await client.GetAsync($"/api/sessions/{id}/optimization");
        Assert.Equal(HttpStatusCode.OK, sectionResponse.StatusCode);
        using var section = JsonDocument.Parse(await sectionResponse.Content.ReadAsStringAsync());
        Assert.Contains(section.RootElement.GetProperty("subjects").EnumerateArray(),
            item => item.GetProperty("subject").GetString() == "orders");

        using var comparisonResponse = await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=orders&database=db&group={fingerprint}");
        Assert.True(comparisonResponse.StatusCode == HttpStatusCode.OK,
            $"Expected OK, got {(int)comparisonResponse.StatusCode}: {await comparisonResponse.Content.ReadAsStringAsync()}");
        using var comparison = JsonDocument.Parse(await comparisonResponse.Content.ReadAsStringAsync());
        Assert.Equal("v0", comparison.RootElement.GetProperty("a").GetString());
        Assert.Equal("v1", comparison.RootElement.GetProperty("b").GetString());
        Assert.Equal(compare.OperationId, comparison.RootElement.GetProperty("operationId").GetInt64());
        Assert.Equal(100, comparison.RootElement.GetProperty("tables")[0].GetProperty("readsBefore").GetInt64());

        using var fragmentResponse = await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=orders&database=db&group={fingerprint}&scope=fragment%3A1");
        Assert.Equal(HttpStatusCode.OK, fragmentResponse.StatusCode);
        using var fragmentComparison = JsonDocument.Parse(await fragmentResponse.Content.ReadAsStringAsync());
        Assert.Equal(300, fragmentComparison.RootElement.GetProperty("tables")[0].GetProperty("readsBefore").GetInt64());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=orders&database=db&group={fingerprint}&aggregate=median")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=missing&database=db&group={fingerprint}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=orders&database=db&group=missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=orders&database=wrong&group={fingerprint}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=orders&database=db&group={fingerprint}&scope=missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/sessions/{id}/optimization/compare?subject=orders&database=db&group={fingerprint}&a=v99")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/sessions/987654/optimization")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/sessions/987654/optimization/compare?subject=orders&database=db&group={fingerprint}")).StatusCode);
    }

    [Fact]
    public async Task Operation_optimization_uses_its_own_compare_evidence_even_when_unassigned()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("cli:operation-optimization");
        var earlier = seed.Compare(session, null, null, "base", "candidate");
        seed.CompareEvidence(earlier, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}}]}""",
            OptimizationMetrics(100, 20));
        var unassigned = seed.Compare(session, null, null, "base", "candidate");
        seed.CompareEvidence(unassigned, JournalSeed.ReproConditions(),
            """{"kind":"compare","comparison":"Ordered","resultsEquivalent":true,"cases":[{"caseId":"0","caseLabel":"@id = 1","equivalence":{"equivalent":true}}]}""",
            OptimizationMetrics(400, 300));
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync($"/api/operations/{unassigned.OperationId}/optimization");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(unassigned.OperationId, result.RootElement.GetProperty("operationId").GetInt64());
        Assert.Equal(400, result.RootElement.GetProperty("tables")[0].GetProperty("readsBefore").GetInt64());
        Assert.Equal(300, result.RootElement.GetProperty("tables")[0].GetProperty("readsAfter").GetInt64());
        Assert.Equal("changed", result.RootElement.GetProperty("view").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/operations/{unassigned.OperationId}/optimization?aggregate=median")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/operations/{unassigned.OperationId}/optimization?view=unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/operations/987654/optimization")).StatusCode);
        var measure = seed.Operation(session);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/operations/{measure.OperationId}/optimization")).StatusCode);
    }

    private static BenchmarkJournalRecord OptimizationMetrics(long before, long after) => new(
    [
        OptimizationVariant("baseline", before),
        OptimizationVariant("candidate", after),
    ], []);

    private static JournalVariantMetrics OptimizationVariant(string variant, long reads) => new(
        variant, null, null, 1, new CompareDistribution(10, 10, 10), new CompareDistribution(8, 8, 8),
        new CompareDistribution(reads, reads, reads), null, null, null, null, null, null, 0, false, false, 0,
        [], null, [new JournalTableIo("Orders", reads, 1, 0, null, null, 0, null, null, 0)], [])
    {
        CaseId = "0",
        CaseLabel = "@id = 1",
    };

    [Fact]
    public async Task Session_sql_route_rejects_malformed_hashes_and_returns_not_found_for_unknown_hashes()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sessions/5/sql?hash=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sessions/5/sql?hash=sha256%3AXYZ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/sessions/5/sql?hash=sha256%3A" + new string('A', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/api/sessions/5/sql?hash=sha256%3A" + new string('a', 64) + "%0A")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/sessions/5/sql?hash=sha256%3A" + new string('a', 64))).StatusCode);
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
    public async Task Plan_manifest_includes_variant_case_repetition_document_and_statement_identity()
    {
        using var home = new TempHome();
        await SetStoreSensitiveAsync(home, true);
        var sql = "SELECT * FROM dbo.Parent AS p;";
        var first = PlanXml(actualRows: 5);
        var second = PlanXml(actualRows: 6);
        var firstDocument = PlanDocument(first);
        var secondDocument = PlanDocument(second);
        var baseline = Metric("baseline", null, "case-eu", "Europe", [
            Occurrence(1, 0, firstDocument.Sha256, sql),
            Occurrence(2, 1, secondDocument.Sha256, sql, matchKind: "prefix"),
        ]);
        var candidate = Metric("candidate", "set-us", "case-us", "United States", [
            Occurrence(1, 0, secondDocument.Sha256, sql, matchKind: "normalized"),
        ]);
        var benchmark = new BenchmarkJournalRecord([baseline, candidate], [firstDocument, secondDocument]);
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:plan-manifest"), operation: "compare", sql: sql, benchmark: benchmark);
        SetOmittedOccurrence(home.DatabasePath, handle.OperationId, "baseline", repetition: 2, documentOrdinal: 1);

        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync($"/api/operations/{handle.OperationId}/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("stored", root.GetProperty("state").GetString());
        var occurrences = root.GetProperty("occurrences").EnumerateArray().ToArray();
        Assert.Equal(3, occurrences.Length);

        var firstRow = Assert.Single(occurrences, row => row.GetProperty("variant").GetString() == "baseline"
            && row.GetProperty("repetition").GetInt32() == 1);
        Assert.Equal("case-eu", firstRow.GetProperty("caseId").GetString());
        Assert.Equal("Europe", firstRow.GetProperty("caseLabel").GetString());
        Assert.Equal(firstDocument.Sha256, firstRow.GetProperty("documentSha256").GetString());
        Assert.Equal(firstDocument.ShapeHash, firstRow.GetProperty("shapeHash").GetString());
        Assert.Equal(0, firstRow.GetProperty("spanStart").GetInt32());
        Assert.Equal(sql.Length, firstRow.GetProperty("spanLength").GetInt32());
        Assert.Equal("exact", firstRow.GetProperty("matchKind").GetString());
        Assert.Equal(7, firstRow.GetProperty("statementId").GetInt32());
        Assert.Equal(2, firstRow.GetProperty("statementCompId").GetInt32());
        Assert.False(firstRow.GetProperty("omitted").GetBoolean());
        Assert.True(firstRow.GetProperty("stored").GetBoolean());

        var omitted = Assert.Single(occurrences, row => row.GetProperty("variant").GetString() == "baseline"
            && row.GetProperty("repetition").GetInt32() == 2);
        Assert.True(omitted.GetProperty("omitted").GetBoolean());
        Assert.Null(omitted.GetProperty("documentSha256").GetString());
        Assert.Null(omitted.GetProperty("shapeHash").GetString());

        var candidateRow = Assert.Single(occurrences, row => row.GetProperty("variant").GetString() == "candidate");
        Assert.Equal("case-us", candidateRow.GetProperty("caseId").GetString());
        Assert.Equal("set-us", candidateRow.GetProperty("parameterSet").GetString());
        Assert.Equal("normalized", candidateRow.GetProperty("matchKind").GetString());
    }

    [Fact]
    public async Task Plan_graph_and_property_endpoints_expose_the_builder_size_bounds()
    {
        using var home = new TempHome();
        await SetStoreSensitiveAsync(home, true);
        var largePlan = PlanXml(predicate: new string('x', 1_100_000));
        var document = PlanDocument(largePlan);
        var benchmark = new BenchmarkJournalRecord(
            [Metric("measure", null, "0", "Case 1 of 1", [Occurrence(1, 0, document.Sha256, "SELECT 1;")])],
            [document]);
        new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:bounded-plan"), operation: "measure", sql: "SELECT 1;", benchmark: benchmark);

        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var graphResponse = await client.GetAsync($"/api/plan-documents/{document.Sha256}/graph");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, graphResponse.StatusCode);
        var graphBytes = await graphResponse.Content.ReadAsByteArrayAsync();
        Assert.True(graphBytes.Length < 1024 * 1024);
        using var graph = JsonDocument.Parse(graphBytes);
        Assert.Equal("graph_size_limit_exceeded", graph.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(graph.RootElement.GetProperty("graph").ValueKind == JsonValueKind.Null);

        using var propertiesResponse = await client.GetAsync($"/api/plan-documents/{document.Sha256}/nodes/0:0/properties");
        Assert.Equal(HttpStatusCode.OK, propertiesResponse.StatusCode);
        var propertiesBytes = await propertiesResponse.Content.ReadAsByteArrayAsync();
        Assert.True(propertiesBytes.Length <= 128 * 1024);
        using var properties = JsonDocument.Parse(propertiesBytes);
        Assert.True(properties.RootElement.GetProperty("tree").GetProperty("isTruncated").GetBoolean());
        Assert.Contains(FlattenProperties(properties.RootElement.GetProperty("tree").GetProperty("properties")),
            property => property.GetProperty("isTruncationMarker").GetBoolean());
    }

    [Fact]
    public async Task Plan_source_map_returns_exact_executed_sql_and_document_scoped_links()
    {
        using var home = new TempHome();
        await SetStoreSensitiveAsync(home, true);
        const string sql = "SELECT * FROM dbo.Parent AS p;";
        var plan = PlanXml(actualRows: 5);
        var document = PlanDocument(plan);
        var benchmark = new BenchmarkJournalRecord(
            [Metric("measure", "set-a", "case-a", "Set A", [Occurrence(1, 0, document.Sha256, sql)])],
            [document]);
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:source-map"), operation: "measure", sql: sql, benchmark: benchmark);

        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync($"/api/operations/{handle.OperationId}/source-map?variant=measure&parameterSet=set-a&repetition=1&caseId=case-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = result.RootElement;
        Assert.Equal("stored", root.GetProperty("state").GetString());
        Assert.Equal(sql, root.GetProperty("executedText").GetString());
        Assert.Equal("sqlserver", root.GetProperty("engine").GetString());
        Assert.Equal(1, root.GetProperty("algorithmVersion").GetInt32());
        var tableLink = Assert.Single(root.GetProperty("links").EnumerateArray(), link =>
            link.GetProperty("link").GetProperty("kind").GetString() == "table_reference");
        Assert.Equal(document.Sha256, tableLink.GetProperty("documentSha256").GetString());
        Assert.Equal(0, tableLink.GetProperty("documentOrdinal").GetInt32());
        Assert.Equal("0:0", tableLink.GetProperty("link").GetProperty("nodeId").GetString());
        Assert.Equal("exact", tableLink.GetProperty("link").GetProperty("state").GetString());

        using var raw = await client.GetAsync($"/api/plan-documents/{document.Sha256}");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal(plan, await raw.Content.ReadAsStringAsync());
        Assert.Equal(document.Sha256.ToLowerInvariant() + ".sqlplan", raw.Content.Headers.ContentDisposition!.FileName);
    }

    [Fact]
    public async Task Plan_source_map_selects_case_repetition_and_document_without_other_case_links()
    {
        using var home = new TempHome();
        await SetStoreSensitiveAsync(home, true);
        const string sql = "SELECT * FROM dbo.Parent AS p;";
        var caseAFirst = PlanDocument(PlanXml(actualRows: 1));
        var caseASecond = PlanDocument(PlanXml(actualRows: 2));
        var caseBFirst = PlanDocument(PlanXml(actualRows: 3));
        var caseBSecond = PlanDocument(PlanXml(actualRows: 5));
        var benchmark = new BenchmarkJournalRecord(
        [
            Metric("measure", "set-a", "case-a", "Case A", [
                Occurrence(1, 0, caseAFirst.Sha256, sql),
                Occurrence(2, 0, caseASecond.Sha256, sql),
            ]),
            Metric("measure", "set-a", "case-b", "Case B", [
                Occurrence(1, 0, caseBFirst.Sha256, sql),
                Occurrence(2, 1, caseBSecond.Sha256, sql),
            ]),
        ], [caseAFirst, caseASecond, caseBFirst, caseBSecond]);
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:source-map-selectors"), operation: "measure", sql: sql, benchmark: benchmark);

        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var response = await client.GetAsync($"/api/operations/{handle.OperationId}/source-map?variant=measure&parameterSet=set-a&repetition=2&caseId=case-b");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = result.RootElement;
        Assert.Equal("case-b", root.GetProperty("caseId").GetString());
        Assert.Equal("Case B", root.GetProperty("caseLabel").GetString());
        Assert.Equal(2, root.GetProperty("repetition").GetInt32());
        Assert.Equal(sql, root.GetProperty("executedText").GetString());

        var links = root.GetProperty("links").EnumerateArray().ToArray();
        Assert.NotEmpty(links);
        Assert.Equal(new string?[] { caseBSecond.Sha256 }, links.Select(link => link.GetProperty("documentSha256").GetString()).Distinct().ToArray());
        Assert.All(links, link => Assert.Equal(1, link.GetProperty("documentOrdinal").GetInt32()));
        var tableLink = Assert.Single(links, link => link.GetProperty("link").GetProperty("kind").GetString() == "table_reference");
        Assert.Equal("0:0", tableLink.GetProperty("link").GetProperty("nodeId").GetString());
        Assert.Equal("exact", tableLink.GetProperty("link").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Source_map_response_limit_returns_explicit_payload_too_large()
    {
        var response = new PlanSourceMapResponse(1, "stored", "sqlserver", "baseline", null,
            "case-1", "case one", 1, new string('x', 1024), 1, [], [], []);
        var result = DashboardServer.BoundedPlanSourceMap(response, StatusCodes.Status200OK, maximumBytes: 512);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("source_map_size_limit_exceeded", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Plan_manifest_response_limit_returns_explicit_payload_too_large()
    {
        var manifest = new OperationPlanManifest(1, "stored", [new PlanOccurrenceManifest(
            "measure", null, "case-1", new string('x', 1024), 1, 0, 0, 0, 10, 1, 1,
            new string('A', 64), new string('B', 64), "showplan-xml", "exact", false, true)]);
        var result = DashboardServer.BoundedPlanManifest(manifest, maximumBytes: 512);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("plan_manifest_size_limit_exceeded", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Plans_not_stored_is_explicit_and_suppresses_sql_and_occurrences()
    {
        using var home = new TempHome();
        await SetStoreSensitiveAsync(home, true);
        const string sql = "SELECT secret_sql;";
        var storedDocument = PlanDocument(PlanXml());
        var benchmark = new BenchmarkJournalRecord(
            [Metric("measure", null, "case-1", "Case 1", [Occurrence(1, 0, storedDocument.Sha256, sql)])],
            [storedDocument]);
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:plans-disabled"), operation: "measure", sql: sql, benchmark: benchmark);
        // Runtime config is authoritative for reads and suppresses earlier sensitive rows too.
        await SetStoreSensitiveAsync(home, false);

        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        using var manifestResponse = await client.GetAsync($"/api/operations/{handle.OperationId}/plans");
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        var manifestBody = await manifestResponse.Content.ReadAsStringAsync();
        using var manifest = JsonDocument.Parse(manifestBody);
        Assert.Equal("plans not stored", manifest.RootElement.GetProperty("state").GetString());
        Assert.Empty(manifest.RootElement.GetProperty("occurrences").EnumerateArray());
        Assert.DoesNotContain("secret_sql", manifestBody, StringComparison.Ordinal);

        using var sourceMapResponse = await client.GetAsync($"/api/operations/{handle.OperationId}/source-map?variant=measure&repetition=1");
        Assert.Equal(HttpStatusCode.OK, sourceMapResponse.StatusCode);
        var sourceMapBody = await sourceMapResponse.Content.ReadAsStringAsync();
        using var sourceMap = JsonDocument.Parse(sourceMapBody);
        Assert.Equal("plans not stored", sourceMap.RootElement.GetProperty("state").GetString());
        Assert.Null(sourceMap.RootElement.GetProperty("executedText").GetString());
        Assert.Empty(sourceMap.RootElement.GetProperty("links").EnumerateArray());
        Assert.DoesNotContain("secret_sql", sourceMapBody, StringComparison.Ordinal);

        using var graphResponse = await client.GetAsync($"/api/plan-documents/{storedDocument.Sha256}/graph");
        Assert.Equal(HttpStatusCode.Conflict, graphResponse.StatusCode);
        using var graph = JsonDocument.Parse(await graphResponse.Content.ReadAsStringAsync());
        Assert.Equal("plans not stored", graph.RootElement.GetProperty("state").GetString());

        using var rawResponse = await client.GetAsync($"/api/plan-documents/{storedDocument.Sha256}");
        Assert.Equal(HttpStatusCode.Conflict, rawResponse.StatusCode);
        using var raw = JsonDocument.Parse(await rawResponse.Content.ReadAsStringAsync());
        Assert.Equal("plans not stored", raw.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Plan_document_routes_validate_sha_and_return_not_found_for_missing_documents()
    {
        using var home = new TempHome();
        await SetStoreSensitiveAsync(home, true);
        var handle = new JournalSeed(home.DatabasePath, storeSensitive: true)
            .Operation(JournalSeed.Session("cli:plan-hash"), operation: "measure", sql: "SELECT 1;");
        _ = handle;

        await using var dashboard = await StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/plan-documents/not-a-sha/graph")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/plan-documents/not-a-sha")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plan-documents/{new string('F', 64)}/graph")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plan-documents/{new string('F', 64)}")).StatusCode);
    }

    [Fact]
    public async Task Corrupt_stored_plan_is_unprocessable()
    {
        using var home = new TempHome();
        await SetStoreSensitiveAsync(home, true);
        var seed = new JournalSeed(home.DatabasePath, storeSensitive: true);
        seed.Operation(JournalSeed.Session("cli:a"));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO plan_documents (sha256, shape_hash, format, raw_size, gz) VALUES ($h, $shape, 'showplan-xml', 3, x'00010203');";
            insert.Parameters.AddWithValue("$h", new string('C', 64));
            insert.Parameters.AddWithValue("$shape", new string('D', 64));
            insert.ExecuteNonQuery();
        }

        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/api/plan-documents/{new string('C', 64)}/graph")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync($"/api/plan-documents/{new string('C', 64)}")).StatusCode);
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

    private static async Task SetStoreSensitiveAsync(TempHome home, bool enabled) =>
        await File.WriteAllTextAsync(Path.Combine(home.Path, "config.json"),
            $"{{\"journal\":{{\"storeSensitive\":{enabled.ToString().ToLowerInvariant()}}}}}");

    private static JournalPlanDocument PlanDocument(string xml) =>
        new(PlanIdentity.DocumentSha256(xml), PlanIdentity.Hash(xml), "showplan-xml", xml);

    private static JournalPlanOccurrence Occurrence(int repetition, int documentOrdinal, string sha256, string sql,
        string matchKind = "exact") => new(repetition, documentOrdinal, 0, sha256)
        {
            SpanStart = 0,
            SpanLength = sql.Length,
            MatchKind = matchKind,
            StatementId = 7,
            StatementCompId = 2,
        };

    private static JournalVariantMetrics Metric(string variant, string? parameterSet, string caseId, string caseLabel,
        IReadOnlyList<JournalPlanOccurrence> occurrences)
    {
        var defaults = JournalSeed.Benchmark().Variants[0];
        return defaults with
        {
            Variant = variant,
            ParameterSet = parameterSet,
            CaseId = caseId,
            CaseLabel = caseLabel,
            PlanLinks = occurrences.Select(item => new JournalPlanLink(item.Repetition, item.DocumentOrdinal, item.DocumentSha!)).ToArray(),
            PlanOccurrences = occurrences,
        };
    }

    private static string PlanXml(int actualRows = 1, string? predicate = "p.Id = 42") =>
        $"""
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
          <BatchSequence><Batch><Statements><StmtSimple StatementId="7" StatementCompId="2" StatementText="SELECT * FROM dbo.Parent AS p;">
            <QueryPlan QueryPlanHash="0xA1"><RelOp NodeId="0" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="1">
              <IndexScan><Object Database="[db]" Schema="[dbo]" Table="[Parent]" Index="[IX_Parent]" Alias="[p]" TableReferenceId="1" /></IndexScan>
              <Predicate><ScalarOperator ScalarString="{predicate}" /></Predicate>
              <RunTimeInformation><RunTimeCountersPerThread ActualRows="{actualRows}" ActualExecutions="1" /></RunTimeInformation>
            </RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    private static IEnumerable<JsonElement> FlattenProperties(JsonElement nodes)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            yield return node;
            foreach (var child in FlattenProperties(node.GetProperty("children")))
                yield return child;
        }
    }

    private static void SetOmittedOccurrence(string databasePath, long operationId, string variant, int repetition, int documentOrdinal)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE plan_occurrences
            SET document_sha = NULL, omitted = 1
            WHERE metric_id = (SELECT id FROM operation_metrics WHERE operation_id = $operation AND variant = $variant)
              AND repetition = $repetition AND document_ordinal = $documentOrdinal;
            """;
        update.Parameters.AddWithValue("$operation", operationId);
        update.Parameters.AddWithValue("$variant", variant);
        update.Parameters.AddWithValue("$repetition", repetition);
        update.Parameters.AddWithValue("$documentOrdinal", documentOrdinal);
        Assert.Equal(1, update.ExecuteNonQuery());
    }
}
