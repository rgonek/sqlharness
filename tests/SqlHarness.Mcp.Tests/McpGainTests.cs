using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;
using SqlHarness.Mcp.Tools;

using Xunit.Abstractions;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T4 gain accounting: one journal emission per logical execution (a second
/// <c>CompleteAsync</c> never counts again), the footprint covers both result
/// representations with the bytes/4 heuristic from the spec, discovery is
/// measured separately instead of being folded into saved data, and CLI/MCP
/// scenarios are compared with the tools/list and artifact follow-up costs on
/// the MCP side — without promising that MCP is cheaper per call.
/// Gain isolation uses a synthetic HOME; no SQL database is opened.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpGainTests : IDisposable
{
    private static readonly SqlHarnessTargetIdentityReport Target = new("req-srv", "req-db", "srv", "db", "profile");

    private readonly ITestOutputHelper _testOutput;
    private readonly string _home;
    private readonly string? _savedHome;

    public McpGainTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
        _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-t4-gain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
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
    public void Footprint_covers_both_representations_with_bytes_per_four_heuristic()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);
        var result = McpResultAdapter.Adapt(outcome, "sqlharness_inspect");

        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        var textBytes = Encoding.UTF8.GetByteCount(text);
        var structuredBytes = Encoding.UTF8.GetByteCount(result.StructuredContent?.GetRawText() ?? string.Empty);
        var measured = McpResultAdapter.MeasureBytes(result);
        var footprint = McpResultAdapter.EmittedFootprint(result);

        Assert.Equal(textBytes, structuredBytes);
        Assert.True(measured > textBytes, "The wire cost includes both representations plus metadata.");
        Assert.Equal(measured, footprint.Bytes);
        Assert.Equal(1, footprint.Lines);
        Assert.Equal(OutputFootprint.EstimateTokens(measured), measured / 4 + (measured % 4 == 0 ? 0 : 1));
    }

    [Fact]
    public async Task Fixed_target_dependent_handler_records_the_final_budgeted_result_once_in_gain()
    {
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new ReceiptModule();
        var wrapped = WrapForMcp(module, journal);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-test"] = new("server.invalid", "database", new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(new McpServerOptions { Profile = "mcp-test", MaxResultBytes = 4096 }, profiles);
        var handlers = new McpToolHandlers(scope, wrapped);

        var result = await handlers.QueryAsync(null!, "SELECT 'local-only-test-value'", maxRows: 1);
        Assert.False(result.IsError == true);
        var emitted = McpResultAdapter.EmittedFootprint(result);
        Assert.InRange(emitted.Bytes, 1, 4096);

        var gain = new JournalGainStore(Path.Combine(_home, "data", "activity.db"), () => true).Aggregate();
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(emitted.Bytes, gain.Total.EmittedBytes);
        Assert.Equal(OutputFootprint.EstimateTokens(emitted.Bytes), gain.Total.EmittedEstimatedTokens);
        Assert.NotNull(module.LastReceipt);
        await module.LastReceipt!.CompleteAsync(new OutputFootprint(1, 1));
        var afterSecondCompletion = new JournalGainStore(Path.Combine(_home, "data", "activity.db"), () => true).Aggregate();
        Assert.Equal(1, afterSecondCompletion.Total.Executions);
        Assert.Equal(emitted.Bytes, afterSecondCompletion.Total.EmittedBytes);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql_text FROM operations";
        Assert.True(command.ExecuteScalar() is null or DBNull);
    }

    [Fact]
    public async Task Request_target_free_plan_completes_receipt_without_scope_resolution()
    {
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new ReceiptModule();
        var process = McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = ["scope-a"] },
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["scope-a"] = new("server.invalid", "database", new Dictionary<string, string>(), "integrated"),
            });
        process.DecorateModules(_ => WrapForMcp(module, journal));
        var handlers = new McpRequestToolHandlers(process);

        var result = await handlers.PlanAsync(null!, content: "<a/>");

        Assert.False(result.IsError == true);
        var emitted = McpResultAdapter.EmittedFootprint(result);
        var gain = new JournalGainStore(Path.Combine(_home, "data", "activity.db"), () => true).Aggregate();
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(emitted.Bytes, gain.Total.EmittedBytes);
        Assert.Equal(OutputFootprint.EstimateTokens(emitted.Bytes), gain.Total.EmittedEstimatedTokens);
        Assert.IsType<SqlHarnessPlanOperation>(Assert.Single(module.Operations));
    }

    [Fact]
    public async Task Fixed_target_free_plan_completes_its_emission_receipt()
    {
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new ReceiptModule();
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-test"] = new("server.invalid", "database", new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(new McpServerOptions { Profile = "mcp-test" }, profiles);
        var handlers = new McpToolHandlers(scope, WrapForMcp(module, journal));

        var result = await handlers.PlanAsync(null!, content: "<a/>");

        Assert.False(result.IsError == true);
        var emitted = McpResultAdapter.EmittedFootprint(result);
        var gain = new JournalGainStore(Path.Combine(_home, "data", "activity.db"), () => true).Aggregate();
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(emitted.Bytes, gain.Total.EmittedBytes);
        Assert.IsType<SqlHarnessPlanOperation>(Assert.Single(module.Operations));
    }

    [Fact]
    public async Task Request_target_dependent_handler_completes_its_emission_receipt()
    {
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new ReceiptModule();
        var process = McpProcessContext.Create(
            new McpServerOptions { RequestScope = true, AllowedProfiles = ["scope-a"] },
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["scope-a"] = new("server.invalid", "database", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
            });
        var handlers = new McpRequestToolHandlers(process, moduleFactory: _ => WrapForMcp(module, journal));

        var result = await handlers.QueryAsync(null!, new McpRequestScopeArgument("scope-a", new() { ["tenant"] = "a" }), "SELECT 1");

        Assert.False(result.IsError == true);
        var emitted = McpResultAdapter.EmittedFootprint(result);
        var gain = new JournalGainStore(Path.Combine(_home, "data", "activity.db"), () => true).Aggregate();
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(emitted.Bytes, gain.Total.EmittedBytes);
        Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(module.Operations));
    }

    [Fact]
    public async Task Controlled_failure_result_is_included_in_gain()
    {
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new ReceiptModule(SqlHarnessExitCode.SqlExecution);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-test"] = new("server.invalid", "database", new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(new McpServerOptions { Profile = "mcp-test" }, profiles);
        var handlers = new McpToolHandlers(scope, WrapForMcp(module, journal));

        var result = await handlers.QueryAsync(null!, "SELECT 1");

        Assert.True(result.IsError == true);
        var emitted = McpResultAdapter.EmittedFootprint(result);
        var gain = new JournalGainStore(Path.Combine(_home, "data", "activity.db"), () => true).Aggregate();
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(1, gain.Total.Failures);
        Assert.Equal(emitted.Bytes, gain.Total.EmittedBytes);
    }

    [Fact]
    public async Task Busy_and_cancelled_calls_do_not_manufacture_emission_receipts()
    {
        var journal = ActivityJournal.Open(new JournalConfig(), TextWriter.Null);
        var module = new BlockingReceiptModule();
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-test"] = new("server.invalid", "database", new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(new McpServerOptions { Profile = "mcp-test" }, profiles);
        var handlers = new McpToolHandlers(scope, WrapForMcp(module, journal));

        var activeCall = handlers.QueryAsync(null!, "SELECT 1");
        await module.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var busy = await handlers.QueryAsync(null!, "SELECT 2");
        Assert.True(busy.IsError == true);
        module.Release.TrySetResult();
        var completed = await activeCall;
        Assert.False(completed.IsError == true);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledResult = await handlers.QueryAsync(null!, "SELECT 3", ct: cancelled.Token);
        Assert.True(cancelledResult.IsError == true);

        var gain = new JournalGainStore(Path.Combine(_home, "data", "activity.db"), () => true).Aggregate();
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(McpResultAdapter.EmittedFootprint(completed).Bytes, gain.Total.EmittedBytes);
        Assert.Collection(module.Operations,
            first => Assert.Equal("SELECT 1", Assert.IsType<SqlHarnessQueryOperation>(first).Sql),
            second => Assert.Equal("SELECT 3", Assert.IsType<SqlHarnessQueryOperation>(second).Sql));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(CASE WHEN emitted_bytes IS NOT NULL THEN 1 ELSE 0 END), SUM(CASE WHEN emitted_bytes IS NULL THEN 1 ELSE 0 END) FROM operations WHERE operation = 'query'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((2L, 1L, 1L), (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
    }

    [Fact]
    public async Task Journal_emission_failure_does_not_change_the_budgeted_result()
    {
        var module = new ReceiptModule();
        var journal = new ThrowingEmissionJournal();
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-test"] = new("server.invalid", "database", new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(new McpServerOptions { Profile = "mcp-test" }, profiles);
        var handlers = new McpToolHandlers(scope, WrapForMcp(module, journal));

        var result = await handlers.QueryAsync(null!, "SELECT 1");

        Assert.False(result.IsError == true);
        Assert.Single(module.Operations);
        Assert.Equal(1, journal.EmissionAttempts);
        Assert.InRange(McpResultAdapter.MeasureBytes(result), 1, scope.MaxResultBytes);
    }

    private static ISqlHarnessModule WrapForMcp(ISqlHarnessModule module, IActivityJournal journal) =>
        new JournalingModule(
            module,
            () => journal,
            () => SessionIdentities.Mcp(ProcessInfo.Current, "mcp:test-session", "codex-test", "1.0", "fixed"));

    private class ReceiptModule(SqlHarnessExitCode exitCode = SqlHarnessExitCode.Success) : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public SqlHarnessEmissionReceipt? LastReceipt { get; private set; }

        public virtual Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operations.Add(operation);
            LastReceipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.Success))
            {
                RawFootprint = new OutputFootprint(800, 20),
            };
            return Task.FromResult(new SqlHarnessOutcome(
                exitCode,
                exitCode == SqlHarnessExitCode.Success ? new { detail = new string('x', 8000) } : null,
                exitCode == SqlHarnessExitCode.Success ? null : "A safe test failure.",
                LastReceipt));
        }

        public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
            SqlHarnessWatchOperation operation,
            TextWriter writer,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class BlockingReceiptModule : ReceiptModule
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await base.ExecuteAsync(operation, ct);
        }
    }

    private sealed class ThrowingEmissionJournal : IActivityJournal
    {
        public int EmissionAttempts;
        public JournalHandle? Begin(SessionIdentity session, OperationStart start) => new(1);
        public bool Complete(JournalHandle? handle, OperationEnd end) => true;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted)
        {
            EmissionAttempts++;
            throw new IOException("storage failure with private text");
        }
        public void RecordBenchmark(JournalHandle? handle, BenchmarkJournalRecord benchmark) { }
        public bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress) => true;
    }

    [Fact]
    public async Task Cli_and_mcp_scenarios_compare_with_discovery_and_followup_costs()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var writer = new StringWriter();
        new Renderer().RenderAgent(outcome, "query", new OutputCaptureWriter(writer), new AgentOutputOptions());
        var cliBytes = Encoding.UTF8.GetByteCount(writer.ToString());

        var call = McpResultAdapter.Adapt(outcome, "sqlharness_inspect");
        var callBytes = McpResultAdapter.MeasureBytes(call);

        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t4-gain"] = new TargetProfile(
                "mcp-unreachable.invalid", "reportdb",
                new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(new McpServerOptions { Profile = "mcp-t4-gain" }, profiles);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t4-test" },
            ProtocolVersion = McpHost.PinnedProtocolVersion,
        };
        McpToolCatalog.Wire(serverOptions, scope, scope.CreateModule());
        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "t4-test-server", NullLoggerFactory.Instance),
            serverOptions, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = McpHost.PinnedProtocolVersion,
            },
            NullLoggerFactory.Instance, cts.Token);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var discoveryBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            tools.Select(tool => tool.ProtocolTool), McpJsonUtilities.DefaultOptions));

        // Follow-up artifact read on the MCP side: a second budgeted call.
        var followup = McpResultAdapter.MeasureBytes(McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, new { section = "summary" }, null),
            "sqlharness_artifact"));

        var mcpScenarioBytes = discoveryBytes + callBytes + followup;
        _testOutput.WriteLine($"CLI agent bytes: {cliBytes}; MCP call: {callBytes}, discovery: {discoveryBytes}, follow-up: {followup}, scenario total: {mcpScenarioBytes}.");

        // Discovery is a separate, visible cost: the scenario total strictly
        // exceeds the single call. No assertion about which side is cheaper —
        // MCP is not promised to win every call.
        Assert.True(mcpScenarioBytes > callBytes, "Discovery and follow-up costs must be counted, not hidden.");
        Assert.True(callBytes <= McpLimits.CallToolResultBudgetBytes, $"MCP call is {callBytes} bytes.");
        Assert.True(discoveryBytes <= McpLimits.ToolsListBudgetBytes, $"Discovery is {discoveryBytes} bytes.");

        await cts.CancelAsync();
        await serverTask;
    }
}