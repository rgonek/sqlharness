using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T5 cancellation contract: the client token, the host shutdown token, and
/// the process/per-call time budget all reach the module through one linked
/// token, so cancellation fires during connect, setup, benchmark repeats,
/// sidecars, watch delays, and artifact writes (every Core phase observes the
/// same token). A cancelled call reports a stable cancelled result and never
/// the natural watch-deadline exit 7; after cleanup the next call runs.
/// Concurrent calls each keep their own deadline and cancellation. Partial artifacts follow the Core contract (owned and tested by
/// the Core suite); MCP never invents a success path before publish. All data
/// is synthetic; no database is opened. Every wait has a 30 s backstop; the
/// only real-time bound asserted is the 1 s process deadline.
/// </summary>
public sealed class McpCancellationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static IReadOnlyDictionary<string, TargetProfile> TestProfiles() =>
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile(
                "mcp-unreachable.invalid", "reportdb",
                new Dictionary<string, string>(), "integrated"),
        };

    private static McpScope TestScope(McpServerOptions? options = null) =>
        McpScope.Create(
            options ?? new McpServerOptions { Profile = "mcp-t5" },
            TestProfiles());

    private sealed class CancellingModule : ISqlHarnessModule
    {
        private int _calls;
        public List<SqlHarnessOperation> Operations { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<int, bool> BlockCall { get; set; } = _ => true;

        public async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            lock (Operations)
            {
                Operations.Add(operation);
                Tokens.Add(ct);
            }

            if (!BlockCall(call))
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);

            Entered.TrySetResult();
            try
            {
                // Honor the token exactly like Core: every phase observes it.
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                CancelObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("A cancelled wait never completes.");
        }
    }

    private static string Text(CallToolResult result) =>
        Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    private static JsonElement Envelope(CallToolResult result) =>
        JsonDocument.Parse(Text(result)).RootElement;

    private static void AssertCancelled(CallToolResult result, string command)
    {
        Assert.True(result.IsError == true, Text(result));
        var envelope = Envelope(result);
        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(command, envelope.GetProperty("command").GetString());
        var error = envelope.GetProperty("error");
        Assert.Equal(McpCallOutcome.CancelledCode, error.GetProperty("code").GetString());
        Assert.Equal(McpCallOutcome.CancelledMessage, error.GetProperty("message").GetString());
        Assert.Empty(McpResultAdapter.ValidateEnvelope(envelope));
    }

    private static void AssertInvalidBudget(CallToolResult result, string command)
    {
        Assert.True(result.IsError == true, Text(result));
        var envelope = Envelope(result);
        Assert.Equal((int)SqlHarnessExitCode.Safety, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal("The requested budget is invalid.", envelope.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(command, envelope.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Precanceled_db_calls_report_cancelled_never_exit7()
    {
        var module = new CancellingModule();
        var handlers = new McpToolHandlers(TestScope(), module);
        var precancelled = new CancellationToken(canceled: true);

        var query = await handlers.QueryAsync(null!, "SELECT 1", ct: precancelled);
        AssertCancelled(query, "sqlharness_query");

        var watch = await handlers.WatchAsync(null!, "SELECT 1", ct: precancelled);
        AssertCancelled(watch, "sqlharness_watch");
    }

    private sealed class FailingAfterCancelModule(CancellationTokenSource cts) : ISqlHarnessModule
    {
        public async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            // Core may translate a provider cancellation into a plain failure outcome.
            await cts.CancelAsync();
            return new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "The operation failed.");
        }
    }

    [Fact]
    public async Task Target_free_failure_under_a_cancelled_token_reports_cancelled()
    {
        using var cts = new CancellationTokenSource(Budget);
        var handlers = new McpToolHandlers(TestScope(), new FailingAfterCancelModule(cts));

        var result = await handlers.PlanAsync(null!, "<ShowPlanXML/>", ct: cts.Token);

        AssertCancelled(result, "sqlharness_plan");
    }

    [Fact]
    public async Task Midflight_cancel_reaches_the_module()
    {
        var module = new CancellingModule();
        var handlers = new McpToolHandlers(TestScope(), module);
        using var cts = new CancellationTokenSource(Budget);

        var inflight = handlers.MeasureAsync(
            null!, new McpSqlSourceArgument { Sql = "SELECT 2" }, ct: cts.Token);
        await module.Entered.Task.WaitAsync(Budget, CancellationToken.None);

        await cts.CancelAsync();
        AssertCancelled(await inflight.WaitAsync(Budget), "sqlharness_measure");

        // The linked token reached the module: the module observed the
        // cancellation instead of hanging, and its token is cancelled.
        await module.CancelObserved.Task.WaitAsync(Budget, CancellationToken.None);
        Assert.True(module.Tokens[0].IsCancellationRequested);

        // Cleanup ran: the next call on a fresh token executes normally.
        module.BlockCall = call => call == 1;
        using var fresh = new CancellationTokenSource(Budget);
        var next = await handlers.QueryAsync(null!, "SELECT 1", ct: fresh.Token);
        Assert.False(next.IsError == true, Text(next));
        Assert.Equal(2, module.Operations.Count);
    }

    [Fact]
    public async Task Request_scope_A_cancellation_disposes_its_session_and_allows_B()
    {
        var process = McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = ["scope-a", "scope-b"] },
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["scope-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
                ["scope-b"] = new("server-b.invalid", "database-b", new Dictionary<string, string> { ["tenant"] = "^b$" }, "integrated"),
            });
        var sessions = new CoreSessionFactory(blockFirstSession: true);
        var handlers = new McpRequestToolHandlers(process, moduleFactory: scope =>
            new SqlHarnessModule(sessions, scope.ProfileProvider));
        using var cancelA = new CancellationTokenSource(Budget);

        var a = handlers.QueryAsync(null!, new McpRequestScopeArgument("scope-a", new() { ["tenant"] = "a" }), "SELECT 1", ct: cancelA.Token);
        await sessions.FirstExecuteEntered.Task.WaitAsync(Budget);
        await cancelA.CancelAsync();

        AssertCancelled(await a.WaitAsync(Budget), "sqlharness_query");
        await sessions.FirstSessionDisposed.Task.WaitAsync(Budget);

        var b = await handlers.QueryAsync(null!, new McpRequestScopeArgument("scope-b", new() { ["tenant"] = "b" }), "SELECT 2");
        Assert.False(b.IsError == true, Text(b));
        Assert.Equal(2, sessions.ConnectedSessions);
        Assert.Equal(["database-a", "database-b"], sessions.Targets.Select(target => target.Database));
    }

    [Fact]
    public async Task Request_scope_process_deadline_reaches_execution_and_disposes_the_session()
    {
        var process = McpProcessContext.Create(
            new McpServerOptions
            {
                RequestScope = true,
                AllowedProfiles = ["scope-a"],
                MaxOperationSeconds = 1,
            },
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["scope-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
            });
        var sessions = new CoreSessionFactory(blockFirstSession: true);
        var handlers = new McpRequestToolHandlers(process, moduleFactory: scope =>
            new SqlHarnessModule(sessions, scope.ProfileProvider));
        using var guard = new CancellationTokenSource(Budget);

        var result = await handlers.QueryAsync(
            null!, new McpRequestScopeArgument("scope-a", new() { ["tenant"] = "a" }), "SELECT 1", ct: guard.Token);

        AssertCancelled(result, "sqlharness_query");
        await sessions.FirstExecuteEntered.Task.WaitAsync(Budget);
        await sessions.FirstSessionDisposed.Task.WaitAsync(Budget);
    }

    private static Dictionary<string, object?> QueryArgs(string sql) => new() { ["sql"] = sql };

    /// <summary>
    /// Real protocol path: the fixed-scope catalog on the SDK stream transport,
    /// the real Core module over the synthetic session factory.
    /// </summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new(Budget);
        private McpServer? _server;
        private Task? _serverTask;

        public McpClient Client { get; private set; } = null!;

        public static async Task<TestServer> StartAsync(ISqlSessionFactory sessions)
        {
            var started = new TestServer();
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var options = McpHost.CreateServerOptions();
            var scope = TestScope();
            McpToolCatalog.Wire(options, scope, new SqlHarnessModule(sessions, scope.ProfileProvider), hostShutdown: started._lifetime.Token);
            started._server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "t5-concurrency", NullLoggerFactory.Instance),
                options, NullLoggerFactory.Instance, serviceProvider: null);
            started._serverTask = started._server.RunAsync(started._lifetime.Token);
            started.Client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" } },
                NullLoggerFactory.Instance, started._lifetime.Token);
            return started;
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            if (Client is not null) await Client.DisposeAsync();
            if (_serverTask is not null)
            {
                try { await _serverTask; } catch (OperationCanceledException) { }
            }
            if (_server is not null) await _server.DisposeAsync();
            _lifetime.Dispose();
        }
    }

    [Fact]
    public async Task A_second_query_runs_while_the_first_is_blocked()
    {
        var sessions = new CoreSessionFactory(blockFirstSession: true);
        await using var server = await TestServer.StartAsync(sessions);

        var first = server.Client.CallToolAsync("sqlharness_query", QueryArgs("SELECT 1 AS A")).AsTask();
        await sessions.SessionEntered(1).WaitAsync(Budget);
        var second = await server.Client.CallToolAsync("sqlharness_query", QueryArgs("SELECT 2 AS A")).AsTask().WaitAsync(Budget);

        Assert.False(second.IsError == true, Text(second));
        Assert.False(first.IsCompleted);
        sessions.ReleaseSession(1);
        var firstResult = await first.WaitAsync(Budget);
        Assert.False(firstResult.IsError == true, Text(firstResult));
        Assert.Equal(2, sessions.ConnectedSessions);
    }

    [Fact]
    public async Task Cancelling_one_call_leaves_the_concurrent_call_running()
    {
        var sessions = new CoreSessionFactory(blockedSessions: 2);
        var scope = TestScope();
        var handlers = new McpToolHandlers(scope, new SqlHarnessModule(sessions, scope.ProfileProvider));
        using var cancelFirst = new CancellationTokenSource(Budget);
        using var keepSecond = new CancellationTokenSource(Budget);

        var first = handlers.QueryAsync(null!, "SELECT 1 AS A", ct: cancelFirst.Token);
        await sessions.SessionEntered(1).WaitAsync(Budget);
        var second = handlers.QueryAsync(null!, "SELECT 2 AS A", ct: keepSecond.Token);
        // A second call that returns before reaching Core was rejected, not run.
        await Task.WhenAny(sessions.SessionEntered(2), second).WaitAsync(Budget);
        Assert.False(second.IsCompleted, second.IsCompleted ? Text(await second) : null);

        // Both calls are parked inside Core at once. Cancel only the first.
        await cancelFirst.CancelAsync();
        AssertCancelled(await first.WaitAsync(Budget), "sqlharness_query");
        await sessions.SessionDisposed(1).WaitAsync(Budget);
        Assert.False(second.IsCompleted);
        Assert.False(sessions.SessionDisposed(2).IsCompleted);

        // The second call kept its own token: released, it completes normally.
        sessions.ReleaseSession(2);
        var secondResult = await second.WaitAsync(Budget);
        Assert.False(secondResult.IsError == true, Text(secondResult));
        await sessions.SessionDisposed(2).WaitAsync(Budget);
    }

    [Fact]
    public async Task Process_deadline_cancels_a_long_call()
    {
        var scope = TestScope(new McpServerOptions { Profile = "mcp-t5", MaxOperationSeconds = 1 });
        var module = new CancellingModule();
        var handlers = new McpToolHandlers(scope, module);
        using var cts = new CancellationTokenSource(Budget);

        var result = await handlers.QueryAsync(null!, "SELECT 1", ct: cts.Token);

        AssertCancelled(result, "sqlharness_query");
        await module.CancelObserved.Task.WaitAsync(Budget, CancellationToken.None);
    }

    [Fact]
    public async Task Per_call_deadline_lowers_the_process_maximum_only()
    {
        var module = new CancellingModule();
        var handlers = new McpToolHandlers(TestScope(), module);
        using var cts = new CancellationTokenSource(Budget);

        // Out-of-range per-call requests fail closed before executing.
        AssertInvalidBudget(
            await handlers.QueryAsync(null!, "SELECT 1", maxOperationSeconds: 0, ct: cts.Token),
            "sqlharness_query");
        AssertInvalidBudget(
            await handlers.QueryAsync(null!, "SELECT 1", maxResultBytes: 1000, ct: cts.Token),
            "sqlharness_query");
        Assert.Empty(module.Operations);

        // Lowering the deadline below the process maximum cancels the call.
        AssertCancelled(
            await handlers.QueryAsync(null!, "SELECT 1", maxOperationSeconds: 1, ct: cts.Token),
            "sqlharness_query");
    }

    [Fact]
    public async Task Watch_deadline_is_capped_by_the_effective_call_budget()
    {
        // The handler clamps the watch maxDuration to the remaining request
        // budget, so a natural stop stays a controlled exit 7 inside it.
        var captured = new List<SqlHarnessOperation>();
        var capturing = new CapturingModule(captured);
        var handlers = new McpToolHandlers(TestScope(), capturing);
        using var cts = new CancellationTokenSource(Budget);

        // Process maximum (900 s) caps a 2 h watch.
        var capped = await handlers.WatchAsync(null!, "SELECT 1", maxDuration: "2h", ct: cts.Token);
        Assert.False(capped.IsError == true, Text(capped));
        Assert.Equal(TimeSpan.FromSeconds(900), Assert.IsType<SqlHarnessWatchOperation>(captured[0]).MaxDuration);

        // A per-call request lowers it further; above-maximum clamps to it.
        var lowered = await handlers.WatchAsync(null!, "SELECT 1", maxDuration: "2h", maxOperationSeconds: 30, ct: cts.Token);
        Assert.False(lowered.IsError == true, Text(lowered));
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<SqlHarnessWatchOperation>(captured[1]).MaxDuration);

        var clamped = await handlers.WatchAsync(null!, "SELECT 1", maxDuration: "2h", maxOperationSeconds: 3600, ct: cts.Token);
        Assert.False(clamped.IsError == true, Text(clamped));
        Assert.Equal(TimeSpan.FromSeconds(900), Assert.IsType<SqlHarnessWatchOperation>(captured[2]).MaxDuration);
    }

    private sealed class CapturingModule(List<SqlHarnessOperation> captured) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            captured.Add(operation);
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }

    [Fact]
    public async Task Natural_watch_deadline_still_reports_exit7()
    {
        // Control: a genuine Core max-duration outcome keeps its controlled
        // status. Only cancellation maps to the cancelled result.
        using var cts = new CancellationTokenSource(Budget);

        // The stub returns what Core returns on a natural watch deadline.
        var natural = new SqlHarnessOutcome(SqlHarnessExitCode.WatchMaxDuration, null, null);
        var handlers = new McpToolHandlers(TestScope(), new FixedModule(natural));
        var result = await handlers.WatchAsync(null!, "SELECT 1", ct: cts.Token);

        Assert.False(result.IsError == true, Text(result));
        var envelope = Envelope(result);
        Assert.Equal((int)SqlHarnessExitCode.WatchMaxDuration, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal("watch_max_duration", envelope.GetProperty("status").GetString());
    }

    private sealed class FixedModule(SqlHarnessOutcome outcome) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(outcome);
    }
}

