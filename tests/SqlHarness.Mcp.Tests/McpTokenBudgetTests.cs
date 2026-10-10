using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

using Xunit.Abstractions;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T4 token budgets, measured on the real serialized <see cref="CallToolResult"/>
/// (both representations, JSON escaping, and SDK metadata included):
/// 16 KiB (16384 B) by default, accepted range 4096..1048576, cell limit
/// 512/4096, and a 32 KiB (32768 B) tools/list catalog. Budgets are met by
/// bounding content, never by cutting runtime validation or truncating JSON.
/// All data is synthetic; no database is opened.
/// </summary>
// 008/T3 (R5 ctx-inclusion race): this class creates served toolsets through
// McpToolCatalog.Wire, so it joins the serialized McpScopeHome collection
// (DisableParallelization = true, defined in McpScopeTests.cs) instead of
// racing SDK input-schema inference against parallel toolset creation. It
// touches no SQLHARNESS_HOME fixture, so collection membership only orders
// execution. Production is unchanged: the host wires one toolset
// sequentially at startup.
[Collection("McpScopeHome")]
public sealed class McpTokenBudgetTests
{
    private readonly ITestOutputHelper _output;

    private static readonly SqlHarnessTargetIdentityReport Target = new("req-srv", "req-db", "srv", "db", "profile");

    public McpTokenBudgetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static int WireBytes(CallToolResult result, int budget)
    {
        var bytes = McpResultAdapter.MeasureBytes(result);
        Assert.True(bytes <= budget, $"Serialized CallToolResult is {bytes} bytes, budget is {budget}.");
        return bytes;
    }

    private static void AssertFitsDefaultBudget(SqlHarnessOutcome outcome, string command)
    {
        var result = McpResultAdapter.Adapt(outcome, command);
        WireBytes(result, (int)McpLimits.CallToolResultBudgetBytes);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
    }

