namespace SqlHarness.Core;

public sealed record JournalWait(string WaitType, double AverageWaitMs, double AverageWaitCount);

/// <summary>Per-table medians across measured runs. Detail counters are null when a run lacked STATISTICS IO detail (Postgres).</summary>
public sealed record JournalTableIo(
    string Table,
    long LogicalReads,
    long? ScanCount,
    long? PhysicalReads,
    long? PageServerReads,
    long? ReadAheadReads,
    long? LobLogicalReads,
    long? LobPhysicalReads,
    long? LobReadAheadReads,
    int ColdRuns);

public sealed record JournalPlanLink(int Repetition, int Ordinal, string Hash);

public sealed record JournalVariantMetrics(
    string Variant,
    string? ParameterSet,
    int? MatrixCell,
    int Runs,
    CompareDistribution? ElapsedMilliseconds,
    CompareDistribution? CpuMilliseconds,
    CompareDistribution? LogicalReads,
    long? GrantRequestedKb,
    long? GrantGrantedKb,
    long? GrantMaxUsedKb,
    int? Dop,
    long? CompileTimeMs,
    long? CompileCpuMs,
    int SpillCount,
    bool HasWarnings,
    bool HasImplicitConversion,
    int MissingIndexCount,
    IReadOnlyList<JournalWait> Waits,
    PostgresBufferCounters? Postgres,
    IReadOnlyList<JournalTableIo> TableIo,
    IReadOnlyList<JournalPlanLink> PlanLinks)
{
    public string CaseId { get; init; } = "0";
    public string CaseLabel { get; init; } = "Case 1 of 1";
    public IReadOnlyList<JournalPlanOccurrence> PlanOccurrences { get; init; } = [];
}

/// <summary>Full plan text. Sensitive: the journal stores it only with journal.storeSensitive.</summary>
public sealed record JournalPlanDocument(string Sha256, string ShapeHash, string Format, string Document);

public sealed record JournalPlanOccurrence(
    int Repetition,
    int DocumentOrdinal,
    int StatementIndex,
    string? DocumentSha,
    bool Omitted = false);

public sealed record JournalStatementMetrics(
    string Variant,
    string? ParameterSet,
    int? MatrixCell,
    int StatementOrdinal,
    string StatementHash,
    long? CpuMilliseconds,
    long? ElapsedMilliseconds,
    int? DegreeOfParallelism,
    IReadOnlyList<StatementOperatorSummary> TopOperators)
{
    public string CaseId { get; init; } = "0";
    public string CaseLabel { get; init; } = "Case 1 of 1";
}

public sealed record BenchmarkJournalRecord(
    IReadOnlyList<JournalVariantMetrics> Variants,
    IReadOnlyList<JournalPlanDocument> PlanDocuments)
{
    public IReadOnlyList<JournalStatementMetrics> Statements { get; init; } = [];
}
