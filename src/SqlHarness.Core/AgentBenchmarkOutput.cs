using System.Text.Json.Serialization;

namespace SqlHarness.Core;

/// <summary>Model-facing min/median/max. Missing evidence is a null distribution, not numeric zero.</summary>
public sealed record AgentMetricDistribution(decimal? Min, decimal? Median, decimal? Max);

/// <summary>Concise resolved target for MCP benchmark results.</summary>
public sealed record AgentResolvedTarget(
    string Server,
    string Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Engine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedServer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedDatabase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransportPolicy = null);

/// <summary>Audience-specific metric projection. Stored measurements stay on the source report.</summary>
public sealed record AgentBenchmarkVariant(
    AgentMetricDistribution? CpuTimeMilliseconds,
    AgentMetricDistribution? ElapsedTimeMilliseconds,
    AgentMetricDistribution? LogicalReads,
    IReadOnlyDictionary<string, AgentMetricDistribution> LogicalReadsByTable,
    IReadOnlyList<string> Warnings,
    string CpuTimeAvailability,
    string ElapsedTimeAvailability,
    string LogicalReadsAvailability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogicalReadsSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RelationBufferSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RelationBuffersAreAdditive = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResultRowSource = null);

public sealed record AgentRegressionDiagnostic(
    string Status,
    decimal? Delta,
    decimal? DeltaPercent);

public sealed record AgentRegressionReport(
    string Verdict,
    IReadOnlyList<string> Reasons,
    int PolicyVersion,
    AgentRegressionDiagnostic Elapsed,
    AgentRegressionDiagnostic LogicalReads,
    AgentRegressionDiagnostic Cpu);

/// <summary>Compact statement-counter coverage grouped by exact execution case and variant.</summary>
public sealed record AgentStatementEvidenceCase(
    string Variant,
    string? ParameterSet,
    int? MatrixCell,
    string CounterAttribution,
    int StatementCount,
    int PartialStatementCount,
    int UnavailableStatementCount,
    IReadOnlyList<string> Limitations);

/// <summary>Saved statement-counter coverage. It describes collected counters, not total query cost.</summary>
public sealed record AgentStatementEvidence(
    string EvidenceAvailability,
    string? CoverageBasis,
    string? CoverageDescription,
    IReadOnlyList<AgentStatementEvidenceCase> Cases,
    int OmittedStatements,
    int OmittedCases = 0);

public sealed record AgentCompareOutput(
    AgentResolvedTarget Target,
    int Repetitions,
    CompareClassificationReport Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    ResultEquivalenceReport Equivalence,
    AgentBenchmarkVariant Baseline,
    AgentBenchmarkVariant Candidate,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentRegressionReport? Regression = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentStatementEvidence? StatementEvidence = null);

public sealed record AgentMeasureOutput(
    AgentResolvedTarget Target,
    int Repetitions,
    bool ResultsStable,
    BenchmarkClassificationReport Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    AgentBenchmarkVariant Query,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentStatementEvidence? StatementEvidence = null);

public sealed record AgentMeasureSetItem(
    string Name,
    IReadOnlyList<MeasureParameterMetadata> Parameters,
    int Repetitions,
    bool ResultsStable,
    AgentMetricDistribution? CpuTimeMilliseconds,
    AgentMetricDistribution? ElapsedTimeMilliseconds,
    AgentMetricDistribution? LogicalReads,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    IReadOnlyList<string> Warnings,
    string CpuTimeAvailability,
    string ElapsedTimeAvailability,
    string LogicalReadsAvailability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogicalReadsSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RelationBufferSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RelationBuffersAreAdditive = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResultRowSource = null);

/// <summary>
/// Cross-set extrema for MCP. Missing evidence is a null value; elapsed uses the exact median when present.
/// </summary>
public sealed record AgentMeasureCrossSetSummary(
    string? MinimumMedianElapsedSet,
    decimal? MinimumMedianElapsedMilliseconds,
    string? MaximumMedianElapsedSet,
    decimal? MaximumMedianElapsedMilliseconds,
    string? MinimumMedianCpuSet,
    decimal? MinimumMedianCpuMilliseconds,
    string? MaximumMedianCpuSet,
    decimal? MaximumMedianCpuMilliseconds,
    string? MinimumMedianReadsSet,
    decimal? MinimumMedianLogicalReads,
    string? MaximumMedianReadsSet,
    decimal? MaximumMedianLogicalReads,
    string CpuTimeAvailability,
    string ElapsedTimeAvailability,
    string LogicalReadsAvailability);

