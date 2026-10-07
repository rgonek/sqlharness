using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

public sealed record SqlHarnessTargetIdentityReport(
    string RequestedServer, string RequestedDatabase, string ActualServer, string ActualDatabase, string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Engine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransportPolicy = null);

public sealed record CompareDistribution(long Min, long Median, long Max);

public sealed record FractionalMilliseconds(decimal Min, decimal Median, decimal Max);

public static class BenchmarkMetricText
{
    public const string PostgresLogicalReadsSource =
        "logicalReads is the root plan node's Shared Hit Blocks + Shared Read Blocks + Local Hit Blocks + Local Read Blocks. Child plans and the Workers array are not added.";

    public const string PostgresRelationBufferSource =
        "Per-relation counts are that node's shared and local hit and read blocks minus its child plans. Workers are not added. The name is schema-qualified when the plan has a schema. These counts are not additive with logicalReads.";

    public const string PostgresCpuUnavailable =
        "PostgreSQL CPU time is unavailable. cpuTimeMilliseconds 0 is not a measured zero.";

    public const string PostgresBuffersMissing =
        "Root plan buffer counters are missing. logicalReads 0 is not a measured zero.";

    public const string PostgresTimingMissing =
        "Planning Time or Execution Time is missing. Whole-millisecond elapsed fields are not an exact measurement.";

    public const string PostgresSubMillisecond =
        "A positive elapsed time is below one millisecond. Whole-millisecond elapsed fields are rounded and are not an exact 0.";

    public const string PostgresRelationBuffersMissing =
        "A relation node is missing buffer counters, so it was left out of the per-relation diagnostics.";

    public const string PostgresRelationChildBuffersMissing =
        "A relation node's child plan is missing buffer counters, so that relation was left out of the per-relation diagnostics.";

    public const string PostgresRelationBuffersExceedParent =
        "A child plan reported more shared and local hit and read blocks than its parent. The negative remainder was not added.";

    public const string SidecarRows =
        "Result rows come from the unmeasured statement sidecar. EXPLAIN does not return those rows.";

    public const string StatementRows =
        "Result rows come from the measured statement.";

    public const string RowsNotCaptured =
        "Statement rows were not read. EXPLAIN does not return result rows.";
}

