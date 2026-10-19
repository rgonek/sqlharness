using SqlHarness.Core;

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
    string? DefaultB)
{
    public string? Subject { get; init; }
    public string? Database { get; init; }
}

public sealed record ScopeVersions(string Scope, IReadOnlyList<VersionRow> Versions)
{
    public string? DefaultA { get; init; }
    public string? DefaultB { get; init; }
}

public sealed record VersionRow(
    string Version,
    string Hash,
    long? LogicalReadsSum,
    long? LogicalReadsMean,
    long? ScansSum,
    double? ElapsedMedianMs,
    string CaseOutcome,
    IReadOnlyList<long> MeasuredIn,
    long LatestOperationId)
{
    public string VerdictBasis { get; init; } = "whole batch";
}

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
    IReadOnlyList<CompareMetricEvidence> Metrics)
{
    public bool IsMeasure { get; init; }
}

public sealed record CompareCaseEvidence(
    string CaseId,
    string Label,
    string? SetName,
    bool? ResultsEquivalent,
    string? RegressionVerdict)
{
    public string? MeasurementFingerprint { get; init; }
}

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
    public string? MeasurementFingerprint { get; init; }
    public IReadOnlyList<CompareStatementEvidence> Statements { get; init; } = [];
    public int OmittedStatements { get; init; }
    public long? StatementReads { get; init; }
    public string StatementAttribution { get; init; } = "unavailable";
}

public sealed record CompareTableIoEvidence(string Table, long LogicalReads, long? LobLogicalReads, long? Scans);
public sealed record CompareStatementEvidence(
    string Key, int Occurrence, string? Text, string? PlanHash, string Status, long? Reads,
    long? Scans, long? Executions, IReadOnlyList<HiddenWork> HiddenWork,
    IReadOnlyList<TableRow> Objects)
{
    public double? UdfElapsedMilliseconds { get; init; }
    public string? PlanLink { get; init; }
}

public enum PairBasis { MeasuredTogether, DifferentRunsSameConditions, ConditionsDiffer, SameSql }

public sealed record OptimizationComparison(
    string A, string B, PairBasis Basis, long? OperationId, string EquivalenceLabel,
    IReadOnlyList<CaseOption> Cases, string SelectedCase, string Aggregate, string CoverageLabel,
    MixedWarning? Mixed, IReadOnlyList<TableRow> Tables, CollapsedRow? Collapsed, ElapsedInfo Elapsed)
{
    public string VerdictBasis { get; init; } = "whole batch";
    public string View { get; init; } = "changed";
    public WholeBatchLine WholeBatch { get; init; } = new(null, null, null, "");
    public IReadOnlyList<StatementRow> Statements { get; init; } = [];
    public string? FallbackNote { get; init; }
    public int OmittedStatementsBefore { get; init; }
    public int OmittedStatementsAfter { get; init; }
    public OptimizationActionReference? BeforeAction { get; init; }
    public OptimizationActionReference? AfterAction { get; init; }
    public bool TableIoUnavailable { get; init; }
    public double? CpuBeforeMs { get; init; }
    public double? CpuAfterMs { get; init; }
    public PostgresBuffers? BuffersBefore { get; init; }
    public PostgresBuffers? BuffersAfter { get; init; }
}

public sealed record WholeBatchLine(long? Before, long? After, long? Shared, string Label)
{
    public long? SharedBefore { get; init; }
    public long? SharedAfter { get; init; }
    public long? ChangedBefore { get; init; }
    public long? ChangedAfter { get; init; }
}

public sealed record StatementRow(string Key, string Text, string Status, long? ReadsBefore, long? ReadsAfter,
    long? ScansBefore, long? ScansAfter, long? Executions, string Change, string Attribution,
    IReadOnlyList<HiddenWork> HiddenWork, IReadOnlyList<TableRow> Objects)
{
    public int Occurrence { get; init; }
    public string? PlanHashBefore { get; init; }
    public string? PlanHashAfter { get; init; }
    public string? PlanLinkBefore { get; init; }
    public string? PlanLinkAfter { get; init; }
    public double? UdfElapsedMilliseconds { get; init; }
}

public sealed record OptimizationActionReference(long OperationId, string Side, IReadOnlyList<string> CaseIds)
{
    public IReadOnlyDictionary<string, string> CaseIdsByFingerprint { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record CaseOption(string Id, string Label, string Outcome);
public sealed record MixedWarning(int Better, int Worse, string WorstCaseLabel, double WorstChangePercent);
public sealed record TableRow(string Table, long? ScansBefore, long? ScansAfter, long? ReadsBefore, long? ReadsAfter, string Change);
public sealed record CollapsedRow(int Count, string Label);
public sealed record ElapsedInfo(double? BeforeMs, double? AfterMs, string? PolicyVerdict, bool DifferentRun);