public sealed record AgentMeasureSetOutput(
    AgentResolvedTarget Target,
    string MeasuredOrderRule,
    string PlanCacheWarning,
    IReadOnlyList<AgentMeasureSetItem> Sets,
    AgentMeasureCrossSetSummary CrossSetSummary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentStatementEvidence? StatementEvidence = null);

public sealed record AgentMatrixCellDetail(
    int Index,
    AgentCompareDetail Compare);

public sealed record AgentCompareDetail(
    AgentBenchmarkVariant Baseline,
    AgentBenchmarkVariant Candidate,
    ResultEquivalenceReport Equivalence,
    IReadOnlyList<NoteworthyOperatorSummary> NoteworthyOperators,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentRegressionReport? Regression);

public sealed record AgentMatrixOutput(
    AgentResolvedTarget? Target,
    int? Repetitions,
    CompareClassificationReport? Classification,
    IReadOnlyList<BenchmarkParameterReport> Parameters,
    ResultComparisonMode? ComparisonMode,
    string? ParameterName,
    string? ParameterType,
    IReadOnlyList<AgentMatrixCase> Cases,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AgentMatrixCellDetail>? Details,
    int CaseCount,
    int OmittedCaseCount,
    int OmittedDetailCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MatrixArtifactId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Continuation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MatrixExecutionCoverage? Coverage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentStatementEvidence? StatementEvidence = null);

/// <summary>Maps CLI/storage summaries onto MCP benchmark DTOs without changing stored measurements.</summary>
public static class AgentBenchmarkProjection
{
    public static AgentResolvedTarget Target(SqlHarnessTargetIdentityReport target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new AgentResolvedTarget(
            target.ActualServer,
            target.ActualDatabase,
            target.Engine,
            RequestedIfDifferent(target.RequestedServer, target.ActualServer),
            RequestedIfDifferent(target.RequestedDatabase, target.ActualDatabase),
            string.IsNullOrWhiteSpace(target.TransportPolicy) ? null : target.TransportPolicy);
    }

    public static AgentCompareOutput Compare(CompareBenchmarkSummary summary) =>
        new(
            Target(summary.Target),
            summary.Repetitions,
            summary.Classification,
            summary.Parameters,
            summary.Equivalence,
            Variant(summary.Baseline),
            Variant(summary.Candidate),
            summary.NoteworthyOperators,
            ArtifactIds.FromDirectory(summary.ArtifactDirectory),
            Regression(summary.Regression));

    public static AgentMeasureOutput Measure(MeasureBenchmarkSummary summary) =>
        new(
            Target(summary.Target),
            summary.Repetitions,
            summary.ResultsStable,
            summary.Classification,
            summary.Parameters,
            Variant(summary.Query),
            summary.NoteworthyOperators,
            ArtifactIds.FromDirectory(summary.ArtifactDirectory));

    public static AgentMeasureSetOutput MeasureSet(MeasureSetBenchmarkSummary summary) =>
        new(
            Target(summary.Target),
            summary.MeasuredOrderRule,
            summary.PlanCacheWarning,
            summary.Sets.Select(Set).ToArray(),
            CrossSet(summary.CrossSetSummary),
            ArtifactIds.FromDirectory(summary.ArtifactDirectory));

    internal static object? WithStatementEvidence(object? output, AgentStatementEvidence? evidence) =>
        (output, evidence) switch
        {
            (AgentCompareOutput value, not null) => value with { StatementEvidence = evidence },
            (AgentMeasureOutput value, not null) => value with { StatementEvidence = evidence },
            (AgentMeasureSetOutput value, not null) => value with { StatementEvidence = evidence },
            (AgentMatrixOutput value, not null) => value with { StatementEvidence = evidence },
            _ => output,
        };

    public static AgentMatrixOutput Matrix(CompareMatrixBenchmarkSummary summary) =>
        new(
            summary.Target is null ? null : Target(summary.Target),
            summary.Repetitions,
            summary.Classification,
            summary.Parameters,
            summary.ComparisonMode,
            summary.ParameterName,
            summary.ParameterType,
            summary.Cases,
            summary.Details.Select(detail => new AgentMatrixCellDetail(
                detail.Index,
                new AgentCompareDetail(
                    Variant(detail.Compare.Baseline),
                    Variant(detail.Compare.Candidate),
                    detail.Compare.Equivalence,
                    detail.Compare.NoteworthyOperators,
                    Regression(detail.Compare.Regression)))).ToArray(),
            summary.CaseCount,
            summary.OmittedCaseCount,
            summary.OmittedDetailCount,
            summary.MatrixArtifactId,
            summary.Continuation,
            summary.Coverage);

