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
/// T4 token budgets, measured on the result text read by the model:
/// 64 KiB (65536 B) by default, accepted range 4096..1048576, cell limit
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

    private static int ResultTextBytes(CallToolResult result, int budget)
    {
        var bytes = McpResultAdapter.MeasureBytes(result);
        Assert.True(bytes <= budget, $"Result text is {bytes} bytes, budget is {budget}.");
        return bytes;
    }

    private static void AssertFitsDefaultBudget(SqlHarnessOutcome outcome, string command)
    {
        var result = McpResultAdapter.Adapt(outcome, command);
        ResultTextBytes(result, (int)McpLimits.DefaultCallToolResultBudgetBytes);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
    }

    private static McpProcessContext RequestProcess() => McpProcessContext.Create(new McpServerOptions
    {
        RequestScope = true,
        AllowedProfiles = ["sample-country"],
    }, () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
    {
        ["sample-country"] = new("server.invalid", "sampledb",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "^[a-z]+$" }, "integrated"),
    });

    private static SqlHarnessOutcome LargeQueryOutcome(int targetBytes)
    {
        var rows = new List<IReadOnlyList<object?>>();
        var resultSet = new SqlHarnessResultSetReport(
            [new SqlHarnessColumnReport(0, "value", "text", true)], rows, 0, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [resultSet], [], 0, 1,
            "raw-hash", new OutputFootprint(0, 0));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
        while (JsonSerializer.SerializeToUtf8Bytes(new SqlHarnessAgentEnvelope(
                   McpResultAdapter.SchemaVersion, "query", "success", 0, report, null, null)).Length < targetBytes)
        {
            rows.Add([new string('x', 32)]);
            resultSet = resultSet with { Rows = rows, RowCount = rows.Count };
            report = report with { ResultSets = [resultSet] };
            outcome = outcome with { Report = report };
        }
        return outcome;
    }

    [Fact]
    public void Results_carry_one_text_block_and_no_structured_content()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);
        var result = McpResultAdapter.Adapt(outcome, "inspect");

        Assert.Null(result.StructuredContent);
        Assert.Single(result.Content.OfType<TextContentBlock>());
    }

    [Fact]
    public void Query_result_uses_shared_v2_compact_projection()
    {
        var set = new SqlHarnessResultSetReport(
            [new SqlHarnessColumnReport(0, "when", "System.DateTime", true)],
            [], 0, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [set], [], 0, 1,
            "canonical-hash", new OutputFootprint(0, 0));

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "query");
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);

        Assert.Equal(AgentEnvelopeSchema.Version, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var query = document.RootElement.GetProperty("result");
        Assert.Equal("db", query.GetProperty("database").GetString());
        Assert.False(query.TryGetProperty("target", out _));
        Assert.False(query.TryGetProperty("resultHash", out _));
        Assert.False(query.TryGetProperty("rawFootprint", out _));
        Assert.Equal("dateTime", query.GetProperty("resultSets")[0].GetProperty("columns")[0].GetProperty("valueType").GetString());
        Assert.Equal(0, query.GetProperty("recordsAffected").GetInt32());
    }

    [Fact]
    public void Budget_counts_only_the_text_the_model_reads()
    {
        var result = McpResultAdapter.Adapt(LargeQueryOutcome(targetBytes: 40_000), "query");
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

        Assert.Equal(Encoding.UTF8.GetByteCount(text), McpResultAdapter.MeasureBytes(result));
        using var document = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("truncation").ValueKind);
    }

    [Theory]
    [InlineData(null, 262144, 65536)]
    [InlineData(131072, 262144, 131072)]
    [InlineData(900000, 262144, 262144)]
    [InlineData(8192, 262144, 8192)]
    [InlineData(null, 16384, 16384)]
    public void Call_budget_resolves_within_the_process_ceiling(int? call, int ceiling, int expected) =>
        Assert.Equal(expected, McpResultBudget.Resolve(ceiling, call).MaximumBytes);

    [Fact]
    public void Tools_advertise_no_output_schema()
    {
        foreach (var tool in McpToolCatalog.CreateTools(RequestProcess()).Select(tool => tool.ProtocolTool))
            Assert.Null(tool.OutputSchema);
    }

    [Fact]
    public void Request_catalog_stays_within_tools_list_budget()
    {
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            McpToolCatalog.CreateTools(RequestProcess()).Select(tool => tool.ProtocolTool)));

        Assert.True(bytes <= McpLimits.ToolsListBudgetBytes, $"request-mode tools/list is {bytes} bytes.");
    }

    [Fact]
    public void Plan_text_with_escaped_quotes_and_long_predicates_fits_small_budgets_and_marks_clipping()
    {
        var predicate = "[Name]=\"" + new string('q', 3000) + "\" AND [Code] IN ('a','b')";
        var children = Enumerable.Range(0, 40)
            .Select(i => new PlanNode("Index Seek", "Index Seek", $"dbo.T{i}", $"IX_{i}", 1, 1, 1, 0.01, predicate, [], []))
            .ToArray();
        var root = new PlanNode("Nested Loops", "Inner Join", null, null, 2, 2, 1, 0.9, predicate, [], children);
        var plan = new DistilledPlan([new PlanStatement("SELECT '" + new string('s', 4000) + "'", root, [])]);
        var applied = McpPlanTextPolicy.Apply(plan, includeText: true);
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, applied, null);

        foreach (var (budget, cells) in new[] { (4096, 64), (16384, 512), (65536, 4096) })
        {
            var result = McpResultAdapter.Adapt(outcome, "sqlharness_plan", new McpResultBudget(budget, cells));
            ResultTextBytes(result, budget);
            var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
            using var document = JsonDocument.Parse(text);
            Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
            var truncation = document.RootElement.GetProperty("truncation");
            Assert.Equal(JsonValueKind.Object, truncation.ValueKind);
            Assert.True(truncation.GetProperty("omittedItems").GetInt32() > 0);
            var hasStatements = document.RootElement.GetProperty("result").TryGetProperty("statements", out var statements);
            Assert.True(hasStatements || budget == 4096, $"statements missing at budget {budget}.");
            if (hasStatements)
            {
                var statementText = statements[0].GetProperty("sql").GetString()!;
                Assert.True(statementText.Length <= cells);
            }
        }

        var hidden = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, McpPlanTextPolicy.Apply(plan, includeText: false), null),
            "sqlharness_plan");
        ResultTextBytes(hidden, (int)McpLimits.DefaultCallToolResultBudgetBytes);
        Assert.DoesNotContain("qqqq", Assert.Single(hidden.Content.OfType<TextContentBlock>()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_plan_pages_fit_the_mcp_budget_and_continue_by_nodes_returned()
    {
        const int budget = 4096;
        const int sourcePageSize = 32;
        var allNodes = Enumerable.Range(0, 100).Select(index => new ArtifactPlanNode(
            index == 0 ? "0" : "0/" + (index - 1), index == 0 ? null : "0",
            new PlanNode("Operator-" + new string('x', 90), "Logical-" + new string('y', 60),
                "dbo.Object-" + new string('z', 40), "IX-" + new string('i', 40), 1, 1, 1, 0.1, null, [], []))).ToArray();
        var seen = new List<string>();
        var offset = 0;
        while (true)
        {
            var pageNodes = allNodes.Skip(offset).Take(sourcePageSize).ToArray();
            var section = new ArtifactPlanSection("artifact", "baseline", null, 0, 1, 1,
                "last-measured", [1], null, allNodes.Length, pageNodes,
                offset + pageNodes.Length < allNodes.Length ? offset + pageNodes.Length : null)
            { Cursor = offset };
            var result = McpResultAdapter.Adapt(
                new SqlHarnessOutcome(SqlHarnessExitCode.Success, section, null),
                "sqlharness_artifact", new McpResultBudget(budget, 64));
            ResultTextBytes(result, budget);
            var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
            using var document = JsonDocument.Parse(text);
            Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
            var projection = document.RootElement.GetProperty("result");
            var returned = projection.GetProperty("nodes").EnumerateArray()
                .Select(node => node.GetProperty("nodePath").GetString()!).ToArray();
            Assert.NotEmpty(returned);
            seen.AddRange(returned);
            if (!projection.TryGetProperty("continuation", out var continuation) || continuation.ValueKind == JsonValueKind.Null)
                break;
            var next = continuation.GetInt32();
            Assert.Equal(offset + returned.Length, next);
            offset = next;
        }
        Assert.Equal(100, seen.Count);
        Assert.Equal(100, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Unicode_cells_fit_the_default_budget_with_escaping_counted()
    {
        var text = string.Concat(Enumerable.Repeat("語😀", 10000));
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[text]], 1, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [set], [], 0, 1, "raw-hash", new OutputFootprint(100000, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        var bytes = ResultTextBytes(result, (int)McpLimits.DefaultCallToolResultBudgetBytes);
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

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");

        ResultTextBytes(result, (int)McpLimits.DefaultCallToolResultBudgetBytes);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("tables").GetArrayLength() < 1000);
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    private static RegressionReport FailVerdict() => new(
        RegressionPolicy.Fail,
        [RegressionPolicy.ReasonElapsedRegressed],
        RegressionPolicy.PolicyVersion,
        new RegressionDiagnostic(RegressionPolicy.StatusRegressed, 100m, 1000m, 900m, 900m),
        new RegressionDiagnostic(RegressionPolicy.StatusRegressed, 1000m, 2000m, 1000m, 100m),
        new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 40m, 40m, 0m, 0m));

    private static SqlHarnessCompareReport BuildCompareReport(RegressionReport? regression = null)
    {
        var variant = new CompareVariantReport("variant", new CompareDistribution(1, 2, 3), new CompareDistribution(4, 5, 6),
            new CompareDistribution(7, 8, 9), new Dictionary<string, long>(), [], []);
        return new SqlHarnessCompareReport(Target, 5, 10, true, variant, variant, "/workspace/compare/artifact-001")
        {
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Multiset, true, null, 0, 0),
            Regression = regression,
        };
    }

    [Fact]
    public void Compare_result_carries_the_regression_verdict_within_the_default_budget()
    {
        var compare = BuildCompareReport(FailVerdict());

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, compare, null), "sqlharness_compare");

        var bytes = ResultTextBytes(result, (int)McpLimits.DefaultCallToolResultBudgetBytes);
        _output.WriteLine($"compare result with regression: {bytes} bytes of {McpLimits.DefaultCallToolResultBudgetBytes}");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var regression = document.RootElement.GetProperty("result").GetProperty("regression");
        Assert.Equal("fail", regression.GetProperty("verdict").GetString());
        Assert.Equal(900m, regression.GetProperty("elapsed").GetProperty("delta").GetDecimal());
    }

    [Fact]
    public void Compare_matrix_result_with_regression_per_cell_fits_the_default_budget()
    {
        var compare = BuildCompareReport(FailVerdict());
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int",
            Enumerable.Range(0, 100).Select(i => new CompareMatrixCellReport(i, i.ToString(), compare)).ToArray());

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null), "sqlharness_compare");

        var bytes = ResultTextBytes(result, (int)McpLimits.DefaultCallToolResultBudgetBytes);
        _output.WriteLine($"compare matrix result with regression: {bytes} bytes of {McpLimits.DefaultCallToolResultBudgetBytes}");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var first = document.RootElement.GetProperty("result").GetProperty("details")[0];
        Assert.Equal("fail", first.GetProperty("compare").GetProperty("regression").GetProperty("verdict").GetString());
    }

    [Fact]
    public void Small_compare_matrix_keeps_both_cells_without_truncation()
    {
        var compare = BuildCompareReport();
        var report = new SqlHarnessCompareMatrixReport("batch", "int",
            [new CompareMatrixCellReport(0, "1", compare), new CompareMatrixCellReport(1, "2", compare)]);

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null), "compare");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var cases = document.RootElement.GetProperty("result").GetProperty("cases");
        Assert.Equal(2, cases.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("truncation").ValueKind);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Matrix_case_inventory_survives_optional_detail_at_minimum_and_default_budgets(int budget)
    {
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int",
            Enumerable.Range(0, 6).Select(index => new CompareMatrixCellReport(index, index.ToString(), MatrixInventoryCompare(index))).ToArray());

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null),
            "sqlharness_compare", new McpResultBudget(budget, 128));

        ResultTextBytes(result, budget);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var projected = document.RootElement.GetProperty("result");
        var cases = projected.GetProperty("cases");
        Assert.Equal(6, cases.GetArrayLength());
        Assert.Equal(6, projected.GetProperty("caseCount").GetInt32());
        Assert.Equal(Enumerable.Range(0, 6), cases.EnumerateArray().Select(item => item.GetProperty("index").GetInt32()));
        Assert.Equal(10m, cases[0].GetProperty("delta").GetProperty("elapsedMilliseconds").GetDecimal());
        Assert.Equal("not-requested", cases[0].GetProperty("equivalenceAvailability").GetString());
        Assert.Equal(JsonValueKind.Null, cases[0].GetProperty("equivalent").ValueKind);
    }

    private static SqlHarnessCompareReport MatrixInventoryCompare(int index)
    {
        var source = BuildCompareReport();
        var baseline = source.Baseline with
        {
            CpuTimeMilliseconds = new CompareDistribution(10, 20, 30),
            ElapsedTimeMilliseconds = new CompareDistribution(30, 40, 50),
            LogicalReads = new CompareDistribution(5, 6, 7),
            LogicalReadsByTable = Enumerable.Range(0, 24).ToDictionary(i => $"table-{i}", _ => new CompareDistribution(1, 2, 3)),
            Warnings = [new string('w', 20_000)],
            Operators = Enumerable.Range(0, 24).Select(i => new CompareOperatorReport(i, $"operator-{i}", $"dbo.t{i}", true, false, false)).ToArray(),
        };
        var candidate = baseline with
        {
            CpuTimeMilliseconds = new CompareDistribution(20, 30, 40),
            ElapsedTimeMilliseconds = new CompareDistribution(40, 50, 60),
            LogicalReads = new CompareDistribution(7, 8, 9),
        };
        return source with
        {
            Baseline = baseline,
            Candidate = candidate,
            ArtifactDirectory = $"/artifacts/case-{index}",
            Equivalence = new ResultEquivalenceReport(ResultComparisonMode.Off, null, null, null, null),
        };
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

        ResultTextBytes(result, (int)McpLimits.DefaultCallToolResultBudgetBytes);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.DoesNotContain(path, document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var projected = document.RootElement.GetProperty("result");
        Assert.Equal(100, projected.GetProperty("caseCount").GetInt32());
        Assert.True(projected.GetProperty("cases").GetArrayLength() <= 100);
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    [Fact]
    public void Minimum_budget_is_accepted_and_smaller_is_rejected()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var fits = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(4096));
        Assert.True(McpResultAdapter.MeasureBytes(fits) <= 4096);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(4095)));
    }

    [Fact]
    public void Call_budget_is_clamped_to_the_process_ceiling()
    {
        var resolved = McpResultBudget.Resolve(processCeilingBytes: 8192, callMaximumBytes: 65536);
        Assert.Equal(8192, resolved.MaximumBytes);

        var lowered = McpResultBudget.Resolve(processCeilingBytes: 8192, callMaximumBytes: 4096);
        Assert.Equal(4096, lowered.MaximumBytes);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultBudget.Resolve(processCeilingBytes: 2 * 1024 * 1024, callMaximumBytes: null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultBudget.Resolve(processCeilingBytes: 16384, callMaximumBytes: 1000));
    }

    [Fact]
    public void Cell_limit_range_is_zero_to_operator_maximum()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var zero = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(16384, 0));
        Assert.False(zero.IsError == true);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(16384, 4097)));

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
        _output.WriteLine($"tools/list bytes: {bytes} of {McpLimits.ToolsListBudgetBytes} ({tools.Count} tools; no output schemas).");
        Assert.Equal(McpLimits.MaxTools, tools.Count);
        Assert.True(bytes <= McpLimits.ToolsListBudgetBytes, $"tools/list is {bytes} bytes.");
        Assert.DoesNotContain("AGENTS.md", catalogJson, StringComparison.Ordinal);
        foreach (var tool in tools)
            Assert.Null(tool.ProtocolTool.OutputSchema);

        // The budget holds with runtime validation intact: unknown properties
        // are still rejected instead of being dropped to save bytes.
        var rejected = await client.CallToolAsync(
            "sqlharness_query",
            new Dictionary<string, object?>
            {
                ["sql"] = "SELECT 1",
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
    public void Minimal_success_envelope_text_bytes_fit_within_default_budget()
    {
        // 008/T3 baseline guard: the smallest success envelope (ping) against
        // the default 64 KiB result-text budget, with the measured bytes
        // reported for the record.
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessPingReport(Target, "srv", "db", "login", 7),
            null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");
        var bytes = McpResultAdapter.MeasureBytes(result);
        _output.WriteLine($"minimal success result-text bytes: {bytes} of {McpLimits.DefaultCallToolResultBudgetBytes}.");
        Assert.True(bytes <= McpLimits.DefaultCallToolResultBudgetBytes, $"Result text is {bytes} bytes, budget is {McpLimits.DefaultCallToolResultBudgetBytes}.");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
    }

    [Fact]
    public void Raw_path_descends_when_envelope_text_exceeds_budget()
    {
        // A projection-unknown report carries more than the explicit 8 KiB
        // text budget and must be reduced to a valid bounded envelope.
        var payload = new string('p', 12 * 1024);
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new UnprojectedProbeReport("probe", payload),
            null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(8192));

        ResultTextBytes(result, 8192);
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
    public void Main_loop_descends_past_first_fit_level_when_text_exceeds()
    {
        // The first projection exceeds this text budget, so the adapter must
        // descend to a lower detail level instead of emitting it.
        const int budget = 8192;
        var cell = new string('c', 4096);
        var message = new string('m', 4096);
        var set = new SqlHarnessResultSetReport([new SqlHarnessColumnReport(0, "value", "text", true)], [[cell]], 1, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [set], [message], 0, 1, "raw-hash", new OutputFootprint(100000, 1));
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(budget, 4096));

        ResultTextBytes(result, budget);
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
        // Level 0 drops the row (and the message) but keeps the result-set
        // inventory and its column schema, with the omitted row counted.
        Assert.Equal(0, document.RootElement.GetProperty("truncation").GetProperty("detailLimit").GetInt32());
        var resultSets = document.RootElement.GetProperty("result").GetProperty("resultSets");
        var projectedSet = Assert.Single(resultSets.EnumerateArray());
        Assert.Equal(0, projectedSet.GetProperty("rows").GetArrayLength());
        Assert.Equal(1, projectedSet.GetProperty("columns").GetArrayLength());
        Assert.Equal(1, projectedSet.GetProperty("omittedRowCount").GetInt64());
        Assert.False(projectedSet.TryGetProperty("omittedColumnCount", out _));
        Assert.True(document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32() > 0);
    }

    private sealed record UnprojectedProbeReport(string Name, string Payload);
}