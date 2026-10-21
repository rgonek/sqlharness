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
/// 2048/4096, and a 32 KiB (32768 B) tools/list catalog. Budgets are met by
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
        while (JsonSerializer.SerializeToUtf8Bytes(new McpAgentEnvelope(
                   McpResultAdapter.SchemaVersion, "success", report)).Length < targetBytes)
        {
            rows.Add([new string('x', 32)]);
            resultSet = resultSet with { Rows = rows, RowCount = rows.Count };
            report = report with { ResultSets = [resultSet] };
            outcome = outcome with { Report = report };
        }
        return outcome;
    }

    [Fact]
    public void Query_result_byte_measurements_are_recorded_with_annotations_off_and_on()
    {
        var resultSet = new SqlHarnessResultSetReport(
            [new SqlHarnessColumnReport(0, "value", "text", true)], [["synthetic result"]], 1, 0);
        var report = new SqlHarnessQueryReport(Target, "read-only", [resultSet], [], 0, 1,
            "synthetic-hash", new OutputFootprint(32, 1));
        var off = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
        var on = off with
        {
            OperationRef = "0123456789abcdef0123456789abcdef",
            Journaled = true,
            OperationRefEligible = true,
        };

        foreach (var budget in new[] { 4096, 65536 })
        {
            var offResult = McpResultAdapter.Adapt(off, "sqlharness_query", new McpResultBudget(budget));
            var onResult = McpResultAdapter.Adapt(on, "sqlharness_query", new McpResultBudget(budget));
            var offText = Assert.Single(offResult.Content.OfType<TextContentBlock>()).Text;
            var onText = Assert.Single(onResult.Content.OfType<TextContentBlock>()).Text;
            using var offJson = JsonDocument.Parse(offText);
            using var onJson = JsonDocument.Parse(onText);

            Assert.False(offJson.RootElement.TryGetProperty("operationRef", out _));
            Assert.Equal("0123456789abcdef0123456789abcdef", onJson.RootElement.GetProperty("operationRef").GetString());
            if (budget == 4096)
            {
                Assert.Equal(285, McpResultAdapter.MeasureBytes(offResult));
                var offHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(offText)));
                Assert.Equal("611BF72927E6ADD8399D4F776D7B9D094FA4ED779CA8BFA59233802B89E085C1", offHash);
                _output.WriteLine($"annotations-off query result SHA256: {offHash}");
            }
            Assert.InRange(McpResultAdapter.MeasureBytes(offResult), 1, budget);
            Assert.InRange(McpResultAdapter.MeasureBytes(onResult), 1, budget);
            _output.WriteLine($"query result @{budget}: off={McpResultAdapter.MeasureBytes(offResult)} on={McpResultAdapter.MeasureBytes(onResult)} UTF-8 bytes");
        }
    }

    [Fact]
    public void Results_carry_one_text_block_and_no_structured_content()
    {
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0),
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

        Assert.Equal(McpAgentEnvelope.Version, document.RootElement.GetProperty("schemaVersion").GetInt32());
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
        Assert.False(document.RootElement.TryGetProperty("truncation", out _));
    }

    [Fact]
    public void Operation_ref_survives_minimum_budget_and_is_never_clipped()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null)
        {
            OperationRef = "0123456789abcdef0123456789abcdef",
            Journaled = true,
            OperationRefEligible = true,
        };

        var result = McpResultAdapter.Adapt(outcome, "query", new McpResultBudget(McpLimits.MinCallToolResultBudgetBytes));
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);

        Assert.Equal(outcome.OperationRef, document.RootElement.GetProperty("operationRef").GetString());
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.MinCallToolResultBudgetBytes);
    }

    [Fact]
    public void Minimal_error_fallback_keeps_operation_ref_inside_budget()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, new string('x', 20_000))
        {
            OperationRef = "0123456789abcdef0123456789abcdef",
            Journaled = true,
            OperationRefEligible = true,
        };

        var result = McpResultAdapter.Adapt(outcome, "query", new McpResultBudget(McpLimits.MinCallToolResultBudgetBytes));
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);

        Assert.Equal(outcome.OperationRef, document.RootElement.GetProperty("operationRef").GetString());
        Assert.True(McpResultAdapter.MeasureBytes(result) <= McpLimits.MinCallToolResultBudgetBytes);
    }

    [Theory]
    [InlineData(false, "sqlharness_query")]
    [InlineData(true, "sqlharness_plan")]
    public void Operation_ref_is_omitted_without_a_journal_row_or_for_other_tools(bool journaled, string command)
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null)
        {
            OperationRef = "0123456789abcdef0123456789abcdef",
            Journaled = journaled,
            OperationRefEligible = true,
        };

        var result = McpResultAdapter.Adapt(outcome, command);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);

        Assert.False(document.RootElement.TryGetProperty("operationRef", out _));
    }

    [Fact]
    public void Dry_run_operation_ref_is_not_eligible_even_when_a_journal_row_exists()
    {
        var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null)
        {
            OperationRef = "0123456789abcdef0123456789abcdef",
            Journaled = true,
            OperationRefEligible = false,
        };

        var result = McpResultAdapter.Adapt(outcome, "sqlharness_query");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);

        Assert.False(document.RootElement.TryGetProperty("operationRef", out _));
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
    public void Artifact_plan_clipping_count_is_stable_in_the_mcp_envelope()
    {
        var nodes = Enumerable.Range(0, 32).Select(index => new ArtifactPlanNode(
            index == 0 ? "0" : "0/" + (index - 1), index == 0 ? null : "0",
            new PlanNode("Scan", null, null, null, 1, 1, 1, 0.1, null, [], []))).ToArray();
        var section = new ArtifactPlanSection("artifact", "baseline", null, 0, 1, 1,
            "last-measured", [1], null, nodes.Length, nodes, null)
        { StatementText = new string('s', 3000) };

        var result = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, section, null), "sqlharness_artifact");
        ResultTextBytes(result, (int)McpLimits.DefaultCallToolResultBudgetBytes);
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);

        Assert.Equal(1, document.RootElement.GetProperty("truncation").GetProperty("omittedItems").GetInt32());
        Assert.Equal(McpLimits.DefaultMaximumCellCharacters,
            document.RootElement.GetProperty("result").GetProperty("statementText").GetString()!.Length);
    }

    [Fact]
    public void Artifact_plan_with_37_short_nodes_fits_one_default_budget_response()
    {
        var (root, id) = CreateSyntheticPlanArtifact(37);
        try
        {
            var seen = new List<string>();
            var cursor = 0;
            var calls = 0;
            var emittedBytes = 0;
            JsonElement lastProjection;
            while (true)
            {
                var result = ReadPlanResult(root, id, cursor, includeText: false, budget: 65536);
                emittedBytes += ResultTextBytes(result, 65536);
                calls++;
                using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
                var projection = document.RootElement.GetProperty("result");
                seen.AddRange(projection.GetProperty("nodes").EnumerateArray()
                    .Select(node => node.GetProperty("nodePath").GetString()!));
                lastProjection = projection.Clone();
                var continuation = projection.GetProperty("continuation");
                if (continuation.ValueKind == JsonValueKind.Null)
                    break;
                cursor = continuation.GetInt32();
            }

            _output.WriteLine($"synthetic-small: nodes=37 selector=baseline/statement-0/repetition-last/includeText=false budget=65536 calls={calls} emittedUtf8Bytes={emittedBytes} estimatedTokensCeilBytesDiv4={(emittedBytes + 3) / 4}; fixture/discovery/input/chat unmeasured");
            Assert.Equal(1, calls);
            Assert.Equal(37, seen.Count);
            Assert.Equal(37, seen.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(Enumerable.Range(0, 37).Select(index => index == 0 ? "0" : $"0/{index - 1}"), seen);
            Assert.Equal(JsonValueKind.Null, lastProjection.GetProperty("continuation").ValueKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Artifact_plan_continuations_reconstruct_reader_nodes_within_budget(bool includeText)
    {
        const int budget = 4096;
        const int firstCursor = 0;
        const int nodeCount = 257;
        var (root, id) = CreateSyntheticPlanArtifact(nodeCount);
        try
        {
            var seen = new List<string>();
            var cursor = firstCursor;
            var calls = 0;
            var emittedBytes = 0;
            var sawFinalPage = false;
            while (true)
            {
                var result = ReadPlanResult(root, id, cursor, includeText, budget);
                var bytes = ResultTextBytes(result, budget);
                emittedBytes += bytes;
                calls++;
                var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
                using var document = JsonDocument.Parse(text);
                Assert.Empty(McpResultAdapter.ValidateEnvelope(document.RootElement));
                var projection = document.RootElement.GetProperty("result");
                var returned = projection.GetProperty("nodes").EnumerateArray()
                    .Select(node => node.GetProperty("nodePath").GetString()!).ToArray();
                Assert.NotEmpty(returned);
                seen.AddRange(returned);
                if (!includeText)
                    Assert.DoesNotContain("synthetic predicate", text, StringComparison.Ordinal);
                else if (cursor == firstCursor)
                    Assert.Contains("synthetic predicate", text, StringComparison.Ordinal);

                var continuation = projection.GetProperty("continuation");
                if (continuation.ValueKind == JsonValueKind.Null)
                {
                    sawFinalPage = true;
                    break;
                }
                var next = continuation.GetInt32();
                Assert.Equal(cursor + returned.Length, next);
                cursor = next;
            }

            _output.WriteLine($"synthetic-large: nodes=257 selector=baseline/statement-0/repetition-last/includeText={includeText.ToString().ToLowerInvariant()}/cursor=0 budget={budget} calls={calls} emittedUtf8Bytes={emittedBytes} estimatedTokensCeilBytesDiv4={(emittedBytes + 3) / 4}; fixture/discovery/input/chat unmeasured");
            Assert.True(sawFinalPage);
            Assert.Equal(nodeCount, seen.Count);
            Assert.Equal(nodeCount, seen.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(Enumerable.Range(0, nodeCount).Select(index => index == 0 ? "0" : $"0/{index - 1}"), seen);

            var nonzeroResult = ReadPlanResult(root, id, cursor: 5, includeText: includeText, budget: budget);
            ResultTextBytes(nonzeroResult, budget);
            using var nonzeroDocument = JsonDocument.Parse(Assert.Single(nonzeroResult.Content.OfType<TextContentBlock>()).Text);
            var nonzeroProjection = nonzeroDocument.RootElement.GetProperty("result");
            Assert.Equal("0/4", nonzeroProjection.GetProperty("nodes")[0].GetProperty("nodePath").GetString());
            if (nonzeroProjection.GetProperty("continuation").ValueKind != JsonValueKind.Null)
                Assert.Equal(5 + nonzeroProjection.GetProperty("nodes").GetArrayLength(),
                    nonzeroProjection.GetProperty("continuation").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Artifact_plan_adapter_reports_no_node_fit_when_one_source_node_exceeds_budget()
    {
        var (root, id) = CreateSyntheticPlanArtifact(1, warningsPerNode: 24, warningLength: 512);
        try
        {
            var result = ReadPlanResult(root, id, cursor: 0, includeText: true, budget: 4096);
            var bytes = ResultTextBytes(result, 4096);
            using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
            var projection = document.RootElement.GetProperty("result");

            _output.WriteLine($"synthetic-no-fit: nodes=1 selector=baseline/statement-0/repetition-last/includeText=true budget=4096 calls=1 emittedUtf8Bytes={bytes} estimatedTokensCeilBytesDiv4={(bytes + 3) / 4}; fixture/discovery/input/chat unmeasured");
            Assert.Empty(projection.GetProperty("nodes").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, projection.GetProperty("continuation").ValueKind);
            Assert.Contains("cannot fit", projection.GetProperty("pageError").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CallToolResult ReadPlanResult(string root, string id, int cursor, bool includeText, int budget)
    {
        var section = ArtifactReader.ReadSection(root, id, "plan", cursor: cursor,
            planSelector: new ArtifactPlanSelector("baseline"), includeText: includeText);
        return McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, section, null),
            "sqlharness_artifact", new McpResultBudget(budget, 512));
    }

    private static (string Root, string Id) CreateSyntheticPlanArtifact(
        int nodeCount, int warningsPerNode = 0, int warningLength = 0)
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlharness-artifact-pages-" + Guid.NewGuid().ToString("N"));
        const string id = "synthetic-plan";
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(directory, "plans"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"plan\"]}");
        var children = Enumerable.Range(1, nodeCount - 1).Select(index => new
        {
            physicalOp = $"Synthetic-{index:D3}",
            nodeId = index,
            logicalOp = "Scan",
            objectName = $"public.fixture_{index:D3}",
            indexName = "fixture_idx",
            estimatedRows = 1,
            actualRows = 1,
            executions = 1,
            costFraction = 0.1,
            predicate = "synthetic predicate",
            warnings = Enumerable.Repeat(new string('w', warningLength), warningsPerNode).ToArray(),
        }).ToArray();
        var plan = new
        {
            statements = new[]
            {
                new
                {
                    statementText = "SELECT fixture_value FROM public.synthetic_fixture",
                    root = new
                    {
                        physicalOp = "Synthetic-Root",
                        nodeId = 0,
                        logicalOp = "Root",
                        objectName = "public.synthetic_fixture",
                        indexName = "fixture_root_idx",
                        estimatedRows = 1,
                        actualRows = 1,
                        executions = 1,
                        costFraction = 1.0,
                        predicate = "synthetic predicate",
                        warnings = Enumerable.Repeat(new string('w', warningLength), warningsPerNode).ToArray(),
                        children,
                    },
                    missingIndexes = Array.Empty<object>(),
                },
            },
        };
        File.WriteAllText(Path.Combine(directory, "plans", "baseline-001-000-000.plan.json"),
            JsonSerializer.Serialize(plan));
        File.WriteAllText(Path.Combine(directory, "runs.jsonl"),
            "{\"variant\":\"baseline\",\"repetition\":1,\"planJsonFiles\":[\"baseline-001-000-000.plan.json\"]}");
        return (root, id);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Operators_with_plan_refs_fit_the_budget_and_keep_callable_selectors(int budget)
    {
        var href = new OperatorPlanRef("artifact-001", "candidate", null, 1, 5, "0/4", 5);
        var operators = Enumerable.Range(0, 7).Select(index => new NoteworthyOperatorSummary(
            "candidate", index, "Clustered Index Scan", "dbo.Accounts", true, true, false)
        {
            PlanRefs = [href with { Cursor = index, NodePath = index == 0 ? "0" : "0/" + (index - 1) }],
            SourceIsCapped = true,
        }).Append(new NoteworthyOperatorSummary(
            "both", null, "Clustered Index Scan", "dbo.Accounts", null, null, null)
        {
            Baseline = new NoteworthyOperatorSideEvidence(false, true, false),
            Candidate = new NoteworthyOperatorSideEvidence(false, false, false),
            PlanRefs = [href with { Variant = "baseline" }, href],
            SourceIsCapped = true,
        }).ToArray();
        var section = new ArtifactOperatorsSection("artifact-001", "compare", operators)
        {
            SourceIsCapped = true,
        };

        var result = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, section, null),
            "sqlharness_artifact", new McpResultBudget(budget, 64));
        var bytes = ResultTextBytes(result, budget);
        _output.WriteLine($"operators-plan-refs: budget={budget} emittedUtf8Bytes={bytes} estimatedTokensCeilBytesDiv4={(bytes + 3) / 4}");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var returned = document.RootElement.GetProperty("result").GetProperty("operators");
        Assert.NotEmpty(returned.EnumerateArray());
        var first = returned[0].GetProperty("planRefs")[0];
        Assert.True(returned[0].GetProperty("hasWarnings").GetBoolean());
        var shared = Assert.Single(returned.EnumerateArray(), item => item.GetProperty("side").GetString() == "both");
        Assert.False(shared.TryGetProperty("hasWarnings", out _));
        Assert.False(shared.TryGetProperty("hasSpill", out _));
        Assert.False(shared.TryGetProperty("hasImplicitConversion", out _));
        Assert.True(shared.GetProperty("baseline").GetProperty("hasSpill").GetBoolean());
        Assert.False(shared.GetProperty("candidate").GetProperty("hasSpill").GetBoolean());
        Assert.Equal("artifact-001", first.GetProperty("artifactId").GetString());
        Assert.Equal("candidate", first.GetProperty("variant").GetString());
        Assert.Equal(1, first.GetProperty("statement").GetInt32());
        Assert.Equal(5, first.GetProperty("repetition").GetInt32());
        Assert.True(first.TryGetProperty("nodePath", out _));
        Assert.True(first.TryGetProperty("cursor", out _));
        Assert.False(first.TryGetProperty("nodeId", out _));
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
        var cases = document.RootElement.GetProperty("result").GetProperty("cases");
        Assert.True(cases.GetArrayLength() > 0);
        Assert.Equal("fail", cases[0].GetProperty("regression").GetProperty("verdict").GetString());
        Assert.Equal(
            [RegressionPolicy.ReasonElapsedRegressed],
            cases[0].GetProperty("regression").GetProperty("reasons").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.False(document.RootElement.GetProperty("result").TryGetProperty("details", out var details) && details.GetArrayLength() > 0);
    }

    [Fact]
    public void Thirty_two_mixed_matrix_pages_fit_4096_and_keep_verdicts()
    {
        var comparePass = BuildCompareReport();
        var cells = Enumerable.Range(0, 32).Select(i =>
        {
            var verdict = i == 7 ? FailVerdict() : i == 15
                ? new RegressionReport(RegressionPolicy.Inconclusive, [RegressionPolicy.ReasonUnstableElapsed],
                    RegressionPolicy.PolicyVersion,
                    new RegressionDiagnostic(RegressionPolicy.StatusInconclusive, 40m, 12m, -28m, -70m),
                    new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 1m, 1m, 0m, 0m),
                    new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 1m, 1m, 0m, 0m))
                : new RegressionReport(RegressionPolicy.Pass, [], RegressionPolicy.PolicyVersion,
                    new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 40m, 12m, -28m, -70m),
                    new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 1m, 1m, 0m, 0m),
                    new RegressionDiagnostic(RegressionPolicy.StatusNotRegressed, 1m, 1m, 0m, 0m));
            return new CompareMatrixCellReport(i, i.ToString(), BuildCompareReport(verdict) with
            {
                ArtifactDirectory = $"/artifacts/case-{i:D2}",
            });
        }).ToArray();
        var matrix = new SqlHarnessCompareMatrixReport("batch", "int", cells)
        {
            Coverage = MatrixExecutionCoverage.AllCompleted(32),
        };

        var result = McpResultAdapter.Adapt(
            new SqlHarnessOutcome(SqlHarnessExitCode.Success, matrix, null) { Coverage = matrix.Coverage },
            "sqlharness_compare", new McpResultBudget(4096, 128));

        var bytes = ResultTextBytes(result, 4096);
        _output.WriteLine($"32-case matrix page: {bytes} bytes of 4096");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var projected = document.RootElement.GetProperty("result");
        Assert.True(projected.GetProperty("cases").GetArrayLength() < 32);
        Assert.True(projected.GetProperty("omittedCaseCount").GetInt32() > 0);
        foreach (var matrixCase in projected.GetProperty("cases").EnumerateArray())
        {
            Assert.True(matrixCase.TryGetProperty("regression", out var regression));
            Assert.False(string.IsNullOrEmpty(regression.GetProperty("verdict").GetString()));
            Assert.True(matrixCase.TryGetProperty("artifactId", out var id));
            Assert.False(string.IsNullOrEmpty(id.GetString()));
        }
        Assert.Equal(32, projected.GetProperty("coverage").GetProperty("requestedCases").GetInt32());
        Assert.Equal(32, projected.GetProperty("coverage").GetProperty("completedCases").GetInt32());
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
        Assert.False(document.RootElement.TryGetProperty("truncation", out _));
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
        Assert.Equal(6, projected.GetProperty("caseCount").GetInt32());
        Assert.InRange(cases.GetArrayLength(), 1, 6);
        Assert.Equal(6 - cases.GetArrayLength(), projected.GetProperty("omittedCaseCount").GetInt32());
        if (budget >= 65536)
            Assert.Equal(6, cases.GetArrayLength());
        Assert.Equal(Enumerable.Range(0, cases.GetArrayLength()), cases.EnumerateArray().Select(item => item.GetProperty("index").GetInt32()));
        foreach (var matrixCase in cases.EnumerateArray())
        {
            Assert.True(matrixCase.TryGetProperty("delta", out _));
            Assert.Equal("not-requested", matrixCase.GetProperty("equivalenceAvailability").GetString());
            Assert.True(matrixCase.TryGetProperty("baseline", out var baseline));
            Assert.True(baseline.TryGetProperty("elapsedMinimum", out _));
            Assert.True(baseline.TryGetProperty("elapsedMaximum", out _));
        }
        Assert.Equal(10m, cases[0].GetProperty("delta").GetProperty("elapsedMilliseconds").GetDecimal());
        Assert.Equal(JsonValueKind.Null, cases[0].GetProperty("equivalent").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Compare_table_metric_keys_keep_identity_under_mcp_cell_clipping(int maximumCellCharacters)
    {
        var firstName = new string('x', maximumCellCharacters) + "first";
        var secondName = new string('x', maximumCellCharacters) + "second";
        var reads = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
        {
            [firstName] = new(1, 11, 21),
            [secondName] = new(2, 29, 39),
        };
        var source = BuildCompareReport();
        var compare = source with
        {
            Baseline = source.Baseline with { LogicalReadsByTable = reads, Warnings = ["ordinary display text"] },
            Candidate = source.Candidate with { LogicalReadsByTable = reads, Warnings = ["ordinary display text"] },
        };

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, compare, null),
            "sqlharness_compare", new McpResultBudget(4096, maximumCellCharacters));

        var responseBytes = ResultTextBytes(result, 4096);
        _output.WriteLine($"MCP compare identity fixture (maxCellChars {maximumCellCharacters}): {responseBytes} UTF-8 bytes (budget 4096).");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var projected = document.RootElement.GetProperty("result");
        AssertTableReadValues(projected.GetProperty("baseline").GetProperty("logicalReadsByTable"), firstName, secondName);
        AssertTableReadValues(projected.GetProperty("candidate").GetProperty("logicalReadsByTable"), firstName, secondName);
        if (maximumCellCharacters == 0)
        {
            Assert.Equal("", projected.GetProperty("baseline").GetProperty("warnings")[0].GetString());
            Assert.Equal("", projected.GetProperty("candidate").GetProperty("warnings")[0].GetString());
        }
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(3500)]
    [InlineData(5000)]
    public void Compare_omits_unfittable_long_table_identity_truthfully_within_minimum_budget(int longNameLength)
    {
        var longName = new string('x', longNameLength) + "table";
        var source = BuildCompareReport();
        var reads = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)
        {
            [longName] = new(80, 99, 120),
            ["dbo.short"] = new(2, 29, 39),
        };
        var compare = source with
        {
            Baseline = source.Baseline with { LogicalReadsByTable = reads },
            Candidate = source.Candidate with { LogicalReadsByTable = reads },
        };

        var result = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, compare, null),
            "sqlharness_compare", new McpResultBudget(4096, 2048));

        var responseBytes = ResultTextBytes(result, 4096);
        _output.WriteLine($"MCP long identity omission fixture: {responseBytes} UTF-8 bytes (budget 4096).");
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        var truncation = document.RootElement.GetProperty("truncation");
        var projectedLongPrefix = longName[..2048];
        var projected = document.RootElement.GetProperty("result");
        foreach (var side in new[] { "baseline", "candidate" })
        {
            var tableReads = projected.GetProperty(side).GetProperty("logicalReadsByTable");
            Assert.False(tableReads.TryGetProperty(longName, out _));
            Assert.False(tableReads.TryGetProperty(projectedLongPrefix, out _));
            var shortReads = Assert.Single(tableReads.EnumerateObject());
            Assert.Equal("dbo.short", shortReads.Name);
            Assert.Equal(29, shortReads.Value.GetProperty("median").GetInt64());
        }
        Assert.Equal(2, truncation.GetProperty("omittedItems").GetInt32());
    }

    private static void AssertTableReadValues(JsonElement reads, string firstName, string secondName)
    {
        Assert.Equal(2, reads.EnumerateObject().Count());
        Assert.Equal(11, reads.GetProperty(firstName).GetProperty("median").GetInt64());
        Assert.Equal(29, reads.GetProperty(secondName).GetProperty("median").GetInt64());
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
            new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0),
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
            new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0),
            null);

        var zero = McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(16384, 0));
        Assert.False(zero.IsError == true);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => McpResultAdapter.Adapt(outcome, "sqlharness_query", new McpResultBudget(16384, 4097)));

        var defaultBudget = McpResultBudget.Resolve(16384, null, null, 4096);
        Assert.Equal(McpLimits.DefaultMaximumCellCharacters, defaultBudget.MaximumCellCharacters);
        var wide = McpResultBudget.Resolve(16384, null, 4096, 4096);
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
        Assert.Equal(McpLimits.MaxToolsWithoutOperationAnnotations, tools.Count);
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
        // 008/T3 baseline guard: the smallest success envelope (dry run) against
        // the default 64 KiB result-text budget, with the measured bytes
        // reported for the record.
        var outcome = new SqlHarnessOutcome(
            SqlHarnessExitCode.Success,
            new SqlHarnessDryRunReport("query", "read-only", "read-only", [], 0),
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
        Assert.False(document.RootElement.GetProperty("truncation").TryGetProperty("detailLimit", out _));
        Assert.Equal(4096, document.RootElement.GetProperty("truncation").GetProperty("maxCellChars").GetInt32());
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