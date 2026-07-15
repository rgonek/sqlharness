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

    internal CompareArtifactWriter() : this(SqlHarnessPaths.CompareDir, () => DateTimeOffset.UtcNow) { }

    internal CompareArtifactWriter(string root, Func<DateTimeOffset> utcNow)
    {
        _root = Path.GetFullPath(root);
        _utcNow = utcNow;
    }

    public string Write(object report, IReadOnlyList<CompareRunArtifact> runs, string target)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(runs);
        var persistedReport = WithArtifactDirectory(report, null);
        var safeTarget = UnsafePathCharacter().Replace(target, "-").Trim('-');
        if (string.IsNullOrEmpty(safeTarget)) safeTarget = "target";

        Directory.CreateDirectory(_root);
        string directory;
        do
        {
            directory = Path.Combine(_root, $"{_utcNow():yyyyMMddTHHmmssfffZ}-{safeTarget}-{Guid.NewGuid():N}");
        } while (Directory.Exists(directory));

        Directory.CreateDirectory(directory);
        var plansDirectory = Path.Combine(directory, "plans");
        Directory.CreateDirectory(plansDirectory);
        persistedReport = WithArtifactDirectory(persistedReport, directory);
        File.WriteAllText(Path.Combine(directory, "report.json"),
            JsonSerializer.Serialize(persistedReport, JsonOptions), new UTF8Encoding(false));

        using var stream = new FileStream(Path.Combine(directory, "runs.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                run.Variant, run.Repetition, run.CpuTimeMilliseconds, run.ElapsedTimeMilliseconds,
                run.LogicalReads, run.LogicalReadsByTable, run.ResultHash, run.MessageCount,
                PlanFiles = run.PlanXmls.Select((_, planIndex) => PlanFileName(run, index, planIndex)).ToArray(),
            }, JsonLineOptions));
            for (var planIndex = 0; planIndex < run.PlanXmls.Count; planIndex++)
                File.WriteAllText(Path.Combine(plansDirectory, PlanFileName(run, index, planIndex)),
                    run.PlanXmls[planIndex], new UTF8Encoding(false));
        }
        return directory;
    }

    private static object WithArtifactDirectory(object report, string? directory) => report switch
    {
        SqlHarnessCompareReport compare => compare with { ArtifactDirectory = directory },
        SqlHarnessMeasureReport measure => measure with { ArtifactDirectory = directory },
        _ => throw new ArgumentOutOfRangeException(nameof(report), report.GetType(), "Unsupported benchmark report type."),
    };

    private static string PlanFileName(CompareRunArtifact run, int runIndex, int planIndex) =>
        $"{run.Variant}-{run.Repetition:D3}-{runIndex:D3}-{planIndex:D3}.sqlplan";

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafePathCharacter();
}
