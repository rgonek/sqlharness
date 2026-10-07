using System.Net;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Dashboard;
using SqlHarness.Tests.Journal;

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

    private static async Task<JsonElement> NextOperationAsync(StreamReader stream, long id, string status, CancellationToken ct)
    {
        while (true)
        {
            var (_, data) = await NextEventAsync(stream, "operation", ct);
            if (data.GetProperty("id").GetInt64() == id && data.GetProperty("status").GetString() == status)
                return data;
        }
    }

    private static IActivityJournal Journal(TempHome home, TimeProvider clock) =>
        ActivityJournal.Open(home.DatabasePath, new JournalConfig(), TextWriter.Null, clock);

    /// <summary>Writes finished operation rows directly, stamped with <paramref name="updatedAt"/>, in one commit.</summary>
    private static List<long> InsertFinished(TempHome home, string updatedAt, int count)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = home.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO operations (session_id, operation, host_pid, started_at, updated_at, finished_at, status, exit_code)
                VALUES ((SELECT MIN(id) FROM sessions), 'counts', 5151, $at, $at, $at, 'succeeded', 0) RETURNING id;
                """;
            insert.Parameters.AddWithValue("$at", updatedAt);
            ids.Add((long)insert.ExecuteScalar()!);
        }

        transaction.Commit();
        return ids;
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
        var (_, session) = await NextEventAsync(stream, "session", timeout.Token);
        Assert.Equal(1, session.GetProperty("abandoned").GetInt32());
        Assert.Equal(0, session.GetProperty("running").GetInt32());
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

    [Fact]
    public async Task Live_feed_requires_the_session_cookie()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/live", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
    }

    [Fact]
    public async Task Late_commit_stamped_before_the_newest_row_is_delivered()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:seen"));
        var (server, client, stream) = await Connect(home, new FakeProcesses());
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Stamped before the newest row the feed has already seen, as when a writer took
        // its timestamp before waiting for the write lock. It is never seen running.
        var late = Assert.Single(InsertFinished(home, "2026-10-07T09:00:00.500Z", 1));

        await NextOperationAsync(stream, late, "succeeded", timeout.Token);
    }

    [Fact]
    public async Task Completion_committed_long_after_its_timestamp_is_delivered()
    {
        using var home = new TempHome();
        var t0 = new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        var clockX = new FixedTimeProvider(t0);
        var clockY = new FixedTimeProvider(t0.AddHours(1));
        var journalX = Journal(home, clockX);
        var journalY = Journal(home, clockY);
        var (server, client, stream) = await Connect(home, new FakeProcesses().Alive(5151, JournalSeed.HostStarted));
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var x = journalX.Begin(JournalSeed.Session("cli:x"), JournalTestData.Start())!;
        await NextOperationAsync(stream, x.OperationId, "running", timeout.Token);
        var y = journalY.Begin(JournalSeed.Session("cli:y"), JournalTestData.Start())!;
        journalY.Complete(y, JournalTestData.End());
        await NextOperationAsync(stream, y.OperationId, "succeeded", timeout.Token);

        // Stamped far behind the lookback window: only the running-set check can find it.
        clockX.Now = t0.AddMilliseconds(1);
        journalX.Complete(x, JournalTestData.End());

        await NextOperationAsync(stream, x.OperationId, "succeeded", timeout.Token);
    }

    [Fact]
    public async Task Begin_and_complete_in_the_same_millisecond_are_both_delivered()
    {
        using var home = new TempHome();
        var journal = Journal(home, new FixedTimeProvider(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero)));
        var (server, client, stream) = await Connect(home, new FakeProcesses().Alive(5151, JournalSeed.HostStarted));
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var handle = journal.Begin(JournalSeed.Session("cli:fast"), JournalTestData.Start())!;
        await NextOperationAsync(stream, handle.OperationId, "running", timeout.Token);
        journal.Complete(handle, JournalTestData.End());

        await NextOperationAsync(stream, handle.OperationId, "succeeded", timeout.Token);
    }

    [Fact]
    public async Task More_rows_than_a_batch_at_one_timestamp_are_all_delivered()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:batch"));
        var (server, client, stream) = await Connect(home, new FakeProcesses());
        await using var _ = server;
        using var __ = client;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var expected = InsertFinished(home, "2026-10-07T09:00:05.000Z", LiveFeed.BatchLimit + 100).ToHashSet();

        while (expected.Count > 0)
        {
            var (_, data) = await NextEventAsync(stream, "operation", timeout.Token);
            expected.Remove(data.GetProperty("id").GetInt64());
        }
    }
}