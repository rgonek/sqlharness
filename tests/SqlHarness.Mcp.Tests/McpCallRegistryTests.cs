using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

public sealed class McpCallRegistryTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-cancel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void Classify_prefers_shutdown_then_client_then_deadline()
    {
        using var request = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, shutdown.Token);
        using (McpCallRegistry.Track(linked, request.Token, shutdown.Token))
        {
            Assert.Null(McpCallRegistry.Classify(linked.Token));
            linked.Cancel();
            Assert.Equal("deadline", McpCallRegistry.Classify(linked.Token));
            request.Cancel();
            Assert.Equal("client", McpCallRegistry.Classify(linked.Token));
            shutdown.Cancel();
            Assert.Equal("shutdown", McpCallRegistry.Classify(linked.Token));
        }

        Assert.Null(McpCallRegistry.Classify(linked.Token));
    }

    [Fact]
    public void Unknown_token_has_no_reason()
    {
        Assert.Null(McpCallRegistry.Classify(new CancellationToken(canceled: true)));
    }

    private static McpScope Scope(int maxOperationSeconds = 300) => McpScope.Create(
        new McpServerOptions { Profile = "mcp-t5", MaxOperationSeconds = maxOperationSeconds },
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
        });

    private sealed class BlockingModule : ISqlHarnessModule
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            if (operation is SqlHarnessPlanOperation)
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("A cancelled wait never completes.");
        }
    }

    private (McpToolHandlers Handlers, BlockingModule Inner, string Database) Create(
        CancellationToken hostShutdown, int maxOperationSeconds = 300)
    {
        var database = Path.Combine(_root, "data", "activity.db");
        var journal = ActivityJournal.Open(database, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var inner = new BlockingModule();
        var module = new JournalingModule(
            inner,
            () => journal,
            () => new SessionIdentity("mcp:test", "claude", "mcp-clientinfo", JournalTransport.Mcp, "c", "1", "fixed", null, null, 1, null, null),
            McpCallRegistry.Classify);
        return (new McpToolHandlers(Scope(maxOperationSeconds), module, new McpExecutionGate(), hostShutdown: hostShutdown), inner, database);
    }

    private static string? ReasonOf(string database, string operation)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cancel_reason FROM operations WHERE operation = $op ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$op", operation);
        var value = command.ExecuteScalar();
        return value is DBNull or null ? null : (string)value;
    }

    [Fact]
    public async Task Client_cancellation_is_recorded_as_client()
    {
        var (handlers, inner, database) = Create(CancellationToken.None);
        using var request = new CancellationTokenSource(Budget);

        var call = handlers.QueryAsync(null!, "SELECT 1", ct: request.Token);
        await inner.Entered.Task.WaitAsync(Budget);
        await request.CancelAsync();
        await call.WaitAsync(Budget);

        Assert.Equal("client", ReasonOf(database, "query"));
    }

    [Fact]
    public async Task Host_shutdown_is_recorded_as_shutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var (handlers, inner, database) = Create(shutdown.Token);
        using var request = new CancellationTokenSource(Budget);

        var call = handlers.QueryAsync(null!, "SELECT 1", ct: request.Token);
        await inner.Entered.Task.WaitAsync(Budget);
        await shutdown.CancelAsync();
        await request.CancelAsync();
        await call.WaitAsync(Budget);

        Assert.Equal("shutdown", ReasonOf(database, "query"));
    }

    [Fact]
    public async Task Exhausted_call_budget_is_recorded_as_deadline()
    {
        var (handlers, _, database) = Create(CancellationToken.None, maxOperationSeconds: 1);
        using var request = new CancellationTokenSource(Budget);

        await handlers.QueryAsync(null!, "SELECT 1", ct: request.Token).WaitAsync(Budget);

        Assert.Equal("deadline", ReasonOf(database, "query"));
    }

    [Fact]
    public async Task Concurrent_target_free_call_does_not_inherit_a_reason()
    {
        var (handlers, inner, database) = Create(CancellationToken.None);
        using var request = new CancellationTokenSource(Budget);

        var call = handlers.QueryAsync(null!, "SELECT 1", ct: request.Token);
        await inner.Entered.Task.WaitAsync(Budget);
        await request.CancelAsync();
        await handlers.PlanAsync(null!, "<ShowPlanXML/>", ct: CancellationToken.None).WaitAsync(Budget);
        await call.WaitAsync(Budget);

        Assert.Equal("client", ReasonOf(database, "query"));
        Assert.Null(ReasonOf(database, "plan"));
    }
}