public sealed record BenchmarkMetricReport(
    string CpuTimeAvailability,
    string ElapsedTimeAvailability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FractionalMilliseconds? ElapsedTimeMillisecondsExact,
    bool ElapsedWholeMillisecondsAreExact,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FractionalMilliseconds? PlanningTimeMilliseconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FractionalMilliseconds? ExecutionTimeMilliseconds,
    string LogicalReadsAvailability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogicalReadsSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RelationBufferSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RelationBuffersAreAdditive,
    string ResultRowSource,
    string ResultRowSourceDescription,
    IReadOnlyList<string> Warnings)
{
    public const string Measured = "measured";
    public const string Unavailable = "unavailable";
    public const string ResultStatement = "statement";
    public const string ResultUnmeasuredSidecar = "unmeasured-sidecar";
    public const string ResultNotCaptured = "not-captured";

    internal static BenchmarkMetricReport FromArtifacts(IReadOnlyList<CompareRunArtifact> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
            throw new ArgumentException("At least one run is required.", nameof(runs));

        if (runs.All(run => run.Metrics is null))
            return FromWholeMilliseconds(runs);

        return FromRunMetrics(runs.Select(run => run.Metrics ?? WholeMillisecondRun(run)).ToArray());
    }

    // Legacy STATISTICS TIME values are already whole milliseconds. Mirror that distribution,
    // including its truncated even-count median, so the exact field does not disagree with it.
    private static BenchmarkMetricReport FromWholeMilliseconds(IReadOnlyList<CompareRunArtifact> runs)
    {
        var elapsed = Distribution.From(runs.Select(run => run.ElapsedTimeMilliseconds));
        return new BenchmarkMetricReport(
            Measured,
            Measured,
            new FractionalMilliseconds(elapsed.Min, elapsed.Median, elapsed.Max),
            true,
            null,
            null,
            Measured,
            null,
            null,
            null,
            ResultStatement,
            BenchmarkMetricText.StatementRows,
            []);
    }

    private static BenchmarkRunMetrics WholeMillisecondRun(CompareRunArtifact run) =>
        new(
            Measured,
            Measured,
            run.ElapsedTimeMilliseconds,
            true,
            null,
            null,
            Measured,
            null,
            null,
            null,
            ResultStatement,
            BenchmarkMetricText.StatementRows,
            []);

    private static BenchmarkMetricReport FromRunMetrics(IReadOnlyList<BenchmarkRunMetrics> metrics)
    {
        var elapsedUnavailable = metrics.Any(metric => metric.ElapsedTimeAvailability == Unavailable);
        var exact = elapsedUnavailable
            ? null
            : Fractional(metrics.Select(metric => metric.ElapsedTimeMillisecondsExact).ToArray());
        var source = metrics.Select(metric => metric.ResultRowSource).Distinct(StringComparer.Ordinal).ToArray();
        var resultSource = source.Length == 1
            ? source[0]
            : source.Contains(ResultUnmeasuredSidecar, StringComparer.Ordinal) ? ResultUnmeasuredSidecar
            : source.Contains(ResultStatement, StringComparer.Ordinal) ? ResultStatement
            : ResultNotCaptured;
        bool? additive = null;
        foreach (var metric in metrics)
        {
            if (metric.RelationBuffersAreAdditive is false)
                additive = false;
            else if (metric.RelationBuffersAreAdditive is true && additive is null)
                additive = true;
        }

        return new BenchmarkMetricReport(
            metrics.Any(metric => metric.CpuTimeAvailability == Unavailable) ? Unavailable : Measured,
            elapsedUnavailable ? Unavailable : Measured,
            exact,
            !elapsedUnavailable && metrics.All(metric => metric.ElapsedWholeMillisecondsAreExact),
            Fractional(metrics.Select(metric => metric.PlanningTimeMilliseconds).ToArray()),
            Fractional(metrics.Select(metric => metric.ExecutionTimeMilliseconds).ToArray()),
            metrics.Any(metric => metric.LogicalReadsAvailability == Unavailable) ? Unavailable : Measured,
            metrics.Select(metric => metric.LogicalReadsSource).FirstOrDefault(value => value is not null),
            metrics.Select(metric => metric.RelationBufferSource).FirstOrDefault(value => value is not null),
            additive,
            resultSource,
            DescribeRows(resultSource),
            metrics.SelectMany(metric => metric.Warnings).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static string DescribeRows(string source) => source switch
    {
        ResultUnmeasuredSidecar => BenchmarkMetricText.SidecarRows,
        ResultNotCaptured => BenchmarkMetricText.RowsNotCaptured,
        _ => BenchmarkMetricText.StatementRows,
    };

    private static FractionalMilliseconds? Fractional(IReadOnlyList<decimal?> samples)
    {
        if (samples.Count == 0 || samples.Any(sample => sample is null))
            return null;

        var ordered = samples.Select(sample => sample!.Value).OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2m
            : ordered[middle];
        return new FractionalMilliseconds(ordered[0], median, ordered[^1]);
    }
}

internal sealed record BenchmarkRunMetrics(
    string CpuTimeAvailability,
    string ElapsedTimeAvailability,
    decimal? ElapsedTimeMillisecondsExact,
    bool ElapsedWholeMillisecondsAreExact,
    decimal? PlanningTimeMilliseconds,
    decimal? ExecutionTimeMilliseconds,
    string LogicalReadsAvailability,
    string? LogicalReadsSource,
    string? RelationBufferSource,
    bool? RelationBuffersAreAdditive,
    string ResultRowSource,
    string ResultRowSourceDescription,
    IReadOnlyList<string> Warnings);

public sealed record CompareOperatorReport(
    int NodeId, string PhysicalOp, string? Object,
    bool HasWarnings, bool HasSpill, bool HasImplicitConversion);

public sealed record CompareVariantReport(
    string Name,
    CompareDistribution CpuTimeMilliseconds,
    CompareDistribution ElapsedTimeMilliseconds,
    CompareDistribution LogicalReads,
    IReadOnlyDictionary<string, long> TotalLogicalReadsByTable,
    IReadOnlyList<CompareOperatorReport> Operators,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyDictionary<string, CompareDistribution> LogicalReadsByTable { get; init; }
        = new Dictionary<string, CompareDistribution>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkMetricReport? MetricReport { get; init; }
}

public sealed record BenchmarkClassificationReport(string Setup, string Query);

public sealed record CompareClassificationReport(string Setup, string Baseline, string Candidate);

public sealed record BenchmarkParameterReport(string Name, string Type, int? Size, byte? Precision, byte? Scale);

public sealed record SqlHarnessCompareReport(
    SqlHarnessTargetIdentityReport Target,
    int Repetitions,
    int MeasuredRunCount,
    bool? ResultsEquivalent,
    CompareVariantReport Baseline,
    CompareVariantReport Candidate,
    string? ArtifactDirectory)
{
    public ResultEquivalenceReport Equivalence { get; init; } =
        new(ResultComparisonMode.Ordered, ResultsEquivalent, 0, 0, 0);

    public CompareClassificationReport Classification { get; init; } =
        new("none", "read-only", "read-only");

    public IReadOnlyList<BenchmarkParameterReport> Parameters { get; init; } = [];
}

public sealed record SqlHarnessMeasureReport(
    SqlHarnessTargetIdentityReport Target, int Repetitions, int MeasuredRunCount,
    bool ResultsStable, CompareVariantReport Query, string? ArtifactDirectory)
{
    public BenchmarkClassificationReport Classification { get; init; } =
        new("none", "read-only");

    public IReadOnlyList<BenchmarkParameterReport> Parameters { get; init; } = [];
}

public sealed record MeasureParameterSetReport(
    string Name,
    IReadOnlyList<MeasureParameterMetadata> Parameters,
    string ValueHash,
    int Repetitions,
    bool ResultsStable,
    string? ResultHash,
    CompareVariantReport Metrics,
    IReadOnlyList<string> PlanHashes);

public sealed record MeasureCrossSetSummary(
    string? MinimumMedianElapsedSet,
    long MinimumMedianElapsedMilliseconds,
    string? MaximumMedianElapsedSet,
    long MaximumMedianElapsedMilliseconds,
    string? MinimumMedianCpuSet,
    long MinimumMedianCpuMilliseconds,
    string? MaximumMedianCpuSet,
    long MaximumMedianCpuMilliseconds,
    string? MinimumMedianReadsSet,
    long MinimumMedianLogicalReads,
    string? MaximumMedianReadsSet,
    long MaximumMedianLogicalReads)
{
    public string CpuTimeAvailability { get; init; } = BenchmarkMetricReport.Measured;

    public string ElapsedTimeAvailability { get; init; } = BenchmarkMetricReport.Measured;

    public string LogicalReadsAvailability { get; init; } = BenchmarkMetricReport.Measured;

    public bool ElapsedWholeMillisecondsAreExact { get; init; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MinimumMedianElapsedMillisecondsExact { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MaximumMedianElapsedMillisecondsExact { get; init; }
}

public sealed record SqlHarnessMeasureSetReport(
    SqlHarnessTargetIdentityReport Target,
    int Repeat,
    int MeasuredRunCount,
    int SetupExecutionCount,
    IReadOnlyList<string> WarmupOrder,
    string MeasuredOrderRule,
    string PlanCacheWarning,
    IReadOnlyList<MeasureParameterSetReport> Sets,
    MeasureCrossSetSummary CrossSetSummary,
    string? ArtifactDirectory);

/// <summary>
/// The per-run metric fields (CpuTimeMilliseconds, ElapsedTimeMilliseconds,
/// LogicalReads) are authoritative only when the corresponding
/// <see cref="BenchmarkRunMetrics"/> availability is "measured". When
/// "unavailable", the stored numbers are preserved parsed diagnostics (for
/// example trailing STATISTICS output), not measurements.
/// </summary>
internal sealed record CompareRunArtifact(
    string Variant, int Repetition, long CpuTimeMilliseconds, long ElapsedTimeMilliseconds,
    long LogicalReads, IReadOnlyDictionary<string, long> LogicalReadsByTable,
    string ResultHash, IReadOnlyList<string> PlanXmls, int MessageCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParameterSet = null,
    BenchmarkRunMetrics? Metrics = null)
{
    /// <summary>Per-table STATISTICS IO detail counters (SQL Server; see <see cref="StatisticsIoDetailParser"/>). Journal only; never serialized.</summary>
    [JsonIgnore]
    public IReadOnlyList<TableIoCounters> TableIo { get; init; } = [];

    /// <summary>Zero-based compare --matrix cell index; never the matrix value. Journal only; never serialized.</summary>
    [JsonIgnore]
    public int? MatrixCell { get; init; }
}

internal interface ICompareArtifactWriter
{
    string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target);

    // Owner-aware publish. The default keeps existing fakes compiling
    // with their behavior unchanged; the production writer overrides it
    // to stamp the manifest. Callers always use this overload.
    string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target, ArtifactOwner? owner) =>
        Write(report, runs, target);
}

internal sealed partial class CompareArtifactWriter : ICompareArtifactWriter
{
    private readonly ArtifactDirectoryPublisher _publisher;

    internal CompareArtifactWriter() : this(SqlHarnessPaths.CompareDir, () => DateTimeOffset.UtcNow) { }

    internal CompareArtifactWriter(string root, Func<DateTimeOffset> utcNow)
        : this(root, utcNow, File.WriteAllText, File.Delete, Directory.Delete) { }

    internal CompareArtifactWriter(string root, Func<DateTimeOffset> utcNow, Action<string, string, Encoding> writeText)
        : this(root, utcNow, writeText, File.Delete, Directory.Delete) { }

    internal CompareArtifactWriter(
        string root,
        Func<DateTimeOffset> utcNow,
        Action<string, string, Encoding> writeText,
        Action<string> deleteFile,
        Action<string, bool> deleteDirectory,
        Action<string, string>? moveDirectory = null,
        Action<string, string>? moveFile = null)
    {
        _publisher = new ArtifactDirectoryPublisher(
            root, utcNow, writeText, moveDirectory, moveFile, deleteFile, deleteDirectory);
    }

    public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target) =>
        Write(report, runs, target, owner: null);

    public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target, ArtifactOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(runs);
        var preparedPlans = runs.Select(run => run.PlanXmls.Select(xml => new PreparedPlan(
            xml, DistillForArtifact(xml))).ToArray()).ToArray();
        // Rejects unsupported report types before any directory is reserved.
        var persistedReport = WithArtifactDirectory(report, null);

        return _publisher.Publish(target, (staging, directory) =>
        {
            persistedReport = WithArtifactDirectory(persistedReport, directory);
            var plansDirectory = Path.Combine(staging, "plans");
            Directory.CreateDirectory(plansDirectory);
            _publisher.WriteText(Path.Combine(staging, "report.json"),
                JsonSerializer.Serialize(persistedReport, ArtifactDirectoryPublisher.JsonOptions), new UTF8Encoding(false));
            _publisher.WriteText(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(ArtifactManifest.ForReport(persistedReport, owner), ArtifactDirectoryPublisher.JsonOptions), new UTF8Encoding(false));
            for (var index = 0; index < runs.Count; index++)
            {
                var run = runs[index];
                for (var planIndex = 0; planIndex < preparedPlans[index].Length; planIndex++)
                    WritePair(plansDirectory, run, index, planIndex, preparedPlans[index][planIndex]);
            }

            var lines = new StringBuilder();
            for (var index = 0; index < runs.Count; index++)
            {
                var run = runs[index];
                // A null set is omitted so legacy measure and compare runs.jsonl bytes stay the same.
                lines.AppendLine(JsonSerializer.Serialize(new RunMetadata(
                    run.Variant,
                    run.ParameterSet is null ? null : SanitizeSetLabel(run.ParameterSet),
                    run.Repetition,
                    run.CpuTimeMilliseconds,
                    run.ElapsedTimeMilliseconds,
                    run.LogicalReads,
                    run.LogicalReadsByTable,
                    run.ResultHash,
                    run.MessageCount,
                    run.PlanXmls.Select((xml, planIndex) => PlanFileName(run, index, planIndex, xml)).ToArray(),
                    run.PlanXmls.Select((_, planIndex) => PlanJsonFileName(run, index, planIndex)).ToArray()), ArtifactDirectoryPublisher.JsonLineOptions));
            }
            _publisher.WriteText(Path.Combine(staging, "runs.jsonl"), lines.ToString(), new UTF8Encoding(false));
        });
    }

    private void WritePair(string directory, CompareRunArtifact run, int runIndex, int planIndex, PreparedPlan plan)
    {
        var xmlPath = CombineChild(directory, PlanFileName(run, runIndex, planIndex, plan.Xml));
        var jsonPath = CombineChild(directory, PlanJsonFileName(run, runIndex, planIndex));
        var xmlTemp = xmlPath + ".tmp";
        var jsonTemp = jsonPath + ".tmp";
        _publisher.WriteText(xmlTemp, plan.Xml, new UTF8Encoding(false));
        _publisher.WriteText(jsonTemp, plan.Json, new UTF8Encoding(false));
        _publisher.MoveFile(xmlTemp, xmlPath);
        _publisher.MoveFile(jsonTemp, jsonPath);
    }

    private static string CombineChild(string directory, string name)
    {
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
            throw new IOException("Plan filename escaped the artifact directory.");

        var path = Path.Combine(directory, name);
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Plan filename escaped the artifact directory.");

        return path;
    }

    private sealed record PreparedPlan(string Xml, string Json);

    // ParameterSet stays null for measure and compare, and is left out of the JSON line.
    private sealed record RunMetadata(
        string Variant,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParameterSet,
        int Repetition,
        long CpuTimeMilliseconds,
        long ElapsedTimeMilliseconds,
        long LogicalReads,
        IReadOnlyDictionary<string, long> LogicalReadsByTable,
        string ResultHash,
        int MessageCount,
        IReadOnlyList<string> PlanFiles,
        IReadOnlyList<string> PlanJsonFiles);

    private static object WithArtifactDirectory(object report, string? directory) => report switch
    {
        SqlHarnessCompareReport compare => compare with { ArtifactDirectory = directory },
        SqlHarnessMeasureReport measure => measure with { ArtifactDirectory = directory },
        SqlHarnessMeasureSetReport measureSet => measureSet with { ArtifactDirectory = directory },
        _ => throw new ArgumentOutOfRangeException(nameof(report), report.GetType(), "Unsupported benchmark report type."),
    };

    private static string DistillForArtifact(string document) =>
        JsonSerializer.Serialize(
            IsJsonPlan(document)
                ? Postgres.PostgresPlanDistiller.Distill(document)
                : PlanDistiller.Distill(document),
            ArtifactDirectoryPublisher.JsonOptions);

    private static string PlanFileName(CompareRunArtifact run, int runIndex, int planIndex, string document)
    {
        var extension = IsJsonPlan(document) ? ".explain.json" : ".sqlplan";
        return PlanStem(run, runIndex, planIndex) + extension;
    }

    private static string PlanStem(CompareRunArtifact run, int runIndex, int planIndex)
    {
        var stem = $"{run.Variant}-{run.Repetition:D3}-{runIndex:D3}-{planIndex:D3}";
        if (run.ParameterSet is null)
            return stem;

        return SanitizeSetLabel(run.ParameterSet) + "-" + stem;
    }

    // Accepted labels are already one path segment. Anything else is reduced so it cannot escape.
    private static string SanitizeSetLabel(string label)
    {
        // '$' matches before a trailing newline; the label must be the entire string.
        var match = SafeSetLabel().Match(label);
        if (label.Length is > 0 and <= 64 && match.Success && match.Length == label.Length)
            return label;

        var sanitized = ArtifactDirectoryPublisher.SanitizePathSegment(label);
        if (sanitized.Length > 64)
            sanitized = sanitized[..64].Trim('-');
        if (sanitized.Length == 0)
            return "set";

        return sanitized;
    }

    private static bool IsJsonPlan(string document)
    {
        var trimmed = document.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[';
    }

    private static string PlanJsonFileName(CompareRunArtifact run, int runIndex, int planIndex) =>
        PlanStem(run, runIndex, planIndex) + ".plan.json";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSetLabel();
}