using System.Net;
using System.Net.Http.Json;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;
using SqlHarness.Dashboard;
using SqlHarness.Dashboard.Mutations;

namespace SqlHarness.Tests.Dashboard;

public sealed class MutationEndpointsTests
{
    [Fact]
    public async Task Mutation_submit_requires_bearer_authentication()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseUri };

        using var response = await client.PostAsJsonAsync("/api/mutations", Request());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Client_without_running_dashboard_and_autostart_disabled_returns_unavailable()
    {
        using var home = new TempHome();
        var client = new HttpMutationBrokerClient(
            () => new SqlHarnessConfig(), home.Path, new FakeProcesses(), new RecordingLauncher());

        var result = await client.SubmitAsync(Request(), null, CancellationToken.None);

        Assert.Equal(MutationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Client_refused_before_receiving_a_response_returns_unavailable()
    {
        using var home = new TempHome();
        var token = DashboardToken.LoadOrCreate(home.Path);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var current = ProcessInfo.Current.Get(Environment.ProcessId);
        held.Publish(new DashboardEndpoint(Environment.ProcessId, current?.StartedAt, port, token));
        var client = new HttpMutationBrokerClient(
            () => new SqlHarnessConfig(), home.Path, ProcessInfo.Current, new RecordingLauncher());

        var result = await client.SubmitAsync(Request(), null, CancellationToken.None);

        Assert.Equal(MutationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Client_reports_unknown_when_the_connection_drops_after_body_send()
    {
        using var home = new TempHome();
        var token = DashboardToken.LoadOrCreate(home.Path);
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var current = ProcessInfo.Current.Get(Environment.ProcessId);
        held.Publish(new DashboardEndpoint(Environment.ProcessId, current?.StartedAt, 47899, token));
        var handler = new DropAfterBodyHandler();
        var client = new HttpMutationBrokerClient(
            () => new SqlHarnessConfig(), home.Path, ProcessInfo.Current, new RecordingLauncher(),
            handlerFactory: () => handler);

        var result = await client.SubmitAsync(Request(), null, CancellationToken.None);

        Assert.Equal(MutationStatus.OutcomeUnknown, result.Status);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task Mutation_submit_rejects_a_database_that_differs_from_server_resolution()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, _) = dashboard;
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        using var response = await client.PostAsJsonAsync("/api/mutations", Request(database: "wrong-db"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MutationStatus.DatabaseMismatch, (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Mutation_submit_rejects_body_fields_that_do_not_match_the_server_prepared_digest()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, _) = dashboard;
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        using var response = await client.PostAsJsonAsync("/api/mutations", Request(grantees: ["forged-principal"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request_mismatch", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Mutation_submit_rejects_unsupported_verification_configuration()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        File.WriteAllText(Path.Combine(home.Path, "config.json"), """
            {"mutations":{"defaultMode":"operator","verification":"webauthn","approvalTimeoutSeconds":30},"journal":{"enabled":true,"storeSensitive":false}}
            """);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, _) = dashboard;
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        using var response = await client.PostAsJsonAsync("/api/mutations", Request());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<StatusErrorBody>())!;
        Assert.Equal(MutationStatus.Unavailable, error.Status);
        Assert.Contains("click is the only supported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_rejects_a_verification_configuration_that_became_invalid()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var cancellation = new CancellationTokenSource();
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request(), cancellation.Token);
        var requestId = await WaitForRequestId(client);
        File.WriteAllText(Path.Combine(home.Path, "config.json"), """
            {"mutations":{"defaultMode":"operator","verification":"webauthn","approvalTimeoutSeconds":30},"journal":{"enabled":true,"storeSensitive":false}}
            """);
        AddWriteHeaders(client, server.Port);

        using var response = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MutationStatus.Unavailable, (await response.Content.ReadFromJsonAsync<StatusErrorBody>())!.Status);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitTask);
    }

    [Fact]
    public async Task Approve_requires_the_guarded_write_header_even_with_a_dashboard_cookie()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var cancellation = new CancellationTokenSource();
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request(), cancellation.Token);
        var requestId = await WaitForRequestId(client);

        using var response = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitTask);
    }

    [Fact]
    public async Task Approve_completes_the_long_poll_and_a_second_approval_conflicts()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request());
        var requestId = await WaitForRequestId(client);
        AddWriteHeaders(client, server.Port);

        using var first = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        using var second = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        using var result = await submitTask;

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(MutationStatus.Success, (await result.Content.ReadFromJsonAsync<MutationBrokerResult>())!.Status);
    }

    [Fact]
    public async Task Events_stream_sends_added_and_removed_after_its_snapshot()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var sse = await client.GetAsync("/api/mutations/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await sse.Content.ReadAsStreamAsync());
        Assert.Equal("snapshot", await ReadEventName(reader));

        var submitTask = client.PostAsJsonAsync("/api/mutations", Request());
        var added = await ReadSseEvent(reader);
        var requestId = added.Data.GetProperty("requestId").GetString()!;
        AddWriteHeaders(client, server.Port);
        using var approval = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        var removed = await ReadSseEvent(reader);
        using var result = await submitTask;

        Assert.Equal("added", added.Name);
        Assert.Equal("removed", removed.Name);
        Assert.Equal(requestId, removed.Data.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Events_stream_snapshot_includes_requests_created_before_subscription()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request());
        var requestId = await WaitForRequestId(client);
        using var sse = await client.GetAsync("/api/mutations/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await sse.Content.ReadAsStreamAsync());
        var snapshot = await ReadSseEvent(reader);

        Assert.Equal("snapshot", snapshot.Name);
        Assert.Equal(requestId, snapshot.Data[0].GetProperty("requestId").GetString());

        AddWriteHeaders(client, server.Port);
        using var approval = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        var removed = await ReadSseEvent(reader);
        using var result = await submitTask;
        Assert.Equal("removed", removed.Name);
    }

    [Fact]
    public async Task Aborting_a_pending_submit_cancels_the_broker_request()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var cancellation = new CancellationTokenSource();
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request(), cancellation.Token);
        var requestId = await WaitForRequestId(client);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitTask);

        Assert.Equal("cancelled", await WaitForRequestState(client, requestId));
    }

    private static MutationRequest Request(string database = "db", IReadOnlyList<string>? grantees = null) => new(
        "app", new Dictionary<string, string>(), "sqlserver", "srv", database, "UPDATE dbo.T SET x = 1",
        [], 30, 10, "dml", grantees ?? [], "session");

    private static void ConfigureHome(TempHome home)
    {
        File.WriteAllText(Path.Combine(home.Path, "targets.json"), """
            {"app":{"server":"srv","database":"db","auth":"integrated","engine":"sqlserver","vars":{},"mutations":{"mode":"operator","writer":{"sameAsReader":true}}}}
            """);
        File.WriteAllText(Path.Combine(home.Path, "config.json"), """
            {"mutations":{"defaultMode":"operator","approvalTimeoutSeconds":30},"journal":{"enabled":true,"storeSensitive":false}}
            """);
    }

    private static async Task<DashboardServerTests.AuthenticatedDashboard> StartMutationDashboard(TempHome home)
    {
        var profiles = () => ProfileStore.Load(Path.Combine(home.Path, "targets.json"));
        var module = new TestMutationModule(new SqlHarnessModule(profiles));
        var config = SqlHarnessConfigLoader.Load(Path.Combine(home.Path, "config.json")).Config;
        var journal = ActivityJournal.Open(home.DatabasePath, config.Journal, TextWriter.Null, TimeProvider.System);
        return await DashboardServerTests.StartAuthenticated(home, options => options with
        {
            MutationModule = module,
            MutationJournal = journal,
            ConfigPath = Path.Combine(home.Path, "config.json"),
            TargetsPath = Path.Combine(home.Path, "targets.json"),
        });
    }

    private static async Task<string> WaitForRequestId(HttpClient client)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await client.GetAsync("/api/mutations?limit=1");
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = page.RootElement.GetProperty("items");
            if (items.GetArrayLength() != 0)
                return items[0].GetProperty("requestId").GetString()!;
            await Task.Delay(10);
        }
        throw new TimeoutException("The pending mutation was not recorded.");
    }

    private static async Task<string> WaitForRequestState(HttpClient client, string requestId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await client.GetAsync("/api/mutations?limit=1");
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = page.RootElement.GetProperty("items");
            if (items.GetArrayLength() != 0 && items[0].GetProperty("requestId").GetString() == requestId)
            {
                var state = items[0].GetProperty("state").GetString();
                if (state != "pending") return state!;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The mutation lifecycle did not reach a terminal state.");
    }

    private static async Task<string> ReadEventName(StreamReader reader)
    {
        var item = await ReadSseEvent(reader);
        return item.Name;
    }

    private static async Task<(string Name, JsonElement Data)> ReadSseEvent(StreamReader reader)
    {
        string? name = null;
        string? data = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0 && name is not null && data is not null)
                return (name, JsonDocument.Parse(data).RootElement.Clone());
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }
        throw new EndOfStreamException("The mutation event stream ended.");
    }

    private static void AddWriteHeaders(HttpClient client, int port)
    {
        client.DefaultRequestHeaders.Add("X-SqlHarness-Dashboard", "1");
        client.DefaultRequestHeaders.Add("Origin", $"http://127.0.0.1:{port}");
    }

    private sealed record ErrorBody(string Error);
    private sealed record StatusErrorBody(string Status, string Error, string Message);

    private sealed class TestMutationModule(ISqlHarnessModule core) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            if (operation is SqlHarnessMutationOperation { Approval: not null })
                return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, QueryReport(), null));
            return core.ExecuteAsync(operation, ct);
        }

        private static SqlHarnessQueryReport QueryReport() => new(
            new SqlHarnessTargetIdentityReport("srv", "db", "srv", "db", "app", "sqlserver"), "update", [], [], 1, 1,
            "hash", new OutputFootprint(0, 0));
    }

    private sealed class RecordingLauncher : IDashboardLauncher
    {
        public DashboardLaunchIdentity Launch(DashboardLaunchCommand command, string workingDirectory) =>
            throw new InvalidOperationException("Autostart must be disabled in this test.");
    }

    private sealed class DropAfterBodyHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            _ = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            throw new HttpRequestException("connection dropped");
        }
    }
}
