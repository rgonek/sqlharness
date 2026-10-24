using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Mcp.Tools;
using SqlHarness.Mcp.WorkflowHost;

using Xunit.Abstractions;

namespace SqlHarness.Mcp.Tests;

[Collection("McpStdioProcess")]
public sealed class AgentOptimizationWorkflowTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private static readonly int[] ScriptedBudgets = [4096, 65536];
    private static readonly string[] ScriptedScenarios =
    [
        WorkflowScenarios.NoImprovementId,
        WorkflowScenarios.PartialMatrixId,
        WorkflowScenarios.CollidingOperatorsId,
        WorkflowScenarios.UnavailableMetricsId,
    ];

    private readonly ITestOutputHelper _output;

    public AgentOptimizationWorkflowTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Manifest_pins_portable_scenario_ids_without_machine_paths()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Fixture("manifest.json")));
        var root = document.RootElement;
        Assert.Equal("synthetic-optimization-workflows", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("replayable").GetBoolean());
        Assert.DoesNotContain(":\\", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", root.GetRawText(), StringComparison.Ordinal);
        var ids = root.GetProperty("scenarios").EnumerateArray().Select(scenario => scenario.GetProperty("id").GetString()).ToArray();
        Assert.Contains(WorkflowScenarios.NoImprovementId, ids);
        Assert.Contains(WorkflowScenarios.PartialMatrixId, ids);
        Assert.Contains(WorkflowScenarios.CollidingOperatorsId, ids);
        Assert.Contains("oracle/no-improvement.json", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("requiredClaims", File.ReadAllText(Fixture("prompts", "no-improvement.md")), StringComparison.Ordinal);

        var summaryText = File.ReadAllText(Fixture("trials", "prechange-summary.json"));
        Assert.DoesNotContain(":\\", summaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", summaryText, StringComparison.Ordinal);
        using var summary = JsonDocument.Parse(summaryText);
        Assert.Equal(9, summary.RootElement.GetArrayLength());
        Assert.All(summary.RootElement.EnumerateArray(), row =>
        {
            Assert.Equal("prechange", row.GetProperty("phase").GetString());
            Assert.Equal("grok-4.6", row.GetProperty("model").GetString());
            Assert.Equal("medium", row.GetProperty("effort").GetString());
            Assert.True(row.TryGetProperty("contaminated", out _));
        });
        var run1 = summary.RootElement.EnumerateArray().Single(row =>
            row.GetProperty("family").GetString() == "no-improvement" && row.GetProperty("run").GetInt32() == 1);
        Assert.True(run1.GetProperty("contaminated").GetBoolean());
        Assert.Equal("temp-host-dll-search", run1.GetProperty("contamination").GetString());
        Assert.NotEqual("incomplete", run1.GetProperty("outcome").GetString());
        var ni3 = summary.RootElement.EnumerateArray().Single(row =>
            row.GetProperty("family").GetString() == "no-improvement" && row.GetProperty("run").GetInt32() == 3);
        Assert.Equal("failed-oracle", ni3.GetProperty("outcome").GetString());
        Assert.Contains(
            "sqlharness_query",
            ni3.GetProperty("outsideAllowedSelectors").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            4,
            summary.RootElement.EnumerateArray().Count(row => row.GetProperty("outcome").GetString() == "evidence-complete"));
        Assert.All(
            summary.RootElement.EnumerateArray().Where(row =>
                row.GetProperty("family").GetString() != "no-improvement" || row.GetProperty("run").GetInt32() != 1),
            row => Assert.False(row.GetProperty("contaminated").GetBoolean()));
    }

    [Fact]
    public async Task Host_handshake_lists_nine_tools_serves_capabilities_and_rejects_unknown_sql()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.NoImprovementId, Budget);
        var tools = await session.Client.ListToolsAsync(cancellationToken: session.Token);
        Assert.Equal(
            McpToolCatalog.ToolNames.Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("sqlharness_mutation", tools.Select(tool => tool.Name));

        var capabilities = await session.Call("sqlharness_capabilities", new Dictionary<string, object?>());
        Assert.False(capabilities.IsError == true, Text(capabilities));
        var envelope = Envelope(capabilities);
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.Equal("fixed", envelope.GetProperty("result").GetProperty("scopeMode").GetString());
        Assert.Single(capabilities.Content.OfType<TextContentBlock>());

        var unknown = await session.Call("sqlharness_compare", CompareArguments("SELECT 1", "SELECT 2"));
        Assert.True(unknown.IsError == true);
        Assert.Contains(WorkflowModule.UnknownSqlMessage, Text(unknown), StringComparison.Ordinal);
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Ordered_compare_requires_ordered_semantics_and_does_not_emit_order_only()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.NoImprovementId, Budget);
        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.NoImprovementBaselineSql, WorkflowScenarios.NoImprovementCandidateSql));
        Assert.False(compare.IsError == true, Text(compare));
        var text = Text(compare);
        var equivalence = Envelope(compare).GetProperty("result").GetProperty("equivalence");

        Assert.Equal("ordered", equivalence.GetProperty("mode").GetString());
        Assert.False(equivalence.GetProperty("equivalent").GetBoolean());
        var reasons = equivalence.GetProperty("mismatchReasons").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Contains("rows", reasons);
        Assert.DoesNotContain("order-only", reasons);
        Assert.DoesNotContain("order-only", text, StringComparison.Ordinal);
        Assert.Equal("ordered", AgentProjectionNames.ComparisonMode(ResultComparisonMode.Ordered));
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Compare_follow_up_reads_a_real_offline_artifact()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.NoImprovementId, Budget);
        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.NoImprovementBaselineSql, WorkflowScenarios.NoImprovementCandidateSql));
        Assert.False(compare.IsError == true, Text(compare));
        var artifactId = ArtifactId(Envelope(compare));
        Assert.False(string.IsNullOrWhiteSpace(artifactId));

        var summary = await session.Call("sqlharness_artifact", new Dictionary<string, object?>
        {
            ["id"] = artifactId,
            ["section"] = "summary",
        });
        Assert.False(summary.IsError == true, Text(summary));
        var summaryText = Text(summary);
        Assert.Contains("inconclusive", summaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT Id, Name FROM dbo.Orders", summaryText, StringComparison.Ordinal);
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Artifact_diagnostics_support_offline_memory_tradeoffs_and_multi_statement_io_at_both_budgets()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.ArtifactDiagnosticEvidenceId, Budget);
        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.ArtifactReviewBaselineSql,
            WorkflowScenarios.ArtifactReviewCandidateV1Sql,
            budget: 65536));
        Assert.False(compare.IsError == true, Text(compare));
        var artifactId = ArtifactId(Envelope(compare));

        async Task<JsonElement> ReadMetricsAsync(int budget)
        {
            var result = await session.Call("sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = artifactId,
                ["section"] = "metrics",
                ["maxResultBytes"] = budget,
            });
            Assert.False(result.IsError == true, Text(result));
            Assert.InRange(Encoding.UTF8.GetByteCount(Text(result)), 1, budget);
            _output.WriteLine($"metrics budget={budget} responseUtf8Bytes={Encoding.UTF8.GetByteCount(Text(result))}");
            return Envelope(result).GetProperty("result").Clone();
        }

        async Task<(List<JsonElement> Rows, List<int> ResponseBytes)> ReadStatementsAsync(int budget, int firstCursor)
        {
            const int maximumPages = 4;
            var rows = new List<JsonElement>();
            var responseBytes = new List<int>();
            int? cursor = firstCursor;
            while (rows.Count < 4 && responseBytes.Count < maximumPages)
            {
                var arguments = new Dictionary<string, object?>
                {
                    ["id"] = artifactId,
                    ["section"] = "statements",
                    ["pageSize"] = 1,
                    ["maxResultBytes"] = budget,
                };
                if (cursor is int current)
                    arguments["cursor"] = current;
                var result = await session.Call("sqlharness_artifact", arguments);
                Assert.False(result.IsError == true, Text(result));
                var text = Text(result);
                responseBytes.Add(Encoding.UTF8.GetByteCount(text));
                Assert.InRange(responseBytes[^1], 1, budget);
                var envelope = Envelope(result);
                rows.AddRange(envelope.GetProperty("result").GetProperty("statements").EnumerateArray().Select(row => row.Clone()));
                var next = Continuation(envelope);
                Assert.True(rows.Count >= 4 || next is int,
                    $"Statement pagination exhausted after {rows.Count} of 4 expected rows.");
                if (next is int continuation)
                    Assert.True(continuation > (cursor ?? 0), $"Statement continuation did not advance from {cursor} to {continuation}.");
                else
                    Assert.Equal(4, rows.Count);
                cursor = next;
            }

            Assert.Equal(4, rows.Count);

            return (rows, responseBytes);
        }

        var defaultMetrics = await ReadMetricsAsync(65536);
        var minimumMetrics = await ReadMetricsAsync(4096);
        foreach (var metrics in new[] { defaultMetrics, minimumMetrics })
        {
            var planDiagnostics = metrics.GetProperty("planDiagnosticDistributions").EnumerateArray()
                .ToArray();
            Assert.NotEmpty(planDiagnostics);
            Assert.All(planDiagnostics, item =>
            {
                Assert.Equal("sql-server-showplan-query-plan", item.GetProperty("source").GetString());
                Assert.True(item.TryGetProperty("planDocumentOrdinal", out _));
                Assert.True(item.TryGetProperty("planQueryOrdinal", out _));
            });
            if (metrics.GetProperty("planDiagnosticDistributionsAvailability").GetString() == "partial")
                Assert.True(metrics.GetProperty("omittedPlanDiagnosticDistributions").GetInt32() > 0);
            var statementRead = Assert.Single(metrics.GetProperty("statisticsIoScanCountDistributions").EnumerateArray(),
                item => item.GetProperty("variant").GetString() == "candidate" && item.GetProperty("table").GetString() == "Orders");
            Assert.Equal("sql-server-statistics-io", statementRead.GetProperty("source").GetString());
            Assert.Equal(5, statementRead.GetProperty("scanCount").GetProperty("median").GetInt64());
        }

        var defaultBaselineStatements = await ReadStatementsAsync(65536, 0);
        var defaultCandidateStatements = await ReadStatementsAsync(65536, 20);
        var minimumBaselineStatements = await ReadStatementsAsync(4096, 0);
        var minimumCandidateStatements = await ReadStatementsAsync(4096, 20);
        foreach (var (variant, statements) in new[]
        {
            ("baseline", defaultBaselineStatements),
            ("candidate", defaultCandidateStatements),
            ("baseline", minimumBaselineStatements),
            ("candidate", minimumCandidateStatements),
        })
        {
            var selected = statements.Rows.Where(row => row.GetProperty("variant").GetString() == variant
                && row.GetProperty("repetition").GetInt32() == 1).OrderBy(row => row.GetProperty("statementOrdinal").GetInt32()).ToArray();
            Assert.Equal(4, selected.Length);
            Assert.Equal(Enumerable.Range(0, 4), selected.Select(row => row.GetProperty("statementOrdinal").GetInt32()));
            for (var ordinal = 0; ordinal < selected.Length; ordinal++)
            {
                var row = selected[ordinal];
                Assert.Equal("available", row.GetProperty("objectIoAvailability").GetString());
                var objects = row.GetProperty("objects");
                var io = Assert.Single(objects.EnumerateArray());
                Assert.Equal(new[] { "[dbo].[Orders]", "[dbo].[Orders]", "[dbo].[Customers]", "[dbo].[Payments]" }[ordinal], io.GetProperty("object").GetString());
                Assert.Equal(100 + ordinal * 10, io.GetProperty("logicalReads").GetInt64());
                Assert.Equal(ordinal * 2, io.GetProperty("lobLogicalReads").GetInt64());
                Assert.Equal(3 + ordinal, io.GetProperty("showplanActualScans").GetInt64());
                var diagnostic = Assert.Single(row.GetProperty("planDiagnostics").EnumerateArray());
                Assert.Equal("sql-server-showplan-query-plan", diagnostic.GetProperty("source").GetString());
                Assert.Equal(1 + ordinal, diagnostic.GetProperty("degreeOfParallelism").GetInt32());
                var isCandidate = variant == "candidate";
                Assert.Equal((isCandidate ? 120 : 240) + ordinal * 50, diagnostic.GetProperty("grantRequestedKb").GetInt64());
                Assert.Equal((isCandidate ? 90 : 200) + ordinal * 40, diagnostic.GetProperty("grantGrantedKb").GetInt64());
                Assert.Equal((isCandidate ? 60 : 160) + ordinal * 30, diagnostic.GetProperty("grantMaxUsedKb").GetInt64());
                Assert.Equal((isCandidate ? 20 : 5) + ordinal, diagnostic.GetProperty("compileTimeMs").GetInt64());
                Assert.Equal((isCandidate ? 10 : 2) + ordinal, diagnostic.GetProperty("compileCpuMs").GetInt64());
            }
            Assert.True(statements.ResponseBytes.Count > 1, "The statement inventory should require continuation at both budgets.");
        }

        _output.WriteLine($"Offline diagnostic retrieval: metrics UTF-8 bytes min/default={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(minimumMetrics))}/{Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(defaultMetrics))}; statements min/default calls={minimumBaselineStatements.ResponseBytes.Count + minimumCandidateStatements.ResponseBytes.Count}/{defaultBaselineStatements.ResponseBytes.Count + defaultCandidateStatements.ResponseBytes.Count}, response bytes={minimumBaselineStatements.ResponseBytes.Sum() + minimumCandidateStatements.ResponseBytes.Sum()}/{defaultBaselineStatements.ResponseBytes.Sum() + defaultCandidateStatements.ResponseBytes.Sum()}; all four statement identities for both variants and grant/compile source values matched.");
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65536)]
    public async Task Compact_compare_keeps_partial_statement_case_and_artifact_reference(int budget)
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.ArtifactReviewStudyId, Budget);
        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.ArtifactReviewBaselineSql,
            WorkflowScenarios.ArtifactReviewCandidateV1Sql,
            budget: budget));
        Assert.False(compare.IsError == true, Text(compare));
        var result = Envelope(compare).GetProperty("result");
        var artifactId = ArtifactId(Envelope(compare));
        Assert.False(string.IsNullOrWhiteSpace(artifactId));
        Assert.Equal(artifactId, result.GetProperty("artifactId").GetString());

        var evidence = result.GetProperty("statementEvidence");
        Assert.Equal("collected-statement-counters", evidence.GetProperty("coverageBasis").GetString());
        var candidateCase = evidence.GetProperty("cases").EnumerateArray().Single(item =>
            item.GetProperty("variant").GetString() == "candidate");
        Assert.Equal("partial", candidateCase.GetProperty("counterAttribution").GetString());
        Assert.Equal(4, candidateCase.GetProperty("partialStatementCount").GetInt32());

        int? cursor = null;
        var affected = new List<JsonElement>();
        do
        {
            var arguments = new Dictionary<string, object?>
            {
                ["id"] = artifactId,
                ["section"] = "statements",
                ["pageSize"] = 32,
                ["maxResultBytes"] = budget,
                ["maxCellChars"] = 0,
            };
            if (cursor is int next)
                arguments["cursor"] = next;
            var statements = await session.Call("sqlharness_artifact", arguments);
            Assert.False(statements.IsError == true, Text(statements));
            affected.AddRange(Envelope(statements).GetProperty("result").GetProperty("statements").EnumerateArray()
                .Where(item => item.GetProperty("variant").GetString() == "candidate"
                    && item.GetProperty("repetition").GetInt32() == 1));
            cursor = Continuation(Envelope(statements));
        }
        while (cursor is not null);
        Assert.Equal(4, affected.Count);
        Assert.All(affected, item => Assert.Equal("partial", item.GetProperty("counterAttribution").GetString()));
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Artifact_513_node_sdk_completion_measures_default_and_explicit_pages()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.PlanPagingId, Budget);
        var tools = await session.Client.ListToolsAsync(cancellationToken: session.Token);
        var catalogBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools.Select(tool => tool.ProtocolTool).ToArray()));
        Assert.InRange(catalogBytes, 1, McpLimits.ToolsListBudgetBytes);

        var compare = await session.Call("sqlharness_compare", CompareArguments(
            "SELECT Id FROM dbo.Orders;", "SELECT Id FROM dbo.Orders;"));
        Assert.False(compare.IsError == true, Text(compare));
        var artifactId = ArtifactId(Envelope(compare));
        Assert.False(string.IsNullOrWhiteSpace(artifactId));

        var defaults = await ReadPlanCompletionAsync(session, artifactId!, pageSize: null, maxResultBytes: null);
        var explicitDefaults = await ReadPlanCompletionAsync(session, artifactId!, pageSize: 128, maxResultBytes: null);
        var explicitLarge = await ReadPlanCompletionAsync(session, artifactId!, pageSize: 512, maxResultBytes: 131072);
        var expectedNodePaths = new[] { "0" }.Concat(Enumerable.Range(0, 512).Select(index => $"0/{index}")).ToArray();
        Assert.Equal(expectedNodePaths, defaults.NodePaths);
        Assert.Equal(expectedNodePaths.Length, defaults.NodePaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expectedNodePaths, explicitDefaults.NodePaths);
        Assert.Equal(expectedNodePaths.Length, explicitDefaults.NodePaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expectedNodePaths, explicitLarge.NodePaths);
        Assert.Equal(expectedNodePaths.Length, explicitLarge.NodePaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(defaults.NodePaths, explicitDefaults.NodePaths);
        Assert.Equal(defaults.NodePaths, explicitLarge.NodePaths);
        Assert.Equal(defaults.NodeRecords, explicitDefaults.NodeRecords);
        Assert.Equal(defaults.NodeRecords, explicitLarge.NodeRecords);
        Assert.Equal(5, defaults.Calls.Count);
        Assert.Equal(2, explicitLarge.Calls.Count);
        Assert.All(defaults.Calls, call => Assert.InRange(call.ResponseTextUtf8Bytes, 1, McpLimits.DefaultCallToolResultBudgetBytes));
        Assert.All(explicitLarge.Calls, call => Assert.InRange(call.ResponseTextUtf8Bytes, 1, 131072));

        var defaultsTotal = defaults.Calls.Sum(call => call.RequestWireUtf8Bytes + call.ResponseWireUtf8Bytes);
        var explicitTotal = explicitLarge.Calls.Sum(call => call.RequestWireUtf8Bytes + call.ResponseWireUtf8Bytes);
        var increase = explicitTotal - defaultsTotal;
        _output.WriteLine($"513-node saved-artifact completion: catalog={catalogBytes} UTF-8 bytes; defaults calls={defaults.Calls.Count}, request={defaults.Calls.Sum(call => call.RequestWireUtf8Bytes)}, response={defaults.Calls.Sum(call => call.ResponseWireUtf8Bytes)}, combined={defaultsTotal}; explicit pageSize=512/maxResultBytes=131072 calls={explicitLarge.Calls.Count}, request={explicitLarge.Calls.Sum(call => call.RequestWireUtf8Bytes)}, response={explicitLarge.Calls.Sum(call => call.ResponseWireUtf8Bytes)}, combined={explicitTotal}, increase={increase} bytes.");
        _output.WriteLine($"Defaults compatibility: omitted pageSize and explicit pageSize=128 reconstruct the same {defaults.NodePaths.Count} node paths; host connectionAttempts=0.");

        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Artifact_review_study_freezes_four_set_batch_revisions_and_late_assertions()
    {
        var capture = new Dictionary<string, object?>
        {
            ["study"] = "plan-109-task-1",
            ["host"] = "SqlHarness.Mcp.WorkflowHost",
            ["sdkClient"] = "ModelContextProtocol.Client.McpClient",
            ["fixtureInputs"] = new Dictionary<string, object?>
            {
                ["batchSql"] = WorkflowScenarios.ArtifactReviewBaselineSql,
                ["setupSql"] = WorkflowScenarios.ArtifactReviewSetupSql,
                ["candidateRevision1"] = WorkflowScenarios.ArtifactReviewCandidateV1Sql,
                ["candidateRevision2"] = WorkflowScenarios.ArtifactReviewCandidateV2Sql,
                ["parameterSets"] = new[] { "small", "medium", "large", "edge" }
                    .Select((name, index) => new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["parameters"] = new[] { new Dictionary<string, object?> { ["name"] = "Batch", ["type"] = "int", ["value"] = (index + 1).ToString() } },
                    }).ToArray(),
                ["querySql"] = WorkflowScenarios.ArtifactReviewQuerySql,
            },
            ["calls"] = new List<object>(),
        };
        var calls = (List<object>)capture["calls"]!;
        var parameterSets = (Dictionary<string, object?>[])((Dictionary<string, object?>)capture["fixtureInputs"]!)["parameterSets"]!;

        var revisionHashes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (scenario, revision, candidateSql) in new[]
                 {
                     (WorkflowScenarios.ArtifactReviewStudyId, "candidate-revision-1", WorkflowScenarios.ArtifactReviewCandidateV1Sql),
                     (WorkflowScenarios.ArtifactReviewStudyV2Id, "candidate-revision-2", WorkflowScenarios.ArtifactReviewCandidateV2Sql),
                 })
        {
            await using var session = await Session.StartAsync(scenario, Budget);
            var compareArguments = CompareArguments(WorkflowScenarios.ArtifactReviewBaselineSql, candidateSql, budget: 65536);
            var compare = await RecordStudyCallAsync(session, calls, revision, "sqlharness_compare", compareArguments, 65536);
            Assert.False(compare.IsError == true, Text(compare));
            var artifactId = ArtifactId(Envelope(compare))!;

            var statementItems = new List<JsonElement>();
            int? statementCursor = null;
            do
            {
                var arguments = new Dictionary<string, object?> { ["id"] = artifactId, ["section"] = "statements", ["pageSize"] = 32, ["maxResultBytes"] = 65536 };
                if (statementCursor is int current)
                    arguments["cursor"] = current;
                var statements = await RecordStudyCallAsync(session, calls, revision, "sqlharness_artifact", arguments, 65536);
                Assert.False(statements.IsError == true, Text(statements));
                statementItems.AddRange(Envelope(statements).GetProperty("result").GetProperty("statements").EnumerateArray().Select(item => item.Clone()));
                statementCursor = Continuation(Envelope(statements));
            }
            while (statementCursor is not null);
            Assert.Equal(40, statementItems.Count);
            foreach (var variant in new[] { "baseline", "candidate" })
            {
                var selected = statementItems.Where(item => item.GetProperty("variant").GetString() == variant).ToArray();
                Assert.Equal(20, selected.Length);
                Assert.Equal(Enumerable.Range(1, 5), selected.Select(item => item.GetProperty("repetition").GetInt32()).Distinct().Order());
                Assert.All(Enumerable.Range(1, 5), repetition =>
                    Assert.Equal(new[] { 0, 1, 2, 3 }, selected.Where(item => item.GetProperty("repetition").GetInt32() == repetition).Select(item => item.GetProperty("statementOrdinal").GetInt32()).ToArray()));
                Assert.All(selected, item =>
                {
                    Assert.Equal(JsonValueKind.Number, item.GetProperty("cpuTimeMilliseconds").ValueKind);
                    Assert.Equal(JsonValueKind.Number, item.GetProperty("elapsedTimeMilliseconds").ValueKind);
                    Assert.NotEmpty(item.GetProperty("topOperators").EnumerateArray());
                });
            }
            revisionHashes[revision] = statementItems
                .Where(item => item.GetProperty("variant").GetString() == "candidate" && item.GetProperty("repetition").GetInt32() == 5)
                .OrderBy(item => item.GetProperty("statementOrdinal").GetInt32())
                .Select(item => item.GetProperty("statementHash").GetString()!)
                .ToArray();

            for (var statement = 0; statement < 4; statement++)
            {
                int? cursor = null;
                do
                {
                    var arguments = new Dictionary<string, object?>
                    {
                        ["id"] = artifactId,
                        ["section"] = "plan",
                        ["variant"] = "candidate",
                        ["statement"] = statement,
                        ["repetition"] = 5,
                        ["includeText"] = false,
                        ["maxResultBytes"] = 65536,
                    };
                    if (cursor is int current)
                        arguments["cursor"] = current;
                    var plan = await RecordStudyCallAsync(session, calls, revision, "sqlharness_artifact", arguments, 65536);
                    Assert.False(plan.IsError == true, Text(plan));
                    var planResult = Envelope(plan).GetProperty("result");
                    Assert.Equal(artifactId, planResult.GetProperty("artifactId").GetString());
                    Assert.Equal("candidate", planResult.GetProperty("variant").GetString());
                    Assert.Equal(statement, planResult.GetProperty("statement").GetInt32());
                    Assert.Equal(5, planResult.GetProperty("repetition").GetInt32());
                    cursor = Continuation(Envelope(plan));
                }
                while (cursor is not null);
            }

            await session.ShutdownAsync();
            session.AssertNoDatabaseConnections();
        }
        var revision1Hashes = revisionHashes["candidate-revision-1"];
        var revision2Hashes = revisionHashes["candidate-revision-2"];
        Assert.Equal(4, revision1Hashes.Length);
        Assert.Equal(4, revision2Hashes.Length);
        for (var ordinal = 0; ordinal < 3; ordinal++)
            Assert.Equal(revision1Hashes[ordinal], revision2Hashes[ordinal]);
        Assert.NotEqual(revision1Hashes[3], revision2Hashes[3]);

        await using (var session = await Session.StartAsync(WorkflowScenarios.ArtifactReviewStudyId, Budget))
        {
            var measure = await RecordStudyCallAsync(session, calls, "named-set-attribution", "sqlharness_measure",
                new Dictionary<string, object?>
                {
                    ["query"] = new Dictionary<string, object?> { ["sql"] = WorkflowScenarios.ArtifactReviewBaselineSql },
                    ["setup"] = new Dictionary<string, object?> { ["sql"] = WorkflowScenarios.ArtifactReviewSetupSql },
                    ["subject"] = "four-statement-batch",
                    ["scope"] = "whole",
                    ["parameterSets"] = parameterSets,
                    ["repeat"] = 3,
                    ["maxResultBytes"] = 65536,
                }, 65536);
            Assert.False(measure.IsError == true, Text(measure));
            var artifactId = ArtifactId(Envelope(measure))!;
            var metrics = await RecordStudyCallAsync(session, calls, "named-set-attribution", "sqlharness_artifact",
                new Dictionary<string, object?> { ["id"] = artifactId, ["section"] = "metrics", ["maxResultBytes"] = 65536 }, 65536);
            Assert.False(metrics.IsError == true, Text(metrics));
            var variants = Envelope(metrics).GetProperty("result").GetProperty("variants").EnumerateArray().ToArray();
            Assert.Equal(new[] { "small", "medium", "large", "edge" }, variants.Select(item => item.GetProperty("name").GetString()).ToArray());
            Assert.All(variants, item => Assert.Single(item.GetProperty("logicalReadsByTable").EnumerateObject()));

            var setStatements = new List<JsonElement>();
            int? setCursor = null;
            do
            {
                var arguments = new Dictionary<string, object?> { ["id"] = artifactId, ["section"] = "statements", ["pageSize"] = 32, ["maxResultBytes"] = 65536 };
                if (setCursor is int current)
                    arguments["cursor"] = current;
                var statements = await RecordStudyCallAsync(session, calls, "named-set-attribution", "sqlharness_artifact", arguments, 65536);
                Assert.False(statements.IsError == true, Text(statements));
                setStatements.AddRange(Envelope(statements).GetProperty("result").GetProperty("statements").EnumerateArray().Select(item => item.Clone()));
                setCursor = Continuation(Envelope(statements));
            }
            while (setCursor is not null);
            Assert.Equal(48, setStatements.Count);
            Assert.Equal(4, setStatements.Select(item => item.GetProperty("parameterSet").GetString()).Distinct(StringComparer.Ordinal).Count());
            Assert.All(new[] { "small", "medium", "large", "edge" }, name =>
                Assert.Equal(12, setStatements.Count(item => item.GetProperty("parameterSet").GetString() == name)));
            Assert.All(new[] { "small", "medium", "large", "edge" }, name =>
                Assert.Equal(new[] { 1, 2, 3 }, setStatements.Where(item => item.GetProperty("parameterSet").GetString() == name).Select(item => item.GetProperty("repetition").GetInt32()).Distinct().Order()));
            await session.ShutdownAsync();
            session.AssertNoDatabaseConnections();
        }

        await using (var session = await Session.StartAsync(WorkflowScenarios.ArtifactReviewStudyId, Budget))
        {
            var query = await RecordStudyCallAsync(session, calls, "late-result-assertions", "sqlharness_query",
                new Dictionary<string, object?>
                {
                    ["sql"] = WorkflowScenarios.ArtifactReviewQuerySql,
                    ["maxRows"] = 96,
                    ["maxResultBytes"] = 16384,
                    ["maxCellChars"] = 128,
                }, 16384);
            Assert.False(query.IsError == true, Text(query));
            var result = Envelope(query).GetProperty("result");
            var resultSets = result.GetProperty("resultSets").EnumerateArray().ToArray();
            Assert.Equal(3, resultSets.Length);
            Assert.Equal(96, resultSets[0].GetProperty("rows").GetArrayLength());
            Assert.Contains("assertion: totals", resultSets[1].GetRawText(), StringComparison.Ordinal);
            Assert.Contains("assertion: final count", resultSets[2].GetRawText(), StringComparison.Ordinal);
            await session.ShutdownAsync();
            session.AssertNoDatabaseConnections();
        }

        capture["equivalentEvidence"] = new Dictionary<string, object?>
        {
            ["batchStatements"] = 4,
            ["setupStatements"] = 1,
            ["expectedSyntheticSetupInvocations"] = parameterSets.Length,
            ["setupInvocationsObserved"] = false,
            ["namedSets"] = 4,
            ["repetitionsPerSet"] = 3,
            ["candidateRevisions"] = 2,
            ["assertionResultSetsAfterLargeFirst"] = 2,
            ["candidateRevisionStatementHashesDiffer"] = !revision1Hashes.SequenceEqual(revision2Hashes, StringComparer.Ordinal),
        };
        var json = JsonSerializer.Serialize(capture, JsonOptions);
        var captureDirectory = Environment.GetEnvironmentVariable("SQLHARNESS_109_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
            File.WriteAllText(Path.Combine(captureDirectory, "task-1-sdk-baseline.json"), json.TrimEnd() + "\n");
        }
        var capturedCalls = calls.Cast<Dictionary<string, object?>>().ToArray();
        _output.WriteLine($"Plan 109 Task 1: SDK calls={calls.Count}; request wire UTF-8 bytes={capturedCalls.Sum(call => (int)call["requestWireUtf8Bytes"]!)}; response wire UTF-8 bytes={capturedCalls.Sum(call => (int)call["responseWireUtf8Bytes"]!)}; equivalent cases=4 statements, 4 named sets x 3 repetitions, 2 candidate revisions, 2 late assertion result sets.");
    }

    [Fact]
    public async Task Query_result_set_selector_returns_original_non_prefix_ordinals_and_complete_selection_metadata()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.ArtifactReviewStudyId, Budget);
        var baseline = await session.Call("sqlharness_query", new Dictionary<string, object?>
        {
            ["sql"] = WorkflowScenarios.ArtifactReviewQuerySql,
            ["parameters"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "batch", ["type"] = "int", ["value"] = "7" },
            },
            ["maxRows"] = 96,
            ["maxResultBytes"] = 16384,
            ["maxCellChars"] = 128,
        });
        Assert.False(baseline.IsError == true, Text(baseline));
        var baselineQuery = Envelope(baseline).GetProperty("result");
        Assert.False(baselineQuery.TryGetProperty("sourceResultSetCount", out _));
        Assert.False(baselineQuery.TryGetProperty("selectedResultSetOrdinals", out _));
        Assert.False(baselineQuery.TryGetProperty("queryHash", out _));

        var result = await session.Call("sqlharness_query", new Dictionary<string, object?>
        {
            ["sql"] = WorkflowScenarios.ArtifactReviewQuerySql,
            ["parameters"] = new object[]
            {
                new Dictionary<string, object?> { ["name"] = "batch", ["type"] = "int", ["value"] = "7" },
            },
            ["resultSetOrdinals"] = new[] { 2, 1 },
            ["maxRows"] = 96,
            ["maxResultBytes"] = 16384,
            ["maxCellChars"] = 128,
        });

        Assert.False(result.IsError == true, Text(result));
        var envelope = Envelope(result);
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        var query = envelope.GetProperty("result");
        Assert.Equal(3, query.GetProperty("sourceResultSetCount").GetInt32());
        Assert.Equal(new[] { 1, 2 }, query.GetProperty("selectedResultSetOrdinals").EnumerateArray()
            .Select(value => value.GetInt32()).ToArray());
        Assert.Equal("00D103C2108B43BD0DB3DB26069E9D5DAF30B5642FF4D39EECBC0BD6B7F60AAC",
            query.GetProperty("queryHash").GetString());
        Assert.Equal(new[] { 1, 2 }, query.GetProperty("resultSets").EnumerateArray()
            .Select(value => value.GetProperty("ordinal").GetInt32()).ToArray());
        Assert.Equal(1, query.GetProperty("omittedResultSetCount").GetInt32());
        Assert.Contains("assertion: totals", query.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("assertion: final count", query.GetRawText(), StringComparison.Ordinal);
        var fullResponseBytes = McpResultAdapter.MeasureBytes(baseline);
        var selectedResponseBytes = McpResultAdapter.MeasureBytes(result);
        Assert.True(selectedResponseBytes < fullResponseBytes);
        _output.WriteLine($"Plan 115 complete bounded MCP response UTF-8 bytes: full={fullResponseBytes}, selected={selectedResponseBytes}, delta={selectedResponseBytes - fullResponseBytes}; source ordinals=1,2; fixture=Plan 109 frozen three-set assertion query.");

        foreach (var ordinal in new[] { 0, 1, 2 })
        {
            var single = await session.Call("sqlharness_query", new Dictionary<string, object?>
            {
                ["sql"] = WorkflowScenarios.ArtifactReviewQuerySql,
                ["resultSetOrdinals"] = new[] { ordinal },
                ["maxRows"] = 96,
                ["maxResultBytes"] = 16384,
                ["maxCellChars"] = 128,
            });
            Assert.False(single.IsError == true, Text(single));
            var singleQuery = Envelope(single).GetProperty("result");
            Assert.Equal(new[] { ordinal }, singleQuery.GetProperty("selectedResultSetOrdinals").EnumerateArray()
                .Select(value => value.GetInt32()).ToArray());
            Assert.Equal(ordinal, Assert.Single(singleQuery.GetProperty("resultSets").EnumerateArray())
                .GetProperty("ordinal").GetInt32());
        }

        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
        var calls = session.WorkflowHostStats().GetProperty("queryCalls").EnumerateArray().ToArray();
        Assert.Equal(5, calls.Length);
        foreach (var call in calls.Take(2))
        {
            Assert.Equal(WorkflowScenarios.ArtifactReviewQuerySql, call.GetProperty("sql").GetString());
            var parameter = Assert.Single(call.GetProperty("parameters").EnumerateArray());
            Assert.Equal("batch", parameter.GetProperty("name").GetString());
            Assert.Equal("int", parameter.GetProperty("type").GetString());
            Assert.Equal("7", parameter.GetProperty("value").GetString());
        }
    }

    [Fact]
    public async Task Query_result_set_selector_distinguishes_empty_selection_from_invalid_and_out_of_range_ordinals()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.ArtifactReviewStudyId, Budget);
        var empty = await session.Call("sqlharness_query", new Dictionary<string, object?>
        {
            ["sql"] = WorkflowScenarios.ArtifactReviewQuerySql,
            ["resultSetOrdinals"] = Array.Empty<int>(),
        });
        Assert.False(empty.IsError == true, Text(empty));
        var emptyQuery = Envelope(empty).GetProperty("result");
        Assert.Equal("success", Envelope(empty).GetProperty("status").GetString());
        Assert.Empty(emptyQuery.GetProperty("resultSets").EnumerateArray());
        Assert.Equal(3, emptyQuery.GetProperty("sourceResultSetCount").GetInt32());
        Assert.Empty(emptyQuery.GetProperty("selectedResultSetOrdinals").EnumerateArray());
        Assert.Equal(3, emptyQuery.GetProperty("omittedResultSetCount").GetInt32());

        foreach (var ordinals in new[] { new[] { 1, 1 }, new[] { -1 }, Enumerable.Range(0, 65).ToArray() })
        {
            var invalid = await session.Call("sqlharness_query", new Dictionary<string, object?>
            {
                ["sql"] = WorkflowScenarios.ArtifactReviewQuerySql,
                ["resultSetOrdinals"] = ordinals,
            });
            Assert.True(invalid.IsError == true, Text(invalid));
            Assert.Equal("error", Envelope(invalid).GetProperty("status").GetString());
            Assert.Contains("resultSetOrdinals", Envelope(invalid).GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        var outOfRange = await session.Call("sqlharness_query", new Dictionary<string, object?>
        {
            ["sql"] = WorkflowScenarios.ArtifactReviewQuerySql,
            ["resultSetOrdinals"] = new[] { 3 },
        });
        Assert.True(outOfRange.IsError == true, Text(outOfRange));
        Assert.Contains("source result-set count (3)", Envelope(outOfRange).GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
        Assert.Equal(2, session.WorkflowHostStats().GetProperty("queryCalls").GetArrayLength());
    }

    [Fact]
    public void Artifact_review_study_compares_deterministic_equivalent_evidence_renderers()
    {
        var repositoryRoot = McpStdioProcessHarness.FindRepositoryRoot();
        var baselinePath = Path.Combine(repositoryRoot, "plans", "109-evidence", "task-1-sdk-baseline.json");
        using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));
        var fixtureInputs = baseline.RootElement.GetProperty("fixtureInputs");
        var calls = baseline.RootElement.GetProperty("calls").EnumerateArray().ToArray();

        JsonElement Call(string caseName, string tool, string section = "") => calls.Single(call =>
            call.GetProperty("case").GetString() == caseName
            && call.GetProperty("tool").GetString() == tool
            && (section.Length == 0 || call.GetProperty("selectors").GetProperty("section").GetString() == section));

        long ExistingBytes(IEnumerable<JsonElement> selected) => selected.Sum(call => call.GetProperty("modelVisibleUtf8Bytes").GetInt64());
        int Utf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value).Length;
        JsonElement Envelope(JsonElement call) => JsonDocument.Parse(call.GetProperty("text").GetString()!).RootElement.Clone();
        JsonElement Result(JsonElement call) => Envelope(call).GetProperty("result").Clone();

        object RenderAssertionEvidence(JsonElement envelope, string queryHash)
        {
            var status = envelope.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            var resultElement = default(JsonElement);
            var hasResult = status == "success" && envelope.TryGetProperty("result", out resultElement);
            var database = hasResult && resultElement.TryGetProperty("database", out var databaseElement)
                ? databaseElement.GetString()
                : null;
            var resultSetElement = default(JsonElement);
            var hasResultSets = hasResult
                && resultElement.TryGetProperty("resultSets", out resultSetElement)
                && resultSetElement.ValueKind == JsonValueKind.Array;
            var resultSets = hasResultSets
                ? resultSetElement.EnumerateArray().Select(set => set.Clone()).Where(set => set.GetProperty("ordinal").GetInt32() is 1 or 2).ToArray()
                : [];
            JsonElement? error = envelope.TryGetProperty("error", out var errorElement) ? errorElement.Clone() : null;
            var evidenceState = status == "error"
                ? "error"
                : status != "success" || !hasResultSets
                    ? "incomplete"
                    : resultSetElement.GetArrayLength() == 0 ? "success-empty" : "success";
            int? sourceResultSetCount = hasResultSets ? resultSetElement.GetArrayLength() : null;
            return new { evidenceState, status, database, error, queryHash, sourceResultSetCount, selectedResultSetCount = resultSets.Length, resultSets };
        }

        var revisionCalls = calls.Where(call => call.GetProperty("case").GetString() is "candidate-revision-1" or "candidate-revision-2").ToArray();
        var namedSetCalls = calls.Where(call => call.GetProperty("case").GetString() == "named-set-attribution").ToArray();
        var queryCall = Call("late-result-assertions", "sqlharness_query");
        var queryEnvelope = Envelope(queryCall);
        var queryResult = Result(queryCall);
        var querySets = queryResult.GetProperty("resultSets").EnumerateArray().Select(element => element.Clone()).ToArray();
        var selectedAssertions = querySets.Where(set => set.GetProperty("ordinal").GetInt32() is 1 or 2).ToArray();
        var queryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fixtureInputs.GetProperty("querySql").GetString()!)));
        var assertionEvidence = RenderAssertionEvidence(queryEnvelope, queryHash);
        var assertionProjection = JsonSerializer.SerializeToElement(assertionEvidence);
        Assert.Equal("success", assertionProjection.GetProperty("status").GetString());
        Assert.Equal("fixturedb", assertionProjection.GetProperty("database").GetString());
        Assert.Equal(3, assertionProjection.GetProperty("sourceResultSetCount").GetInt32());
        Assert.Equal(2, assertionProjection.GetProperty("selectedResultSetCount").GetInt32());
        Assert.Contains("assertion: totals", assertionProjection.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("assertion: final count", assertionProjection.GetRawText(), StringComparison.Ordinal);

        using var emptySuccessDocument = JsonDocument.Parse("""{"schemaVersion":3,"status":"success","result":{"database":"fixturedb","resultSets":[]}}""");
        var emptySuccessProjection = JsonSerializer.SerializeToElement(RenderAssertionEvidence(emptySuccessDocument.RootElement, queryHash));
        using var errorDocument = JsonDocument.Parse("""{"schemaVersion":3,"status":"error","error":{"code":"FixtureFailure","message":"synthetic failure"}}""");
        var errorProjection = JsonSerializer.SerializeToElement(RenderAssertionEvidence(errorDocument.RootElement, queryHash));
        using var missingResultDocument = JsonDocument.Parse("""{"schemaVersion":3,"status":"success"}""");
        var missingResultProjection = JsonSerializer.SerializeToElement(RenderAssertionEvidence(missingResultDocument.RootElement, queryHash));
        Assert.Equal("success", emptySuccessProjection.GetProperty("status").GetString());
        Assert.Equal("fixturedb", emptySuccessProjection.GetProperty("database").GetString());
        Assert.Equal("success-empty", emptySuccessProjection.GetProperty("evidenceState").GetString());
        Assert.Equal(0, emptySuccessProjection.GetProperty("sourceResultSetCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, emptySuccessProjection.GetProperty("error").ValueKind);
        Assert.Equal("error", errorProjection.GetProperty("status").GetString());
        Assert.Equal("error", errorProjection.GetProperty("evidenceState").GetString());
        Assert.Equal(JsonValueKind.Null, errorProjection.GetProperty("database").ValueKind);
        Assert.Equal(JsonValueKind.Null, errorProjection.GetProperty("sourceResultSetCount").ValueKind);
        Assert.Equal("FixtureFailure", errorProjection.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("incomplete", missingResultProjection.GetProperty("evidenceState").GetString());
        Assert.Equal(JsonValueKind.Null, missingResultProjection.GetProperty("sourceResultSetCount").ValueKind);

        var statementCalls = revisionCalls.Concat(namedSetCalls)
            .Where(call => call.GetProperty("selectors").TryGetProperty("section", out var section) && section.GetString() == "statements")
            .ToArray();
        var compactRecords = statementCalls.SelectMany(call =>
        {
            var result = Result(call);
            var artifactId = result.GetProperty("artifactId").GetString();
            return result.GetProperty("statements").EnumerateArray().Select(item => new
            {
                artifactId,
                variant = item.GetProperty("variant").GetString(),
                parameterSet = item.TryGetProperty("parameterSet", out var parameterSet) ? parameterSet.GetString() : null,
                statementOrdinal = item.GetProperty("statementOrdinal").GetInt32(),
                statementHash = item.GetProperty("statementHash").GetString(),
                repetition = (int?)null,
                cpuTimeMilliseconds = item.GetProperty("cpuTimeMilliseconds").Clone(),
                elapsedTimeMilliseconds = item.GetProperty("elapsedTimeMilliseconds").Clone(),
            });
        }).ToArray();
        Assert.Equal(32, compactRecords.Length);
        Assert.All(statementCalls.SelectMany(call => Result(call).GetProperty("statements").EnumerateArray()), item =>
            Assert.True(item.GetProperty("topOperators").GetArrayLength() > 0));

        var metricsCall = Call("named-set-attribution", "sqlharness_artifact", "metrics");
        var metrics = Result(metricsCall);
        var metricsArtifactId = metrics.GetProperty("artifactId").GetString()!;
        var metricsArtifactKind = metrics.GetProperty("artifactKind").GetString();
        var namedSetMetrics = metrics.GetProperty("variants").EnumerateArray().Select(item => new
        {
            parameterSet = item.GetProperty("name").GetString(),
            cpuTimeMilliseconds = item.GetProperty("cpuTimeMilliseconds").Clone(),
            elapsedTimeMilliseconds = item.GetProperty("elapsedTimeMilliseconds").Clone(),
            logicalReads = item.GetProperty("logicalReads").Clone(),
            logicalReadsByTable = item.GetProperty("logicalReadsByTable").Clone(),
            warnings = item.GetProperty("warnings").Clone(),
        }).ToArray();
        Assert.Equal(new[] { "small", "medium", "large", "edge" }, namedSetMetrics.Select(item => item.parameterSet));

        var metricsPointers = namedSetMetrics.Select(item => new
        {
            artifactId = metricsArtifactId,
            section = "metrics",
            parameterSet = item.parameterSet,
        }).ToArray();

        var setupSql = fixtureInputs.GetProperty("setupSql").GetString()!;
        var batchSql = fixtureInputs.GetProperty("batchSql").GetString()!;
        var inBatchSql = setupSql + batchSql;
        var tvpForm = new
        {
            sql = "SELECT o.Id, o.Region FROM dbo.Orders AS o JOIN @Batches AS b ON b.Batch = o.Batch; SELECT COUNT_BIG(*) FROM dbo.Orders; SELECT Id FROM dbo.Customers; SELECT SUM(Amount) FROM dbo.Payments;",
            parameter = new { name = "Batches", type = "dbo.BatchIds READONLY", rows = new[] { new { Batch = 1 }, new { Batch = 2 }, new { Batch = 3 }, new { Batch = 4 } } },
        };

        var existingSequences = new[]
        {
            new { name = "candidate-revision-1", calls = revisionCalls.Where(call => call.GetProperty("case").GetString() == "candidate-revision-1").ToArray() },
            new { name = "candidate-revision-2", calls = revisionCalls.Where(call => call.GetProperty("case").GetString() == "candidate-revision-2").ToArray() },
            new { name = "named-set-attribution", calls = namedSetCalls },
            new { name = "late-result-assertions", calls = new[] { queryCall } },
        }.Select(path => new
        {
            path.name,
            callCount = path.calls.Length,
            modelVisibleUtf8Bytes = ExistingBytes(path.calls),
            estimatedTokensByteDiv4 = ExistingBytes(path.calls) / 4m,
            requestWireUtf8Bytes = path.calls.Sum(call => call.GetProperty("requestWireUtf8Bytes").GetInt64()),
            responseWireUtf8Bytes = path.calls.Sum(call => call.GetProperty("responseWireUtf8Bytes").GetInt64()),
            selectors = path.calls.Select(call => new
            {
                tool = call.GetProperty("tool").GetString(),
                selectors = call.GetProperty("selectors").Clone(),
                modelVisibleUtf8Bytes = call.GetProperty("modelVisibleUtf8Bytes").GetInt64(),
                continuation = call.GetProperty("continuation").Clone(),
                omittedItems = call.GetProperty("omittedItems").Clone(),
                truncation = call.GetProperty("truncation").Clone(),
            }).ToArray(),
        }).ToArray();

        var compactBytes = Utf8Bytes(new { records = compactRecords });
        var metricsBytes = Utf8Bytes(new { artifactId = metricsArtifactId, artifactKind = metricsArtifactKind, variants = namedSetMetrics });
        var pointerBytes = Utf8Bytes(new { pointers = metricsPointers });
        var assertionBytes = Utf8Bytes(assertionEvidence);
        var fullStatementBytes = ExistingBytes(statementCalls);
        var fullMetricsBytes = metricsCall.GetProperty("modelVisibleUtf8Bytes").GetInt64();
        var fullQueryBytes = queryCall.GetProperty("modelVisibleUtf8Bytes").GetInt64();

        var output = new
        {
            study = "plan-109-task-2",
            source = "plans/109-evidence/task-1-sdk-baseline.json",
            renderer = "deterministic test-only JSON projections over retained real SDK output",
            inputIdentities = new
            {
                batchSqlSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fixtureInputs.GetProperty("batchSql").GetString()!))),
                querySqlSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fixtureInputs.GetProperty("querySql").GetString()!))),
                parameterSets = fixtureInputs.GetProperty("parameterSets").Clone(),
            },
            actualTokenizerTokenCount = (int?)null,
            byteDiv4Estimate = "model-visible UTF-8 bytes / 4; rough estimate only",
            existingRequestSequences = existingSequences,
            candidates = new object[]
            {
                new { name = "compact-per-statement-cpu-elapsed", modelVisibleUtf8Bytes = compactBytes, estimatedTokensByteDiv4 = compactBytes / 4m, records = compactRecords.Length, evidenceCompleteFor = new[] { "artifact", "variant", "parameterSet", "statementOrdinal", "statementHash", "CPU", "elapsed" }, omitted = new[] { "TopOperators", "other statement metrics" }, equivalentToBaselineForNamedFields = true },
                new { name = "named-set-metrics-values", modelVisibleUtf8Bytes = metricsBytes, estimatedTokensByteDiv4 = metricsBytes / 4m, records = namedSetMetrics.Length, evidenceCompleteFor = new[] { "artifactId", "parameterSet", "logicalReadsByTable" }, omitted = new[] { "statement-level CPU/elapsed and plans" }, equivalentToBaselineForNamedFields = true },
                new { name = "named-set-metrics-pointers-only", modelVisibleUtf8Bytes = pointerBytes, estimatedTokensByteDiv4 = pointerBytes / 4m, records = metricsPointers.Length, evidenceCompleteFor = new[] { "artifactId", "section", "parameterSet navigation" }, omitted = new[] { "metric values until dereferenced" }, equivalentToBaselineForValues = false, requires = "a named-set selector or consumer-side dereference; current baseline metrics response contains all variants" },
                new { name = "assertion-result-sets-only", modelVisibleUtf8Bytes = assertionBytes, estimatedTokensByteDiv4 = assertionBytes / 4m, records = selectedAssertions.Length, evidenceCompleteFor = new[] { "success status", "target database", "source and selected result-set counts", "query hash", "result-set ordinals", "both assertion result sets" }, omitted = new[] { "first result set (96 rows)", "unneeded query envelope details" }, equivalentForAssertionCheck = true, requires = "result-set selection or deterministic consumer projection" },
            },
            assertionProjection,
            assertionBoundary = new
            {
                validEmpty = emptySuccessProjection,
                error = errorProjection,
                incomplete = missingResultProjection,
                distinction = "valid empty retains evidenceState=success-empty, status=success, database identity and sourceResultSetCount=0; failure retains status=error and structured error; absent result data is incomplete, not empty success",
            },
            equivalentEvidenceComparisons = new[]
            {
                new { scope = "statement CPU/elapsed discovery", baselineModelVisibleUtf8Bytes = fullStatementBytes, candidateModelVisibleUtf8Bytes = compactBytes, deltaBytes = compactBytes - fullStatementBytes, equivalent = true, omission = "operators and other statement metrics", candidateClipping = "not applied by deterministic renderer", baselineContinuationOrOmission = "none" },
                new { scope = "all named-set metric values", baselineModelVisibleUtf8Bytes = fullMetricsBytes, candidateModelVisibleUtf8Bytes = metricsBytes, deltaBytes = metricsBytes - fullMetricsBytes, equivalent = true, omission = "none from metrics section; statement page is separate evidence", candidateClipping = "not applied by deterministic renderer", baselineContinuationOrOmission = "none" },
                new { scope = "assertion check from result sets 1 and 2", baselineModelVisibleUtf8Bytes = fullQueryBytes, candidateModelVisibleUtf8Bytes = assertionBytes, deltaBytes = assertionBytes - fullQueryBytes, equivalent = true, omission = "unneeded first result set and query envelope details", candidateClipping = "not applied by deterministic renderer", baselineContinuationOrOmission = "none" },
                new { scope = "named-set metrics pointer only", baselineModelVisibleUtf8Bytes = fullMetricsBytes, candidateModelVisibleUtf8Bytes = pointerBytes, deltaBytes = pointerBytes - fullMetricsBytes, equivalent = false, omission = "all metric values until dereferenced", candidateClipping = "not applied by deterministic renderer", baselineContinuationOrOmission = "none" },
            },
            identities = new
            {
                candidateRevisionArtifactIds = revisionCalls.Where(call => call.GetProperty("tool").GetString() == "sqlharness_compare").Select(call => Result(call).GetProperty("artifactId").GetString()).ToArray(),
                candidateStatementHashes = revisionCalls.Where(call => call.GetProperty("tool").GetString() == "sqlharness_artifact" && call.GetProperty("selectors").TryGetProperty("section", out var section) && section.GetString() == "statements")
                    .Select(call => new { artifactId = call.GetProperty("selectors").GetProperty("id").GetString(), statements = Result(call).GetProperty("statements").EnumerateArray().Where(item => item.GetProperty("variant").GetString() == "candidate").Select(item => new { ordinal = item.GetProperty("statementOrdinal").GetInt32(), hash = item.GetProperty("statementHash").GetString() }).ToArray() }).ToArray(),
                reuseRule = "artifactId + variant + parameterSet + statementOrdinal/hash + repetition; compact statement records do not carry repetition and therefore are not reusable repetition evidence; identical SQL text across candidate artifact revisions is not reused because artifact identity differs",
            },
            setupComparison = new
            {
                existingSetupSql = setupSql,
                measuredBatchSql = batchSql,
                existingSetupIsSeparateInput = !batchSql.Contains(setupSql, StringComparison.Ordinal),
                setupPrependedBatch = inBatchSql,
                setupPrependedBatchPreservesMeasuredSql = inBatchSql == batchSql,
                setupInvocationCardinality = new { expectedSyntheticSetupInvocations = fixtureInputs.GetProperty("parameterSets").GetArrayLength(), observed = false },
                cannotSubtractSetupFromWholeBatchCounters = true,
                tvpAlternative = tvpForm,
                tvpPreservesCurrentScalarInputAndCompilationForm = false,
                conclusion = "retain existing separate setup field for this fixture; a new in-batch mode or scalar-to-TVP conversion changes submitted/compiled SQL, and no attributable counters support subtraction",
            },
            ranking = new
            {
                bestExistingSequenceByScope = new[]
                {
                    new { scope = "candidate revision review", minimumModelVisibleUtf8Bytes = existingSequences.Where(path => path.name.StartsWith("candidate-revision-", StringComparison.Ordinal)).Min(path => path.modelVisibleUtf8Bytes), sequences = existingSequences.Where(path => path.name.StartsWith("candidate-revision-", StringComparison.Ordinal)).OrderBy(path => path.modelVisibleUtf8Bytes).Select(path => new { path = path.name, callCount = path.callCount, modelVisibleUtf8Bytes = path.modelVisibleUtf8Bytes }).ToArray() },
                    new { scope = "named-set attribution", minimumModelVisibleUtf8Bytes = existingSequences.Where(path => path.name == "named-set-attribution").Min(path => path.modelVisibleUtf8Bytes), sequences = existingSequences.Where(path => path.name == "named-set-attribution").Select(path => new { path = path.name, callCount = path.callCount, modelVisibleUtf8Bytes = path.modelVisibleUtf8Bytes }).ToArray() },
                    new { scope = "late result assertions", minimumModelVisibleUtf8Bytes = existingSequences.Where(path => path.name == "late-result-assertions").Min(path => path.modelVisibleUtf8Bytes), sequences = existingSequences.Where(path => path.name == "late-result-assertions").Select(path => new { path = path.name, callCount = path.callCount, modelVisibleUtf8Bytes = path.modelVisibleUtf8Bytes }).ToArray() },
                },
                crossScopeRanking = "not computed; these sequences serve different evidence scopes",
                candidateComparisons = "compared only with the existing output for the matching evidence scope; pointer-only is excluded because it is incomplete",
            },
        };

        Assert.True(compactBytes < fullStatementBytes, $"Compact renderer bytes {compactBytes} should be below full statement bytes {fullStatementBytes}.");
        Assert.True(assertionBytes < fullQueryBytes);
        Assert.Equal(2, selectedAssertions.Length);
        Assert.Contains("assertion: totals", selectedAssertions[0].GetRawText(), StringComparison.Ordinal);
        Assert.Contains("assertion: final count", selectedAssertions[1].GetRawText(), StringComparison.Ordinal);
        Assert.True(setupSql == "SET NOCOUNT ON;" && !batchSql.Contains(setupSql, StringComparison.Ordinal));
        Assert.NotEqual(batchSql, tvpForm.sql);

        var outputJson = JsonSerializer.Serialize(output, JsonOptions);
        var captureDirectory = Environment.GetEnvironmentVariable("SQLHARNESS_109_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
            File.WriteAllText(Path.Combine(captureDirectory, "task-2-candidate-study.json"), outputJson.TrimEnd() + "\n");
        }
        _output.WriteLine($"Plan 109 Task 2: existing paths={existingSequences.Sum(path => path.callCount)} calls; compact CPU/elapsed={compactBytes} bytes; metrics values={metricsBytes} bytes; pointer-only={pointerBytes} bytes (incomplete until dereferenced); assertion sets={assertionBytes} bytes; actual tokenizer unavailable.");
    }

    private static async Task<CallToolResult> RecordStudyCallAsync(
        Session session, List<object> calls, string studyCase, string tool, Dictionary<string, object?> arguments, int budget)
    {
        var (result, requestBytes, responseBytes) = await session.CallWithWireBytes(tool, arguments);
        var text = Text(result).TrimEnd();
        var visibleBytes = Encoding.UTF8.GetByteCount(text);
        Assert.InRange(visibleBytes, 1, budget);
        var envelope = Envelope(result);
        calls.Add(new Dictionary<string, object?>
        {
            ["case"] = studyCase,
            ["tool"] = tool,
            ["selectors"] = arguments.Where(pair => pair.Key is "id" or "section" or "variant" or "statement" or "parameterSet" or "parameterSets" or "repetition" or "includeText" or "cursor" or "pageSize" or "maxResultBytes" or "maxRows" or "maxCellChars")
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            ["requestWireUtf8Bytes"] = requestBytes,
            ["responseWireUtf8Bytes"] = responseBytes,
            ["modelVisibleUtf8Bytes"] = visibleBytes,
            ["estimatedTokensByteDiv4"] = visibleBytes / 4m,
            ["actualTokenizerTokenCount"] = null,
            ["continuation"] = Continuation(envelope),
            ["omittedItems"] = Omitted(envelope),
            ["truncation"] = envelope.TryGetProperty("truncation", out var truncation) ? truncation.Clone() : null,
            ["text"] = text,
        });
        return result;
    }

    private static async Task<PlanCompletion> ReadPlanCompletionAsync(Session session, string artifactId, int? pageSize, int? maxResultBytes)
    {
        var calls = new List<PlanCallCost>();
        var nodePaths = new List<string>();
        var nodeRecords = new List<string>();
        int? cursor = null;
        do
        {
            Assert.True(calls.Count < 513, "Plan completion exceeded the one-item-per-call traversal bound.");
            var requestedCursor = cursor;
            var arguments = new Dictionary<string, object?>
            {
                ["id"] = artifactId,
                ["section"] = "plan",
                ["variant"] = "candidate",
                ["statement"] = 0,
            };
            if (cursor is int current)
                arguments["cursor"] = current;
            if (pageSize is int requestedPageSize)
                arguments["pageSize"] = requestedPageSize;
            if (maxResultBytes is int requestedBudget)
                arguments["maxResultBytes"] = requestedBudget;

            var (result, requestBytes, responseBytes) = await session.CallWithWireBytes("sqlharness_artifact", arguments);
            Assert.False(result.IsError == true, Text(result));
            var text = Text(result);
            var textBytes = Encoding.UTF8.GetByteCount(text);
            Assert.InRange(textBytes, 1, maxResultBytes ?? McpLimits.DefaultCallToolResultBudgetBytes);
            var envelope = Envelope(result);
            var plan = envelope.GetProperty("result");
            Assert.Equal("candidate", plan.GetProperty("variant").GetString());
            Assert.Equal(0, plan.GetProperty("statement").GetInt32());
            Assert.True(plan.TryGetProperty("nodes", out var nodes), plan.GetRawText());
            foreach (var node in nodes.EnumerateArray())
            {
                nodePaths.Add(node.GetProperty("nodePath").GetString()!);
                nodeRecords.Add(node.GetRawText());
            }
            cursor = Continuation(envelope);
            if (cursor is int nextCursor)
            {
                Assert.NotEmpty(nodes.EnumerateArray());
                Assert.True(requestedCursor is null || nextCursor > requestedCursor,
                    $"Plan continuation did not advance: requested {requestedCursor}, received {nextCursor}.");
            }
            calls.Add(new PlanCallCost(requestBytes, responseBytes, textBytes));
        }
        while (cursor is not null);

        return new PlanCompletion(nodePaths, nodeRecords, calls);
    }

    private sealed record PlanCompletion(IReadOnlyList<string> NodePaths, IReadOnlyList<string> NodeRecords, IReadOnlyList<PlanCallCost> Calls);
    private sealed record PlanCallCost(int RequestWireUtf8Bytes, int ResponseWireUtf8Bytes, int ResponseTextUtf8Bytes);

    [Fact]
    public async Task Request_scoped_host_maps_the_synthetic_profile()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.RequestScopeId, Budget);
        var capabilities = await session.Call("sqlharness_capabilities", new Dictionary<string, object?>());
        Assert.False(capabilities.IsError == true, Text(capabilities));
        Assert.Equal("request", Envelope(capabilities).GetProperty("result").GetProperty("scopeMode").GetString());

        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.NoImprovementBaselineSql,
            WorkflowScenarios.NoImprovementCandidateSql,
            requestScope: true));
        Assert.False(compare.IsError == true, Text(compare));
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Child_process_stdout_is_pure_protocol_and_records_zero_connections()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.UnavailableMetricsId, Budget);
        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.UnavailableBaselineSql, WorkflowScenarios.UnavailableCandidateSql));
        Assert.False(compare.IsError == true, Text(compare));
        var text = Text(compare);
        Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.4", text, StringComparison.Ordinal);

        await session.ShutdownAsync();
        McpStdioProcessHarness.AssertStdoutIsPureProtocol(session.Child.StdoutTee.Recorded);
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Scripted_workflows_fit_4096_and_65536_and_freeze_portable_records()
    {
        var identities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "SqlHarness.Mcp.WorkflowHost",
            ["manifest"] = "tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/manifest.json",
        };
        var recordsDir = Path.Combine(
            McpStdioProcessHarness.FindRepositoryRoot(),
            "tests", "SqlHarness.Mcp.Tests", "Fixtures", "OptimizationWorkflows", "records");
        // These records freeze the current MCP contract, including per-repetition statement identities.
        Directory.CreateDirectory(recordsDir);

        foreach (var scenario in ScriptedScenarios)
        {
            foreach (var budget in ScriptedBudgets)
            {
                await using var session = await Session.StartAsync(scenario, Budget);
                var discovery = await session.Client.ListToolsAsync(cancellationToken: session.Token);
                var discoveryBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                    discovery.Select(tool => tool.ProtocolTool).ToArray()));
                Assert.InRange(discoveryBytes, 1, McpLimits.ToolsListBudgetBytes);
                var calls = new List<object>();
                var artifactId = await RunScriptedAsync(session, scenario, budget, calls);
                Assert.False(string.IsNullOrWhiteSpace(artifactId),
                    $"{scenario}@{budget}: no artifact ID returned. Calls: {JsonSerializer.Serialize(calls, JsonOptions)}");

                await session.ShutdownAsync();
                var protocolBytes = session.Child.StdoutTee.Recorded.Length;
                var record = new Dictionary<string, object?>
                {
                    ["scenario"] = scenario,
                    ["budget"] = budget,
                    ["discoveryUtf8Bytes"] = discoveryBytes,
                    ["identities"] = identities,
                    ["calls"] = calls,
                };
                var json = Normalize(JsonSerializer.Serialize(record, JsonOptions), session.Home);
                AssertScenarioRecord(scenario, budget, json);
                var captureDirectory = Environment.GetEnvironmentVariable("SQLHARNESS_077_CAPTURE_DIR");
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    Directory.CreateDirectory(captureDirectory);
                    File.WriteAllText(Path.Combine(captureDirectory, $"{scenario}-{budget}.json"), json);
                }
                else
                {
                    var path = Path.Combine(recordsDir, $"{scenario}-{budget}.json");
                    Assert.True(File.Exists(path), $"Frozen current-contract record missing: {path}");
                    _ = Normalize(File.ReadAllText(path));
                    var annotationGolden = Path.Combine(AppContext.BaseDirectory, "Fixtures", "OperationAnnotations", "off", "results", $"{scenario}-{budget}.json");
                    Assert.True(File.Exists(annotationGolden), $"Operation-annotation off golden record missing: {annotationGolden}");
                    Assert.Equal(ComparableWorkflowRecord(Normalize(File.ReadAllText(annotationGolden))), ComparableWorkflowRecord(json));
                }
                _output.WriteLine($"{scenario}@{budget}: discovery={discoveryBytes} protocolFrameBytes={protocolBytes} (machine-local; omitted from snapshot) calls={calls.Count}");
                session.AssertNoDatabaseConnections();
            }
        }
    }

    private static async Task<string?> RunScriptedAsync(Session session, string scenario, int budget, List<object> calls)
    {
        string? artifactId = null;
        if (scenario == WorkflowScenarios.PartialMatrixId)
        {
            var compare = await RecordAsync(session, calls, "sqlharness_compare", MatrixArguments(budget), budget);
            artifactId = MatrixArtifactId(Envelope(compare)) ?? ArtifactId(Envelope(compare));
            var cursor = Continuation(Envelope(compare));
            while (cursor is int next)
            {
                var page = await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
                {
                    ["id"] = artifactId,
                    ["section"] = "matrix-cells",
                    ["cursor"] = next,
                }, McpLimits.DefaultCallToolResultBudgetBytes);
                cursor = Continuation(Envelope(page));
            }

            return artifactId;
        }

        var sql = scenario switch
        {
            WorkflowScenarios.NoImprovementId => (WorkflowScenarios.NoImprovementBaselineSql, WorkflowScenarios.NoImprovementCandidateSql),
            WorkflowScenarios.CollidingOperatorsId => (WorkflowScenarios.CollidingBaselineSql, WorkflowScenarios.CollidingCandidateSql),
            WorkflowScenarios.UnavailableMetricsId => (WorkflowScenarios.UnavailableBaselineSql, WorkflowScenarios.UnavailableCandidateSql),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var produced = await RecordAsync(session, calls, "sqlharness_compare", CompareArguments(sql.Item1, sql.Item2, budget: budget), budget);
        artifactId = ArtifactId(Envelope(produced));
        if (scenario == WorkflowScenarios.UnavailableMetricsId)
            return artifactId;

        if (scenario == WorkflowScenarios.CollidingOperatorsId)
        {
            await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = artifactId,
                ["section"] = "operators",
            }, McpLimits.DefaultCallToolResultBudgetBytes);
            await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = artifactId,
                ["section"] = "plan",
                ["variant"] = "candidate",
                ["statement"] = 1,
            }, McpLimits.DefaultCallToolResultBudgetBytes);
            return artifactId;
        }

        await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
        {
            ["id"] = artifactId,
            ["section"] = "summary",
        }, McpLimits.DefaultCallToolResultBudgetBytes);
        return artifactId;
    }

    private static async Task<CallToolResult> RecordAsync(
        Session session, List<object> calls, string tool, Dictionary<string, object?> arguments, int budget)
    {
        var result = await session.Call(tool, arguments);
        var text = Normalize(Text(result), session.Home);
        var storedText = text.TrimEnd();
        var visible = Encoding.UTF8.GetByteCount(storedText);
        Assert.InRange(visible, 1, budget);
        var continuation = Continuation(Envelope(result));
        calls.Add(new Dictionary<string, object?>
        {
            ["tool"] = tool,
            ["arguments"] = StripSql(arguments),
            ["isError"] = result.IsError == true,
            ["modelVisibleUtf8Bytes"] = visible,
            ["text"] = storedText,
            ["omittedItems"] = Omitted(Envelope(result)),
            ["continuation"] = continuation,
            ["terminal"] = result.IsError == true ? "error" : continuation is null ? "complete" : "continuation",
            ["selectedArtifacts"] = ArtifactId(Envelope(result)) is { } id ? new[] { id } : Array.Empty<string>(),
        });
        return result;
    }

    private static Dictionary<string, object?> CompareArguments(string baseline, string candidate, bool requestScope = false, int? budget = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["baseline"] = new Dictionary<string, object?> { ["sql"] = baseline },
            ["candidate"] = new Dictionary<string, object?> { ["sql"] = candidate },
            ["subject"] = "workflow-fixture",
            ["scope"] = "whole",
            ["repeat"] = 5,
        };
        if (requestScope)
        {
            arguments["target"] = new Dictionary<string, object?>
            {
                ["profile"] = WorkflowScenarios.SqlServerProfile,
                ["vars"] = new Dictionary<string, string>(),
            };
        }

        if (budget is not null)
            arguments["maxResultBytes"] = budget;
        return arguments;
    }

    private static Dictionary<string, object?> MatrixArguments(int budget)
    {
        var arguments = CompareArguments(WorkflowScenarios.PartialMatrixBaselineSql, WorkflowScenarios.PartialMatrixCandidateSql, budget: budget);
        arguments["matrix"] = new Dictionary<string, object?>
        {
            ["name"] = "BatchSize",
            ["type"] = "int",
            ["values"] = Enumerable.Range(1, 32).Select(value => value.ToString()).ToArray(),
        };
        return arguments;
    }

    private static Dictionary<string, object?> StripSql(Dictionary<string, object?> arguments)
    {
        var copy = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        foreach (var key in new[] { "baseline", "candidate" })
        {
            if (copy.TryGetValue(key, out var value) && value is Dictionary<string, object?> nested && nested.ContainsKey("sql"))
                copy[key] = new Dictionary<string, object?> { ["sql"] = "<synthetic>" };
        }

        return copy;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static JsonElement Envelope(CallToolResult result) =>
        JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement.Clone();

    private static string? ArtifactId(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        if (result.TryGetProperty("artifactId", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        if (result.TryGetProperty("matrixArtifactId", out var matrix) && matrix.ValueKind == JsonValueKind.String)
            return matrix.GetString();
        if (result.TryGetProperty("artifactDirectory", out var directory) && directory.ValueKind == JsonValueKind.String)
            return Path.GetFileName(directory.GetString());
        return null;
    }

    private static string? MatrixArtifactId(JsonElement envelope) =>
        envelope.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("matrixArtifactId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : ArtifactId(envelope);

    private static int? Continuation(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        if (!result.TryGetProperty("continuation", out var continuation) || continuation.ValueKind != JsonValueKind.Number)
            return null;
        return continuation.GetInt32();
    }

    private static int? Omitted(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("truncation", out var truncation) || truncation.ValueKind != JsonValueKind.Object)
            return null;
        return truncation.TryGetProperty("omittedItems", out var omitted) && omitted.ValueKind == JsonValueKind.Number
            ? omitted.GetInt32()
            : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Normalize(string text, string? home = null)
    {
        var unix = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var full = Path.GetFullPath(home).TrimEnd('\\', '/');
            foreach (var form in new[]
                     {
                         JsonSerializer.Serialize(full).Trim('"'),
                         JsonSerializer.Serialize(full.Replace('\\', '/')).Trim('"'),
                         full.Replace('\\', '/'),
                         full,
                     }.Distinct(StringComparer.Ordinal))
            {
                unix = unix.Replace(form, "<home>", StringComparison.OrdinalIgnoreCase);
            }
        }

        unix = Regex.Replace(unix, @"\d{8}T\d{9}Z-[A-Za-z0-9._-]+-[0-9a-f]{32}", "<artifact-id>");
        unix = Regex.Replace(unix, @"""(?:parentOperationRef|operationRef)""\s*:\s*""[0-9a-f]{32}""", m =>
            m.Value.Contains("parent", StringComparison.Ordinal) ? "\"parentOperationRef\":\"<operation-ref>\"" : "\"operationRef\":\"<operation-ref>\"");
        unix = Regex.Replace(unix, @"cfg_[0-9a-f]+", "<configuration-ref>");
        unix = Regex.Replace(unix, @"sqlharness-077-[0-9a-f]{32}", "<home-id>");
        unix = unix.Replace("<home>\\\\", "<home>/", StringComparison.Ordinal);
        unix = unix.Replace("<home>\\", "<home>/", StringComparison.Ordinal);
        unix = unix.Replace("\\\\compare\\\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("\\compare\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("/compare\\\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("/compare\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("\\\\<artifact-id>", "/<artifact-id>", StringComparison.Ordinal);
        unix = unix.Replace("\\<artifact-id>", "/<artifact-id>", StringComparison.Ordinal);
        return unix.TrimEnd() + "\n";
    }

    private static string ComparableWorkflowRecord(string json) =>
        Regex.Replace(json, @"""discoveryUtf8Bytes""\s*:\s*\d+", "\"discoveryUtf8Bytes\":\"<measured>\"");

    private static void AssertScenarioRecord(string scenario, int budget, string json)
    {
        Assert.DoesNotContain("<home>\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/compare\\\\", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.GetProperty("calls")[0].GetProperty("text").GetString()!;
        using var envelope = JsonDocument.Parse(first);
        var result = envelope.RootElement.GetProperty("result");
        var artifact = result.TryGetProperty("artifactId", out var artifactId)
            ? artifactId.GetString()
            : result.TryGetProperty("matrixArtifactId", out var matrixId)
                ? matrixId.GetString()
                : result.TryGetProperty("artifactDirectory", out var directory)
                    ? directory.GetString()
                    : null;
        Assert.False(string.IsNullOrWhiteSpace(artifact), first[..Math.Min(400, first.Length)]);
        Assert.DoesNotContain("\\", artifact, StringComparison.Ordinal);
        Assert.DoesNotContain(":", artifact, StringComparison.Ordinal);
        switch (scenario)
        {
            case WorkflowScenarios.UnavailableMetricsId:
                Assert.Equal("[]", result.GetProperty("parameters").GetRawText());
                Assert.Equal(0.8m, result.GetProperty("baseline").GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetDecimal());
                Assert.Equal(0.4m, result.GetProperty("candidate").GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetDecimal());
                Assert.Equal(JsonValueKind.Null, result.GetProperty("baseline").GetProperty("cpuTimeMilliseconds").ValueKind);
                Assert.Equal(JsonValueKind.Null, result.GetProperty("baseline").GetProperty("logicalReads").ValueKind);
                var regression = result.GetProperty("regression");
                Assert.Equal("unavailable", regression.GetProperty("logicalReads").GetProperty("status").GetString());
                Assert.Equal("unavailable", regression.GetProperty("cpu").GetProperty("status").GetString());
                Assert.False(regression.GetProperty("elapsed").TryGetProperty("baselineMedian", out _));
                break;
            case WorkflowScenarios.CollidingOperatorsId:
                Assert.Equal("[]", result.GetProperty("parameters").GetRawText());
                Assert.Contains("Clustered Index Scan", first, StringComparison.Ordinal);
                Assert.Contains("[dbo].[Accounts]", first, StringComparison.Ordinal);
                Assert.Equal("pass", result.GetProperty("regression").GetProperty("verdict").GetString());
                Assert.DoesNotContain("unstable-elapsed", first, StringComparison.Ordinal);
                break;
            case WorkflowScenarios.PartialMatrixId when budget == 65536:
                JsonElement CaseAt(int index) => result.GetProperty("cases").EnumerateArray()
                    .Single(item => item.GetProperty("index").GetInt32() == index);
                var fail = CaseAt(7).GetProperty("regression");
                Assert.Equal("fail", fail.GetProperty("verdict").GetString());
                Assert.Contains("elapsed-regressed", fail.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()));
                var inconclusive = CaseAt(15).GetProperty("regression");
                Assert.Equal("inconclusive", inconclusive.GetProperty("verdict").GetString());
                Assert.Contains("unstable-elapsed", inconclusive.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()));
                var verdicts = result.GetProperty("cases").EnumerateArray()
                    .Select(cell => cell.GetProperty("regression").GetProperty("verdict").GetString())
                    .ToArray();
                Assert.Equal(1, verdicts.Count(value => value == "fail"));
                Assert.Equal(1, verdicts.Count(value => value == "inconclusive"));
                Assert.Equal(30, verdicts.Count(value => value == "pass"));
                var coverage = result.GetProperty("coverage");
                Assert.Equal(32, coverage.GetProperty("requestedCases").GetInt32());
                Assert.Equal(32, coverage.GetProperty("completedCases").GetInt32());
                Assert.Equal(0, coverage.GetProperty("notExecutedCases").GetInt32());
                Assert.False(coverage.TryGetProperty("failedCase", out _));
                Assert.False(envelope.RootElement.TryGetProperty("error", out var liveError) && liveError.TryGetProperty("matrixStop", out _));
                break;
            case WorkflowScenarios.NoImprovementId:
                Assert.Contains("Region", result.GetProperty("parameters").GetRawText(), StringComparison.Ordinal);
                var noImprovement = result.GetProperty("regression");
                Assert.Equal("inconclusive", noImprovement.GetProperty("verdict").GetString());
                var reasons = noImprovement.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()).ToArray();
                Assert.Contains("results-not-equivalent", reasons);
                Assert.DoesNotContain("unstable-elapsed", reasons);
                break;
        }
    }

    private static string Fixture(params string[] path) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", "OptimizationWorkflows", .. path]);

    private static string ConfigurationName() =>
        typeof(AgentOptimizationWorkflowTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>()
            .Single().Configuration;

    private static string HostDll()
    {
        var dll = Path.Combine(
            McpStdioProcessHarness.FindRepositoryRoot(),
            "tests", "SqlHarness.Mcp.WorkflowHost", "bin", ConfigurationName(), "net10.0",
            "SqlHarness.Mcp.WorkflowHost.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"Expected the workflow host at {dll}. Build the solution before the tests.", dll);
        return dll;
    }

    private sealed class Session : IAsyncDisposable
    {
        private Session(string home, McpStdioProcessHarness.StdioChild child, McpClient client, CancellationTokenSource cts)
        {
            Home = home;
            Child = child;
            Client = client;
            Token = cts.Token;
            _cts = cts;
        }

        public string Home { get; }
        public McpStdioProcessHarness.StdioChild Child { get; }
        public McpClient Client { get; }
        public RecordingWriteStream StdinTee { get; init; } = null!;
        public CancellationToken Token { get; }
        private readonly CancellationTokenSource _cts;
        private bool _stopped;

        public static async Task<Session> StartAsync(string scenario, TimeSpan budget)
        {
            var cts = new CancellationTokenSource(budget);
            var home = Path.Combine(Path.GetTempPath(), "sqlharness-077-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            var psi = new ProcessStartInfo("dotnet", $"\"{HostDll()}\" --scenario {scenario} --home \"{home}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["SQLHARNESS_HOME"] = home;
            psi.Environment["SQLHARNESS_077_FIXTURE_PW"] = "unused-fixture-password";
            var process = Process.Start(psi) ?? throw new InvalidOperationException("The workflow host did not start.");
            var tee = new RecordingStream(process.StandardOutput.BaseStream);
            var stdinTee = new RecordingWriteStream(process.StandardInput.BaseStream);
            var stderr = process.StandardError.ReadToEndAsync();
            var child = new McpStdioProcessHarness.StdioChild(process, tee, stderr);
            try
            {
                var client = await McpClient.CreateAsync(
                    new StreamClientTransport(stdinTee, tee, NullLoggerFactory.Instance),
                    new McpClientOptions
                    {
                        ClientInfo = new Implementation { Name = "sqlharness-077-tests", Version = "1.0.0" },
                        ProtocolVersion = McpStdioProcessHarness.DefaultProtocolVersion,
                    },
                    NullLoggerFactory.Instance,
                    cts.Token);
                return new Session(home, child, client, cts) { StdinTee = stdinTee };
            }
            catch (Exception exception)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                var error = "";
                try { error = await stderr.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
                try { Directory.Delete(home, true); } catch (IOException) { }
                cts.Dispose();
                throw new InvalidOperationException($"Workflow host failed to start: {error}", exception);
            }
        }

        public ValueTask<CallToolResult> Call(string tool, Dictionary<string, object?> arguments) =>
            Client.CallToolAsync(tool, arguments, cancellationToken: Token);

        public async Task<(CallToolResult Result, int RequestBytes, int ResponseBytes)> CallWithWireBytes(
            string tool, Dictionary<string, object?> arguments)
        {
            var requestStart = StdinTee.BytesWritten;
            var responseStart = Child.StdoutTee.Recorded.Length;
            var result = await Client.CallToolAsync(tool, arguments, cancellationToken: Token);
            var requestBytes = StdinTee.BytesWritten - requestStart;
            var responseBytes = Child.StdoutTee.Recorded.Length - responseStart;
            return (result, checked((int)requestBytes), checked((int)responseBytes));
        }

        public async Task ShutdownAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            await Client.DisposeAsync();
            try { Child.Process.StandardInput.Close(); } catch (ObjectDisposedException) { }
            if (!Child.Process.WaitForExit(10_000))
                Child.Process.Kill(entireProcessTree: true);
            await Child.StdoutTee.DrainRemainingAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        public void AssertNoDatabaseConnections()
        {
            Assert.Equal(0, WorkflowHostStats().GetProperty("connectionAttempts").GetInt32());
        }

        public JsonElement WorkflowHostStats()
        {
            var stats = Path.Combine(Home, "workflow-host-stats.json");
            Assert.True(File.Exists(stats), "The host did not record workflow statistics.");
            using var document = JsonDocument.Parse(File.ReadAllText(stats));
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ShutdownAsync();
            }
            finally
            {
                _cts.Dispose();
                try { Directory.Delete(Home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}

internal sealed class RecordingWriteStream(Stream inner) : Stream
{
    private long _bytesWritten;

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }
}
