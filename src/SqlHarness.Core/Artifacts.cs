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
    string MinimumMedianElapsedSet,
    long MinimumMedianElapsedMilliseconds,
    string MaximumMedianElapsedSet,
    long MaximumMedianElapsedMilliseconds,
    string MinimumMedianCpuSet,
    long MinimumMedianCpuMilliseconds,
    string MaximumMedianCpuSet,
    long MaximumMedianCpuMilliseconds,
    string MinimumMedianReadsSet,
    long MinimumMedianLogicalReads,
    string MaximumMedianReadsSet,
    long MaximumMedianLogicalReads);

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

internal sealed record CompareRunArtifact(
    string Variant, int Repetition, long CpuTimeMilliseconds, long ElapsedTimeMilliseconds,
    long LogicalReads, IReadOnlyDictionary<string, long> LogicalReadsByTable,
    string ResultHash, IReadOnlyList<string> PlanXmls, int MessageCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParameterSet = null);

internal interface ICompareArtifactWriter
{
    string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target);
}

internal sealed partial class CompareArtifactWriter : ICompareArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string, string, Encoding> _writeText;
    private readonly Action<string> _deleteFile;
    private readonly Action<string, bool> _deleteDirectory;

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
        Action<string, bool> deleteDirectory)
    {
        _root = Path.GetFullPath(root);
        _utcNow = utcNow;
        _writeText = writeText;
        _deleteFile = deleteFile;
        _deleteDirectory = deleteDirectory;
    }

    public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(runs);
        var preparedPlans = runs.Select(run => run.PlanXmls.Select(xml => new PreparedPlan(
            xml, DistillForArtifact(xml))).ToArray()).ToArray();
        var persistedReport = WithArtifactDirectory(report, null);
        var safeTarget = UnsafePathCharacter().Replace(target, "-").Trim('-');
        if (string.IsNullOrEmpty(safeTarget)) safeTarget = "target";

        Directory.CreateDirectory(_root);
        string directory;
        do
        {
            directory = Path.Combine(_root, $"{_utcNow():yyyyMMddTHHmmssfffZ}-{safeTarget}-{Guid.NewGuid():N}");
        } while (Directory.Exists(directory));

        var staging = directory + ".staging-" + Guid.NewGuid().ToString("N");
        persistedReport = WithArtifactDirectory(persistedReport, directory);

        try
        {
            Directory.CreateDirectory(staging);
            var plansDirectory = Path.Combine(staging, "plans");
            Directory.CreateDirectory(plansDirectory);
            _writeText(Path.Combine(staging, "report.json"),
                JsonSerializer.Serialize(persistedReport, JsonOptions), new UTF8Encoding(false));
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
                    run.PlanXmls.Select((_, planIndex) => PlanJsonFileName(run, index, planIndex)).ToArray()), JsonLineOptions));
            }
            _writeText(Path.Combine(staging, "runs.jsonl"), lines.ToString(), new UTF8Encoding(false));
            Directory.Move(staging, directory);
        }
        catch
        {
            Cleanup(staging);
            throw;
        }
        return directory;
    }

    private void WritePair(string directory, CompareRunArtifact run, int runIndex, int planIndex, PreparedPlan plan)
    {
        var xmlPath = CombineChild(directory, PlanFileName(run, runIndex, planIndex, plan.Xml));
        var jsonPath = CombineChild(directory, PlanJsonFileName(run, runIndex, planIndex));
        var xmlTemp = xmlPath + ".tmp";
        var jsonTemp = jsonPath + ".tmp";
        _writeText(xmlTemp, plan.Xml, new UTF8Encoding(false));
        _writeText(jsonTemp, plan.Json, new UTF8Encoding(false));
        File.Move(xmlTemp, xmlPath);
        File.Move(jsonTemp, jsonPath);
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

    private void Cleanup(string directory)
    {
        if (!Directory.Exists(directory)) return;
        string[] files;
        try { files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories); }
        catch { files = []; }
        foreach (var file in files) Try(() => _deleteFile(file));

        string[] directories;
        try { directories = Directory.GetDirectories(directory, "*", SearchOption.AllDirectories); }
        catch { directories = []; }
        foreach (var child in directories.OrderByDescending(path => path.Length))
            Try(() => _deleteDirectory(child, false));
        Try(() => _deleteDirectory(directory, false));
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch { }
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
            JsonOptions);

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

        var sanitized = UnsafePathCharacter().Replace(label, "-").Trim('-');
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

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafePathCharacter();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSetLabel();
}