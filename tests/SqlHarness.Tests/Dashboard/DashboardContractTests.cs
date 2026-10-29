using System.Text.Json;
using System.Text.RegularExpressions;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

/// <summary>
/// The SPA's hand-written ui/src/api/types.ts mirrors these records. A rename here
/// must be made there too; this test makes such a rename a deliberate, visible change.
/// </summary>
public sealed class DashboardContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string[] Names<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Json)).RootElement.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void Operation_summary_names_match_the_ui()
    {
        var summary = new OperationSummary(1, 2, "claude", "query", "running", null, null, "s", "u", null, null, null, null,
            null, null, false, null, null, null, false, false, false, null, null, null, null, null);

        Assert.Equal(
            ["id", "sessionId", "agentKind", "operation", "status", "exitCode", "errorKind", "startedAt", "updatedAt",
             "finishedAt", "durationMs", "profile", "engine", "server", "database", "mutationRequested", "sqlHash",
             "rowsReturned", "logicalReadsMedian", "hasSpill", "coldCache", "overGranted", "progress", "errorMessage", "cancelReason", "agentModel", "agentReasoningEffort",
             "operationRef", "parentOperationRef", "dryRun", "agentModelSource", "subject", "versionPair", "readsBefore", "readsAfter", "safetyNotices",
             "agentLabelInitial", "agentLabel", "agentKeep", "ownerStarred", "ownerNote",
             "dashboardEvidenceVersion", "recordsAffected", "sqlPreview", "sqlPreviewTruncated"],
            Names(summary));
        AssertTypescriptFieldsMatch("OperationSummary", summary);
    }

    [Fact]
    public void Session_summary_names_match_the_ui()
    {
        var session = new SessionSummary(1, "k", "claude", "cli", "process-tree", null, null, null, null, "f", "l", 0, 0, 0, 0, 0, null, null, null, null, false, false, null);

        Assert.Equal(
            ["id", "sessionKey", "agentKind", "transport", "source", "clientName", "clientVersion", "mcpMode", "cwd",
             "firstSeen", "lastSeen", "operations", "failed", "rejected", "running", "abandoned", "clientTitle", "roots", "protocolVersion",
             "title", "starred", "archived", "notePreview", "agentTitle", "hasCompare", "optimization"],
            Names(session));
        AssertTypescriptFieldsMatch("SessionSummary", session);
        AssertTypescriptFieldsMatch("SessionOptimizationBadge", new SessionOptimizationBadge("orders", "−94%", false));

        Assert.Equal(["session", "operations", "compareSubjects", "unassignedCompareCount", "note", "conversations"],
            Names(new SessionDetail(session, [], [], 0, null, [])));
        AssertTypescriptFieldsMatch("AgentSessionSummary", new AgentSessionSummary(new AgentSessionKey("codex", "t"), null, null, null,
            null, false, false, 0, 0, 0, "t", []));
        AssertTypescriptFieldsMatch("AgentSessionChild", new AgentSessionChild(session, 0, 0, 0));
        AssertTypescriptFieldsMatch("AgentSessionOperation", new AgentSessionOperation(
            new OperationSummary(1, 2, "codex", "query", "succeeded", 0, null, "t", "t", null, 1, null, null, null, null,
                false, null, null, null, false, false, false, null, null, null, null, null), "exact"));
        Assert.Equal(["items", "nextCursor", "matching"], Names(new SessionListPage([], null, true)));
        Assert.Equal(["operationId", "subject", "scope", "baselineHash", "candidateHash", "baselineVersion",
                "candidateVersion", "status", "startedAt", "regression", "cases", "conditions", "summary"],
            Names(new CompareAttemptSummary(1, "orders", "whole", null, null, null, null, "running", "t", null, null, null, null)));
        Assert.Equal(["subject", "scopes", "latestWhole", "latestCompletedWhole", "startToLatestWhole", "scopeVersions", "versionsFromRetainedHistory"],
            Names(new CompareSubjectSummary("orders", [], null, null, null, [])));
        Assert.Equal(["scope", "versions"], Names(new CompareScopeVersions("whole", [])));
        Assert.Equal(["version", "hash"], Names(new CompareSqlVersion("v0", "h0")));
    }

    [Fact]
    public void Session_sql_text_names_match_the_ui()
    {
        Assert.Equal(["hash", "text", "tooLarge"], Names(new SqlVersionText("sha256:" + new string('a', 64), null, false)));
    }

    [Fact]
    public void Settings_response_exposes_default_execution_timeout_to_the_ui()
    {
        var config = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes("{}")).Config!;
        var response = new SettingsResponse("valid", "config.json", config);

        Assert.Equal(30, JsonDocument.Parse(JsonSerializer.Serialize(response, Json)).RootElement
            .GetProperty("settings").GetProperty("execution").GetProperty("defaultTimeoutSeconds").GetInt32());
        AssertTypescriptFieldsMatch("ExecutionSettings", config.Execution);
    }

    [Fact]
    public void Variant_and_stats_names_match_the_ui()
    {
        var variant = new VariantDetail(0, "measure", null, null, 1, null, null, null, null, null, null, null, null, null,
            0, false, false, 0, null, null, [], []);
        var stats = new DashboardStats([], [], [], [], [], [], [], [], [], 0, 0, [],
            new ProfileDimensionStats(null, false, 0, [], [], 0),
            []);

        Assert.Equal(
            ["ordinal", "variant", "parameterSet", "matrixCell", "runs", "elapsedMs", "cpuMs", "logicalReads",
             "grantRequestedKb", "grantGrantedKb", "grantMaxUsedKb", "dop", "compileTimeMs", "compileCpuMs", "spillCount",
             "hasWarnings", "hasImplicitConversion", "missingIndexCount", "waits", "postgres", "tableIo", "plans", "caseId", "caseLabel",
             "totalLogicalReadsMedian", "totalLogicalReadsLabel"],
            Names(variant));
        AssertTypescriptFieldsMatch("VariantDetail", variant);
        var tableIo = new TableIoRow("Orders", 4, 1, 0, 0, 0, 1, 0, 0, 0);
        Assert.Equal(["table", "logicalReads", "scanCount", "physicalReads", "pageServerReads", "readAheadReads",
            "lobLogicalReads", "lobPhysicalReads", "lobReadAheadReads", "coldRuns", "scans", "totalLogicalReads"], Names(tableIo));
        AssertTypescriptFieldsMatch("TableIoRow", tableIo);
        Assert.Equal(
            ["operationsPerDay", "statuses", "exitCodes", "operations", "topSqlByCount", "topSqlByDuration",
             "topTablesByLogicalReads", "topWaits", "targets", "spillOperations", "coldCacheOperations",
             "profileOperations", "profileDimensions", "workflowCosts", "topSqlByLogicalReads", "sessionCount"],
            Names(stats));
        AssertTypescriptFieldsMatch("DashboardStats", stats);
    }

    [Fact]
    public void Agent_call_key_count_and_session_root_names_match_the_ui()
    {
        Assert.Equal(["callId", "model", "reasoningEffort", "sessionId", "turnId", "turnTrigger", "threadSource",
                "transcriptModel", "transcriptReasoningEffort", "conversationTitle"],
            Names(new AgentCall(null, null, null, null, null, null, null, null, null, null)));
        Assert.Equal(["agentKind", "conversationId", "title", "operations"],
            Names(new AgentConversation("claude", "conversation", null, 0)));
        Assert.Equal(["agentKind", "conversationId"], Names(new AgentSessionKey("claude", "conversation")));
        Assert.Equal(["agentSession", "operations", "note"], Names(new AgentSessionDetail(
            new AgentSessionSummary(new AgentSessionKey("claude", "conversation"), null, null, null, null,
                false, false, 0, 0, 0, "t", []), [])));
        Assert.Equal(["key", "count"], Names(new KeyCount("claude", 1)));
        Assert.Equal(["uri", "name"], Names(new SessionRoot("file:///w", null)));
    }

    [Fact]
    public void Workflow_cost_contract_names_match_the_ui()
    {
        var cost = new WorkflowCost("root", 1200, 300, 2, "utf8-bytes-div-4", "workflow-output",
            new WorkflowCostCoverage("unmeasured", "unmeasured", "unmeasured"),
            new WorkflowCostComparison("single-measured-execution-output", 1200, 300, 0, 0), 0, 1);

        Assert.Equal(["rootOperationRef", "outputBytes", "estimatedOutputTokens", "callCount", "tokenEstimationMethod",
                "accountingKind", "coverage", "comparison", "dryRunCalls", "databaseExecutionCalls"], Names(cost));
        Assert.Equal(["baselineKind", "baselineBytes", "baselineEstimatedTokens", "outputOverheadBytes", "estimatedOutputTokenDelta"],
            Names(cost.Comparison));
    }

    [Fact]
    public void Operation_detail_exposes_the_agent_property()
    {
        Assert.Contains("agent", typeof(OperationDetail).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)));
        Assert.Contains("events", typeof(OperationDetail).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)));
        Assert.Equal(["operation", "session", "vars", "candidateSqlHash", "sqlText", "candidateSqlText", "rawTokens",
                "emittedTokens", "artifactDirectory", "summary", "variants", "dimensions", "agent", "rawBytes", "emittedBytes",
                "tokenEstimationMethod", "compareSubject", "compareScope", "baselineVersion", "candidateVersion",
                "compareRegression", "compareCases", "compareConditions", "phases", "setupSqlText", "conditions", "statements", "mutationRequest", "mutationRequestId", "omittedStatements", "statementAttribution"],
            Names(new OperationDetail(default!, default!, null, null, null, null, null, null, null, null, [], default!, null)));
    }

    [Fact]
    public void Serialized_compare_contracts_match_the_typescript_declarations()
    {
        var session = new SessionSummary(1, "k", "claude", "cli", "process-tree", null, null, null, null, "f", "l", 0, 0, 0, 0, 0, null, null, null, null, false, false, null);
        var attempt = new CompareAttemptSummary(1, "orders", "whole", "a", "b", "v0", "v1", "succeeded", "t", null, [], null, null);
        var subject = new CompareSubjectSummary("orders", ["whole"], attempt, attempt, attempt, []);

        AssertTypescriptFieldsMatch("CompareAttemptSummary", attempt);
        AssertTypescriptFieldsMatch("CompareSubjectSummary", subject);
        AssertTypescriptFieldsMatch("CompareScopeVersions", new CompareScopeVersions("whole", []));
        AssertTypescriptFieldsMatch("CompareSqlVersion", new CompareSqlVersion("v0", "a"));
        AssertTypescriptFieldsMatch("SessionDetail", new SessionDetail(session, [], [subject], 0, null, []));
        AssertTypescriptFieldsMatch("OperationDetail", new OperationDetail(default!, session, null, null, null, null,
            null, null, null, null, [], default!, null, Phases: [], Events: []));
    }

    private static void AssertTypescriptFieldsMatch<T>(string typeName, T value)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/SqlHarness.Dashboard/ui/src/api/types.ts")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src/SqlHarness.Dashboard/ui/src/api/types.ts"));
        var declaration = Regex.Match(source,
            $@"export type {Regex.Escape(typeName)}\s*=\s*\{{(?<body>[\s\S]*?)\n\}}",
            RegexOptions.CultureInvariant);
        Assert.True(declaration.Success, $"Missing TypeScript declaration: {typeName}");
        var fields = Regex.Matches(declaration.Groups["body"].Value,
                @"(?:^|;|\r?\n)\s*(?<name>[A-Za-z][A-Za-z0-9]*)\??\s*:", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        Assert.Equal(Names(value).Order(StringComparer.Ordinal), fields.Order(StringComparer.Ordinal));
    }
}