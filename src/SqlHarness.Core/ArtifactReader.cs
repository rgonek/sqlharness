using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtifactOwner? Owner { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProducerOperationRef { get; init; }

    internal static ArtifactManifest ForReport(object report, ArtifactOwner? owner = null, string? operationRef = null) => (report switch
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
    }) with
    { Owner = owner, ProducerOperationRef = operationRef };
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
    IReadOnlyList<NoteworthyOperatorSummary> Operators)
{
    public int? Continuation { get; init; }
    public int Cursor { get; init; }
    public bool SourceIsCapped { get; init; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EvidenceGaps { get; init; }
}

public sealed record ArtifactPlanSelector(string Variant, int Statement = 0, string? ParameterSet = null, int? Repetition = null);
public sealed record ArtifactPlanNode(string NodePath, string? ParentPath, PlanNode Operator);

public sealed record ArtifactMissingIndex(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Schema,
    string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    double Impact,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IndexOverlap? Overlap);

public sealed record ArtifactPlanSection(string ArtifactId, string Variant, string? ParameterSet, int Statement,
    int StatementCount, int Repetition, string Selection, IReadOnlyList<int> AvailableRepetitions,
    bool? PlanShapeStable, int NodeCount, IReadOnlyList<ArtifactPlanNode> Nodes, int? Continuation)
{
    [JsonIgnore]
    public int Cursor { get; init; }
    public IReadOnlyList<ArtifactMissingIndex> MissingIndexes { get; init; } = [];
    public string? StatementText { get; init; }
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PageError { get; init; }
}

/// <summary>Bounded per-statement plan diagnostics. SQL text is never included.</summary>
public sealed record ArtifactStatementMetric(
    string Variant,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParameterSet,
    int? MatrixCell,
    int StatementOrdinal,
    string StatementHash,
    long? CpuTimeMilliseconds,
    long? ElapsedTimeMilliseconds,
    int? DegreeOfParallelism,
    IReadOnlyList<StatementOperatorSummary> TopOperators);

public sealed record ArtifactStatementsSection(
    string ArtifactId,
    string ArtifactKind,
    IReadOnlyList<ArtifactStatementMetric> Statements,
    int OmittedStatements)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Continuation { get; init; }

    [JsonIgnore]
    public int Cursor { get; init; }
}

/// <summary>One safe reference to a saved compare cell, addressed only by its artifact id.</summary>
/// <summary>A bounded page of saved case outcomes in the same schema as agent matrix inventories.</summary>
public sealed record ArtifactMatrixCellsSection(
    string ArtifactId,
    IReadOnlyList<AgentMatrixCase> Cases,
    int? Continuation,
    int Cursor = 0)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MatrixExecutionCoverage? Coverage { get; init; }
}

/// <summary>Safe refusal of a selective artifact read. The message never carries artifact content.</summary>
public sealed class ArtifactReadException(string message, SqlHarnessExitCode exitCode) : Exception(message)
{
    public SqlHarnessExitCode ExitCode { get; } = exitCode;
}

/// <summary>
/// Offline selective reader for existing benchmark artifacts. The artifact
/// id is a single manifest-bound directory name, never an arbitrary path.
/// Opens no connection and reads only bounded report, index, and distilled
/// plan JSON files; it never reads raw plan XML, queries.jsonl, or source SQL.
/// </summary>
public static class ArtifactReader
{
    public const int CurrentManifestVersion = 2;

    public const string CompareKind = "compare";
    public const string MeasureKind = "measure";
    public const string MeasureSetKind = "measure-set";

    public const string SummarySection = "summary";
    public const string MetricsSection = "metrics";
    public const string OperatorsSection = "operators";
    public const string StatementsSection = "statements";
    public const string MatrixCellsSection = "matrix-cells";
    public const string PlanSection = "plan";

    internal const string ReportFileName = "report.json";
    internal const string StatementsFileName = "statements.json";
    private const string ManifestFileName = "manifest.json";
    private const string IndexInventoryFileName = "index-inventory.json";
    private const string InventoryNotCapturedGap =
        "Index inventory was not captured for this artifact; missing-index hints have no overlap verdict.";
    private const string InventoryUnreadableGap = "Index inventory is unreadable.";

