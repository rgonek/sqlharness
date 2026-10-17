namespace SqlHarness.Dashboard;

public sealed record OptimizationSection(
    IReadOnlyList<OptimizationSubject> Subjects,
    IReadOnlyList<UnassignedCompare> Unassigned);

public sealed record OptimizationSubject(
    string Subject,
    string Database,
    string Engine,
    IReadOnlyList<ConditionsGroup> Groups);

public sealed record ConditionsGroup(
    string Fingerprint,
    string Label,
    IReadOnlyList<ScopeVersions> Scopes,
    string DefaultA,
    string? DefaultB);

public sealed record ScopeVersions(string Scope, IReadOnlyList<VersionRow> Versions);

public sealed record VersionRow(
    string Version,
    string Hash,
    long? LogicalReadsSum,
    long? LogicalReadsMean,
    long? ScansSum,
    double? ElapsedMedianMs,
    string CaseOutcome,
    IReadOnlyList<long> MeasuredIn,
    long LatestOperationId);

public sealed record UnassignedCompare(long OperationId, string StartedAt, string Status);

/// <summary>One journal compare and its actual per-case metrics, retained plans, and verdicts.</summary>
public sealed record CompareEvidence(
    long OperationId,
    string? Subject,
    string? Scope,
    string? Database,
    string? Engine,
    string? Fingerprint,
    string? BaselineHash,
    string? CandidateHash,
    string StartedAt,
    string Status,
    string? ComparisonMode,
    bool? ResultsEquivalent,
    string? RegressionVerdict,
    IReadOnlyList<CompareCaseEvidence> Cases,
    IReadOnlyList<CompareMetricEvidence> Metrics);

public sealed record CompareCaseEvidence(
    string CaseId,
    string Label,
    string? SetName,
    bool? ResultsEquivalent,
    string? RegressionVerdict);

/// <summary>One baseline or candidate per-case metric row and its journaled per-table IO rows.</summary>
public sealed record CompareMetricEvidence(
    string Variant,
    string CaseId,
    string CaseLabel,
    long? LogicalReadsMedian,
    long? ElapsedMedianMs,
    long? ScansSum,
    IReadOnlyList<CompareTableIoEvidence> TableRows)
{
    public double? CpuMedianMs { get; init; }
    public PostgresBuffers? Postgres { get; init; }
}

public sealed record CompareTableIoEvidence(string Table, long LogicalReads, long? LobLogicalReads, long? Scans);

public enum PairBasis { MeasuredTogether, DifferentRunsSameConditions, ConditionsDiffer, SameSql }

public sealed record OptimizationComparison(
    string A, string B, PairBasis Basis, long? OperationId, string EquivalenceLabel,
    IReadOnlyList<CaseOption> Cases, string SelectedCase, string Aggregate, string CoverageLabel,
    MixedWarning? Mixed, IReadOnlyList<TableRow> Tables, CollapsedRow? Collapsed, ElapsedInfo Elapsed)
{
    public bool TableIoUnavailable { get; init; }
    public double? CpuBeforeMs { get; init; }
    public double? CpuAfterMs { get; init; }
    public PostgresBuffers? BuffersBefore { get; init; }
    public PostgresBuffers? BuffersAfter { get; init; }
}

public sealed record CaseOption(string Id, string Label, string Outcome);
public sealed record MixedWarning(int Better, int Worse, string WorstCaseLabel, double WorstChangePercent);
public sealed record TableRow(string Table, long? ScansBefore, long? ScansAfter, long? ReadsBefore, long? ReadsAfter, string Change);
public sealed record CollapsedRow(int Count, string Label);
public sealed record ElapsedInfo(double? BeforeMs, double? AfterMs, string? PolicyVerdict, bool DifferentRun);