    public static AgentBenchmarkVariant Variant(BenchmarkVariantSummary variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var report = variant.MetricReport;
        var cpuAvailability = report?.CpuTimeAvailability ?? BenchmarkMetricReport.Measured;
        var elapsedAvailability = report?.ElapsedTimeAvailability ?? BenchmarkMetricReport.Measured;
        var readsAvailability = report?.LogicalReadsAvailability ?? BenchmarkMetricReport.Measured;
        return new AgentBenchmarkVariant(
            cpuAvailability == BenchmarkMetricReport.Unavailable ? null : FromLong(variant.CpuTimeMilliseconds),
            elapsedAvailability == BenchmarkMetricReport.Unavailable ? null : Elapsed(variant, report),
            readsAvailability == BenchmarkMetricReport.Unavailable ? null : FromLong(variant.LogicalReads),
            readsAvailability == BenchmarkMetricReport.Unavailable
                ? new Dictionary<string, AgentMetricDistribution>(StringComparer.Ordinal)
                : variant.LogicalReadsByTable.ToDictionary(pair => pair.Key, pair => FromLong(pair.Value), StringComparer.Ordinal),
            variant.Warnings,
            cpuAvailability,
            elapsedAvailability,
            readsAvailability,
            report?.LogicalReadsSource,
            report?.RelationBufferSource,
            report?.RelationBuffersAreAdditive,
            report?.ResultRowSource);
    }

    private static AgentMeasureSetItem Set(MeasureParameterSetSummary set)
    {
        var report = set.MetricReport;
        var cpuAvailability = report?.CpuTimeAvailability ?? BenchmarkMetricReport.Measured;
        var elapsedAvailability = report?.ElapsedTimeAvailability ?? BenchmarkMetricReport.Measured;
        var readsAvailability = report?.LogicalReadsAvailability ?? BenchmarkMetricReport.Measured;
        var elapsed = elapsedAvailability == BenchmarkMetricReport.Unavailable
            ? null
            : report?.ElapsedTimeMillisecondsExact is { } exact
                ? new AgentMetricDistribution(exact.Min, exact.Median, exact.Max)
                : new AgentMetricDistribution(set.MinimumElapsedMilliseconds, set.MedianElapsedMilliseconds, set.MaximumElapsedMilliseconds);
        return new AgentMeasureSetItem(
            set.Name,
            set.Parameters,
            set.Repetitions,
            set.ResultsStable,
            cpuAvailability == BenchmarkMetricReport.Unavailable
                ? null
                : new AgentMetricDistribution(set.MinimumCpuMilliseconds, set.MedianCpuMilliseconds, set.MaximumCpuMilliseconds),
            elapsed,
            readsAvailability == BenchmarkMetricReport.Unavailable
                ? null
                : new AgentMetricDistribution(set.MinimumLogicalReads, set.MedianLogicalReads, set.MaximumLogicalReads),
            set.NoteworthyOperators,
            MergeWarnings(set.Warnings, report?.Warnings),
            cpuAvailability,
            elapsedAvailability,
            readsAvailability,
            report?.LogicalReadsSource,
            report?.RelationBufferSource,
            report?.RelationBuffersAreAdditive,
            report?.ResultRowSource);
    }

    private static AgentMeasureCrossSetSummary CrossSet(MeasureCrossSetSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var elapsedUnavailable = summary.ElapsedTimeAvailability == BenchmarkMetricReport.Unavailable;
        var cpuUnavailable = summary.CpuTimeAvailability == BenchmarkMetricReport.Unavailable;
        var readsUnavailable = summary.LogicalReadsAvailability == BenchmarkMetricReport.Unavailable;
        return new AgentMeasureCrossSetSummary(
            summary.MinimumMedianElapsedSet,
            elapsedUnavailable ? null : summary.MinimumMedianElapsedMillisecondsExact ?? summary.MinimumMedianElapsedMilliseconds,
            summary.MaximumMedianElapsedSet,
            elapsedUnavailable ? null : summary.MaximumMedianElapsedMillisecondsExact ?? summary.MaximumMedianElapsedMilliseconds,
            summary.MinimumMedianCpuSet,
            cpuUnavailable ? null : summary.MinimumMedianCpuMilliseconds,
            summary.MaximumMedianCpuSet,
            cpuUnavailable ? null : summary.MaximumMedianCpuMilliseconds,
            summary.MinimumMedianReadsSet,
            readsUnavailable ? null : summary.MinimumMedianLogicalReads,
            summary.MaximumMedianReadsSet,
            readsUnavailable ? null : summary.MaximumMedianLogicalReads,
            summary.CpuTimeAvailability,
            summary.ElapsedTimeAvailability,
            summary.LogicalReadsAvailability);
    }

