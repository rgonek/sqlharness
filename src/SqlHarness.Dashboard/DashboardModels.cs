using System.Text.Json;
using System.Text.Json.Serialization;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record Page<T>(IReadOnlyList<T> Items, long? NextCursor);

public sealed record SessionListPage(IReadOnlyList<SessionSummary> Items, long? NextCursor, bool Matching);

public sealed record MutationRequestSummary(
    long Id, string RequestId, string SessionKey, string Profile, string Engine, string Server, string Database,
    string Tier, string Mode, string Digest, string? Sql, string? ParametersJson, string State, string? Status,
    string? Verification, string CreatedAt, string? DecidedAt, string? FinishedAt, string? Environment = null);

/// <summary>
/// Session list filter. With <c>Starred</c> set the list is ordered by id and pages by an exact id cursor; unfiltered, starred
/// sessions sort first, the id cursor would not be exact across the star boundary, so only the first page is returned
/// (<c>NextCursor</c> is null).
/// <c>Archived</c> null lists archived and active sessions together.
/// </summary>
public sealed record SessionQuery(
    string? Agent = null, string? Transport = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long? Cursor = null, int Limit = JournalReader.DefaultLimit,
    bool? Archived = false, bool? Starred = null, string? Search = null, bool? HasCompare = null);

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
    string? Title, bool Starred, bool Archived, string? NotePreview, string? AgentTitle = null)
{
    public bool HasCompare { get; init; }
    public IReadOnlyList<SessionOptimizationBadge> Optimization { get; init; } = [];
}

public sealed record SessionOptimizationBadge(string Subject, string Change, bool Warning)
{
    public string VerdictBasis { get; init; } = "whole batch";
}

public sealed record OperationSummary(
    long Id, long SessionId, string AgentKind, string Operation, string Status, int? ExitCode, string? ErrorKind,
    string StartedAt, string UpdatedAt, string? FinishedAt, long? DurationMs, string? Profile, string? Engine,
    string? Server, string? Database, bool MutationRequested, string? SqlHash, long? RowsReturned,
    long? LogicalReadsMedian, bool HasSpill, bool ColdCache, bool OverGranted, JsonElement? Progress,
    string? ErrorMessage, string? CancelReason, string? AgentModel, string? AgentReasoningEffort,
    string? OperationRef = null, string? ParentOperationRef = null, bool DryRun = false, string? AgentModelSource = null,
    string? Subject = null, string? VersionPair = null, long? ReadsBefore = null, long? ReadsAfter = null)
{
    public IReadOnlyList<string> SafetyNotices { get; init; } = [];
    public string? AgentLabelInitial { get; init; }
    public string? AgentLabel { get; init; }
    public bool AgentKeep { get; init; }
    public bool OwnerStarred { get; init; }
    public string? OwnerNote { get; init; }
    public int? DashboardEvidenceVersion { get; init; }
    public int? RecordsAffected { get; init; }
    public string? SqlPreview { get; init; }
    public bool SqlPreviewTruncated { get; init; }
}

public sealed record OperationAnnotation(string? AgentLabel, bool AgentKeep, bool OwnerStarred, string? OwnerNote);

public sealed record AgentCall(
    string? CallId, string? Model, string? ReasoningEffort, string? SessionId, string? TurnId,
    string? TurnTrigger, string? ThreadSource, string? TranscriptModel = null, string? TranscriptReasoningEffort = null,
    string? ConversationTitle = null);

public sealed record AgentConversation(string AgentKind, string ConversationId, string? Title, int Operations);

public sealed record AgentSessionChild(SessionSummary Session, int Exact, int Inferred, int Unassigned);

public sealed record AgentSessionSummary(
    AgentSessionKey Key, string? Title, string? ConversationTitle, string? OwnerTitle, string? NotePreview,
    bool Starred, bool Archived, int Exact, int Inferred, int Unassigned, string LastSeen,
    IReadOnlyList<AgentSessionChild> Children);

public sealed record AgentSessionOperation(OperationSummary Operation, string Membership);

public sealed record AgentSessionDetail(AgentSessionSummary AgentSession, IReadOnlyList<AgentSessionOperation> Operations, string? Note = null);

public sealed record SessionDetail(SessionSummary Session, IReadOnlyList<OperationSummary> Operations,
    IReadOnlyList<CompareSubjectSummary> CompareSubjects, int UnassignedCompareCount, string? Note,
    IReadOnlyList<AgentConversation> Conversations);

public sealed record SqlVersionText(string Hash, string? Text, bool TooLarge);

public sealed record Spread(long Min, long Median, long Max);

public sealed record TableIoRow(
    string Table, long LogicalReads, long? ScanCount, long? PhysicalReads, long? PageServerReads, long? ReadAheadReads,
    long? LobLogicalReads, long? LobPhysicalReads, long? LobReadAheadReads, int ColdRuns)
{
    public long? Scans => ScanCount;
    public long TotalLogicalReads => LogicalReads;
}

