using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

/// <summary>
/// Versioned manifest written next to every benchmark artifact. It maps the
/// artifact to the safe sections a selective read may serve. Anything not
/// listed here — queries.jsonl, raw plan XML, snapshot cells, source SQL —
/// has no read path in v1.
/// </summary>
public sealed record ArtifactManifest(
    int ManifestVersion,
    string ArtifactKind,
    string ReportFile,
    IReadOnlyList<string> Sections)
{
    internal static ArtifactManifest ForReport(object report) => report switch
    {
        SqlHarnessCompareReport => new ArtifactManifest(
            ArtifactReader.CurrentManifestVersion, ArtifactReader.CompareKind,
            ArtifactReader.ReportFileName, ArtifactReader.SupportedSections),
        SqlHarnessMeasureReport => new ArtifactManifest(
            ArtifactReader.CurrentManifestVersion, ArtifactReader.MeasureKind,
            ArtifactReader.ReportFileName, ArtifactReader.SupportedSections),
        SqlHarnessMeasureSetReport => new ArtifactManifest(
            ArtifactReader.CurrentManifestVersion, ArtifactReader.MeasureSetKind,
            ArtifactReader.ReportFileName, ArtifactReader.SupportedSections),
        _ => throw new ArgumentOutOfRangeException(
            nameof(report), report.GetType(), "Unsupported benchmark report type."),
    };
}

/// <summary>One named variant (baseline/candidate/query/set) with its measured metrics.</summary>
public sealed record ArtifactNamedMetrics(
    string Name,
    CompareDistribution CpuTimeMilliseconds,
    CompareDistribution ElapsedTimeMilliseconds,
    CompareDistribution LogicalReads,
    IReadOnlyDictionary<string, CompareDistribution> LogicalReadsByTable,
    IReadOnlyList<string> Warnings)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkMetricReport? MetricReport { get; init; }
}

/// <summary>Safe metrics projection of a saved benchmark artifact. No run arrays, plans, or hashes.</summary>
public sealed record ArtifactMetricsSection(
    string ArtifactId,
    string ArtifactKind,
    IReadOnlyList<ArtifactNamedMetrics> Variants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResultEquivalenceReport? Equivalence = null);

/// <summary>Noteworthy operators of a saved benchmark artifact (capped at ten by the summary projector).</summary>
public sealed record ArtifactOperatorsSection(
    string ArtifactId,
    string ArtifactKind,
    IReadOnlyList<NoteworthyOperatorSummary> Operators);

/// <summary>Safe refusal of a selective artifact read. The message never carries artifact content.</summary>
public sealed class ArtifactReadException(string message, SqlHarnessExitCode exitCode) : Exception(message)
{
    public SqlHarnessExitCode ExitCode { get; } = exitCode;
}

/// <summary>
/// Offline selective reader for existing benchmark artifacts. Reads only
/// <c>manifest.json</c> and <c>report.json</c> under a fixed root: the
/// artifact id is a single manifest-bound directory name, never an arbitrary
/// path. Opens no connection and never loads run arrays, plan files,
/// queries.jsonl, or source SQL.
/// </summary>
public static partial class ArtifactReader
{
    public const int CurrentManifestVersion = 1;

    public const string CompareKind = "compare";
    public const string MeasureKind = "measure";
    public const string MeasureSetKind = "measure-set";

    public const string SummarySection = "summary";
    public const string MetricsSection = "metrics";
    public const string OperatorsSection = "operators";

    internal const string ReportFileName = "report.json";
    private const string ManifestFileName = "manifest.json";

    /// <summary>Manifest bound: mirrors the 64 KiB strict parameter-set file cap.</summary>
    public const long MaxManifestBytes = 64L * 1024;

    /// <summary>Report bound: mirrors the 16 MiB SQL input bound.</summary>
    public const long MaxReportBytes = 16L * 1024 * 1024;

    public static readonly IReadOnlyList<string> SupportedSections =
        [SummarySection, MetricsSection, OperatorsSection];

    private static readonly IReadOnlySet<string> SupportedKinds =
        new HashSet<string>(StringComparer.Ordinal) { CompareKind, MeasureKind, MeasureSetKind };

    /// <summary>
    /// Reads one safe section of an existing artifact. Throws
    /// <see cref="ArtifactReadException"/> with a content-free message on any
    /// refusal: unknown id, traversal or link escape, legacy artifact without
    /// a manifest, corrupt manifest or report, oversized file, or a section
    /// outside the manifest mapping.
    /// </summary>
    public static object ReadSection(string root, string artifactId, string section)
    {
        if (!SupportedSections.Contains(section, StringComparer.Ordinal))
            throw new ArtifactReadException(
                $"Unknown artifact section '{section}'. Supported sections: summary, metrics, operators.",
                SqlHarnessExitCode.Safety);