    [Fact]
    public void Request_catalog_stays_within_tools_list_budget()
    {
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["sample-country"] = new("server.invalid", "sampledb",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "^[a-z]+$" }, "integrated"),
        };
        var process = McpProcessContext.Create(new McpServerOptions
        {
            RequestScope = true,
            AllowedProfiles = ["sample-country"],
        }, () => profiles);

        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            McpToolCatalog.CreateTools(process).Select(tool => tool.ProtocolTool)));

        Assert.True(bytes <= McpLimits.ToolsListBudgetBytes, $"request-mode tools/list is {bytes} bytes.");
    }

    [Fact]
    public void Unicode_cells_fit_the_default_budget_with_escaping_counted()
    {
        var text = string.Concat(Enumerable.Repeat("語😀", 10000));
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(100000, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        var bytes = WireBytes(result, (int)McpLimits.CallToolResultBudgetBytes);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var projected = document.RootElement.GetProperty("result")
            .GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetString()!;
        Assert.True(projected.Length <= McpLimits.DefaultMaximumCellCharacters);
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
        Assert.True(bytes > Encoding.UTF8.GetByteCount(projected), "Escaping and metadata are part of the measured cost.");
    }

    [Fact]
    public void Thousand_table_counts_fit_the_default_budget()
    {
        var report = new SqlHarnessCountsReport(Target,
            Enumerable.Range(0, 1000).Select(i => new SqlHarnessCountReport("dbo", $"表{i}", i, "estimate")).ToArray(), 0);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_inspect");

        WireBytes(result, (int)McpLimits.CallToolResultBudgetBytes);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("tables").GetArrayLength() < 1000);
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Long_warnings_paths_and_big_cells_fit_the_default_budget()
    {
        var warning = new string('w', 100000);
        var path = "/workspace/compare/artifact-001";
        var variant = new CompareVariantReport("variant", new CompareDistribution(1, 2, 3), new CompareDistribution(4, 5, 6),
            new CompareDistribution(7, 8, 9), new Dictionary<string, long>(), [], [warning]);
        var compare = new SqlHarnessCompareReport(Target, 1, 2, false, variant, variant, path)
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Multiset, true, null, 0, 0),
            Classification = new CompareClassificationReport("none", "read-only", "read-only"),
        };
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int",
            Enumerable.Range(0, 100).Select(i => new CompareMatrixCellReport(i, i.ToString(), compare)).ToArray());
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_compare");

        WireBytes(result, (int)McpLimits.CallToolResultBudgetBytes);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.DoesNotContain(path, document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("cells").GetArrayLength() < 100);
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Minimum_budget_is_accepted_and_smaller_is_rejected()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var fits = McpResultAdapter.Adapt(outcome, "sqlharness_inspect", new McpResultBudget(4096));
        Assert.True(McpResultAdapter.MeasureBytes(fits) <= 4096);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultAdapter.Adapt(outcome, "sqlharness_inspect", new McpResultBudget(4095)));
    }

    [Fact]
    public void Call_budget_may_only_lower_the_process_maximum()
    {
        var resolved = McpResultBudget.Resolve(processMaximumBytes: 8192, callMaximumBytes: 65536);
        Assert.Equal(8192, resolved.MaximumBytes);

        var lowered = McpResultBudget.Resolve(processMaximumBytes: 8192, callMaximumBytes: 4096);
        Assert.Equal(4096, lowered.MaximumBytes);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultBudget.Resolve(processMaximumBytes: 2 * 1024 * 1024, callMaximumBytes: null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultBudget.Resolve(processMaximumBytes: 16384, callMaximumBytes: 1000));
    }

    [Fact]
    public void Cell_limit_range_is_zero_to_operator_maximum()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var zero = McpResultAdapter.Adapt(outcome, "sqlharness_inspect", new McpResultBudget(16384, 0));
        Assert.False(zero.IsError == true);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultAdapter.Adapt(outcome, "sqlharness_inspect", new McpResultBudget(16384, 4097)));

        var wide = McpResultBudget.Resolve(16384, null, null, 4096);
        Assert.Equal(4096, wide.MaximumCellCharacters);
        var narrowed = McpResultBudget.Resolve(16384, null, 100, 4096);
        Assert.Equal(100, narrowed.MaximumCellCharacters);
    }

    [Fact]
    public async Task Served_tools_list_fits_32KiB_without_full_examples_or_runtime_cuts()
    {
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t4-budget"] = new TargetProfile(
                "mcp-unreachable.invalid", "reportdb",
                new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(
            new McpServerOptions { Profile = "mcp-t4-budget" }, profiles);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpHost.ServerName, Version = "t4-test" },
            ProtocolVersion = McpHost.FallbackProtocolVersion,
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
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            },
            NullLoggerFactory.Instance, cts.Token);

        var tools = (await client.ListToolsAsync(cancellationToken: CancellationToken.None)).ToList();
        var catalogJson = JsonSerializer.Serialize(
            tools.Select(tool => tool.ProtocolTool), McpJsonUtilities.DefaultOptions);
        var bytes = Encoding.UTF8.GetByteCount(catalogJson);
        _output.WriteLine($"tools/list wire bytes: {bytes} of {McpLimits.ToolsListBudgetBytes} ({tools.Count} tools; output schemas asserted below).");
        Assert.Equal(McpLimits.MaxTools, tools.Count);
        Assert.True(bytes <= McpLimits.ToolsListBudgetBytes, $"tools/list is {bytes} bytes.");
        Assert.DoesNotContain("AGENTS.md", catalogJson, StringComparison.Ordinal);
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.OutputSchema;
            Assert.True(schema.HasValue, $"{tool.Name} publishes no outputSchema; the budget must hold with all 11 schemas served.");
            Assert.Equal(JsonValueKind.Object, schema!.Value.ValueKind);
        }

        // The budget holds with runtime validation intact: unknown properties
        // are still rejected instead of being dropped to save bytes.
        var rejected = await client.CallToolAsync(
            "sqlharness_validate",
            new Dictionary<string, object?>
            {
                ["sql"] = "SELECT 1",
                ["usage"] = "query",
                ["bogus"] = 1,
            },
            cancellationToken: CancellationToken.None);
        var text = string.Concat(rejected.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(rejected.IsError == true, text);
        Assert.DoesNotContain("bogus", text, StringComparison.Ordinal);

        await cts.CancelAsync();
        await serverTask;
    }

    [Fact]
    public void Minimal_success_envelope_reports_wire_bytes_within_default_budget()
    {
        // 008/T3 baseline guard: the smallest success envelope (ping) against
        // the default 16 KiB CallToolResult budget, with the measured bytes
        // reported for the record.
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_inspect");
        var bytes = McpResultAdapter.MeasureBytes(result);
        _output.WriteLine($"minimal success CallToolResult wire bytes: {bytes} of {McpLimits.CallToolResultBudgetBytes}.");
        Assert.True(bytes <= McpLimits.CallToolResultBudgetBytes, $"Serialized CallToolResult is {bytes} bytes, budget is {McpLimits.CallToolResultBudgetBytes}.");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
    }

    [Fact]
    public void Raw_path_descends_when_envelope_fits_but_wire_exceeds()
    {
        // Review probe for I1: a named record the shared projection does not
        // know, carrying ~12 KiB. The raw envelope (~12.5 KiB) fits the
        // default 16 KiB budget, but the wire (both representations plus SDK
        // metadata) is ~2x — an envelope-only check admits it over budget.
        var payload = new string('p', 12 * 1024);
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new UnprojectedProbeReport("probe", payload),
            null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_inspect");

        WireBytes(result, (int)McpLimits.CallToolResultBudgetBytes);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        Assert.DoesNotContain(payload, text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
        Assert.Equal(
            "UnprojectedProbeReport",
            document.RootElement.GetProperty("result").GetProperty("reportType").GetString());
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Main_loop_descends_past_first_fit_level_when_wire_exceeds()
    {
        // Boundary shape for I1: at budget 8192 with 4096-char cells the
        // first-fit projected envelope (~6 KiB) fits, but the wire does not,
        // so the adapter must descend to level 0 instead of emitting it.
        const int budget = 8192;
        var cell = new string('c', 4000);
        var message = new string('m', 2000);
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[cell]], 1, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [set], [message], 0, 1, "raw-hash", new OutputFootprint(100000, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(budget, 4096));

        WireBytes(result, budget);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
        var resultSets = document.RootElement.GetProperty("result").GetProperty("resultSets");
        Assert.Equal(0, resultSets.GetArrayLength());
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    private sealed record UnprojectedProbeReport(string Name, string Payload);
}