    /// <summary>Manifest bound: mirrors the 64 KiB strict parameter-set file cap.</summary>
    public const long MaxManifestBytes = 64L * 1024;

    /// <summary>Report bound: mirrors the 16 MiB SQL input bound.</summary>
    public const long MaxReportBytes = 16L * 1024 * 1024;

    /// <summary>Maximum source items accepted in one saved artifact page.</summary>
    public const int MaxPageSourceItems = 512;

    /// <summary>Default saved plan nodes loaded for one budgeted response.</summary>
    public const int MaxPlanPageSourceNodes = 128;
    public const int DefaultStatementsPageSourceItems = 128;
    public const int DefaultMatrixCellsPageSourceItems = 32;
    public const int DefaultOperatorsPageSourceItems = 32;

    public static readonly IReadOnlyList<string> SupportedSections =
        [SummarySection, MetricsSection, OperatorsSection, StatementsSection, MatrixCellsSection, PlanSection];

    private static readonly IReadOnlySet<string> SupportedKinds =
        new HashSet<string>(StringComparer.Ordinal) { CompareKind, MeasureKind, MeasureSetKind };

    /// <summary>
    /// Reads one safe section of an existing artifact. Throws
    /// <see cref="ArtifactReadException"/> with a content-free message on any
    /// refusal: unknown id, traversal or link escape, legacy artifact without
    /// a manifest, corrupt manifest or report, oversized file, or a section
    /// outside the manifest mapping, or a scope-owner mismatch when an
    /// owner is supplied (MCP always supplies the frozen scope owner;
    /// the offline CLI passes none and keeps reading by name).
    /// </summary>
    public static object ReadSection(string root, string artifactId, string section, ArtifactOwner? owner = null, int? cursor = null,
        ArtifactPlanSelector? planSelector = null, bool includeText = false, int? pageSize = null)
        => ReadSectionWithMetadata(root, artifactId, section, owner, cursor, planSelector, includeText, pageSize).Section;

    public static (object Section, string? ProducerOperationRef) ReadSectionWithMetadata(
        string root, string artifactId, string section, ArtifactOwner? owner = null, int? cursor = null,
        ArtifactPlanSelector? planSelector = null, bool includeText = false, int? pageSize = null)
    {
        var pageable = section is MatrixCellsSection or OperatorsSection or StatementsSection or PlanSection;
        if (pageSize is < 1 or > MaxPageSourceItems || pageSize is not null && !pageable)
            throw new ArtifactReadException(
                $"Artifact pageSize must be 1..{MaxPageSourceItems} and is only valid for pageable sections: matrix-cells, operators, statements, or plan.",
                SqlHarnessExitCode.Safety);
        if (!SupportedSections.Contains(section, StringComparer.Ordinal))
            throw new ArtifactReadException(
                $"Unknown artifact section '{section}'. Supported sections: summary, metrics, operators, statements, matrix-cells, plan.",
                SqlHarnessExitCode.Safety);

        if (cursor is < 0 || cursor is not null && section is not (MatrixCellsSection or StatementsSection or OperatorsSection or PlanSection))
            throw new ArtifactReadException("The artifact cursor is invalid.", SqlHarnessExitCode.Safety);
        if (section != PlanSection && (planSelector is not null || includeText))
            throw new ArtifactReadException("Plan selectors are only valid for the plan section.", SqlHarnessExitCode.Safety);

        var directory = ResolveDirectory(root, artifactId);
        var manifest = ReadManifest(directory, artifactId);
        if (owner is not null && !owner.Matches(manifest.Owner))
            throw new ArtifactReadException(
                "The artifact is not available in the current scope.",
                SqlHarnessExitCode.Safety);
        if (!manifest.Sections.Contains(section, StringComparer.Ordinal)
            && !(section == PlanSection && manifest.ArtifactKind is CompareKind or MeasureKind or MeasureSetKind))
            throw new ArtifactReadException(
                $"Artifact section '{section}' is not available for this artifact.",
                SqlHarnessExitCode.Safety);

        var result = section switch
        {
            SummarySection => ReadSummary(directory, artifactId, manifest),
            MetricsSection => ReadMetrics(directory, artifactId, manifest),
            OperatorsSection => ReadOperators(directory, artifactId, manifest, cursor ?? 0, pageSize ?? DefaultOperatorsPageSourceItems),
            StatementsSection => ReadStatements(directory, artifactId, manifest, cursor ?? 0, pageSize ?? DefaultStatementsPageSourceItems),
            MatrixCellsSection => ReadMatrixCells(directory, artifactId, cursor ?? 0, pageSize ?? DefaultMatrixCellsPageSourceItems),
            PlanSection => ReadPlan(directory, artifactId, manifest, planSelector ?? new ArtifactPlanSelector(DefaultVariant(manifest.ArtifactKind)), cursor ?? 0, includeText, pageSize ?? MaxPlanPageSourceNodes),
            _ => throw new ArtifactReadException(
                $"Unknown artifact section '{section}'. Supported sections: summary, metrics, operators, statements, matrix-cells, plan.",
                SqlHarnessExitCode.Safety),
        };
        // Producer metadata is exposed only after the scope owner and section have been checked.
        return (result, manifest.ProducerOperationRef);
    }

