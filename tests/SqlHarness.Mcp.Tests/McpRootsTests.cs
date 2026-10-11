#pragma warning disable MCP9005 // Roots is deprecated as of 2026-07-28; these tests cover the handshake revisions.

using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpRootsTests : IDisposable
{
    private static readonly string PlanXml = File.ReadAllText(McpStdioProcessHarness.FindRepositoryFile(
        "tests", "SqlHarness.Tests", "Fixtures", "distiller-sample.sqlplan"));

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-roots-" + Guid.NewGuid().ToString("N"));

    public McpRootsTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_home, true);
    }

    // ---- Normalize ----

    [Fact]
    public void Normalize_omits_missing_names_and_keeps_order()
    {
        var (json, kept, total) = McpRootsTracker.Normalize(
            [new Root { Uri = "file:///b", Name = "b" }, new Root { Uri = "file:///a" }]);

        Assert.Equal("""[{"uri":"file:///b","name":"b"},{"uri":"file:///a"}]""", json);
        Assert.Equal((2, 2), (kept, total));
    }

    [Fact]
    public void Normalize_of_empty_or_missing_list_is_an_empty_array()
    {
        Assert.Equal("[]", McpRootsTracker.Normalize([]).Json);
        Assert.Equal("[]", McpRootsTracker.Normalize(null).Json);
    }

    [Fact]
    public void Normalize_applies_count_and_length_limits()
    {
        var roots = Enumerable.Range(0, 40)
            .Select(i => new Root { Uri = "file:///" + new string('u', 3000) + i, Name = new string('n', 300) })
            .ToList();

        var (json, kept, total) = McpRootsTracker.Normalize(roots);

        Assert.Equal((32, 40), (kept, total));
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(32, parsed.GetArrayLength());
        Assert.Equal(McpRootsTracker.MaximumUriLength, parsed[0].GetProperty("uri").GetString()!.Length);
        Assert.Equal(McpRootsTracker.MaximumNameLength, parsed[0].GetProperty("name").GetString()!.Length);
    }

    [Fact]
    public void Normalize_keeps_uris_verbatim()
    {
        const string uri = "file:///D:/Projekty/Za%C5%BC%C3%B3%C5%82%C4%87%20g%C4%99%C5%9Bl%C4%85";
        var json = McpRootsTracker.Normalize([new Root { Uri = uri }]).Json;
        Assert.Equal(uri, JsonDocument.Parse(json).RootElement[0].GetProperty("uri").GetString());
    }

    // ---- Tracker ----

    private static Func<CancellationToken, ValueTask<ListRootsResult>> Answer(params string[] uris) =>
        _ => ValueTask.FromResult(new ListRootsResult { Roots = uris.Select(u => new Root { Uri = u }).ToList() });

    [Fact]
    public async Task Successful_fetch_publishes_the_snapshot()
    {
        var tracker = new McpRootsTracker(TextWriter.Null, CancellationToken.None);
        Assert.Null(tracker.Current);

        await tracker.Refresh(Answer("file:///w")).WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///w"}]""", tracker.Current);
    }

    [Fact]
    public async Task Failed_fetch_keeps_previous_snapshot_and_logs_no_uri()
    {
        var log = new StringWriter();
        var tracker = new McpRootsTracker(log, CancellationToken.None);
        await tracker.Refresh(Answer("file:///SQLH_ROOT_MARKER")).WaitAsync(Budget);

        await tracker.Refresh(_ => throw new InvalidOperationException("file:///SQLH_ROOT_MARKER")).WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///SQLH_ROOT_MARKER"}]""", tracker.Current);
        Assert.Contains("InvalidOperationException", log.ToString());
        Assert.DoesNotContain("SQLH_ROOT_MARKER", log.ToString());
    }

    [Fact]
    public async Task Null_roots_list_is_a_failure_that_keeps_the_previous_snapshot()
    {
        var log = new StringWriter();
        var tracker = new McpRootsTracker(log, CancellationToken.None);
        await tracker.Refresh(Answer("file:///w")).WaitAsync(Budget);

        await tracker.Refresh(_ => ValueTask.FromResult(new ListRootsResult { Roots = null! })).WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///w"}]""", tracker.Current);
        Assert.Contains("roots/list failed (InvalidDataException)", log.ToString());
    }

    [Fact]
    public void Normalize_never_cuts_inside_a_surrogate_pair()
    {
        // The pair straddles the cap: units [limit-1] and [limit].
        var uri = new string('u', McpRootsTracker.MaximumUriLength - 1) + "😀" + "tail";
        var name = new string('n', McpRootsTracker.MaximumNameLength - 1) + "😀";

        var json = McpRootsTracker.Normalize([new Root { Uri = uri, Name = name }]).Json;

        var root = JsonDocument.Parse(json).RootElement[0];
        Assert.Equal(McpRootsTracker.MaximumUriLength - 1, root.GetProperty("uri").GetString()!.Length);
        Assert.Equal(McpRootsTracker.MaximumNameLength - 1, root.GetProperty("name").GetString()!.Length);
    }

    [Fact]
    public async Task Timed_out_fetch_keeps_previous_snapshot()
    {
        var log = new StringWriter();
        var tracker = new McpRootsTracker(log, CancellationToken.None, TimeSpan.FromMilliseconds(100));
        await tracker.Refresh(Answer("file:///old")).WaitAsync(Budget);

        await tracker.Refresh(async ct => { await Task.Delay(Timeout.Infinite, ct); return new ListRootsResult { Roots = [] }; }).WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///old"}]""", tracker.Current);
        Assert.Contains("timed out", log.ToString());
    }

    [Fact]
    public async Task Newer_fetch_wins_over_a_slow_older_one()
    {
        var tracker = new McpRootsTracker(TextWriter.Null, CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = tracker.Refresh(async _ =>
        {
            await release.Task; // ignores cancellation on purpose: a late answer must still lose
            return new ListRootsResult { Roots = [new Root { Uri = "file:///old" }] };
        });
        await tracker.Refresh(Answer("file:///new")).WaitAsync(Budget);
        release.SetResult();
        await slow.WaitAsync(Budget);

        Assert.Equal("""[{"uri":"file:///new"}]""", tracker.Current);
    }

    [Fact]
    public async Task Truncation_is_reported_as_counts_only()
    {
        var log = new StringWriter();
        var tracker = new McpRootsTracker(log, CancellationToken.None);

        await tracker.Refresh(Answer(Enumerable.Range(0, 40).Select(i => "file:///SQLH_ROOT_MARKER" + i).ToArray())).WaitAsync(Budget);

        Assert.Contains("kept 32 of 40", log.ToString());
        Assert.DoesNotContain("SQLH_ROOT_MARKER", log.ToString());
    }

    // ---- Host ----

    private static Func<IReadOnlyDictionary<string, TargetProfile>> Profiles => () =>
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
        };

    private string? RootsJson()
    {
        var database = Path.Combine(_home, "data", "activity.db");
        if (!File.Exists(database))
            return null;
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT roots_json FROM sessions";
        var value = command.ExecuteScalar();
        return value is DBNull or null ? null : (string)value;
    }

    /// <summary>Calls plan until the session row carries the expected roots (the fetch is asynchronous).</summary>
    private async Task<string?> CallUntilRoots(McpClient client, string? expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Budget;
        string? seen;
        do
        {
            await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: ct);
            seen = RootsJson();
            if (seen == expected)
                return seen;
            await Task.Delay(50, ct);
        }
        while (DateTime.UtcNow < deadline);
        return seen;
    }

    private async Task RunHost(ClientCapabilities? capabilities, McpClientHandlers? handlers, Func<McpClient, CancellationToken, Task> body, StringWriter log, string revision = McpHost.FallbackProtocolVersion)
    {
        using var cts = new CancellationTokenSource(Budget * 2);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(new McpServerOptions { Profile = "mcp-t5" },
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), log, Profiles, cts.Token);
        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9" },
                ProtocolVersion = revision,
                Capabilities = capabilities,
                Handlers = handlers ?? new McpClientHandlers(),
            },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            await body(client, cts.Token);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
    }

    [Fact]
    public async Task Client_roots_reach_the_session_and_list_changed_overwrites_them()
    {
        var current = "file:///SQLH_ROOT_MARKER/one";
        var log = new StringWriter();
        await RunHost(
            new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } },
            new McpClientHandlers
            {
                RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult { Roots = [new Root { Uri = current, Name = "one" }] }),
            },
            async (client, ct) =>
            {
                Assert.Equal($$"""[{"uri":"{{current}}","name":"one"}]""", await CallUntilRoots(client, $$"""[{"uri":"{{current}}","name":"one"}]""", ct));

                current = "file:///SQLH_ROOT_MARKER/two";
                await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification, ct);
                Assert.Equal($$"""[{"uri":"{{current}}","name":"one"}]""", await CallUntilRoots(client, $$"""[{"uri":"{{current}}","name":"one"}]""", ct));
            },
            log);

        Assert.DoesNotContain("SQLH_ROOT_MARKER", log.ToString());
    }

    [Fact]
    public async Task Client_without_roots_capability_is_never_asked()
    {
        // SDK 2.2.0 advertises roots whenever a RootsHandler is set, so this client has none;
        // a roots/list sent to it would fail and leave a stderr diagnostic.
        var log = new StringWriter();
        await RunHost(
            new ClientCapabilities(),
            null,
            async (client, ct) =>
            {
                await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: ct);
                await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification, ct);
                await Task.Delay(200, ct);
                await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: ct);
            },
            log);

        Assert.DoesNotContain("roots", log.ToString());
        Assert.Null(RootsJson());
    }

    [Fact]
    public async Task List_changed_without_capability_is_ignored()
    {
        var asked = 0;
        await RunHost(
            new ClientCapabilities { Roots = new RootsCapability { ListChanged = false } },
            new McpClientHandlers
            {
                RootsHandler = (_, _) =>
                {
                    Interlocked.Increment(ref asked);
                    return ValueTask.FromResult(new ListRootsResult { Roots = [new Root { Uri = "file:///w" }] });
                },
            },
            async (client, ct) =>
            {
                Assert.Equal("""[{"uri":"file:///w"}]""", await CallUntilRoots(client, """[{"uri":"file:///w"}]""", ct));
                await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification, ct);
                await Task.Delay(200, ct);
            },
            new StringWriter());

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Roots_stay_null_on_2026_07_28()
    {
        // Probe outcome: request-scoped roots/list answers only while the call awaits it,
        // and a non-blocking fetch hangs to its timeout, so this revision never asks.
        var asked = 0;
        await RunHost(
            new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } },
            new McpClientHandlers
            {
                RootsHandler = (_, _) =>
                {
                    Interlocked.Increment(ref asked);
                    return ValueTask.FromResult(new ListRootsResult { Roots = [new Root { Uri = "file:///w" }] });
                },
            },
            async (client, ct) =>
            {
                Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
                await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: ct);
                await Task.Delay(200, ct);
                await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: ct);
            },
            new StringWriter(),
            revision: "2026-07-28");

        Assert.Equal(0, asked);
        Assert.Null(RootsJson());
    }
}