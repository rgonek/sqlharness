using System.Text.Json;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class LiveFeedTests
{
    private static async Task<(string Event, JsonElement Data)> NextEventAsync(StreamReader stream, string name, CancellationToken ct)
    {
        string? currentEvent = null;
        while (await stream.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
                currentEvent = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && currentEvent == name)
                return (currentEvent, JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone());
        }

        throw new EndOfStreamException();
    }

    private static async Task<(RunningDashboard Server, HttpClient Client)> Start(TempHome home, FakeProcesses processes)
    {
        var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, 0, processes) { LivePollInterval = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new System.Net.CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = server.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        await client.GetAsync($"/?t={server.Token}");
        return (server, client);
    }

    private static async Task<(RunningDashboard Server, HttpClient Client, StreamReader Stream)> Connect(TempHome home, FakeProcesses processes)
    {
        var (server, client) = await Start(home, processes);
        var response = await client.GetAsync("/api/live", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var stream = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await stream.ReadLineAsync());
        return (server, client, stream);
    }

    [Fact]
    public async Task New_and_completed_operations_are_pushed()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:old"));
        var (server, client, stream) = await Connect(home, new FakeProcesses().Alive(5151, JournalSeed.HostStarted));
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var handle = seed.Operation(JournalSeed.Session("cli:new"), operation: "ping");

        var (_, data) = await NextEventAsync(stream, "operation", timeout.Token);
        Assert.Equal(handle.OperationId, data.GetProperty("id").GetInt64());
        Assert.Equal("ping", data.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task Live_feed_reports_a_running_operation_turning_abandoned()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var processes = new FakeProcesses().Alive(5151, JournalSeed.HostStarted);
        seed.Operation(JournalSeed.Session("cli:run"), complete: false);
        var (server, client, stream) = await Connect(home, processes);
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        processes.Clear();

        var (_, data) = await NextEventAsync(stream, "operation", timeout.Token);
        Assert.Equal("abandoned", data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Feed_waits_for_a_database_that_does_not_exist_yet()
    {
        using var home = new TempHome();
        var (server, client, stream) = await Connect(home, new FakeProcesses());
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var handle = new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:late"));

        var (_, data) = await NextEventAsync(stream, "operation", timeout.Token);
        Assert.Equal(handle.OperationId, data.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Head_returns_the_event_stream_headers_without_a_body()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:head"));
        var (server, client) = await Start(home, new FakeProcesses());
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Head, "/api/live");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(timeout.Token));
    }
    [Fact]
    public async Task Stopping_the_server_ends_open_streams()
    {
        using var home = new TempHome();
        var (server, client, stream) = await Connect(home, new FakeProcesses());
        using var _ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await server.DisposeAsync().AsTask().WaitAsync(timeout.Token);

        while (await stream.ReadLineAsync(timeout.Token) is not null)
        {
        }
    }
}