        var directory = ResolveDirectory(root, artifactId);
        var manifest = ReadManifest(directory, artifactId);
        if (!manifest.Sections.Contains(section, StringComparer.Ordinal))
            throw new ArtifactReadException(
                $"Artifact section '{section}' is not available for this artifact.",
                SqlHarnessExitCode.Safety);

        return section switch
        {
            SummarySection => ReadSummary(directory, artifactId, manifest),
            MetricsSection => ReadMetrics(directory, artifactId, manifest),
            OperatorsSection => ReadOperators(directory, artifactId, manifest),
            _ => throw new ArtifactReadException(
                $"Unknown artifact section '{section}'. Supported sections: summary, metrics, operators.",
                SqlHarnessExitCode.Safety),
        };
    }

    private static string ResolveDirectory(string root, string artifactId)
    {
        if (string.IsNullOrWhiteSpace(artifactId) || artifactId.Length > 128
            || !ArtifactIdPattern().IsMatch(artifactId))
            throw new ArtifactReadException("Unknown artifact id.", SqlHarnessExitCode.Safety);

        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArtifactReadException("Artifact storage is unavailable.", SqlHarnessExitCode.LocalStorage);
        }

        var candidate = Path.GetFullPath(Path.Combine(rootFull, artifactId));
        if (!string.Equals(Path.GetDirectoryName(candidate), rootFull, StringComparison.OrdinalIgnoreCase))
            throw new ArtifactReadException("Unknown artifact id.", SqlHarnessExitCode.Safety);

        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ArtifactReadException("Unknown artifact id.", SqlHarnessExitCode.Safety);
        }

        if (!info.Exists || (info.Attributes & FileAttributes.Directory) == 0 || IsLinkOrReparse(info))
            throw new ArtifactReadException("Unknown artifact id.", SqlHarnessExitCode.Safety);

        return candidate;
    }

    private static ArtifactManifest ReadManifest(string directory, string artifactId)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (!IsPlainFile(path))
        {
            // A directory without a manifest is a pre-manifest (legacy) artifact.
            // Confidentiality is never guessed from file extensions.
            throw new ArtifactReadException(
                $"Artifact '{artifactId}' has no versioned manifest and cannot be read selectively. Re-run the benchmark to create a readable artifact.",
                SqlHarnessExitCode.Safety);
        }

        ArtifactManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ArtifactManifest>(
                ReadBoundedText(path, MaxManifestBytes), ArtifactDirectoryPublisher.JsonOptions);
        }
        catch (JsonException)
        {
            throw new ArtifactReadException("Artifact manifest is invalid.", SqlHarnessExitCode.Safety);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactReadException("Artifact storage is unavailable.", SqlHarnessExitCode.LocalStorage);
        }

        if (manifest is null
            || !SupportedKinds.Contains(manifest.ArtifactKind)
            || !string.Equals(manifest.ReportFile, ReportFileName, StringComparison.Ordinal)
            || manifest.Sections is null || manifest.Sections.Count == 0
            || manifest.Sections.Any(section => !SupportedSections.Contains(section, StringComparer.Ordinal)))
            throw new ArtifactReadException("Artifact manifest is invalid.", SqlHarnessExitCode.Safety);

        if (manifest.ManifestVersion != CurrentManifestVersion)
            throw new ArtifactReadException(
                $"Unsupported artifact manifest version {manifest.ManifestVersion}.",
                SqlHarnessExitCode.Safety);

        return manifest;
    }

    private static object ReadSummary(string directory, string artifactId, ArtifactManifest manifest) =>
        manifest.ArtifactKind switch
        {
            CompareKind => BenchmarkSummaryProjector.Project(ReadReport<SqlHarnessCompareReport>(directory)),
            MeasureKind => BenchmarkSummaryProjector.Project(ReadReport<SqlHarnessMeasureReport>(directory)),
            MeasureSetKind => BenchmarkSummaryProjector.Project(ReadReport<SqlHarnessMeasureSetReport>(directory)),
            _ => throw new ArtifactReadException("Artifact manifest is invalid.", SqlHarnessExitCode.Safety),
        };

    private static ArtifactMetricsSection ReadMetrics(string directory, string artifactId, ArtifactManifest manifest)
    {
        return manifest.ArtifactKind switch
        {
            CompareKind => FromCompare(ReadReport<SqlHarnessCompareReport>(directory)),
            MeasureKind => FromMeasure(ReadReport<SqlHarnessMeasureReport>(directory)),
            MeasureSetKind => FromMeasureSet(ReadReport<SqlHarnessMeasureSetReport>(directory)),
            _ => throw new ArtifactReadException("Artifact manifest is invalid.", SqlHarnessExitCode.Safety),
        };

        ArtifactMetricsSection FromCompare(SqlHarnessCompareReport report) => new(
            artifactId, manifest.ArtifactKind,
            [Of("baseline", report.Baseline), Of("candidate", report.Candidate)],
            report.Equivalence);

        ArtifactMetricsSection FromMeasure(SqlHarnessMeasureReport report) => new(
            artifactId, manifest.ArtifactKind, [Of("query", report.Query)]);

        ArtifactMetricsSection FromMeasureSet(SqlHarnessMeasureSetReport report) => new(
            artifactId, manifest.ArtifactKind,
            report.Sets.Select(set => Of(set.Name, set.Metrics)).ToArray());
    }

    private static ArtifactOperatorsSection ReadOperators(string directory, string artifactId, ArtifactManifest manifest)
    {
        IReadOnlyList<NoteworthyOperatorSummary> operators = manifest.ArtifactKind switch
        {
            CompareKind => BenchmarkSummaryProjector.Project(ReadReport<SqlHarnessCompareReport>(directory)).NoteworthyOperators,
            MeasureKind => BenchmarkSummaryProjector.Project(ReadReport<SqlHarnessMeasureReport>(directory)).NoteworthyOperators,
            MeasureSetKind => FlattenMeasureSet(ReadReport<SqlHarnessMeasureSetReport>(directory)),
            _ => throw new ArtifactReadException("Artifact manifest is invalid.", SqlHarnessExitCode.Safety),
        };
        return new ArtifactOperatorsSection(artifactId, manifest.ArtifactKind, operators);
    }

    private static IReadOnlyList<NoteworthyOperatorSummary> FlattenMeasureSet(SqlHarnessMeasureSetReport report) =>
        BenchmarkSummaryProjector.Project(report).Sets
            .SelectMany(set => set.NoteworthyOperators)
            .Take(10)
            .ToArray();

    private static ArtifactNamedMetrics Of(string name, CompareVariantReport variant) => new(
        name,
        variant.CpuTimeMilliseconds,
        variant.ElapsedTimeMilliseconds,
        variant.LogicalReads,
        variant.LogicalReadsByTable,
        variant.Warnings)
    {
        MetricReport = variant.MetricReport,
    };

    private static T ReadReport<T>(string directory)
    {
        var path = Path.Combine(directory, ReportFileName);
        if (!IsPlainFile(path))
            throw new ArtifactReadException("Artifact report is invalid.", SqlHarnessExitCode.Safety);

        try
        {
            var report = JsonSerializer.Deserialize<T>(
                ReadBoundedText(path, MaxReportBytes), ArtifactDirectoryPublisher.JsonOptions);
            if (report is null || HasMissingMembers(report))
                throw new ArtifactReadException("Artifact report is invalid.", SqlHarnessExitCode.Safety);
            return report;
        }
        catch (ArtifactReadException)
        {
            throw;
        }
        catch (JsonException)
        {
            // JsonException text may echo document content; never propagate it.
            throw new ArtifactReadException("Artifact report is invalid.", SqlHarnessExitCode.Safety);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactReadException("Artifact storage is unavailable.", SqlHarnessExitCode.LocalStorage);
        }
    }

    // A manifest that names one kind while report.json holds another (or a
    // truncated document) deserializes with null members instead of throwing.
    // Reject it here so a tampered artifact fails closed instead of faulting later.
    private static bool HasMissingMembers<T>(T report) => report switch
    {
        SqlHarnessCompareReport compare =>
            compare.Target is null || compare.Baseline is null || compare.Candidate is null || compare.Equivalence is null,
        SqlHarnessMeasureReport measure =>
            measure.Target is null || measure.Query is null,
        SqlHarnessMeasureSetReport measureSet =>
            measureSet.Target is null || measureSet.Sets is null || measureSet.CrossSetSummary is null,
        _ => true,
    };

    private static bool IsPlainFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                && (info.Attributes & FileAttributes.Directory) == 0
                && !IsLinkOrReparse(info);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ReadBoundedText(string path, long maxBytes)
    {
        // Fast path mirrors SqlInputReader: reject by on-disk size before allocating.
        if (new FileInfo(path).Length > maxBytes)
            throw new ArtifactReadException(
                "Artifact file exceeds the read limit and cannot be read selectively.",
                SqlHarnessExitCode.Safety);
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            throw new ArtifactReadException(
                "Artifact file exceeds the read limit and cannot be read selectively.",
                SqlHarnessExitCode.Safety);
        return text;
    }

    private static bool IsLinkOrReparse(FileSystemInfo info)
    {
        string? target;
        try
        {
            target = info.LinkTarget;
        }
        catch (IOException)
        {
            return true;
        }
        return IsEscapeLink(target, info.Attributes);
    }

    internal static bool IsEscapeLink(string? linkTarget, FileAttributes attributes) =>
        linkTarget is not null || (attributes & FileAttributes.ReparsePoint) != 0;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIdPattern();
}
