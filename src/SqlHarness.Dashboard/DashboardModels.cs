using System.Text.Json;

namespace SqlHarness.Dashboard;

public sealed record Page<T>(IReadOnlyList<T> Items, long? NextCursor);

/// <summary>
/// Session list filter. With <c>Starred</c> set the list is ordered by id and the id cursor is exact; unfiltered, starred
/// sessions sort first so the id cursor is not exact across the star boundary and only the first page is reliable.
/// <c>Archived</c> null lists archived and active sessions together.
/// </summary>
public sealed record SessionQuery(
    string? Agent = null, string? Transport = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long? Cursor = null, int Limit = JournalReader.DefaultLimit,
    bool? Archived = false, bool? Starred = null, string? Search = null);

public sealed record OperationQuery(
    long? SessionId = null, string? Status = null, string? Operation = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, long? Cursor = null, int Limit = JournalReader.DefaultLimit,
    string? Profile = null, IReadOnlyDictionary<string, string?>? Dimensions = null, bool UnprofiledOnly = false);

public sealed record StatsQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Profile = null,
    IReadOnlyDictionary<string, string?>? Dimensions = null, string? RowDimension = null, string? ColumnDimension = null,
    bool UnprofiledOnly = false);

public sealed record SessionRoot(string Uri, string? Name);

public sealed record SessionSummary(
    long Id, string SessionKey, string AgentKind, string Transport, string Source, string? ClientName,
    string? ClientVersion, string? McpMode, string? Cwd, string FirstSeen, string LastSeen,
    int Operations, int Failed, int Rejected, int Running, int Abandoned,
    string? ClientTitle, IReadOnlyList<SessionRoot>? Roots, string? ProtocolVersion,
    string? Title, bool Starred, bool Archived, string? NotePreview);

public sealed record OperationSummary(
    long Id, long SessionId, string AgentKind, string Operation, string Status, int? ExitCode, string? ErrorKind,
    string StartedAt, string UpdatedAt, string? FinishedAt, long? DurationMs, string? Profile, string? Engine,
    string? Server, string? Database, bool MutationRequested, string? SqlHash, long? RowsReturned,
    long? LogicalReadsMedian, bool HasSpill, bool ColdCache, bool OverGranted, JsonElement? Progress,
    string? ErrorMessage, string? CancelReason, string? AgentModel, string? AgentReasoningEffort,
    string? OperationRef = null, string? ParentOperationRef = null, bool DryRun = false);

public sealed record AgentCall(
    string? CallId, string? Model, string? ReasoningEffort, string? SessionId, string? TurnId,
    string? TurnTrigger, string? ThreadSource);

public sealed record SessionDetail(SessionSummary Session, IReadOnlyList<OperationSummary> Operations,
    IReadOnlyList<CompareSubjectSummary> CompareSubjects, int UnassignedCompareCount, string? Note);

public sealed record Spread(long Min, long Median, long Max);

public sealed record TableIoRow(
    string Table, long LogicalReads, long? ScanCount, long? PhysicalReads, long? PageServerReads, long? ReadAheadReads,
    long? LobLogicalReads, long? LobPhysicalReads, long? LobReadAheadReads, int ColdRuns);

public sealed record PlanLinkRow(int Repetition, int Ordinal, string Hash, bool Stored);

public sealed record PostgresBuffers(long? SharedHit, long? SharedRead, long? SharedDirtied, long? SharedWritten, long? TempRead, long? TempWritten);

public sealed record VariantDetail(
    int Ordinal, string Variant, string? ParameterSet, int? MatrixCell, int Runs,
    Spread? ElapsedMs, Spread? CpuMs, Spread? LogicalReads,
    long? GrantRequestedKb, long? GrantGrantedKb, long? GrantMaxUsedKb, int? Dop, long? CompileTimeMs, long? CompileCpuMs,
    int SpillCount, bool HasWarnings, bool HasImplicitConversion, int MissingIndexCount,
    JsonElement? Waits, PostgresBuffers? Postgres, IReadOnlyList<TableIoRow> TableIo, IReadOnlyList<PlanLinkRow> Plans);