    private static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> variant, IReadOnlyList<string>? report)
    {
        if (report is null || report.Count == 0)
            return variant;
        if (variant.Count == 0)
            return report;
        return variant.Concat(report).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static AgentRegressionReport? Regression(RegressionReport? report) =>
        report is null ? null : new AgentRegressionReport(
            report.Verdict,
            report.Reasons,
            report.PolicyVersion,
            new AgentRegressionDiagnostic(report.Elapsed.Status, report.Elapsed.Delta, report.Elapsed.DeltaPercent),
            new AgentRegressionDiagnostic(report.LogicalReads.Status, report.LogicalReads.Delta, report.LogicalReads.DeltaPercent),
            new AgentRegressionDiagnostic(report.Cpu.Status, report.Cpu.Delta, report.Cpu.DeltaPercent));

    private static AgentMetricDistribution FromLong(CompareDistribution distribution) =>
        new(distribution.Min, distribution.Median, distribution.Max);

    private static AgentMetricDistribution Elapsed(BenchmarkVariantSummary variant, BenchmarkMetricReport? report) =>
        report?.ElapsedTimeMillisecondsExact is { } exact
            ? new AgentMetricDistribution(exact.Min, exact.Median, exact.Max)
            : FromLong(variant.ElapsedTimeMilliseconds);

    private static string? RequestedIfDifferent(string requested, string actual) =>
        string.Equals(requested, actual, StringComparison.Ordinal) ? null : requested;
}

internal static class AgentStatementEvidenceProjection
{
    private const int MaximumCompactCases = 8;

    internal static AgentStatementEvidence Create(string artifactKind, IReadOnlyList<CompareRunArtifact>? runs)
    {
        if (runs is null || runs.Count == 0)
            return new("unavailable", null, null, [], 0);

        var section = ArtifactStatementProjector.Project(artifactKind, runs);
        var statementsByCase = section.Statements
            .GroupBy(statement => (statement.Variant, statement.ParameterSet, statement.MatrixCell))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var runsByCase = runs
            .GroupBy(run => (run.Variant, run.ParameterSet, run.MatrixCell))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var omittedByCase = section.OmittedByCase
            .ToDictionary(item => (item.Variant, item.ParameterSet, item.MatrixCell), item => item.Count);
        var caseKeys = statementsByCase.Keys.Concat(runsByCase.Keys).ToHashSet();

        var groups = caseKeys
            .Select(key =>
            {
                var rows = statementsByCase.GetValueOrDefault(key, []);
                var caseRuns = runsByCase.GetValueOrDefault(key, []);
                var missingRunCount = caseRuns
                    .GroupBy(run => run.Repetition)
                    .Sum(group => Math.Max(0, group.Count() - rows.Count(row => row.Repetition == group.Key)));
                var partial = rows.Count(row => row.CounterAttribution == "partial");
                var unavailable = rows.Count(row => row.CounterAttribution == "unavailable");
                var attribution = partial > 0 || (rows.Length > 0 && omittedByCase.GetValueOrDefault(key) > 0) || (missingRunCount > 0 && rows.Length > 0)
                    ? "partial"
                    : rows.Length > 0 && unavailable == 0 && rows.All(row => row.CounterAttribution == "complete")
                        ? "complete"
                        : "unavailable";
                var limitations = rows.SelectMany(row => row.Limitations).ToList();
                if (missingRunCount > 0)
                    limitations.Add($"Statement evidence is missing for {missingRunCount} of {caseRuns.Length} runs.");
                return new AgentStatementEvidenceCase(
                    key.Variant,
                    key.ParameterSet,
                    key.MatrixCell,
                    attribution,
                    rows.Length,
                    partial,
                    unavailable,
                    limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            })
            .OrderByDescending(item => item.CounterAttribution == "partial")
            .ThenBy(item => item.Variant, StringComparer.Ordinal)
            .ThenBy(item => item.ParameterSet, StringComparer.Ordinal)
            .ThenBy(item => item.MatrixCell)
            .ToArray();

        return new AgentStatementEvidence(
            section.EvidenceAvailability,
            section.CoverageBasis,
            section.CoverageDescription,
            groups.Take(MaximumCompactCases).ToArray(),
            section.OmittedStatements,
            Math.Max(0, groups.Length - MaximumCompactCases));
    }
}