    private static ArtifactMatrixCellsSection ReadMatrixCells(string directory, string artifactId, int cursor, int pageSize)
    {
        const string fileName = "matrix-cells.json";
        var path = Path.Combine(directory, fileName);
        if (!IsPlainFile(path))
            throw new ArtifactReadException("Artifact matrix index is invalid.", SqlHarnessExitCode.Safety);

        try
        {
            using var document = JsonDocument.Parse(ReadBoundedText(path, MaxReportBytes));
            var hasCaseInventory = document.RootElement.TryGetProperty("cases", out var cases);
            var hasLegacyCells = !hasCaseInventory && document.RootElement.TryGetProperty("cells", out cases);
            if ((!hasCaseInventory && !hasLegacyCells)
                || cases.ValueKind != JsonValueKind.Array
                || cases.GetArrayLength() > 100_000)
                throw new ArtifactReadException("Artifact matrix index is invalid.", SqlHarnessExitCode.Safety);

            if (cursor > cases.GetArrayLength())
                throw new ArtifactReadException("The artifact cursor is invalid.", SqlHarnessExitCode.Safety);

            var page = cases.EnumerateArray().Skip(cursor).Take(pageSize).Select(item =>
            {
                if (!item.TryGetProperty("index", out var index)
                    || !index.TryGetInt32(out var cellIndex)
                    || cellIndex < 0
                    || !item.TryGetProperty("artifactId", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { } artifactCellId
                    || !ArtifactIds.IsValid(artifactCellId))
                    throw new ArtifactReadException("Artifact matrix index is invalid.", SqlHarnessExitCode.Safety);
                var matrixCase = hasLegacyCells
                    ? new AgentMatrixCase(cellIndex, artifactCellId, null, "unavailable",
                        new AgentMedianMetrics(null, null, null), new AgentMedianMetrics(null, null, null),
                        new AgentMedianMetrics(null, null, null), [])
                    : JsonSerializer.Deserialize<AgentMatrixCase>(item.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (matrixCase is null || matrixCase.Index != cellIndex || matrixCase.ArtifactId != artifactCellId
                    || matrixCase.Baseline is null || matrixCase.Candidate is null || matrixCase.Delta is null || matrixCase.Warnings is null)
                    throw new ArtifactReadException("Artifact matrix index is invalid.", SqlHarnessExitCode.Safety);
                return matrixCase;
            }).ToArray();
            var next = cursor + page.Length;
            MatrixExecutionCoverage? coverage = null;
            if (document.RootElement.TryGetProperty("coverage", out var coverageElement)
                && coverageElement.ValueKind == JsonValueKind.Object)
            {
                coverage = JsonSerializer.Deserialize<MatrixExecutionCoverage>(
                    coverageElement.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            return new ArtifactMatrixCellsSection(artifactId, page, next < cases.GetArrayLength() ? next : null, cursor)
            {
                Coverage = coverage,
            };
        }
        catch (ArtifactReadException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ArtifactReadException("Artifact matrix index is invalid.", SqlHarnessExitCode.Safety);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactReadException("Artifact storage is unavailable.", SqlHarnessExitCode.LocalStorage);
        }
    }

    private static string ResolveDirectory(string root, string artifactId)
    {
        if (!ArtifactIds.IsValid(artifactId))
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

        if (manifest.ManifestVersion is < 1 or > CurrentManifestVersion)
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

    private static ArtifactOperatorsSection ReadOperators(string directory, string artifactId, ArtifactManifest manifest, int cursor, int pageSize)
    {
        IReadOnlyList<NoteworthyOperatorSummary> operators = manifest.ArtifactKind switch
        {
            CompareKind => BenchmarkSummaryProjector.Project(ReadReport<SqlHarnessCompareReport>(directory)).NoteworthyOperators,
            MeasureKind => BenchmarkSummaryProjector.Project(ReadReport<SqlHarnessMeasureReport>(directory)).NoteworthyOperators,
            MeasureSetKind => FlattenMeasureSet(ReadReport<SqlHarnessMeasureSetReport>(directory)),
            _ => throw new ArtifactReadException("Artifact manifest is invalid.", SqlHarnessExitCode.Safety),
        };
        if (cursor > operators.Count) throw new ArtifactReadException("The artifact cursor is invalid.", SqlHarnessExitCode.Safety);
        var page = operators.Skip(cursor).Take(pageSize).ToArray();
        IReadOnlyList<string>? gaps = operators.Count > 0 && operators.Any(op => op.PlanRefs is not { Count: > 0 })
            ? ["Exact plan source was not retained for one or more noteworthy operators."]
            : null;
        return new ArtifactOperatorsSection(artifactId, manifest.ArtifactKind, page)
        {
            Cursor = cursor,
            Continuation = cursor + page.Length < operators.Count ? cursor + page.Length : null,
            EvidenceGaps = gaps,
        };
    }

    private static string DefaultVariant(string artifactKind) => artifactKind switch
    {
        CompareKind => "baseline",
        MeasureKind or MeasureSetKind => "query",
        _ => throw new ArtifactReadException("A parameter-set variant must be selected for this artifact.", SqlHarnessExitCode.Safety),
    };

    private static ArtifactPlanSection ReadPlan(string directory, string artifactId, ArtifactManifest manifest,
        ArtifactPlanSelector selector, int cursor, bool includeText, int pageSize)
    {
        if (selector.Statement < 0 || selector.Repetition is < 1)
            throw new ArtifactReadException("The artifact plan selector is invalid.", SqlHarnessExitCode.Safety);
        var validVariant = manifest.ArtifactKind switch
        {
            CompareKind => selector.Variant is "baseline" or "candidate",
            MeasureKind => selector.Variant == "query",
            MeasureSetKind => selector.Variant == "query",
            _ => false,
        };
        if (!validVariant) throw new ArtifactReadException("The artifact plan variant is invalid.", SqlHarnessExitCode.Safety);
        var storedVariant = selector.Variant == "query" && manifest.ArtifactKind is (MeasureKind or MeasureSetKind)
            ? "measure"
            : selector.Variant;
        var path = Path.Combine(directory, "runs.jsonl");
        if (!IsPlainFile(path)) throw new ArtifactReadException("Artifact plan index is invalid.", SqlHarnessExitCode.Safety);
        try
        {
            var text = ReadBoundedText(path, MaxReportBytes);
            var matching = new List<(int Repetition, string? ParameterSet, string[] Files)>();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var row = doc.RootElement;
                if (row.ValueKind != JsonValueKind.Object)
                    throw new ArtifactReadException("Artifact plan index is invalid.", SqlHarnessExitCode.Safety);
                if (!TryString(row, "variant", out var variant) || !TryInt(row, "repetition", out var repetition))
                    throw new ArtifactReadException("Artifact plan index is invalid.", SqlHarnessExitCode.Safety);
                var set = TryString(row, "parameterSet", out var parameterSet) ? parameterSet : null;
                if (variant != storedVariant || selector.ParameterSet is not null
                    && !string.Equals(set, selector.ParameterSet, StringComparison.Ordinal)) continue;
                if (!TryGetProperty(row, "planJsonFiles", out var fileList) || fileList.ValueKind != JsonValueKind.Array)
                    throw new ArtifactReadException("Artifact plan index is incomplete.", SqlHarnessExitCode.Safety);
                matching.Add((repetition, set, fileList.EnumerateArray().Select(file => file.ValueKind == JsonValueKind.String
                    ? file.GetString()! : throw new ArtifactReadException("Artifact plan index is invalid.", SqlHarnessExitCode.Safety)).ToArray()));
            }
            if (manifest.ArtifactKind == MeasureSetKind && selector.ParameterSet is null && matching.Count > 0)
            {
                var selectedSet = matching.Select(item => item.ParameterSet).Where(item => item is not null)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
                matching = matching.Where(item => item.ParameterSet == selectedSet).ToList();
            }
            if (matching.Count == 0)
                throw new ArtifactReadException("The selected plan variant or parameter set is unavailable.", SqlHarnessExitCode.Safety);
            var repetitions = matching.Select(item => item.Repetition).Distinct().Order().ToArray();
            var selectedRepetition = selector.Repetition ?? repetitions[^1];
            var selectedRun = matching.FirstOrDefault(item => item.Repetition == selectedRepetition);
            if (selectedRun.Files is null)
                throw new ArtifactReadException($"Repetition {selectedRepetition} is unavailable; available repetitions: {string.Join(", ", repetitions)}.", SqlHarnessExitCode.Safety);
            if (selectedRun.Files.Length == 0)
                throw new ArtifactReadException($"Repetition {selectedRepetition} has no saved plan evidence; available measured repetitions: {string.Join(", ", repetitions)}.", SqlHarnessExitCode.Safety);
            var plans = ReadRunPlans(directory, selectedRun.Files);
            var statements = plans.SelectMany(plan => plan.Statements).ToArray();
            if (statements.Length == 0)
                throw new ArtifactReadException("The selected repetition contains no saved statement evidence.", SqlHarnessExitCode.Safety);
            if (selector.Statement >= statements.Length)
                throw new ArtifactReadException($"Statement {selector.Statement} is unavailable; available statement range: 0..{Math.Max(-1, statements.Length - 1)}.", SqlHarnessExitCode.Safety);
            var selected = statements[selector.Statement];
            var allNodes = PlanDistiller.Preorder(selected.Root)
                .Select(node => new ArtifactPlanNode(node.Path, node.Parent, node.Node with { Children = [] }))
                .ToArray();
            if (cursor > allNodes.Length) throw new ArtifactReadException("The artifact cursor is invalid.", SqlHarnessExitCode.Safety);
            var nodes = allNodes.Skip(cursor).Take(pageSize).Select(node => includeText ? node : node with
            { Operator = node.Operator with { Predicate = null } }).ToArray();
            bool? stable = null;
            var hasComparisonGap = false;
            if (repetitions.Length >= 2)
            {
                var comparable = new List<PlanStatement>();
                foreach (var repetition in repetitions)
                {
                    var run = matching.First(item => item.Repetition == repetition);
                    try
                    {
                        var candidates = ReadRunPlans(directory, run.Files).SelectMany(plan => plan.Statements).ToArray();
                        if (selector.Statement < candidates.Length) comparable.Add(candidates[selector.Statement]);
                        else hasComparisonGap = true;
                    }
                    catch (Exception exception) when (exception is ArtifactReadException or JsonException)
                    { hasComparisonGap = true; }
                }
                if (comparable.Count >= 2 && !hasComparisonGap)
                    stable = comparable.All(statement => Shape(statement.Root) == Shape(comparable[0].Root));
            }
            var evidenceGaps = new List<string>();
            if (hasComparisonGap)
                evidenceGaps.Add("One or more measured repetitions have incomplete stored plan evidence; shape stability is unavailable.");
            IReadOnlyList<ArtifactMissingIndex> missingIndexes = [];
            if (cursor == 0)
            {
                var overlaps = ReadHintOverlaps(directory, selected.MissingIndexes);
                missingIndexes = overlaps.Indexes;
                foreach (var gap in overlaps.Gaps)
                    AddGap(evidenceGaps, gap);
            }
            return new ArtifactPlanSection(artifactId, selector.Variant, selector.ParameterSet, selector.Statement,
                statements.Length, selectedRepetition, selector.Repetition is null ? "last-measured" : "explicit",
                repetitions, stable, allNodes.Length, nodes, cursor + nodes.Length < allNodes.Length ? cursor + nodes.Length : null)
            {
                Cursor = cursor,
                MissingIndexes = missingIndexes,
                StatementText = includeText && cursor == 0 ? selected.StatementText : null,
                EvidenceGaps = evidenceGaps,
            };
        }
        catch (ArtifactReadException) { throw; }
        catch (JsonException) { throw new ArtifactReadException("Stored plan evidence is invalid.", SqlHarnessExitCode.Safety); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ArtifactReadException("Artifact storage is unavailable.", SqlHarnessExitCode.LocalStorage); }

        static string Shape(PlanNode root) => JsonSerializer.Serialize(PlanDistiller.Preorder(root).Select(node => new[]
        {
            node.Node.PhysicalOp, node.Node.LogicalOp, node.Node.ObjectName, node.Node.IndexName, node.Node.Predicate,
        }));
    }

    private static IReadOnlyList<DistilledPlan> ReadRunPlans(string directory, IReadOnlyList<string> names)
    {
        if (names.Count > 100_000) throw new ArtifactReadException("Artifact plan index is invalid.", SqlHarnessExitCode.Safety);
        var plans = new List<DistilledPlan>();
        foreach (var name in names)
        {
            if (name.Length > 256 || !name.EndsWith(".plan.json", StringComparison.Ordinal)
                || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
                throw new ArtifactReadException("Artifact plan index is invalid.", SqlHarnessExitCode.Safety);
            var plansDirectory = Path.Combine(directory, "plans");
            if (!IsPlainDirectory(plansDirectory)) throw new ArtifactReadException("Stored plan evidence is missing.", SqlHarnessExitCode.Safety);
            var planPath = Path.Combine(plansDirectory, name);
            if (!IsPlainFile(planPath)) throw new ArtifactReadException("Stored plan evidence is missing.", SqlHarnessExitCode.Safety);
            plans.Add(StoredPlanReader.Read(ReadBoundedText(planPath, MaxReportBytes)));
        }
        return plans;
    }

    private static (IReadOnlyList<ArtifactMissingIndex> Indexes, IReadOnlyList<string> Gaps) ReadHintOverlaps(
        string directory, IReadOnlyList<MissingIndex> hints)
    {
        var gaps = new List<string>();
        var path = Path.Combine(directory, IndexInventoryFileName);
        if (!IsPlainFile(path))
        {
            if (InventoryEntryExists(path))
                gaps.Add(InventoryUnreadableGap);
            else if (hints.Count > 0)
                gaps.Add(InventoryNotCapturedGap);
            return (hints.Select(hint => ToArtifact(hint, null)).ToArray(), gaps);
        }

        IndexInventory? inventory;
        try
        {
            // A legal inventory is 8 tables by 64 indexes of indented JSON, which exceeds the 64 KiB manifest cap.
            inventory = JsonSerializer.Deserialize<IndexInventory>(
                ReadBoundedText(path, MaxReportBytes), ArtifactDirectoryPublisher.JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or ArtifactReadException or IOException or UnauthorizedAccessException)
        {
            gaps.Add(InventoryUnreadableGap);
            return (hints.Select(hint => ToArtifact(hint, null)).ToArray(), gaps);
        }

        if (!IsUsableInventory(inventory))
        {
            gaps.Add(InventoryUnreadableGap);
            return (hints.Select(hint => ToArtifact(hint, null)).ToArray(), gaps);
        }

        var indexes = new List<ArtifactMissingIndex>(hints.Count);
        foreach (var hint in hints)
            indexes.Add(ClassifyHint(inventory!, hint, gaps));
        foreach (var gap in inventory!.Gaps)
            AddGap(gaps, gap);
        return (indexes, gaps);
    }

    private static ArtifactMissingIndex ClassifyHint(IndexInventory inventory, MissingIndex hint, List<string> gaps)
    {
        if (hint.Database is not null
            && !string.Equals(hint.Database, inventory.Database, StringComparison.OrdinalIgnoreCase))
            return ToArtifact(hint, null);

        var schema = hint.Schema ?? "dbo";
        InventoryTable? table = null;
        foreach (var candidate in inventory.Tables)
        {
            if (string.Equals(candidate.Schema, schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Table, hint.Table, StringComparison.OrdinalIgnoreCase))
            {
                table = candidate;
                break;
            }
        }

        if (table is null)
            return ToArtifact(hint, null);
        if (!table.ObjectFound)
        {
            AddGap(gaps, $"{schema}.{hint.Table} was not found when the index inventory was captured.");
            return ToArtifact(hint, null);
        }

        var overlap = IndexOverlapClassifier.Classify(hint, table);
        if (table.IndexesTruncated && overlap.Verdict == IndexOverlapVerdict.NewShape)
        {
            AddGap(gaps, $"{schema}.{hint.Table} has more than 64 indexes; a new-shape verdict cannot be trusted.");
            return ToArtifact(hint, null);
        }

        return ToArtifact(hint, overlap);
    }

    private static bool IsUsableInventory(IndexInventory? inventory)
    {
        if (inventory?.Database is null || inventory.Tables is null || inventory.Gaps is null)
            return false;
        foreach (var table in inventory.Tables)
        {
            if (table?.Schema is null || table.Table is null || table.Indexes is null)
                return false;
            foreach (var index in table.Indexes)
            {
                if (index?.Name is null || index.Type is null || index.KeyColumns is null || index.IncludeColumns is null)
                    return false;
            }
        }

        return inventory.Gaps.All(gap => gap is not null);
    }

    private static ArtifactMissingIndex ToArtifact(MissingIndex hint, IndexOverlap? overlap) =>
        new(hint.Database, hint.Schema, hint.Table, hint.EqualityColumns, hint.InequalityColumns, hint.IncludeColumns, hint.Impact, overlap);

    private static void AddGap(List<string> gaps, string gap)
    {
        if (!gaps.Contains(gap))
            gaps.Add(gap);
    }

    private static bool InventoryEntryExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()!) is not null;
    }
    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property) && property.TryGetInt32(out value);
    }
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }

    private static ArtifactStatementsSection ReadStatements(string directory, string artifactId, ArtifactManifest manifest, int cursor, int pageSize)
    {
        var path = Path.Combine(directory, StatementsFileName);
        if (!IsPlainFile(path))
            throw new ArtifactReadException("Artifact statements are invalid.", SqlHarnessExitCode.Safety);

        try
        {
            var section = JsonSerializer.Deserialize<ArtifactStatementsSection>(
                ReadBoundedText(path, MaxReportBytes), ArtifactDirectoryPublisher.JsonOptions);
            if (section is null || section.Statements is null || section.OmittedStatements < 0
                || section.Statements.Any(statement => statement is null || statement.StatementHash is null
                    || statement.Variant is null || statement.TopOperators is null))
                throw new ArtifactReadException("Artifact statements are invalid.", SqlHarnessExitCode.Safety);
            if (cursor > section.Statements.Count)
                throw new ArtifactReadException("The artifact cursor is invalid.", SqlHarnessExitCode.Safety);
            return section with
            {
                ArtifactId = artifactId,
                ArtifactKind = manifest.ArtifactKind,
                Statements = section.Statements.Skip(cursor).Take(pageSize).ToArray(),
                Continuation = cursor + Math.Min(pageSize, section.Statements.Count - cursor) < section.Statements.Count
                    ? cursor + Math.Min(pageSize, section.Statements.Count - cursor)
                    : null,
                Cursor = cursor,
            };
        }
        catch (ArtifactReadException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ArtifactReadException("Artifact statements are invalid.", SqlHarnessExitCode.Safety);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactReadException("Artifact storage is unavailable.", SqlHarnessExitCode.LocalStorage);
        }
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

    private static bool IsPlainDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.Directory) != 0 && !IsLinkOrReparse(info);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
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
}