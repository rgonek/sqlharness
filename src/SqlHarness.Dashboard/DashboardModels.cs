using System.Text.Json;

namespace SqlHarness.Dashboard;

public sealed record Page<T>(IReadOnlyList<T> Items, long? NextCursor);

public sealed record SessionQuery(
    string? Agent = null, string? Transport = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long? Cursor = null, int Limit = JournalReader.DefaultLimit);

public sealed record OperationQuery(
    long? SessionId = null, string? Status = null, string? Operation = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, long? Cursor = null, int Limit = JournalReader.DefaultLimit);

public sealed record StatsQuery(DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record SessionSummary(
    long Id, string SessionKey, string AgentKind, string Transport, string Source, string? ClientName,
    string? ClientVersion, string? McpMode, string? Cwd, string FirstSeen, string LastSeen,
    int Operations, int Failed, int Rejected, int Running, int Abandoned);

public sealed record OperationSummary(
    long Id, long SessionId, string AgentKind, string Operation, string Status, int? ExitCode, string? ErrorKind,
    string StartedAt, string UpdatedAt, string? FinishedAt, long? DurationMs, string? Profile, string? Engine,
    string? Server, string? Database, bool MutationRequested, string? SqlHash, long? RowsReturned,
    long? LogicalReadsMedian, bool HasSpill, bool ColdCache, bool OverGranted, JsonElement? Progress,
    string? ErrorMessage);

public sealed record SessionDetail(SessionSummary Session, IReadOnlyList<OperationSummary> Operations);

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
    string? ArtifactDirectory, JsonElement? Summary, IReadOnlyList<VariantDetail> Variants);

public sealed record StoredPlan(string Hash, string Format, string Document);

public sealed record DayAgentCount(string Day, string AgentKind, int Count);

public sealed record KeyCount(string Key, int Count);

public sealed record SqlHashStat(string SqlHash, int Count, long TotalDurationMs, long MaxDurationMs);

public sealed record TableReadStat(string Table, long LogicalReads, int Operations);

public sealed record WaitStat(string WaitType, double TotalWaitMs);

public sealed record TargetStat(string? Profile, string? Database, int Count);

/// <summary>Token totals over operations that carry both a raw and an emitted count (MCP rows carry raw only).</summary>
public sealed record TokenStat(long Raw, long Emitted);

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
    TokenStat Tokens,
    int SpillOperations,
    int ColdCacheOperations);