/// <summary>
/// Synthetic Core session factory for the real <see cref="SqlHarnessModule"/>.
/// The first <c>blockedSessions</c> sessions (numbered in connect order from 1)
/// park in ExecuteReaderAsync until released or cancelled, and signal entry
/// and disposal, so concurrency tests synchronize on signals, never on time.
/// </summary>
internal sealed class CoreSessionFactory : ISqlSessionFactory
{
    private const int TrackedSessions = 8;
    private readonly int _blockedSessions;
    private readonly TaskCompletionSource[] _entered = Signals();
    private readonly TaskCompletionSource[] _released = Signals();
    private readonly TaskCompletionSource[] _disposed = Signals();
    private int _connectedSessions;

    public CoreSessionFactory(bool blockFirstSession)
        : this(blockFirstSession ? 1 : 0)
    {
    }

    public CoreSessionFactory(int blockedSessions)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockedSessions, TrackedSessions);
        _blockedSessions = blockedSessions;
    }

    public int ConnectedSessions => Volatile.Read(ref _connectedSessions);
    public List<ResolvedTarget> Targets { get; } = [];
    public TaskCompletionSource FirstExecuteEntered => _entered[0];
    public TaskCompletionSource FirstSessionDisposed => _disposed[0];

    /// <summary>Completes when session <paramref name="number"/> (1-based) parks in execute.</summary>
    public Task SessionEntered(int number) => _entered[number - 1].Task;

    /// <summary>Completes when session <paramref name="number"/> (1-based) is disposed.</summary>
    public Task SessionDisposed(int number) => _disposed[number - 1].Task;

    /// <summary>Lets the parked session <paramref name="number"/> (1-based) finish its execute.</summary>
    public void ReleaseSession(int number) => _released[number - 1].TrySetResult();

    public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var number = Interlocked.Increment(ref _connectedSessions);
        lock (Targets) Targets.Add(target);
        var tracked = number <= TrackedSessions;
        return Task.FromResult<ISqlSession>(new CoreSession(
            target,
            tracked && number <= _blockedSessions ? _entered[number - 1] : null,
            tracked ? _released[number - 1].Task : Task.CompletedTask,
            () =>
            {
                if (tracked) _disposed[number - 1].TrySetResult();
            }));
    }

    private static TaskCompletionSource[] Signals() =>
        Enumerable.Range(0, TrackedSessions)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

    private sealed class CoreSession(
        ResolvedTarget target,
        TaskCompletionSource? entered,
        Task released,
        Action disposed) : ISqlSession
    {
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = new(target.Server, target.Database, target.Server, target.Database, "profile");
        public async Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            if (entered is not null)
            {
                entered.TrySetResult();
                await released.WaitAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            return new CoreReader();
        }
        public ValueTask DisposeAsync() { disposed(); return ValueTask.CompletedTask; }
    }

    private sealed class CoreReader : ISqlReader
    {
        public int FieldCount => 0;
        public int RecordsAffected => 0;
        public string GetName(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Type GetFieldType(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public bool GetAllowNull(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public object GetValue(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Task<bool> ReadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(false); }
        public Task<bool> NextResultAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(false); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}