public sealed record OperationDetail(
    OperationSummary Operation, SessionSummary Session, IReadOnlyDictionary<string, string>? Vars,
    string? CandidateSqlHash, string? SqlText, string? CandidateSqlText, long? RawTokens, long? EmittedTokens,
    string? ArtifactDirectory, JsonElement? Summary, IReadOnlyList<VariantDetail> Variants,
    OperationDimensions Dimensions,
    AgentCall? Agent, long? RawBytes = null, long? EmittedBytes = null,
    string TokenEstimationMethod = "utf8-bytes-div-4",
    string? CompareSubject = null, string? CompareScope = null, string? BaselineVersion = null,
    string? CandidateVersion = null, JsonElement? CompareRegression = null,
    IReadOnlyList<JsonElement>? CompareCases = null, JsonElement? CompareConditions = null);

public sealed record StoredPlan(string Hash, string Format, string Document);

public sealed record DayAgentCount(string Day, string AgentKind, int Count);

public sealed record KeyCount(string Key, int Count);

public sealed record SqlHashStat(string SqlHash, int Count, long TotalDurationMs, long MaxDurationMs);

public sealed record TableReadStat(string Table, long LogicalReads, int Operations);

public sealed record WaitStat(string WaitType, double TotalWaitMs);

public sealed record TargetStat(string? Profile, string? Database, int Count, string? Engine = null, string? Server = null);

/// <summary>One profile's complete operation count in the selected time window.</summary>
public sealed record ProfileOperationCount(string? Profile, int Operations);

/// <summary>
/// A resolved scope value, or a missing value. IsUnknown keeps a missing value distinct
/// from literal "Unknown"; aggregate metrics are populated when the summary is grouped.
/// </summary>
public sealed record DimensionValueSummary(
    string Name, string Value, bool IsUnknown, string Source, int? Operations = null, double? Percentage = null,
    long? TotalDurationMs = null, int? DurationAvailableOperations = null, int? DurationUnavailableOperations = null,
    int? Failed = null, int? Rejected = null);

public sealed record OperationDimensions(IReadOnlyList<DimensionValueSummary> Values);

public sealed record DimensionStat(string Name, IReadOnlyList<DimensionValueSummary> Values);

public sealed record DimensionAggregate(
    int Operations, long? TotalDurationMs, int DurationAvailableOperations, int DurationUnavailableOperations,
    int Failed, int Rejected);

public sealed record DimensionMatrixCell(
    DimensionValueSummary Row, DimensionValueSummary Column, DimensionAggregate Metrics);

public sealed record DimensionMatrixStats(
    string RowDimension, string ColumnDimension, IReadOnlyList<DimensionMatrixCell> Cells,
    DimensionAggregate Totals);

public sealed record ProfileDimensionStats(
    string? Profile, bool ProfileDefinitionAvailable, int Operations, IReadOnlyList<DimensionStat> Dimensions,
    IReadOnlyList<TargetStat> Targets, int TargetCount, DimensionMatrixStats? Matrix = null);

public sealed record WorkflowCost(
    string? RootOperationRef, long OutputBytes, long EstimatedOutputTokens, int CallCount,
    string TokenEstimationMethod, string AccountingKind, WorkflowCostCoverage Coverage,
    WorkflowCostComparison? Comparison, int DryRunCalls, int DatabaseExecutionCalls);

public sealed record WorkflowCostCoverage(string Input, string Discovery, string Chat);

public sealed record WorkflowCostComparison(
    string BaselineKind, long BaselineBytes, long BaselineEstimatedTokens, long OutputOverheadBytes,
    long? EstimatedOutputTokenDelta);

public sealed record DashboardStats(
    IReadOnlyList<DayAgentCount> OperationsPerDay,
    IReadOnlyList<KeyCount> Statuses,
    IReadOnlyList<KeyCount> ExitCodes,
    IReadOnlyList<KeyCount> Operations,
    IReadOnlyList<SqlHashStat> TopSqlByCount,
    IReadOnlyList<SqlHashStat> TopSqlByDuration,
    IReadOnlyList<TableReadStat> TopTablesByLogicalReads,
    IReadOnlyList<WaitStat> TopWaits,
    IReadOnlyList<TargetStat> Targets,
    int SpillOperations,
    int ColdCacheOperations,
    IReadOnlyList<ProfileOperationCount> ProfileOperations,
    ProfileDimensionStats ProfileDimensions,
    IReadOnlyList<WorkflowCost> WorkflowCosts);

public sealed record SessionAnnotation(string? Title, string? Note, bool Starred, bool Archived);
