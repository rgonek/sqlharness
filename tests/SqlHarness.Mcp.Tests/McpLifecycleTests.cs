using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T5 lifecycle contract: one active database operation per process (a second
/// concurrent database call gets a stable BUSY rejection without a queue),
/// local tools run in parallel with a held gate, matrix/param-set batches
/// count as one operation, calls share no request state, EOF shuts the host
/// down cleanly, EOF during a call cancels it through the explicit binding
/// (the SDK alone does not propagate EOF), shutdown cancels an in-flight
/// call so no watch hangs, MCP
/// watch never touches the NDJSON path or stdout, progress fires only for a
/// client token at most once per second without SQL, and annotations stay
/// conservative. All data is synthetic; no database is opened. Every wait has
/// a 30 s backstop; synchronization uses task completion sources, never
/// performance-dependent sleeps.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly SqlHarnessTargetIdentityReport Target = new("req-srv", "req-db", "srv", "db", "profile");

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

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public int NdjsonCalls;
        public Func<SqlHarnessOperation, CancellationToken, Task<SqlHarnessOutcome>>? Behavior;

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            lock (Operations)
                Operations.Add(operation);
            return Behavior is null
                ? Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))
                : Behavior(operation, ct);
        }

        public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
            SqlHarnessWatchOperation operation, TextWriter writer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref NdjsonCalls);
            throw new NotSupportedException("MCP must never use the NDJSON watch path.");
        }
    }

    private sealed class ManualMcpClock(DateTimeOffset start) : IMcpClock
    {
        private DateTimeOffset _now = start;
        public DateTimeOffset UtcNow => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class ProgressCollector : IProgress<ProgressNotificationValue>
    {
        private readonly object _sync = new();
        public List<ProgressNotificationValue> Values { get; } = [];
        public void Report(ProgressNotificationValue value)
        {
            lock (_sync)
                Values.Add(value);
        }

        public IReadOnlyList<ProgressNotificationValue> Snapshot()
        {
            lock (_sync)
                return Values.ToArray();
        }
    }

    private static async Task<IReadOnlyList<ProgressNotificationValue>> WaitForProgressCountAsync(
            ProgressCollector collector, int expected, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (true)
        {
            var snapshot = collector.Snapshot();
            if (snapshot.Count >= expected)
                return snapshot;
            await Task.Delay(TimeSpan.FromMilliseconds(25), linked.Token);
        }
    }

    private static async Task AssertNoProgressGrowthAsync(
            ProgressCollector collector, int expected, TimeSpan settle, CancellationToken ct)
    {
        // Negative silence assertion: poll until the settle window elapses
        // and fail loudly on any growth, so a lagging/spurious notification
        // arriving after the token-less call cannot pass on a too-early
        // snapshot. The 30 s Budget on the caller's token is the backstop.
        var deadline = DateTime.UtcNow + settle;
        while (true)
        {
            Assert.Equal(expected, collector.Snapshot().Count);
            if (DateTime.UtcNow >= deadline)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
    }

    private static ProgressNotificationValue? TryParseProgress(JsonNode? paramsNode)
    {
        try
        {
            if (paramsNode is not JsonObject obj
                || obj["progress"]?.GetValue<float>() is not float progress)
                return null;
            return new ProgressNotificationValue
            {
                Progress = progress,
                Total = obj["total"]?.GetValue<float?>(),
                Message = obj["message"]?.GetValue<string>(),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Text(CallToolResult result) =>
        Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    private static JsonElement Envelope(CallToolResult result) =>
        JsonDocument.Parse(Text(result)).RootElement;

    private static void AssertBusy(CallToolResult result, string command)
    {
        Assert.True(result.IsError == true, Text(result));
        var envelope = Envelope(result);
        Assert.Equal("error", envelope.GetProperty("status").GetString());
        Assert.Equal((int)SqlHarnessExitCode.Safety, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(command, envelope.GetProperty("command").GetString());
        var error = envelope.GetProperty("error");
        Assert.Equal(McpExecutionGate.BusyCode, error.GetProperty("code").GetString());
        Assert.Equal(McpExecutionGate.BusyMessage, error.GetProperty("message").GetString());
        Assert.Empty(McpResultAdapter.ValidateEnvelope(envelope));
    }

    [Fact]
    public void Db_tool_classification_covers_exactly_the_six_gated_tools()
    {
        Assert.True(McpExecutionGate.IsDbTool("sqlharness_query"));
        Assert.True(McpExecutionGate.IsDbTool("sqlharness_measure"));
        Assert.True(McpExecutionGate.IsDbTool("sqlharness_compare"));
        Assert.True(McpExecutionGate.IsDbTool("sqlharness_watch"));
        Assert.True(McpExecutionGate.IsDbTool("sqlharness_snapshot"));
        Assert.False(McpExecutionGate.IsDbTool("sqlharness_capabilities"));
        Assert.True(McpExecutionGate.IsDbTool("sqlharness_inspect"));
        Assert.False(McpExecutionGate.IsDbTool("sqlharness_validate"));
        Assert.False(McpExecutionGate.IsDbTool("sqlharness_plan"));
        Assert.False(McpExecutionGate.IsDbTool("sqlharness_artifact"));
        Assert.False(McpExecutionGate.IsDbTool("sqlharness_gain"));
        Assert.False(McpExecutionGate.IsDbTool(null));
        Assert.False(McpExecutionGate.IsDbTool("SQLHARNESS_QUERY"));
    }

    [Fact]
    public async Task Concurrent_db_calls_run_once_and_reject_stably_without_queue()
    {
        var gate = new McpExecutionGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            },
        };
        var handlers = new McpToolHandlers(TestScope(), module, gate);
        using var cts = new CancellationTokenSource(Budget);

        var first = handlers.QueryAsync(null!, "SELECT 1", ct: cts.Token);
        await entered.Task.WaitAsync(Budget, cts.Token);

        // Two further database calls while the first holds the gate: neither
        // executes, both get the identical stable BUSY rejection.
        var busyMeasure = await handlers.MeasureAsync(
            null!, new McpSqlSourceArgument { Sql = "SELECT 2" }, ct: cts.Token);
        var busyQuery = await handlers.QueryAsync(null!, "SELECT 3", ct: cts.Token);
        AssertBusy(busyMeasure, "sqlharness_measure");
        AssertBusy(busyQuery, "sqlharness_query");
        var measureError = Envelope(busyMeasure).GetProperty("error").GetRawText();
        var queryError = Envelope(busyQuery).GetProperty("error").GetRawText();
        Assert.Equal(measureError, queryError);

        // The hint names the tool that holds the slot, not the rejected one.
        const string expectedHint =
            "One database operation runs per process. Wait for the running sqlharness_query call to return, then send the next call.";
        Assert.Equal(expectedHint, Envelope(busyMeasure).GetProperty("error").GetProperty("hint").GetString());
        Assert.Equal(expectedHint, Envelope(busyQuery).GetProperty("error").GetProperty("hint").GetString());

        release.TrySetResult();
        Assert.False((await first).IsError == true);

        // The gate is free again: the next call executes on the module.
        var next = await handlers.QueryAsync(null!, "SELECT 4", ct: cts.Token);
        Assert.False(next.IsError == true, Text(next));
        Assert.Equal(2, module.Operations.Count);
    }

    [Fact]
    public void Busy_hint_falls_back_to_generic_text_when_no_holder_is_recorded()
    {
        var gate = new McpExecutionGate();
        Assert.True(gate.TryEnterDb());
        Assert.Null(gate.RunningTool);

        var result = McpExecutionGate.BusyResult("sqlharness_query", runningTool: gate.RunningTool);
        Assert.Equal(
            "One database operation runs per process. Wait for the running database call to return, then send the next call.",
            Envelope(result).GetProperty("error").GetProperty("hint").GetString());

        gate.ExitDb();
        Assert.Null(gate.RunningTool);
        Assert.True(gate.TryEnterDb("sqlharness_watch"));
        Assert.Equal("sqlharness_watch", gate.RunningTool);
        gate.ExitDb();
        Assert.Null(gate.RunningTool);
    }

    [Fact]
    public async Task Request_scopes_share_gate_while_local_capabilities_keeps_running()
    {
        var process = McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a", "sample-b"] },
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["sample-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
                ["sample-b"] = new("server-b.invalid", "database-b", new Dictionary<string, string> { ["tenant"] = "^b$" }, "integrated"),
            });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            },
        };
        var handlers = new McpRequestToolHandlers(process, moduleFactory: _ => module);
        using var guard = new CancellationTokenSource(Budget);

        var blocked = handlers.QueryAsync(null!, new McpRequestScopeArgument("sample-a", new() { ["tenant"] = "a" }), "SELECT 1", ct: guard.Token);
        await entered.Task.WaitAsync(Budget, guard.Token);
        var capabilities = await handlers.CapabilitiesAsync(null!, ct: guard.Token);
        Assert.False(capabilities.IsError == true, Text(capabilities));
        var capabilitiesJson = Envelope(capabilities);
        Assert.Equal("request", capabilitiesJson.GetProperty("result").GetProperty("scopeMode").GetString());
        Assert.Contains("sample-a", capabilitiesJson.GetProperty("result").GetProperty("allowedProfiles").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("server-a", Text(capabilities), StringComparison.Ordinal);

        release.TrySetResult();
        Assert.False((await blocked).IsError == true);
        Assert.Single(module.Operations);
        Assert.Equal("sample-a", Assert.IsType<SqlHarnessQueryOperation>(module.Operations[0]).Target.Profile);
    }

    [Fact]
    public async Task Local_capabilities_runs_while_a_database_call_holds_the_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            },
        };
        var handlers = new McpToolHandlers(TestScope(), module, new McpExecutionGate());
        using var cts = new CancellationTokenSource(Budget);

        var blocked = handlers.QueryAsync(null!, "SELECT 1", ct: cts.Token);
        await entered.Task.WaitAsync(Budget, cts.Token);

        var capabilities = await handlers.CapabilitiesAsync(null!, false, cts.Token);
        Assert.False(capabilities.IsError == true, Text(capabilities));

        release.TrySetResult();
        Assert.False((await blocked).IsError == true);
        Assert.Single(module.Operations);
    }

    [Fact]
    public async Task Active_query_blocks_inspect_with_stable_busy()
    {
        var gate = new McpExecutionGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                // Only the first (gate-holding) call parks: a concurrent
                // second execution on old code returns at once, so RED is a
                // clean busy assertion failure instead of a deadlock.
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            },
        };
        var handlers = new McpToolHandlers(TestScope(), module, gate);
        using var cts = new CancellationTokenSource(Budget);

        var blockedQuery = handlers.QueryAsync(null!, "SELECT 1", ct: cts.Token);
        await entered.Task.WaitAsync(Budget, cts.Token);

        var busy = await handlers.InspectAsync(null!, "ping", timeout: 5, ct: cts.Token);
        AssertBusy(busy, "sqlharness_inspect");

        release.TrySetResult();
        Assert.False((await blockedQuery).IsError == true);
        Assert.Single(module.Operations);
    }

    [Fact]
    public async Task Active_inspect_blocks_query_and_second_inspect()
    {
        var gate = new McpExecutionGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                // Only the first (gate-holding) call parks: concurrent
                // executions on old code return at once, so RED is a clean
                // busy assertion failure instead of a deadlock.
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            },
        };
        var handlers = new McpToolHandlers(TestScope(), module, gate);
        using var cts = new CancellationTokenSource(Budget);

        var holder = handlers.InspectAsync(null!, "ping", timeout: 5, ct: cts.Token);
        await entered.Task.WaitAsync(Budget, cts.Token);

        // Both a different database tool and a second inspect get the same
        // stable BUSY rejection without executing.
        var busyQuery = await handlers.QueryAsync(null!, "SELECT 1", ct: cts.Token);
        AssertBusy(busyQuery, "sqlharness_query");
        var busyInspect = await handlers.InspectAsync(null!, "ping", timeout: 5, ct: cts.Token);
        AssertBusy(busyInspect, "sqlharness_inspect");
        var queryError = Envelope(busyQuery).GetProperty("error").GetRawText();
        var inspectError = Envelope(busyInspect).GetProperty("error").GetRawText();
        Assert.Equal(queryError, inspectError);

        release.TrySetResult();
        Assert.False((await holder).IsError == true);
        Assert.Single(module.Operations);
    }

    [Fact]
    public async Task Inspect_mapping_rejection_releases_the_gate()
    {
        var gate = new McpExecutionGate();
        var module = new RecordingModule();
        var handlers = new McpToolHandlers(TestScope(), module, gate);
        using var cts = new CancellationTokenSource(Budget);

        // An unknown kind never reaches the module: a stable exit-2
        // contract rejection, and the gate slot is not held.
        var rejected = await handlers.InspectAsync(null!, "bogus", ct: cts.Token);
        Assert.True(rejected.IsError == true, Text(rejected));
        var rejectedEnvelope = Envelope(rejected);
        Assert.Equal("error", rejectedEnvelope.GetProperty("status").GetString());
        Assert.Equal((int)SqlHarnessExitCode.Safety, rejectedEnvelope.GetProperty("exitCode").GetInt32());
        Assert.Equal("sqlharness_inspect", rejectedEnvelope.GetProperty("command").GetString());
        Assert.Empty(McpResultAdapter.ValidateEnvelope(rejectedEnvelope));
        Assert.Empty(module.Operations);

        // The slot is free: the next inspect and query both execute.
        var nextInspect = await handlers.InspectAsync(null!, "ping", timeout: 5, ct: cts.Token);
        Assert.False(nextInspect.IsError == true, Text(nextInspect));
        var nextQuery = await handlers.QueryAsync(null!, "SELECT 1", ct: cts.Token);
        Assert.False(nextQuery.IsError == true, Text(nextQuery));
        Assert.Equal(2, module.Operations.Count);
    }

    [Fact]
    public async Task Inspect_core_failure_releases_the_gate()
    {
        var gate = new McpExecutionGate();
        var module = new RecordingModule
        {
            Behavior = (_, _) => throw new InvalidOperationException("boom"),
        };
        var handlers = new McpToolHandlers(TestScope(), module, gate);
        using var cts = new CancellationTokenSource(Budget);

        var failed = await handlers.InspectAsync(null!, "ping", timeout: 5, ct: cts.Token);
        Assert.True(failed.IsError == true, Text(failed));
        var failedEnvelope = Envelope(failed);
        Assert.Equal("error", failedEnvelope.GetProperty("status").GetString());
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, failedEnvelope.GetProperty("exitCode").GetInt32());
        Assert.Equal("sqlharness_inspect", failedEnvelope.GetProperty("command").GetString());
        Assert.Empty(McpResultAdapter.ValidateEnvelope(failedEnvelope));
        Assert.Single(module.Operations);

        // The slot is free: the next query executes on the module.
        module.Behavior = null;
        var next = await handlers.QueryAsync(null!, "SELECT 1", ct: cts.Token);
        Assert.False(next.IsError == true, Text(next));
        Assert.Equal(2, module.Operations.Count);
    }

    [Fact]
    public async Task Host_shutdown_cancels_an_inflight_inspect_and_releases_the_gate()
    {
        var gate = new McpExecutionGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                entered.TrySetResult();
                // Honor the token exactly like Core: the linked host token
                // reaches the module, so shutdown cancels the wait.
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("A cancelled wait never completes.");
            },
        };
        using var hostCts = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(Budget);
        var handlers = new McpToolHandlers(TestScope(), module, gate, hostShutdown: hostCts.Token);

        var inflight = handlers.InspectAsync(null!, "ping", timeout: 5, ct: guard.Token);
        await entered.Task.WaitAsync(Budget, guard.Token);

        // Process close: the linked token reaches the module, the inspect
        // reports cancelled, and the gate slot is released.
        await hostCts.CancelAsync();
        var cancelled = await inflight.WaitAsync(Budget, guard.Token);
        Assert.True(cancelled.IsError == true, Text(cancelled));
        var cancelledEnvelope = Envelope(cancelled);
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, cancelledEnvelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(McpExecutionGate.CancelledCode, cancelledEnvelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(McpExecutionGate.CancelledMessage, cancelledEnvelope.GetProperty("error").GetProperty("message").GetString());
        Assert.True(gate.TryEnterDb());
        gate.ExitDb();
    }

    [Fact]
    public async Task Local_capabilities_and_validate_run_while_the_gate_is_held()
    {
        var heldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localModule = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                heldEntered.TrySetResult();
                await heldRelease.Task.WaitAsync(ct);
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            },
        };
        var localHandlers = new McpToolHandlers(TestScope(), localModule, new McpExecutionGate());
        using var localCts = new CancellationTokenSource(Budget);

        var held = localHandlers.QueryAsync(null!, "SELECT 1", ct: localCts.Token);
        await heldEntered.Task.WaitAsync(Budget, localCts.Token);

        // Offline local tools share no gate and stay available while a
        // database call holds it.
        var capabilities = await localHandlers.CapabilitiesAsync(null!, false, localCts.Token);
        Assert.False(capabilities.IsError == true, Text(capabilities));
        var validate = await localHandlers.ValidateAsync(null!, "query", sql: "SELECT 1", ct: localCts.Token);
        Assert.False(validate.IsError == true, Text(validate));

        heldRelease.TrySetResult();
        Assert.False((await held).IsError == true);
        Assert.Single(localModule.Operations);
    }

    [Fact]
    public async Task Compare_with_matrix_counts_as_a_single_db_operation()
    {
        var module = new RecordingModule();
        var handlers = new McpToolHandlers(TestScope(), module, new McpExecutionGate());
        using var cts = new CancellationTokenSource(Budget);

        var result = await handlers.CompareAsync(
            null!,
            new McpSqlSourceArgument { Sql = "SELECT 1" },
            new McpSqlSourceArgument { Sql = "SELECT 2" },
            matrix: new McpMatrixArgument { Name = "batch", Type = "int", Values = ["1", "20"] },
            ct: cts.Token);

        Assert.False(result.IsError == true, Text(result));
        Assert.IsType<SqlHarnessCompareMatrixOperation>(Assert.Single(module.Operations));
    }

    [Fact]
    public async Task Calls_share_no_reader_temp_or_parameter_state()
    {
        var module = new RecordingModule();
        var handlers = new McpToolHandlers(TestScope(), module, new McpExecutionGate());
        using var cts = new CancellationTokenSource(Budget);

        var first = await handlers.QueryAsync(
            null!, "SELECT 1", parameters: [new McpParameterArgument { Name = "customerId", Type = "int", Value = "42" }],
            ct: cts.Token);
        var second = await handlers.QueryAsync(
            null!, "SELECT 2", parameters: [new McpParameterArgument { Name = "otherId", Type = "int", Value = "7" }],
            ct: cts.Token);

        Assert.False(first.IsError == true, Text(first));
        Assert.False(second.IsError == true, Text(second));
        Assert.Equal(2, module.Operations.Count);
        var firstOperation = Assert.IsType<SqlHarnessQueryOperation>(module.Operations[0]);
        var secondOperation = Assert.IsType<SqlHarnessQueryOperation>(module.Operations[1]);
        Assert.NotSame(firstOperation, secondOperation);
        Assert.Equal("SELECT 1", firstOperation.Sql);
        Assert.Equal([new SqlHarnessParameterInput("customerId", "int", "42")], firstOperation.TypedParameters);
        Assert.Equal("SELECT 2", secondOperation.Sql);
        Assert.Equal([new SqlHarnessParameterInput("otherId", "int", "7")], secondOperation.TypedParameters);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Eof_on_stdin_shuts_the_host_down_cleanly(string revision)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var log = new StringWriter();
        var options = new McpServerOptions { Profile = "mcp-t5" };

        Task<int> hostTask = McpHost.RunAsync(
            options,
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            log,
            TestProfiles,
            cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = revision,
            },
            NullLoggerFactory.Instance,
            cts.Token))
        {
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
        }

        // EOF on stdin: the server loop ends and the process exits cleanly.
        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
    }

    [Fact]
    public async Task Host_shutdown_cancels_an_inflight_call_and_releases_the_gate()
    {
        var gate = new McpExecutionGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                entered.TrySetResult();
                // Honor the token exactly like Core: connect, setup, repeat,
                // sidecar, watch delay, and artifact write all observe it.
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("A cancelled wait never completes.");
            },
        };
        using var hostCts = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(Budget);
        var handlers = new McpToolHandlers(TestScope(), module, gate, hostShutdown: hostCts.Token);

        var inflight = handlers.WatchAsync(null!, "SELECT 1", ct: guard.Token);
        await entered.Task.WaitAsync(Budget, guard.Token);

        // Process close: the linked token reaches the module, the watch does
        // not hang, and the call reports cancelled, never exit 7.
        await hostCts.CancelAsync();
        var cancelled = await inflight.WaitAsync(Budget, guard.Token);
        Assert.True(cancelled.IsError == true, Text(cancelled));
        var envelope = Envelope(cancelled);
        Assert.Equal((int)SqlHarnessExitCode.SqlExecution, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(McpExecutionGate.CancelledCode, envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(McpExecutionGate.CancelledMessage, envelope.GetProperty("error").GetProperty("message").GetString());

        // Cleanup ran: the gate slot is free again. (New calls still
        // observe the dead host token and report cancelled; recovery of a
        // live process after client cancellation is covered by the
        // cancellation tests.)
        Assert.True(gate.TryEnterDb());
        gate.ExitDb();
    }

    [Fact]
    public async Task Eof_during_inflight_cancels_the_call_and_releases_the_gate()
    {
        var gate = new McpExecutionGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moduleCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moduleExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                using var reg = ct.Register(() => moduleCancelled.TrySetResult());
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    moduleExited.TrySetResult();
                }
                throw new InvalidOperationException("A cancelled wait never completes.");
            },
        };
        using var guard = new CancellationTokenSource(Budget);
        // The external host token stays live: only stdin EOF may cancel here.
        using var externalShutdown = new CancellationTokenSource();
        using var eofShutdown = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(externalShutdown.Token, eofShutdown.Token);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t5-test" },
            ProtocolVersion = McpHost.FallbackProtocolVersion,
        };
        // Same composition as McpHost.RunAsync: one process gate, one
        // EOF-bound shutdown token, the real stream transport.
        McpToolCatalog.Wire(serverOptions, TestScope(), module, gate, hostShutdown: lifetime.Token);
        using var eofInput = new EofShutdownInput(clientToServer.Reader.AsStream(), eofShutdown);
        await using var server = McpServer.Create(
            new StreamServerTransport(
                eofInput, serverToClient.Writer.AsStream(), "t5-test-server", NullLoggerFactory.Instance),
            serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(lifetime.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            },
            NullLoggerFactory.Instance, guard.Token);

        var inflight = client.CallToolAsync(
            "sqlharness_watch",
            new Dictionary<string, object?> { ["sql"] = "SELECT 1" },
            cancellationToken: guard.Token);
        await entered.Task.WaitAsync(Budget, guard.Token);

        // EOF on stdin with a live host token. The SDK alone does not
        // propagate this (T5 fix R1 probe: handler token stayed live, server
        // loop parked); the explicit binding must cancel the in-flight watch.
        // The module observes cancellation, so the call can never complete as
        // a natural watch exit 7; the server loop then ends and the gate slot
        // is released.
        await clientToServer.Writer.CompleteAsync();
        await moduleCancelled.Task.WaitAsync(Budget, guard.Token);
        await moduleExited.Task.WaitAsync(Budget, guard.Token);
        try
        {
            await serverTask.WaitAsync(Budget, guard.Token);
        }
        catch (OperationCanceledException)
        {
        }
        Assert.True(gate.TryEnterDb());
        gate.ExitDb();
        _ = inflight;
    }

    [Fact]
    public async Task Request_scope_EOF_cancels_A_and_never_runs_queued_B()
    {
        var process = McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a", "sample-b"] },
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["sample-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
                ["sample-b"] = new("server-b.invalid", "database-b", new Dictionary<string, string> { ["tenant"] = "^b$" }, "integrated"),
            });
        var gate = process.Gate;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moduleCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moduleExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new RecordingModule
        {
            Behavior = async (_, ct) =>
            {
                using var reg = ct.Register(() => moduleCancelled.TrySetResult());
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { moduleExited.TrySetResult(); }
                throw new InvalidOperationException("A cancelled request never completes.");
            },
        };
        using var guard = new CancellationTokenSource(Budget);
        using var externalShutdown = new CancellationTokenSource();
        using var eofShutdown = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(externalShutdown.Token, eofShutdown.Token);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t5-test" },
            ProtocolVersion = McpHost.FallbackProtocolVersion,
        };
        McpToolCatalog.Wire(serverOptions, process, hostShutdown: lifetime.Token, moduleFactory: _ => module);
        using var eofInput = new EofShutdownInput(clientToServer.Reader.AsStream(), eofShutdown);
        await using var server = McpServer.Create(
            new StreamServerTransport(eofInput, serverToClient.Writer.AsStream(), "t5-request-server", NullLoggerFactory.Instance),
            serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(lifetime.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            }, NullLoggerFactory.Instance, guard.Token);

        static Dictionary<string, object?> WatchArgs(string profile, string tenant) => new()
        {
            ["scope"] = new Dictionary<string, object?>
            {
                ["profile"] = profile,
                ["vars"] = new Dictionary<string, string> { ["tenant"] = tenant },
            },
            ["sql"] = "SELECT 1",
        };

        var inflight = client.CallToolAsync("sqlharness_watch", WatchArgs("sample-a", "a"), cancellationToken: guard.Token);
        await entered.Task.WaitAsync(Budget, guard.Token);
        var queuedB = await client.CallToolAsync("sqlharness_watch", WatchArgs("sample-b", "b"), cancellationToken: guard.Token);
        Assert.True(queuedB.IsError == true, Text(queuedB));
        Assert.Equal(McpExecutionGate.BusyCode, Envelope(queuedB).GetProperty("error").GetProperty("code").GetString());

        await clientToServer.Writer.CompleteAsync();
        await moduleCancelled.Task.WaitAsync(Budget, guard.Token);
        await moduleExited.Task.WaitAsync(Budget, guard.Token);
        try { await serverTask.WaitAsync(Budget, guard.Token); }
        catch (OperationCanceledException) { }
        Assert.Single(module.Operations);
        Assert.Equal("sample-a", Assert.IsType<SqlHarnessWatchOperation>(module.Operations[0]).Target.Profile);
        Assert.True(gate.TryEnterDb());
        gate.ExitDb();
        _ = inflight;
    }

    [Fact]
    public async Task Watch_returns_a_final_report_without_touching_ndjson_or_stdout()
    {
        var module = new RecordingModule
        {
            Behavior = (_, _) => Task.FromResult(new SqlHarnessOutcome(
                SqlHarnessExitCode.Success,
                new SqlHarnessWatchReport(Target, 1, 10, WatchExitReason.ConditionMet, [], 1, 0),
                null)),
        };
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t5-test" },
            ProtocolVersion = McpHost.FallbackProtocolVersion,
        };
        McpToolCatalog.Wire(serverOptions, TestScope(), module, new McpExecutionGate());
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "t5-test-server", NullLoggerFactory.Instance),
            serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            },
            NullLoggerFactory.Instance, cts.Token);

        // The framed protocol round-trip proves stdout carries only protocol
        // frames: any NDJSON write would corrupt framing and fail this call.
        var result = await client.CallToolAsync(
            "sqlharness_watch",
            new Dictionary<string, object?> { ["sql"] = "SELECT 42 AS answer" },
            cancellationToken: cts.Token);

        Assert.False(result.IsError == true, Text(result));
        Assert.Equal(0, module.NdjsonCalls);

        await cts.CancelAsync();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void Progress_throttle_windows_are_exact()
    {
        var start = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.True(McpProgressReporter.ShouldSend(start, null));
        Assert.False(McpProgressReporter.ShouldSend(start, start));
        Assert.False(McpProgressReporter.ShouldSend(start.AddMilliseconds(999), start));
        Assert.True(McpProgressReporter.ShouldSend(start.AddSeconds(1), start));
        Assert.True(McpProgressReporter.ShouldSend(start.AddHours(1), start));
        Assert.False(McpProgressReporter.ShouldSend(start.AddSeconds(-1), start));
    }

    [Fact]
    public async Task Progress_fires_only_for_a_client_token_without_sql_payload()
    {
        const string sql = "SELECT 42 AS answer";
        var clock = new ManualMcpClock(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var module = new RecordingModule();
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t5-test" },
            ProtocolVersion = McpHost.FallbackProtocolVersion,
        };
        McpToolCatalog.Wire(serverOptions, TestScope(), module, new McpExecutionGate(), clock);
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "t5-test-server", NullLoggerFactory.Instance),
            serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            },
            NullLoggerFactory.Instance, cts.Token);

        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
            var query = tools.Single(tool => tool.Name == "sqlharness_query");
            var arguments = new Dictionary<string, object?> { ["sql"] = sql };
            var collector = new ProgressCollector();

            // Session-level receiver: the SDK pumps incoming messages fire-and-forget
            // and disposes the per-call progress registration when the tool result wins
            // that race, so observing only through the CallAsync progress flakes under
            // suite load (005/T2: repeated 15 s timeouts on dual full-suite runs). This
            // registration outlives every call, so a lagging notification is recorded.
            // The request still carries a client token (via a discard sink); without a
            // token the server stays silent.
            var discard = new Progress<ProgressNotificationValue>(_ => { });
            await using var progressSubscription = client.RegisterNotificationHandler(
                NotificationMethods.ProgressNotification,
                (notification, _) =>
                {
                    if (TryParseProgress(notification.Params) is { } value)
                        collector.Report(value);
                    return ValueTask.CompletedTask;
                });

            // With a token: exactly one stage notification (the finish lands
            // on the same controlled instant and is throttled away).
            // Delivery is asynchronous, so wait for it after the result returns.
            var first = await query.CallAsync(
                arguments, discard, null, cts.Token);
            Assert.False(first.IsError == true, string.Concat(first.Content.OfType<TextContentBlock>().Select(block => block.Text)));
            var seen = await WaitForProgressCountAsync(collector, 1, cts.Token);
            var single = Assert.Single(seen);
            Assert.Equal("sqlharness_query started", single.Message);
            Assert.Equal(0, single.Progress);
            Assert.Equal(1, single.Total);
            Assert.DoesNotContain("SELECT", single.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("42", single.Message, StringComparison.Ordinal);

            // The clock advanced past the throttle window: the next call
            // reports again, still without any SQL.
            clock.Advance(TimeSpan.FromSeconds(2));
            var second = await query.CallAsync(
                arguments, discard, null, cts.Token);
            Assert.False(second.IsError == true);
            Assert.Equal(2, (await WaitForProgressCountAsync(collector, 2, cts.Token)).Count);

            // Without a token the channel stays silent: assert no growth
            // over a short settle window instead of an immediate snapshot.
            var silent = await query.CallAsync(
                arguments, null, null, cts.Token);
            Assert.False(silent.IsError == true);
            await AssertNoProgressGrowthAsync(collector, 2, TimeSpan.FromMilliseconds(300), cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public void Annotations_are_conservative_and_never_gate_access()
    {
        var tools = McpToolCatalog.CreateTools(TestScope(), new RecordingModule(), new McpExecutionGate())
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        Assert.Equal(McpToolCatalog.ToolNames.Count, tools.Count);

        // readOnly is claimed only where no local write can happen: benchmark
        // and snapshot capture persist data and gain accounting may write, so
        // they stay unclaimed. Unknown stays null rather than guessing.
        AssertAnnotations(tools, "sqlharness_capabilities", ReadOnly: true, destructive: false, idempotent: true, openWorld: false);
        AssertAnnotations(tools, "sqlharness_inspect", ReadOnly: true, destructive: false, idempotent: null, openWorld: true);
        AssertAnnotations(tools, "sqlharness_validate", ReadOnly: true, destructive: false, idempotent: true, openWorld: false);
        AssertAnnotations(tools, "sqlharness_query", ReadOnly: true, destructive: false, idempotent: null, openWorld: true);
        AssertAnnotations(tools, "sqlharness_measure", ReadOnly: null, destructive: false, idempotent: null, openWorld: true);
        AssertAnnotations(tools, "sqlharness_compare", ReadOnly: null, destructive: false, idempotent: null, openWorld: true);
        AssertAnnotations(tools, "sqlharness_watch", ReadOnly: true, destructive: false, idempotent: null, openWorld: true);
        AssertAnnotations(tools, "sqlharness_snapshot", ReadOnly: null, destructive: false, idempotent: null, openWorld: true);
        AssertAnnotations(tools, "sqlharness_plan", ReadOnly: true, destructive: false, idempotent: true, openWorld: false);
        AssertAnnotations(tools, "sqlharness_artifact", ReadOnly: true, destructive: false, idempotent: true, openWorld: false);
        AssertAnnotations(tools, "sqlharness_gain", ReadOnly: null, destructive: false, idempotent: null, openWorld: false);
    }

    private static void AssertAnnotations(
        Dictionary<string, McpServerTool> tools,
        string name,
        bool? ReadOnly,
        bool? destructive,
        bool? idempotent,
        bool? openWorld)
    {
        var annotations = tools[name].ProtocolTool.Annotations;
        Assert.NotNull(annotations);
        Assert.Equal(ReadOnly, annotations.ReadOnlyHint);
        Assert.Equal(destructive, annotations.DestructiveHint);
        Assert.Equal(idempotent, annotations.IdempotentHint);
        Assert.Equal(openWorld, annotations.OpenWorldHint);
    }

    [Fact]
    public async Task Hints_never_replace_policy_enforcement()
    {
        // Tools without readOnlyHint still execute through the same Core
        // safety pipeline: access is decided by enforcement, not by hints.
        var module = new RecordingModule();
        var handlers = new McpToolHandlers(TestScope(), module, new McpExecutionGate());
        using var cts = new CancellationTokenSource(Budget);

        var measure = await handlers.MeasureAsync(
            null!, new McpSqlSourceArgument { Sql = "SELECT 1" }, ct: cts.Token);
        var snapshot = await handlers.SnapshotAsync(
            null!, "capture", "t5-hints", "SELECT 1", ct: cts.Token);
        var gain = await handlers.GainAsync(null!, cts.Token);

        Assert.False(measure.IsError == true, Text(measure));
        Assert.False(snapshot.IsError == true, Text(snapshot));
        Assert.False(gain.IsError == true, Text(gain));
        Assert.Equal(3, module.Operations.Count);
    }

    [Fact]
    public void Process_budgets_default_bind_validate_and_advertise()
    {
        var defaults = new McpServerOptions { Profile = "mcp-t5" };
        Assert.Equal(16384, defaults.MaxResultBytes);
        Assert.Equal(900, defaults.MaxOperationSeconds);

        // Out-of-range process maxima fail closed at startup with a generic,
        // value-free message.
        foreach (var bad in new McpServerOptions[]
        {
            new() { Profile = "mcp-t5", MaxResultBytes = 4095 },
            new() { Profile = "mcp-t5", MaxResultBytes = 1048577 },
            new() { Profile = "mcp-t5", MaxOperationSeconds = 0 },
            new() { Profile = "mcp-t5", MaxOperationSeconds = 86401 },
        })
        {
            var failure = Assert.Throws<McpStartupException>(() => TestScope(bad));
            Assert.Equal(
                bad.MaxResultBytes is 4095 or 1048577
                    ? "The MCP result budget is invalid."
                    : "The MCP operation budget is invalid.",
                failure.Message);
        }

        var scoped = TestScope(new McpServerOptions
        {
            Profile = "mcp-t5",
            MaxResultBytes = 8192,
            MaxOperationSeconds = 60,
        });
        var capabilities = McpOperationMapper.BuildCapabilities(scoped, includeDiagnostics: false);
        Assert.Equal(8192, capabilities.Limits["callToolResultBudgetBytes"]);
        Assert.Equal(60, capabilities.Limits["maxOperationSeconds"]);

        // Per-call time budgets only lower the process maximum, mirroring
        // the T4 byte budget: above-maximum clamps, below-minimum throws.
        Assert.Equal(60, McpLimits.ResolveOperationSeconds(60, 3600));
        Assert.Equal(30, McpLimits.ResolveOperationSeconds(60, 30));
        Assert.Equal(60, McpLimits.ResolveOperationSeconds(60, null));
        Assert.Equal(60, McpLimits.ResolveOperationSeconds(60, 86401));
        Assert.Throws<ArgumentOutOfRangeException>(() => McpLimits.ResolveOperationSeconds(0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => McpLimits.ResolveOperationSeconds(86401, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => McpLimits.ResolveOperationSeconds(60, 0));
    }
}

/// <summary>
/// T5 startup surface: the minimal <c>mcp serve</c> budget flags reach the
/// frozen scope, and out-of-range values fail closed before the handshake
/// with a generic stderr message and empty stdout. Synthetic HOME only; user
/// profiles are never touched.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpServeBudgetTests : IDisposable
{
    private const string ProfileName = "mcp-t5-serve";

    private readonly string _home;
    private readonly string? _savedHome;
    private readonly string _targetsFile;

    public McpServeBudgetTests()
    {
        _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-t5-serve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
        _targetsFile = Path.Combine(_home, "targets.json");
        File.WriteAllText(
            _targetsFile,
            "{\"" + ProfileName + "\": {\"server\": \"mcp-unreachable.invalid\", \"database\": \"reportdb\", \"auth\": \"integrated\"}}");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Serve_rejects_operation_seconds_out_of_range_with_exit_2()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = SqlHarnessCli.Create(
            new SqlHarnessModule(),
            output,
            new StringReader(""),
            stdinRedirected: false,
            mcpError: error);

        var exit = await app.RunAsync(["mcp", "serve", ProfileName, "--max-operation-seconds", "0"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("sqlharness-mcp", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Serve_rejects_result_bytes_out_of_range_with_exit_2()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = SqlHarnessCli.Create(
            new SqlHarnessModule(),
            output,
            new StringReader(""),
            stdinRedirected: false,
            mcpError: error);

        var exit = await app.RunAsync(["mcp", "serve", ProfileName, "--max-result-bytes", "1000"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("sqlharness-mcp", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }
}