namespace SqlHarness.Core;

public interface ISqlHarnessModule
{
    Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default);
}

public abstract record SqlHarnessOperation;

public sealed record SqlHarnessQueryOperation(
    SqlTargetRequest Target,
    string Sql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int MaxRows,
    bool AllowMutation,
    string? ConfirmDatabase) : SqlHarnessOperation;

public sealed record SqlHarnessCompareOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered) : SqlHarnessOperation;

public sealed record SqlHarnessCompareMatrixOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    string Matrix,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered) : SqlHarnessOperation;

public sealed record CompareMatrixCellReport(
    int Index,
    string ParameterValue,
    SqlHarnessCompareReport Compare);

public sealed record SqlHarnessCompareMatrixReport(
    string ParameterName,
    string ParameterType,
    IReadOnlyList<CompareMatrixCellReport> Cells);

public sealed record SqlHarnessMeasureOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string QuerySql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat) : SqlHarnessOperation;

public sealed record SqlHarnessGainOperation : SqlHarnessOperation;

public sealed record SqlHarnessPlanOperation(string ShowplanXml, OutputFootprint RawFootprint) : SqlHarnessOperation;

public sealed record SqlHarnessSchemaOperation(
    SqlTargetRequest Target,
    string? Filter,
    int TimeoutSeconds,
    int MaxObjects = 50,
    string? Object = null) : SqlHarnessOperation;

public sealed record SqlHarnessPingOperation(
    SqlTargetRequest Target, int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessCountsOperation(
    SqlTargetRequest Target, IReadOnlyList<string> Tables, string? Like,
    int Top, bool Exact, int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessSpaceOperation(
    SqlTargetRequest Target, int Top, string? Object,
    int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessQueryStoreTopOperation(
    SqlTargetRequest Target, int Top, int WindowMinutes,
    int TimeoutSeconds) : SqlHarnessOperation;

public sealed record SqlHarnessWatchOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, TimeSpan Interval, TimeSpan MaxDuration,
    string? Until, int? UntilUnchanged) : SqlHarnessOperation;

public sealed record SqlHarnessSnapshotOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<string> Parameters,
    int TimeoutSeconds, int MaxRows, string Name, bool Diff, bool Force) : SqlHarnessOperation;

public enum WatchExitReason { ConditionMet, Unchanged, MaxDuration }
public enum SnapshotVerdict { Stored, Identical, Different }

public sealed record SqlHarnessWatchPoll(
    int Poll, long ElapsedMilliseconds, string ResultHash,
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets);

public sealed record SqlHarnessWatchReport(
    SqlHarnessTargetIdentityReport Target, int PollCount, long ElapsedMilliseconds,
    WatchExitReason ExitReason, IReadOnlyList<SqlHarnessWatchPoll> EmittedPolls);

public sealed record SqlHarnessSnapshotDifference(
    int ResultSet, long? Row, int? Column, string Kind);

public sealed record SqlHarnessSnapshotReport(
    SqlHarnessTargetIdentityReport Target, string Name, SnapshotVerdict Verdict,
    int DifferenceCount, IReadOnlyList<SqlHarnessSnapshotDifference> Differences);

public sealed record DatabaseFileSpaceReport(
    string LogicalName, string Type, string? PhysicalName,
    decimal SizeMb, decimal UsedMb, decimal FreeMb);

public sealed record DatabaseAllocationReport(
    decimal ReservedMb, decimal UsedMb, decimal DataMb);

public sealed record TableSpaceReport(
    string Schema, string Name, long Rows,
    decimal ReservedMb, decimal UsedMb, decimal DataMb);

public sealed record IndexSpaceReport(
    string Schema, string Table, string Index, string Type,
    decimal ReservedMb, decimal UsedMb, decimal DataMb, string? Compression);

public sealed record SqlHarnessSpaceReport(
    SqlHarnessTargetIdentityReport Target,
    IReadOnlyList<DatabaseFileSpaceReport> Files,
    DatabaseAllocationReport Allocation,
    IReadOnlyList<TableSpaceReport> Tables,
    IReadOnlyList<IndexSpaceReport> Indexes);

public sealed record QueryStoreTopItemReport(
    long QueryId,
    string QueryHash,
    string? ObjectName,
    long ExecutionCount,
    int PlanCount,
    decimal TotalDurationMilliseconds,
    decimal AverageDurationMilliseconds,
    decimal MaximumDurationMilliseconds,
    decimal TotalCpuMilliseconds,
    decimal AverageCpuMilliseconds,
    decimal MaximumCpuMilliseconds,
    decimal TotalLogicalReads,
    decimal AverageLogicalReads,
    decimal MaximumLogicalReads,
    DateTimeOffset LastExecutionAt);

public sealed record SqlHarnessQueryStoreTopReport(
    SqlHarnessTargetIdentityReport Target,
    int WindowMinutes,
    int Top,
    IReadOnlyList<QueryStoreTopItemReport> Queries,
    string? ArtifactDirectory);

public sealed record SqlHarnessPingReport(
    SqlHarnessTargetIdentityReport Target, string Server, string Database,
    string Login, long DurationMilliseconds);

public sealed record SqlHarnessCountReport(
    string Schema, string Name, long Rows, string Method);

public sealed record SqlHarnessCountsReport(
    SqlHarnessTargetIdentityReport Target,
    IReadOnlyList<SqlHarnessCountReport> Tables, int Omitted);

public sealed record SqlTargetRequest(
    string? Profile,
    IReadOnlyDictionary<string, string> Vars,
    string? Server = null,
    string? Database = null,
    string? Auth = null,
    bool UnsafeDirect = false,
    string? SqlUser = null,
    string? PasswordEnvVar = null,
    bool TrustServerCertificate = false,
    string? Engine = null);

public sealed record SqlHarnessOutcome(
    SqlHarnessExitCode ExitCode,
    object? Report,
    string? SafeError,
    SqlHarnessEmissionReceipt? EmissionReceipt = null);

public sealed class SqlHarnessEmissionReceipt
{
    private readonly object _sync = new();
    private readonly Func<OutputFootprint, CancellationToken, Task<SqlHarnessExitCode>> _complete;
    private Task<SqlHarnessExitCode>? _completion;

    internal SqlHarnessEmissionReceipt(
        Func<OutputFootprint, CancellationToken, Task<SqlHarnessExitCode>> complete) =>
        _complete = complete;

    public Task<SqlHarnessExitCode> CompleteAsync(
        OutputFootprint emitted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(emitted);
        ArgumentOutOfRangeException.ThrowIfNegative(emitted.Bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(emitted.Lines);
        lock (_sync)
            return _completion ??= _complete(emitted, ct);
    }
}

public enum SqlHarnessExitCode
{
    Success = 0,
    Safety = 2,
    Authentication = 3,
    TargetMismatch = 4,
    SqlExecution = 5,
    LocalStorage = 6,
    WatchMaxDuration = 7,
    SnapshotDifferences = 8,
}