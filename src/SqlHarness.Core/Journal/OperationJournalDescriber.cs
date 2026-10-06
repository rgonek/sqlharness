using System.Security.Cryptography;
using System.Text;

namespace SqlHarness.Core;

/// <summary>
/// Maps operations and outcomes to journal rows. It reads only names, scope,
/// SQL text (for hashing and opt-in storage), and target identity; it never
/// reads parameter values, result rows, or messages.
/// </summary>
internal static class OperationJournalDescriber
{
    internal static OperationStart DescribeStart(SqlHarnessOperation operation) => operation switch
    {
        SqlHarnessQueryOperation query => Start("query", query.Target, query.AllowMutation, query.Sql, null),
        SqlHarnessMeasureOperation measure => Start("measure", measure.Target, false, measure.QuerySql, null),
        SqlHarnessCompareOperation compare => Start("compare", compare.Target, false, compare.BaselineSql, compare.CandidateSql),
        SqlHarnessCompareMatrixOperation matrix => Start("compare", matrix.Target, false, matrix.BaselineSql, matrix.CandidateSql),
        SqlHarnessWatchOperation watch => Start("watch", watch.Target, false, watch.Sql, null),
        SqlHarnessSnapshotOperation snapshot => Start("snapshot", snapshot.Target, false, snapshot.Sql, null),
        SqlHarnessSchemaOperation schema => Start("schema", schema.Target, false, null, null),
        SqlHarnessPingOperation ping => Start("ping", ping.Target, false, null, null),
        SqlHarnessCountsOperation counts => Start("counts", counts.Target, false, null, null),
        SqlHarnessSpaceOperation space => Start("space", space.Target, false, null, null),
        SqlHarnessQueryStoreTopOperation qstop => Start("qstop", qstop.Target, false, null, null),
        SqlHarnessIndexesOperation indexes => Start("indexes", indexes.Target, false, null, null),
        SqlHarnessPlanOperation => TargetFree("plan"),
        SqlHarnessGainOperation => TargetFree("gain"),
        _ => TargetFree(FallbackName(operation)),
    };

    internal static OperationEnd DescribeEnd(SqlHarnessOutcome outcome, long durationMilliseconds)
    {
        var identity = TargetOf(outcome.Report);
        var (resultSets, rows) = outcome.Report is SqlHarnessQueryReport query
            ? (query.ResultSets.Count, query.ResultSets.Sum(set => set.RowCount))
            : ((int?)null, (long?)null);
        return new OperationEnd(
            Status(outcome.ExitCode),
            (int)outcome.ExitCode,
            outcome.MachineError?.Code,
            Math.Max(durationMilliseconds, 0),
            identity is null ? null : identity.Engine ?? "sqlserver",
            identity?.ActualServer,
            identity?.ActualDatabase,
            resultSets,
            rows);
    }

    internal static OperationEnd Cancelled(long durationMilliseconds) =>
        new("failed", -1, "cancelled", Math.Max(durationMilliseconds, 0), null, null, null, null, null);

    internal static OperationEnd Crashed(long durationMilliseconds) =>
        new("failed", -1, "unhandled_exception", Math.Max(durationMilliseconds, 0), null, null, null, null, null);

    internal static string Status(SqlHarnessExitCode exitCode) => exitCode switch
    {
        SqlHarnessExitCode.Success or SqlHarnessExitCode.WatchMaxDuration or SqlHarnessExitCode.SnapshotDifferences => "succeeded",
        SqlHarnessExitCode.Safety => "rejected",
        _ => "failed",
    };

    internal static string SqlHash(string sql) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

    private static OperationStart Start(string name, SqlTargetRequest target, bool mutation, string? sql, string? candidate) =>
        new(
            name,
            target.UnsafeDirect ? null : target.Profile,
            target.Vars.Count == 0 ? null : target.Vars,
            mutation,
            sql is null ? null : SqlHash(sql),
            candidate is null ? null : SqlHash(candidate),
            sql,
            candidate);

    private static OperationStart TargetFree(string name) => new(name, null, null, false, null, null, null, null);

    private static string FallbackName(SqlHarnessOperation operation)
    {
        var name = operation.GetType().Name;
        if (name.StartsWith("SqlHarness", StringComparison.Ordinal))
            name = name["SqlHarness".Length..];
        if (name.EndsWith("Operation", StringComparison.Ordinal))
            name = name[..^"Operation".Length];
        return name.ToLowerInvariant();
    }

    private static SqlHarnessTargetIdentityReport? TargetOf(object? report) => report switch
    {
        SqlHarnessQueryReport value => value.Target,
        SqlHarnessMeasureReport value => value.Target,
        SqlHarnessMeasureSetReport value => value.Target,
        SqlHarnessCompareReport value => value.Target,
        SqlHarnessWatchReport value => value.Target,
        SqlHarnessSnapshotReport value => value.Target,
        SqlHarnessSchemaReport value => value.Target,
        SqlHarnessPingReport value => value.Target,
        SqlHarnessCountsReport value => value.Target,
        SqlHarnessSpaceReport value => value.Target,
        SqlHarnessQueryStoreTopReport value => value.Target,
        SqlHarnessIndexesReport value => value.Target,
        _ => null,
    };
}