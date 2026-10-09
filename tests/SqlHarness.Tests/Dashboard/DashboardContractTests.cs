using System.Text.Json;

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
            null, null, false, null, null, null, false, false, false, null, null);

        Assert.Equal(
            ["id", "sessionId", "agentKind", "operation", "status", "exitCode", "errorKind", "startedAt", "updatedAt",
             "finishedAt", "durationMs", "profile", "engine", "server", "database", "mutationRequested", "sqlHash",
             "rowsReturned", "logicalReadsMedian", "hasSpill", "coldCache", "overGranted", "progress", "errorMessage"],
            Names(summary));
    }

    [Fact]
    public void Session_summary_names_match_the_ui()
    {
        var session = new SessionSummary(1, "k", "claude", "cli", "process-tree", null, null, null, null, "f", "l", 0, 0, 0, 0, 0);

        Assert.Equal(
            ["id", "sessionKey", "agentKind", "transport", "source", "clientName", "clientVersion", "mcpMode", "cwd",
             "firstSeen", "lastSeen", "operations", "failed", "rejected", "running", "abandoned"],
            Names(session));
    }

    [Fact]
    public void Variant_and_stats_names_match_the_ui()
    {
        var variant = new VariantDetail(0, "measure", null, null, 1, null, null, null, null, null, null, null, null, null,
            0, false, false, 0, null, null, [], []);
        var stats = new DashboardStats([], [], [], [], [], [], [], [], [], new TokenStat(0, 0), 0, 0, [],
            new ProfileDimensionStats(null, false, 0, [], [], 0));

        Assert.Equal(
            ["ordinal", "variant", "parameterSet", "matrixCell", "runs", "elapsedMs", "cpuMs", "logicalReads",
             "grantRequestedKb", "grantGrantedKb", "grantMaxUsedKb", "dop", "compileTimeMs", "compileCpuMs", "spillCount",
             "hasWarnings", "hasImplicitConversion", "missingIndexCount", "waits", "postgres", "tableIo", "plans"],
            Names(variant));
        Assert.Equal(
            ["operationsPerDay", "statuses", "exitCodes", "operations", "topSqlByCount", "topSqlByDuration",
             "topTablesByLogicalReads", "topWaits", "targets", "tokens", "spillOperations", "coldCacheOperations",
             "profileOperations", "profileDimensions"],
            Names(stats));
    }
}