public sealed record PlanLinkRow(int Repetition, int Ordinal, string Hash, bool Stored);

public sealed record PostgresBuffers(long? SharedHit, long? SharedRead, long? SharedDirtied, long? SharedWritten, long? TempRead, long? TempWritten);

public sealed record VariantDetail(
    int Ordinal, string Variant, string? ParameterSet, int? MatrixCell, int Runs,
    Spread? ElapsedMs, Spread? CpuMs, Spread? LogicalReads,
    long? GrantRequestedKb, long? GrantGrantedKb, long? GrantMaxUsedKb, int? Dop, long? CompileTimeMs, long? CompileCpuMs,
    int SpillCount, bool HasWarnings, bool HasImplicitConversion, int MissingIndexCount,
    JsonElement? Waits, PostgresBuffers? Postgres, IReadOnlyList<TableIoRow> TableIo, IReadOnlyList<PlanLinkRow> Plans)
{
    public string CaseId { get; init; } = "0";
    public string CaseLabel { get; init; } = "Case 1 of 1";
    public long? TotalLogicalReadsMedian => LogicalReads?.Median;
    public string TotalLogicalReadsLabel => "median of run totals (data + LOB)";
}

public sealed record OperationDetail(
    OperationSummary Operation, SessionSummary Session, IReadOnlyDictionary<string, string>? Vars,
    string? CandidateSqlHash, string? SqlText, string? CandidateSqlText, long? RawTokens, long? EmittedTokens,
    string? ArtifactDirectory, JsonElement? Summary, IReadOnlyList<VariantDetail> Variants,
    OperationDimensions Dimensions,
    AgentCall? Agent, long? RawBytes = null, long? EmittedBytes = null,
    string TokenEstimationMethod = "utf8-bytes-div-4",
    string? CompareSubject = null, string? CompareScope = null, string? BaselineVersion = null,
    string? CandidateVersion = null, JsonElement? CompareRegression = null,
    IReadOnlyList<JsonElement>? CompareCases = null, JsonElement? CompareConditions = null,
    IReadOnlyList<OperationPhaseSummary>? Phases = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OperationEventSummary>? Events = null,
    string? SetupSqlText = null, JsonElement? Conditions = null,
    IReadOnlyList<StatementRow>? Statements = null)
{
    public MutationLifecycle? MutationRequest { get; init; }
    public string? MutationRequestId => MutationRequest?.RequestId;
    public int OmittedStatements { get; init; }
    public string StatementAttribution { get; init; } = "unavailable";
}

public sealed record MutationLifecycle(string RequestId, string State, string? Status, string? Verification, string CreatedAt, string? DecidedAt, string? FinishedAt);

public sealed record OperationPhaseSummary(string Phase, int Count, long TotalMs, long MaxMs);

public sealed record OperationEventSummary(
    long OffsetMs, string Phase, long DurationMs, string Outcome, int? ErrorNumber,
    string? ClientConnectionId, int? DroppedCount);

public sealed record StoredPlan(string Hash, string Format, string Document);

public sealed record PlanOccurrenceManifest(
    string Variant,
    string? ParameterSet,
    string CaseId,
    string CaseLabel,
    int Repetition,
    int DocumentOrdinal,
    int StatementIndex,
    int? SpanStart,
    int? SpanLength,
    int? StatementId,
    int? StatementCompId,
    string? DocumentSha256,
    string? ShapeHash,
    string? Format,
    string MatchKind,
    bool Omitted,
    bool Stored);

public sealed record OperationPlanManifest(
    long OperationId,
    string State,
    IReadOnlyList<PlanOccurrenceManifest> Occurrences);

public sealed record PlanSourceMapSelection(
    long OperationId,
    string State,
    string? Engine,
    string? Variant,
    string? ParameterSet,
    string? CaseId,
    string? CaseLabel,
    int? Repetition,
    string? ExecutedText,
    IReadOnlyList<PlanOccurrenceManifest> Occurrences,
    string? ErrorCode = null,
    string? Error = null);

public sealed record PlanSourceMapResponse(
    long OperationId,
    string State,
    string? Engine,
    string? Variant,
    string? ParameterSet,
    string? CaseId,
    string? CaseLabel,
    int? Repetition,
    string? ExecutedText,
    int? AlgorithmVersion,
    IReadOnlyList<PlanSourceMapLink> Links,
    IReadOnlyList<PlanSourceMapOmission> OmittedDocuments,
    IReadOnlyList<PlanSourceMapError> Errors);

public sealed record PlanSourceMapLink(string DocumentSha256, int DocumentOrdinal, SourceLink Link);

public sealed record PlanSourceMapOmission(
    int DocumentOrdinal,
    int StatementIndex,
    string? DocumentSha256,
    string MatchKind,
    bool Omitted,
    bool Stored);

public sealed record PlanSourceMapError(
    int DocumentOrdinal,
    string? DocumentSha256,
    string Code,
    string Message);

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