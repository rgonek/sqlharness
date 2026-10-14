using System.Security.Cryptography;
using System.Text;

namespace SqlHarness.Core;

/// <summary>
/// Maps operations and outcomes to journal rows. It reads only names, scope,
/// SQL text (for hashing and opt-in storage), and target identity, plus the
/// already-redacted error message for storage; it never reads parameter values or result rows.
/// </summary>
internal static class OperationJournalDescriber
{
    internal static OperationStart DescribeStart(SqlHarnessOperation operation)
    {
        var start = operation switch
        {
            SqlHarnessQueryOperation query => Start("query", query.Target, query.AllowMutation, query.Sql, null),
            SqlHarnessMeasureOperation measure => Start("measure", measure.Target, false, measure.QuerySql, null),
            SqlHarnessCompareOperation compare => Start("compare", compare.Target, false, compare.BaselineSql, compare.CandidateSql),
            SqlHarnessCompareMatrixOperation matrix => Start("compare", matrix.Target, false, matrix.BaselineSql, matrix.CandidateSql),
            SqlHarnessWatchOperation watch => Start("watch", watch.Target, false, watch.Sql, null),
            SqlHarnessSnapshotOperation snapshot => Start("snapshot", snapshot.Target, false, snapshot.Sql, null),
            SqlHarnessArtifactReadOperation => TargetFree("artifact"),
            SqlHarnessPlanOperation => TargetFree("plan"),
            _ => TargetFree(FallbackName(operation)),
        };
        start = operation switch
        {
            SqlHarnessCompareOperation compare => start with
            {
                Context = compare.Context,
                CompareConditions = new CompareJournalConditions(
                    compare.TimeoutSeconds, compare.Repeat, compare.CompareResults, compare.CompareNullability,
                    compare.SetupSql, compare.Parameters, null, null, compare.TypedParameters),
            },
            SqlHarnessCompareMatrixOperation matrix => start with
            {
                Context = matrix.Context,
                CompareConditions = new CompareJournalConditions(
                    matrix.TimeoutSeconds, matrix.Repeat, matrix.CompareResults, matrix.CompareNullability,
                    matrix.SetupSql, matrix.Parameters, matrix.TypedMatrix, matrix.ParameterSets, matrix.TypedParameters),
            },
            _ => start,
        };
        return start with { OperationRef = operation.OperationRef, DryRun = IsDryRun(operation) };
    }

    private static bool IsDryRun(SqlHarnessOperation operation) => operation switch
    {
        SqlHarnessQueryOperation value => value.DryRun,
        SqlHarnessMeasureOperation value => value.DryRun,
        SqlHarnessCompareOperation value => value.DryRun,
        SqlHarnessCompareMatrixOperation value => value.DryRun,
        _ => false,
    };

    internal static OperationEnd DescribeEnd(SqlHarnessOutcome outcome, long durationMilliseconds)
    {
        var status = Status(outcome.ExitCode);
        var identity = TargetOf(outcome.Report);
        var (resultSets, rows) = outcome.Report is SqlHarnessQueryReport query
            ? (query.ResultSets.Count, query.ResultSets.Sum(set => set.RowCount))
            : ((int?)null, (long?)null);
        var artifactDirectory = outcome.Report switch
        {
            SqlHarnessMeasureReport value => value.ArtifactDirectory,
            SqlHarnessMeasureSetReport value => value.ArtifactDirectory,
            SqlHarnessCompareReport value => value.ArtifactDirectory,
            _ => null,
        };
        return new OperationEnd(
            status,
            (int)outcome.ExitCode,
            outcome.MachineError?.Code,
            Math.Max(durationMilliseconds, 0),
            identity is null ? null : identity.Engine ?? "sqlserver",
            identity?.ActualServer,
            identity?.ActualDatabase,
            resultSets,
            rows,
            // MCP never completes the emission receipt, so the raw footprint is recorded here.
            outcome.EmissionReceipt?.RawFootprint?.EstimatedTokenCount,
            ArtifactDirectory: artifactDirectory,
            SummaryJson: JournalSummary.Build(outcome.Report),
            ErrorMessage: status is "failed" or "rejected" ? Message(outcome.MachineError) : null,
            ParentOperationRef: outcome.ParentOperationRef);
    }

    internal static OperationEnd Cancelled(long durationMilliseconds, string? reason = null) =>
        new("failed", -1, "cancelled", Math.Max(durationMilliseconds, 0), null, null, null, null, null, CancelReason: reason);

    internal static OperationEnd Crashed(long durationMilliseconds) =>
        new("failed", -1, "unhandled_exception", Math.Max(durationMilliseconds, 0), null, null, null, null, null);

    internal static string Status(SqlHarnessExitCode exitCode) => exitCode switch
    {
        SqlHarnessExitCode.Success or SqlHarnessExitCode.WatchMaxDuration or SqlHarnessExitCode.SnapshotDifferences => "succeeded",
        SqlHarnessExitCode.Safety => "rejected",
        _ => "failed",
    };

    private static string? Message(SqlHarnessError? error) =>
        error is null ? null : string.IsNullOrWhiteSpace(error.Hint) ? error.Message : error.Message + "\n" + error.Hint;

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
        SqlHarnessCompareMatrixReport { Cells.Count: > 0 } value => value.Cells[0].Compare.Target,
        SqlHarnessWatchReport value => value.Target,
        SqlHarnessSnapshotReport value => value.Target,
        _ => null,
    };
}