using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

public sealed record SqlHarnessTargetIdentityReport(
    string RequestedServer, string RequestedDatabase, string ActualServer, string ActualDatabase, string Mode);

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
    IReadOnlyList<string> Warnings);

public sealed record SqlHarnessCompareReport(
    SqlHarnessTargetIdentityReport Target, int Repetitions, int MeasuredRunCount,
    bool ResultsEquivalent, CompareVariantReport Baseline, CompareVariantReport Candidate,
    string? ArtifactDirectory);

public sealed record SqlHarnessMeasureReport(
    SqlHarnessTargetIdentityReport Target, int Repetitions, int MeasuredRunCount,
    bool ResultsStable, CompareVariantReport Query, string? ArtifactDirectory);

internal sealed record CompareRunArtifact(
    string Variant, int Repetition, long CpuTimeMilliseconds, long ElapsedTimeMilliseconds,
    long LogicalReads, IReadOnlyDictionary<string, long> LogicalReadsByTable,
    string ResultHash, IReadOnlyList<string> PlanXmls, int MessageCount);

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
            xml, JsonSerializer.Serialize(PlanDistiller.Distill(xml), JsonOptions))).ToArray()).ToArray();
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
                lines.AppendLine(JsonSerializer.Serialize(new
                {
                    run.Variant,
                    run.Repetition,
                    run.CpuTimeMilliseconds,
                    run.ElapsedTimeMilliseconds,
                    run.LogicalReads,
                    run.LogicalReadsByTable,
                    run.ResultHash,
                    run.MessageCount,
                    PlanFiles = run.PlanXmls.Select((_, planIndex) => PlanFileName(run, index, planIndex)).ToArray(),
                    PlanJsonFiles = run.PlanXmls.Select((_, planIndex) => PlanJsonFileName(run, index, planIndex)).ToArray(),
                }, JsonLineOptions));
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
        var xmlPath = Path.Combine(directory, PlanFileName(run, runIndex, planIndex));
        var jsonPath = Path.Combine(directory, PlanJsonFileName(run, runIndex, planIndex));
        var xmlTemp = xmlPath + ".tmp";
        var jsonTemp = jsonPath + ".tmp";
        _writeText(xmlTemp, plan.Xml, new UTF8Encoding(false));
        _writeText(jsonTemp, plan.Json, new UTF8Encoding(false));
        File.Move(xmlTemp, xmlPath);
        File.Move(jsonTemp, jsonPath);
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

    private static object WithArtifactDirectory(object report, string? directory) => report switch
    {
        SqlHarnessCompareReport compare => compare with { ArtifactDirectory = directory },
        SqlHarnessMeasureReport measure => measure with { ArtifactDirectory = directory },
        _ => throw new ArgumentOutOfRangeException(nameof(report), report.GetType(), "Unsupported benchmark report type."),
    };

    private static string PlanFileName(CompareRunArtifact run, int runIndex, int planIndex) =>
        $"{run.Variant}-{run.Repetition:D3}-{runIndex:D3}-{planIndex:D3}.sqlplan";

    private static string PlanJsonFileName(CompareRunArtifact run, int runIndex, int planIndex) =>
        $"{run.Variant}-{run.Repetition:D3}-{runIndex:D3}-{planIndex:D3}.plan.json";

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafePathCharacter();
}