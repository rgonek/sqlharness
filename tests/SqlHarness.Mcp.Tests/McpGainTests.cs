using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

using Xunit.Abstractions;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T4 gain accounting: one gain record per logical execution (a second
/// <c>CompleteAsync</c> never writes again), the footprint covers both result
/// representations with the bytes/4 heuristic from the spec, discovery is
/// measured separately instead of being folded into saved data, and CLI/MCP
/// scenarios are compared with the tools/list and artifact follow-up costs on
/// the MCP side — without promising that MCP is cheaper per call.
/// Gain isolation uses a synthetic HOME; no database is opened.
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
    public async Task One_execution_writes_one_gain_record_despite_double_complete()
    {
        // Empty profile map: resolution fails before any connection, but the
        // failure outcome still carries its emission receipt.
        var module = new SqlHarnessModule(() => new Dictionary<string, TargetProfile>(StringComparer.Ordinal));
        var operation = new SqlHarnessQueryOperation(
            new SqlTargetRequest("missing-profile", new Dictionary<string, string>()),
            "SELECT 1", [], 30, 50, false, null);
        var outcome = await module.ExecuteAsync(operation);

        Assert.NotEqual(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.NotNull(outcome.EmissionReceipt);
        var receipt = outcome.EmissionReceipt!;
        var emitted = McpResultAdapter.EmittedFootprint(McpResultAdapter.Adapt(outcome, "sqlharness_query"));

        var first = await receipt.CompleteAsync(emitted);
        var second = await receipt.CompleteAsync(emitted);

        Assert.Equal(first, second);
        var gain = Assert.IsType<SqlHarnessGainReport>(
            (await module.ExecuteAsync(new SqlHarnessGainOperation())).Report);
        Assert.Equal(1, gain.Total.Executions);
        Assert.Equal(emitted.Bytes, gain.Total.EmittedBytes);
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