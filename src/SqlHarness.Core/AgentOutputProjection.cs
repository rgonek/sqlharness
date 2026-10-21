using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHarness.Core;

public sealed record AgentBoundedOutput(string ReportType, int OmittedItems);
public sealed record AgentBinaryValue(long ByteLength, string Base64Prefix, bool Truncated);

/// <summary>A query column with its CLR value type alias and source nullability.</summary>
public sealed record AgentQueryColumn(string Name, string ValueType, bool AllowNull);

public sealed record AgentClippedCell(int Row, int Column, long OriginalLength, int ReturnedLength, string Unit);

/// <summary>
/// One query result set in the agent projection. <c>Ordinal</c> is the
/// zero-based position in the source report. Every emitted row has exactly
/// <c>Columns.Count</c> cells. Omitted rows and columns are counted; a set with
/// omitted columns carries no rows.
/// </summary>
public sealed record AgentCompactResultSet(
    int Ordinal,
    IReadOnlyList<AgentQueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    long RowCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long OmittedRowCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int OmittedColumnCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AgentClippedCell>? ClippedCells = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long ProjectionOmittedRowCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long SourceOmittedRowCount = 0);

/// <summary>
/// Compact agent projection of a query report. <c>Database</c> is the
/// resolved database name; <c>ResultSets</c> is a prefix of the source sets,
/// with trailing omitted sets counted in <c>OmittedResultSetCount</c>.
/// </summary>
public sealed record AgentCompactQuery(
    string Database,
    string StatementClassification,
    IReadOnlyList<AgentCompactResultSet> ResultSets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int OmittedResultSetCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string?>? Messages = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RecordsAffected = null,
    long DurationMilliseconds = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

/// <summary>Bounded agent projection of a mutation broker response.</summary>
public sealed record AgentMutationResult(string Status, string? Message, object? Result);

/// <summary>The CLI agent response envelope version. MCP uses its own constant.</summary>
public static class AgentEnvelopeSchema
{
    public const int Version = 2;
}

/// <summary>Audience for <see cref="AgentOutputProjection.Project"/>.</summary>
public enum AgentProjectionProfile
{
    Cli,
    Mcp,
}

/// <summary>Documented model-facing names for comparison, watch, and snapshot enums.</summary>
public static class AgentProjectionNames
{
    public static readonly IReadOnlyList<string> ComparisonModes = ["ordered", "multiset", "set", "off"];
    public static readonly IReadOnlyList<string> WatchExitReasons = ["condition-met", "unchanged", "max-duration"];
    public static readonly IReadOnlyList<string> SnapshotVerdicts = ["stored", "identical", "different"];

    public static string ComparisonMode(ResultComparisonMode mode) => mode switch
    {
        ResultComparisonMode.Ordered => "ordered",
        ResultComparisonMode.Multiset => "multiset",
        ResultComparisonMode.Set => "set",
        ResultComparisonMode.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string WatchExitReason(WatchExitReason reason) => reason switch
    {
        Core.WatchExitReason.ConditionMet => "condition-met",
        Core.WatchExitReason.Unchanged => "unchanged",
        Core.WatchExitReason.MaxDuration => "max-duration",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    public static string SnapshotVerdict(SnapshotVerdict verdict) => verdict switch
    {
        Core.SnapshotVerdict.Stored => "stored",
        Core.SnapshotVerdict.Identical => "identical",
        Core.SnapshotVerdict.Different => "different",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };
}

/// <summary>MCP watch projection with a documented <c>exitReason</c> string.</summary>
public sealed record AgentWatchOutput(
    SqlHarnessTargetIdentityReport Target,
    int PollCount,
    long ElapsedMilliseconds,
    string ExitReason,
    IReadOnlyList<AgentWatchPollOutput> EmittedPolls,
    int TotalChangedPolls = 0,
    int OmittedPolls = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int OmittedEmittedPolls = 0);

/// <summary>CLI watch output keeps the established numeric enum representation.</summary>
public sealed record AgentCliWatchOutput(
    SqlHarnessTargetIdentityReport Target,
    int PollCount,
    long ElapsedMilliseconds,
    WatchExitReason ExitReason,
    IReadOnlyList<AgentWatchPollOutput> EmittedPolls,
    int TotalChangedPolls = 0,
    int OmittedPolls = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int OmittedEmittedPolls = 0);

public sealed record AgentWatchPollOutput(
    int Poll,
    long ElapsedMilliseconds,
    string ResultHash,
    IReadOnlyList<AgentCompactResultSet> ResultSets);

/// <summary>MCP snapshot projection with a documented <c>verdict</c> string.</summary>
public sealed record AgentSnapshotOutput(
    SqlHarnessTargetIdentityReport Target,
    string Name,
    string Verdict,
    int DifferenceCount,
    IReadOnlyList<SqlHarnessSnapshotDifference> Differences);

/// <summary>Presentation-only bounds for the opt-in agent response.</summary>
public sealed record AgentOutputOptions(int MaximumBytes = 65_536, int MaximumCellCharacters = AgentOutputLimits.DefaultCellCharacters);

/// <summary>Creates bounded, lower-priority-detail projections without changing source reports.</summary>
public static class AgentOutputProjection
{
    private static readonly JsonSerializerOptions ProjectionJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Detail limits tried in order until the rendered envelope fits the budget. The first
    /// entry renders the report without truncation. Every report type uses the same steps.
    /// </summary>
    public static IReadOnlyList<int> CandidateDetailLimits { get; } = [int.MaxValue, 128, 64, 32, 16, 8, 4, 2, 1, 0];

    internal static IEnumerable<KeyValuePair<string, CompareDistribution>> BySignificance(
        IReadOnlyDictionary<string, CompareDistribution> reads) =>
        reads.OrderByDescending(pair => pair.Value.Median)
            .ThenByDescending(pair => pair.Value.Max)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal);

    public static object? Project(object? report, int maximumCellCharacters, int detailLimit, out int omittedItems, int maximumBytes = 65_536, AgentProjectionProfile profile = AgentProjectionProfile.Cli)
        => ProjectCore(report, maximumCellCharacters, detailLimit, out omittedItems, maximumBytes, profile, null);

    internal static object? ProjectForTesting(
        object? report,
        int maximumCellCharacters,
        int detailLimit,
        out int omittedItems,
        int maximumBytes,
        AgentProjectionProfile profile,
        Action<long> tableSelectionAccounting)
        => ProjectCore(report, maximumCellCharacters, detailLimit, out omittedItems, maximumBytes, profile, tableSelectionAccounting);

    private static object? ProjectCore(
        object? report,
        int maximumCellCharacters,
        int detailLimit,
        out int omittedItems,
        int maximumBytes,
        AgentProjectionProfile profile,
        Action<long>? tableSelectionAccounting)
    {
        omittedItems = 0;
        var omissions = 0;
        var clippedItems = 0;
        string? Clip(string? value) => ClipText(value, maximumCellCharacters, ref clippedItems);
        IReadOnlyList<T> Take<T>(IReadOnlyList<T> values)
        {
            var selected = values.Take(detailLimit).ToArray();
            omissions += Math.Max(0, values.Count - selected.Length);
            return selected;
        }
        try
        {
            switch (report)
            {
                case SqlHarnessQueryReport query:
                    {
                        var messages = Take(query.Messages).Select(Clip).ToArray();
                        var warnings = Take(query.Warnings).Select(warning => Clip(warning)!).ToArray();
                        var queryProjection = detailLimit > 0
                            ? ProjectQueryRowSample(query, messages, warnings, detailLimit, maximumCellCharacters, maximumBytes, out var queryOmitted)
                            : ProjectQueryInventory(query, messages, warnings, maximumCellCharacters, maximumBytes, out queryOmitted);
                        omissions += queryOmitted;
                        omissions += clippedItems;
                        return queryProjection;
                    }
                case Mutations.MutationBrokerResult mutation:
                    {
                        var message = Clip(mutation.Message);
                        object? projected = null;
                        var queryOmitted = 0;
                        if (mutation.Result is not null)
                            projected = Project(mutation.Result, maximumCellCharacters, detailLimit, out queryOmitted, maximumBytes, profile);
                        omissions += clippedItems;
                        omissions += queryOmitted;
                        return new AgentMutationResult(mutation.Status, message, projected);
                    }
                case SqlHarnessMeasureSetReport setReport:
                    var selectedSets = Take(setReport.Sets).Select(set =>
                    {
                        var projected = BenchmarkSummaryProjector.ProjectSet(
                            set,
                            BenchmarkSummaryProjector.SelectMeasureNoteworthy(
                                ProjectSetOperators(set.Metrics.Operators)
                                    .Select(op => op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) })
                                    .ToArray(),
                                ArtifactIds.FromDirectory(setReport.ArtifactDirectory),
                                "query",
                                set.Name));
                        return projected with
                        {
                            Name = Clip(projected.Name)!,
                            Warnings = Take(projected.Warnings).Select(warning => Clip(warning)!).ToArray(),
                        };
                    }).ToArray();
                    var planCacheWarning = Clip(setReport.PlanCacheWarning)!;
                    var measureSetDirectory = profile == AgentProjectionProfile.Mcp
                        ? setReport.ArtifactDirectory
                        : Clip(setReport.ArtifactDirectory);
                    omissions += clippedItems;
                    var measureSetSummary = new MeasureSetBenchmarkSummary(
                        setReport.Target, setReport.MeasuredOrderRule, planCacheWarning, selectedSets,
                        setReport.CrossSetSummary, measureSetDirectory);
                    return profile == AgentProjectionProfile.Mcp
                        ? AgentBenchmarkProjection.MeasureSet(measureSetSummary)
                        : measureSetSummary;
                case SqlHarnessCompareMatrixReport matrix:
                    var shared = BenchmarkSummaryProjector.Project(matrix);
                    var orderedMatrixCells = matrix.Cells.OrderBy(cell => cell.Index).ToArray();
                    var matrixArtifactId = shared.MatrixArtifactId;
                    var parameterName = Clip(matrix.ParameterName);
                    var parameterType = Clip(matrix.ParameterType);
                    var inventoryShell = shared with
                    {
                        ParameterName = parameterName,
                        ParameterType = parameterType,
                        Cases = [],
                        Details = [],
                        OmittedCaseCount = orderedMatrixCells.Length,
                        OmittedDetailCount = profile == AgentProjectionProfile.Mcp ? 0 : orderedMatrixCells.Length,
                        Continuation = null,
                        Coverage = shared.Coverage,
                    };
                    var cases = new List<AgentMatrixCase>();
                    var omittedCaseWarnings = 0;
                    var availableCases = orderedMatrixCells.Select(BenchmarkSummaryProjector.ProjectCase)
                        .Select(item =>
                        {
                            var warnings = item.Warnings.Take(1)
                                .Select(value => ClipText(value, Math.Min(128, maximumCellCharacters), ref clippedItems)!).ToArray();
                            omittedCaseWarnings += item.Warnings.Count - warnings.Length;
                            return item with { Warnings = warnings };
                        }).ToArray();
                    omissions += omittedCaseWarnings;
                    foreach (var item in availableCases)
                    {
                        var candidateCases = cases.Append(item).ToArray();
                        var omittedCaseCount = orderedMatrixCells.Length - candidateCases.Length;
                        var candidateSummary = inventoryShell with
                        {
                            Cases = candidateCases,
                            OmittedCaseCount = omittedCaseCount,
                            Continuation = omittedCaseCount > 0 && matrixArtifactId is not null ? candidateCases.Length : null,
                            Coverage = shared.Coverage,
                        };
                        var fitted = profile == AgentProjectionProfile.Mcp
                            ? (object)(AgentBenchmarkProjection.Matrix(candidateSummary) with { Details = null, OmittedDetailCount = 0 })
                            : candidateSummary;
                        // Leave room for the outer command/status/error/truncation envelope.
                        if (SerializedLength(fitted) + 384 > maximumBytes)
                            break;
                        cases.Add(item);
                    }

                    var omittedCases = orderedMatrixCells.Length - cases.Count;
                    var selectedDetails = new List<CompareMatrixCellDetail>();
                    var nestedOmitted = 0;
                    if (profile != AgentProjectionProfile.Mcp)
                    {
                        foreach (var cell in orderedMatrixCells.Take(cases.Count).Take(detailLimit))
                        {
                            var projected = Project(BenchmarkSummaryProjector.Project(cell.Compare), maximumCellCharacters, detailLimit,
                                out var cellOmitted, maximumBytes, profile);
                            nestedOmitted += cellOmitted;
                            var summary = (CompareBenchmarkSummary)projected!;
                            selectedDetails.Add(new CompareMatrixCellDetail(cell.Index, new CompareMatrixCellDetailContent(
                                summary.Baseline, summary.Candidate, summary.Equivalence, summary.NoteworthyOperators, summary.Regression)));
                        }
                    }
                    var omittedDetails = profile == AgentProjectionProfile.Mcp ? 0 : orderedMatrixCells.Length - selectedDetails.Count;
                    omissions += omittedCases + omittedDetails + nestedOmitted + clippedItems;
                    var matrixSummary = inventoryShell with
                    {
                        Cases = cases,
                        Details = selectedDetails,
                        OmittedCaseCount = omittedCases,
                        OmittedDetailCount = omittedDetails,
                        Continuation = omittedCases > 0 && matrixArtifactId is not null ? cases.Count : null,
                        Coverage = shared.Coverage,
                    };
                    return profile == AgentProjectionProfile.Mcp
                        ? AgentBenchmarkProjection.Matrix(matrixSummary) with { Details = null, OmittedDetailCount = 0 }
                        : matrixSummary;
                case SqlHarnessCompareReport compare:
                    {
                        var nestedProjection = ProjectCore(BenchmarkSummaryProjector.Project(compare), maximumCellCharacters, detailLimit, out var compareSummaryOmitted, maximumBytes, profile, tableSelectionAccounting);
                        omissions += compareSummaryOmitted;
                        return nestedProjection;
                    }
                case SqlHarnessMeasureReport measure:
                    {
                        var nestedProjection = ProjectCore(BenchmarkSummaryProjector.Project(measure), maximumCellCharacters, detailLimit, out var measureSummaryOmitted, maximumBytes, profile, tableSelectionAccounting);
                        omissions += measureSummaryOmitted;
                        return nestedProjection;
                    }
                case CompareBenchmarkSummary summary:
                    {
                        var baseline = ClipVariant(summary.Baseline, new Dictionary<string, CompareDistribution>(StringComparer.Ordinal));
                        var candidate = ClipVariant(summary.Candidate, new Dictionary<string, CompareDistribution>(StringComparer.Ordinal));
                        var compareShell = summary with
                        {
                            Baseline = baseline,
                            Candidate = candidate,
                            NoteworthyOperators = Take(summary.NoteworthyOperators.Take(10).ToArray()).Select(op => op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) }).ToArray(),
                            ArtifactDirectory = profile == AgentProjectionProfile.Mcp ? summary.ArtifactDirectory : Clip(summary.ArtifactDirectory),
                        };
                        var emptyCompareSummary = compareShell with
                        {
                            Baseline = baseline with { LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal) },
                            Candidate = candidate with { LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal) },
                        };
                        object fittedEmptyCompare = profile == AgentProjectionProfile.Mcp
                            ? AgentBenchmarkProjection.Compare(emptyCompareSummary)
                            : emptyCompareSummary;
                        var compareFixedBytes = SerializedLength(fittedEmptyCompare);
                        tableSelectionAccounting?.Invoke(compareFixedBytes);
                        var compareMaps = SelectTableReadMaps(
                            [summary.Baseline.LogicalReadsByTable, summary.Candidate.LogicalReadsByTable],
                            selectedBytes => compareFixedBytes + selectedBytes + 384 <= maximumBytes);
                        var compareProjection = compareShell with
                        {
                            Baseline = baseline with { LogicalReadsByTable = compareMaps[0] },
                            Candidate = candidate with { LogicalReadsByTable = compareMaps[1] },
                        };
                        omissions += clippedItems;
                        return profile == AgentProjectionProfile.Mcp
                            ? AgentBenchmarkProjection.Compare(compareProjection)
                            : compareProjection;
                    }
                case MeasureBenchmarkSummary summary:
                    {
                        var query = ClipVariant(summary.Query, new Dictionary<string, CompareDistribution>(StringComparer.Ordinal));
                        var measureShell = summary with
                        {
                            Query = query,
                            NoteworthyOperators = Take(summary.NoteworthyOperators.Take(10).ToArray()).Select(op => op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) }).ToArray(),
                            ArtifactDirectory = profile == AgentProjectionProfile.Mcp ? summary.ArtifactDirectory : Clip(summary.ArtifactDirectory),
                        };
                        var emptyMeasureSummary = measureShell with
                        {
                            Query = query with { LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal) },
                        };
                        object fittedEmptyMeasure = profile == AgentProjectionProfile.Mcp
                            ? AgentBenchmarkProjection.Measure(emptyMeasureSummary)
                            : emptyMeasureSummary;
                        var measureFixedBytes = SerializedLength(fittedEmptyMeasure);
                        tableSelectionAccounting?.Invoke(measureFixedBytes);
                        var queryMaps = SelectTableReadMaps(
                            [summary.Query.LogicalReadsByTable],
                            selectedBytes => measureFixedBytes + selectedBytes + 384 <= maximumBytes);
                        var measureProjection = measureShell with
                        {
                            Query = query with { LogicalReadsByTable = queryMaps[0] },
                        };
                        omissions += clippedItems;
                        return profile == AgentProjectionProfile.Mcp
                            ? AgentBenchmarkProjection.Measure(measureProjection)
                            : measureProjection;
                    }
                case DistilledPlan plan:
                    var nodeCostEstimate = Math.Max(1024L, (long)Math.Max(1, maximumCellCharacters) * 36);
                    var planProjection = ProjectPlan(plan, detailLimit, Clip, Take, Math.Max(1, (int)(maximumBytes / nodeCostEstimate)));
                    omissions += planProjection.OmittedItems;
                    omissions += clippedItems;
                    return planProjection.Report;
                case SqlHarnessWatchReport watch:
                    var selectedPolls = Take(watch.EmittedPolls);
                    var polls = selectedPolls.Select(poll =>
                    {
                        var query = new SqlHarnessQueryReport(
                            watch.Target, "read-only", poll.ResultSets, [], 0, poll.ElapsedMilliseconds, poll.ResultHash, new OutputFootprint(0, 0));
                        var compact = detailLimit > 0
                            ? ProjectQueryRowSample(query, [], [], detailLimit, maximumCellCharacters, maximumBytes, out var pollOmitted)
                            : ProjectQueryInventory(query, [], [], maximumCellCharacters, maximumBytes, out pollOmitted);
                        omissions += pollOmitted;
                        return new AgentWatchPollOutput(poll.Poll, poll.ElapsedMilliseconds, poll.ResultHash, compact.ResultSets);
                    }).ToArray();
                    omissions += clippedItems;
                    return profile == AgentProjectionProfile.Mcp
                        ? new AgentWatchOutput(
                            watch.Target,
                            watch.PollCount,
                            watch.ElapsedMilliseconds,
                            AgentProjectionNames.WatchExitReason(watch.ExitReason),
                            polls,
                            watch.TotalChangedPolls,
                            watch.OmittedPolls,
                            watch.EmittedPolls.Count - polls.Length)
                        : new AgentCliWatchOutput(
                            watch.Target,
                            watch.PollCount,
                            watch.ElapsedMilliseconds,
                            watch.ExitReason,
                            polls,
                            watch.TotalChangedPolls,
                            watch.OmittedPolls,
                            watch.EmittedPolls.Count - polls.Length);
                case SqlHarnessSnapshotReport snapshot:
                    var snapshotName = Clip(snapshot.Name)!;
                    var differences = Take(snapshot.Differences);
                    omissions += clippedItems;
                    var projectedSnapshot = snapshot with { Name = snapshotName, Differences = differences };
                    return profile == AgentProjectionProfile.Mcp
                        ? new AgentSnapshotOutput(
                            projectedSnapshot.Target,
                            projectedSnapshot.Name,
                            AgentProjectionNames.SnapshotVerdict(projectedSnapshot.Verdict),
                            projectedSnapshot.DifferenceCount,
                            projectedSnapshot.Differences)
                        : projectedSnapshot;
                case ArtifactMetricsSection metrics:
                    {
                        var sourceVariants = Take(metrics.Variants);
                        var variantsShell = sourceVariants.Select(variant => variant with
                        {
                            Name = Clip(variant.Name)!,
                            LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal),
                            Warnings = Take(variant.Warnings).Select(warning => Clip(warning)!).ToArray(),
                        }).ToArray();
                        var emptyMetrics = metrics with
                        {
                            Variants = variantsShell.Select(variant => variant with
                            {
                                LogicalReadsByTable = new Dictionary<string, CompareDistribution>(StringComparer.Ordinal),
                            }).ToArray(),
                        };
                        var metricsFixedBytes = SerializedLength(emptyMetrics);
                        tableSelectionAccounting?.Invoke(metricsFixedBytes);
                        var variantMaps = SelectTableReadMaps(
                            sourceVariants.Select(variant => variant.LogicalReadsByTable).ToArray(),
                            selectedBytes => metricsFixedBytes + selectedBytes + 384 <= maximumBytes);
                        var variants = variantsShell.Select((variant, index) => variant with
                        {
                            LogicalReadsByTable = variantMaps[index],
                        }).ToArray();
                        omissions += clippedItems;
                        return metrics with { Variants = variants };
                    }
                case ArtifactOperatorsSection operators:
                    {
                        var selected = new List<NoteworthyOperatorSummary>();
                        foreach (var op in operators.Operators.Take(detailLimit))
                        {
                            var item = op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) };
                            var candidate = operators with
                            {
                                Operators = selected.Append(item).ToArray(),
                                Continuation = selected.Count + 1 < operators.Operators.Count ? operators.Cursor + selected.Count + 1 : null
                            };
                            if (SerializedLength(candidate) + 384 > maximumBytes) break;
                            selected.Add(item);
                        }
                        if (selected.Count == 0 && operators.Operators.Count > 0)
                            throw new SqlHarnessSafetyException("The output budget cannot fit an operator page.");
                        omissions += Math.Max(0, operators.Operators.Count - selected.Count) + clippedItems;
                        return operators with
                        {
                            Operators = selected,
                            Continuation = selected.Count < operators.Operators.Count
                            ? operators.Cursor + selected.Count : operators.Continuation
                        };
                    }
                case ArtifactPlanSection plan:
                    {
                        var statementText = Clip(plan.StatementText);
                        var missingIndexes = Take(plan.MissingIndexes).Select(index => index with
                        {
                            Table = Clip(index.Table)!,
                            EqualityColumns = Take(index.EqualityColumns).Select(value => Clip(value)!).ToArray(),
                            InequalityColumns = Take(index.InequalityColumns).Select(value => Clip(value)!).ToArray(),
                            IncludeColumns = Take(index.IncludeColumns).Select(value => Clip(value)!).ToArray(),
                        }).ToArray();
                        var preparedNodes = plan.Nodes.Select(node =>
                        {
                            var nodeClippedItems = 0;
                            string? ClipNodeText(string? value) => ClipText(value, maximumCellCharacters, ref nodeClippedItems);
                            var op = node.Operator with
                            {
                                PhysicalOp = ClipNodeText(node.Operator.PhysicalOp)!,
                                LogicalOp = ClipNodeText(node.Operator.LogicalOp),
                                ObjectName = ClipNodeText(node.Operator.ObjectName),
                                IndexName = ClipNodeText(node.Operator.IndexName),
                                Predicate = ClipNodeText(node.Operator.Predicate),
                                Warnings = node.Operator.Warnings.Take(detailLimit).Select(value => ClipNodeText(value)!).ToArray(),
                            };
                            return (Node: node with { Operator = op }, ClippedItems: nodeClippedItems);
                        }).ToArray();
                        var selected = new List<ArtifactPlanNode>();
                        var selectedClippedItems = 0;
                        foreach (var preparedNode in preparedNodes)
                        {
                            var candidate = plan with
                            {
                                Nodes = selected.Append(preparedNode.Node).ToArray(),
                                MissingIndexes = missingIndexes,
                                StatementText = statementText,
                                Continuation = selected.Count + 1 < plan.Nodes.Count ? plan.Cursor + selected.Count + 1 : plan.Continuation
                            };
                            if (SerializedLength(candidate) + 384 > maximumBytes) break;
                            selected.Add(preparedNode.Node);
                            selectedClippedItems += preparedNode.ClippedItems;
                        }
                        if (selected.Count == 0 && plan.Nodes.Count > 0)
                        {
                            omissions += plan.Nodes.Count + clippedItems;
                            return plan with
                            {
                                Nodes = [],
                                Continuation = null,
                                MissingIndexes = missingIndexes,
                                StatementText = statementText,
                                PageError = "The output budget cannot fit a plan node page."
                            };
                        }
                        int? nextCursor = selected.Count < plan.Nodes.Count
                            ? plan.Cursor + selected.Count
                            : plan.Continuation;
                        omissions += Math.Max(0, plan.Nodes.Count - selected.Count) + clippedItems + selectedClippedItems;
                        return plan with
                        {
                            Nodes = selected,
                            Continuation = nextCursor,
                            MissingIndexes = missingIndexes,
                            StatementText = statementText,
                            PageError = null
                        };
                    }
                case ArtifactStatementsSection statements:
                    {
                        var selected = new List<ArtifactStatementMetric>();
                        foreach (var sourceStatement in statements.Statements)
                        {
                            var statement = sourceStatement with
                            {
                                Variant = Clip(sourceStatement.Variant)!,
                                ParameterSet = Clip(sourceStatement.ParameterSet),
                                TopOperators = Take(sourceStatement.TopOperators).Select(op => op with
                                {
                                    PhysicalOp = Clip(op.PhysicalOp)!,
                                    Object = Clip(op.Object),
                                    Index = Clip(op.Index),
                                }).ToArray(),
                            };
                            var candidateStatements = selected.Append(statement).ToArray();
                            var candidate = statements with
                            {
                                Statements = candidateStatements,
                                OmittedStatements = statements.OmittedStatements + statements.Statements.Count - candidateStatements.Length,
                                Continuation = candidateStatements.Length < statements.Statements.Count
                                    ? statements.Cursor + candidateStatements.Length
                                    : statements.Continuation,
                            };
                            if (SerializedLength(candidate) + 384 > maximumBytes)
                                break;
                            selected.Add(statement);
                        }
                        if (selected.Count == 0 && statements.Statements.Count > 0)
                            throw new SqlHarnessSafetyException("The output budget cannot fit an artifact statement.");
                        omissions += Math.Max(0, statements.Statements.Count - selected.Count) + clippedItems;
                        return statements with
                        {
                            Statements = selected,
                            OmittedStatements = statements.OmittedStatements + Math.Max(0, statements.Statements.Count - selected.Count),
                            Continuation = selected.Count < statements.Statements.Count
                                ? statements.Cursor + selected.Count
                                : statements.Continuation,
                        };
                    }
                case ArtifactMatrixCellsSection matrixCells:
                    {
                        var selected = new List<AgentMatrixCase>();
                        foreach (var matrixCase in matrixCells.Cases)
                        {
                            var projectedCase = matrixCase with
                            {
                                Warnings = matrixCase.Warnings.Take(1)
                                    .Select(value => ClipText(value, Math.Min(128, maximumCellCharacters), ref clippedItems)!)
                                    .ToArray(),
                            };
                            var candidateCases = selected.Append(projectedCase).ToArray();
                            var candidate = matrixCells with
                            {
                                Cases = candidateCases,
                                Continuation = candidateCases.Length < matrixCells.Cases.Count
                                    ? matrixCells.Cursor + candidateCases.Length
                                    : matrixCells.Continuation,
                            };
                            if (SerializedLength(candidate) + 384 > maximumBytes)
                                break;
                            selected.Add(projectedCase);
                            omissions += Math.Max(0, matrixCase.Warnings.Count - projectedCase.Warnings.Count);
                        }
                        omissions += matrixCells.Cases.Count - selected.Count + clippedItems;
                        return matrixCells with
                        {
                            Cases = selected,
                            Continuation = selected.Count < matrixCells.Cases.Count
                                ? matrixCells.Cursor + selected.Count
                                : matrixCells.Continuation,
                        };
                    }
                case SqlHarnessDryRunReport dryRun:
                    return dryRun;
                default:
                    if (report is null) return null;
                    if (report.GetType().IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
                    {
                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                        var properties = report.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                        foreach (var property in properties.Take(detailLimit))
                        {
                            object? value;
                            try { value = property.GetValue(report); }
                            catch (System.Reflection.TargetInvocationException) { omissions++; continue; }
                            if (value is string text) fields[property.Name] = Clip(text);
                            else if (IsScalar(value)) fields[property.Name] = value;
                            else omissions++;
                        }
                        omissions += Math.Max(0, properties.Length - detailLimit);
                        omissions += clippedItems;
                        return fields;
                    }
                    omissions++;
                    return new AgentBoundedOutput(report.GetType().Name, 1);
            }
        }
        finally
        {
            omittedItems = omissions;
        }

        IReadOnlyList<IReadOnlyDictionary<string, CompareDistribution>> SelectTableReadMaps(
            IReadOnlyList<IReadOnlyDictionary<string, CompareDistribution>> sources,
            Func<long, bool> fits)
        {
            var ordered = sources.Select(reads => BySignificance(reads).ToArray()).ToArray();
            var entryCosts = ordered.Select(entries => entries.Select(pair => SerializedLength(
                new Dictionary<string, CompareDistribution>(StringComparer.Ordinal) { [pair.Key] = pair.Value }) - 2).ToArray()).ToArray();
            tableSelectionAccounting?.Invoke(entryCosts.SelectMany(costs => costs).Sum());
            var selected = sources.Select(_ => new Dictionary<string, CompareDistribution>(StringComparer.Ordinal)).ToArray();
            var indexes = new int[sources.Count];
            var selectedBytes = 0L;
            var selectedCounts = new int[sources.Count];
            var progressed = true;
            while (progressed)
            {
                progressed = false;
                for (var mapIndex = 0; mapIndex < sources.Count; mapIndex++)
                {
                    if (selected[mapIndex].Count >= detailLimit)
                    {
                        indexes[mapIndex] = ordered[mapIndex].Length;
                        continue;
                    }
                    if (indexes[mapIndex] >= ordered[mapIndex].Length)
                        continue;

                    progressed = true;
                    var entryIndex = indexes[mapIndex]++;
                    var pair = ordered[mapIndex][entryIndex];
                    selected[mapIndex].Add(pair.Key, pair.Value);
                    var additionalBytes = entryCosts[mapIndex][entryIndex] + (selectedCounts[mapIndex] > 0 ? 1 : 0);
                    // Fit each complete entry in the enclosing projection, leaving
                    // room for its response envelope. A rejected identity does not
                    // consume a detail slot, so later complete entries can still fit.
                    if (!fits(selectedBytes + additionalBytes))
                        selected[mapIndex].Remove(pair.Key);
                    else
                    {
                        selectedBytes += additionalBytes;
                        selectedCounts[mapIndex]++;
                    }
                }
            }

            omissions += sources.Select((source, index) => Math.Max(0, source.Count - selected[index].Count)).Sum();
            return selected;
        }

        BenchmarkVariantSummary ClipVariant(
            BenchmarkVariantSummary variant,
            IReadOnlyDictionary<string, CompareDistribution> reads)
        {
            var warnings = Take(variant.Warnings).Select(w => Clip(w)!).ToArray();
            return variant with { LogicalReadsByTable = reads, Warnings = warnings };
        }

        IReadOnlyList<CompareOperatorReport> ProjectSetOperators(IReadOnlyList<CompareOperatorReport> source)
        {
            var selected = source.Where(op => op.HasWarnings || op.HasSpill || op.HasImplicitConversion)
                .OrderByDescending(op => op.HasWarnings || op.HasSpill || op.HasImplicitConversion)
                .Take(10).ToArray();
            return Take(selected);
        }

        static bool IsScalar(object? value) => value is null || value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or char or DateTime or DateTimeOffset or Guid or Enum;

    }

    /// <summary>
    /// Query projection for a positive detail limit: every result set and
    /// every column is kept, and each emitted row keeps all its cells (values
    /// are only clipped), so each row stays aligned with the complete column
    /// schema. Rows are a source prefix per set. A set whose complete rows fit
    /// a conservative reserve is kept whole; the reserve is one third of the
    /// outer budget minus the rowless shell (all schemas and inventories), and
    /// it is granted to sets cheapest first (ties by ordinal), whole set or
    /// nothing. Every other set is sampled to <paramref name="rowLimit"/> rows.
    /// The caller's real measurement stays the authority.
    /// </summary>
    private static AgentCompactQuery ProjectQueryRowSample(
        SqlHarnessQueryReport query,
        IReadOnlyList<string?> messages,
        IReadOnlyList<string> warnings,
        int rowLimit,
        int maximumCellCharacters,
        int maximumBytes,
        out int omittedItems)
    {
        var count = query.ResultSets.Count;
        var caches = query.ResultSets.Select(set => new QueryRowCache(set, maximumCellCharacters)).ToArray();
        var rowless = new AgentCompactResultSet[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var set = query.ResultSets[ordinal];
            rowless[ordinal] = new AgentCompactResultSet(ordinal, set.Columns.Select(CompactColumn).ToArray(), [], set.RowCount,
                set.OmittedRowCount + set.Rows.Count, 0, null, set.Rows.Count, set.OmittedRowCount);
        }

        long reserve = PreFilterBudget(maximumBytes) - SerializedLength(QueryShell(query, rowless, 0, messages, warnings));
        var whole = new bool[count];
        if (reserve > 0)
        {
            var candidates = Enumerable.Range(0, count)
                .Where(ordinal => caches[ordinal].Count > rowLimit)
                .Select(ordinal => (Ordinal: ordinal, Cost: caches[ordinal].CompleteCost(reserve)))
                .OrderBy(candidate => candidate.Cost)
                .ThenBy(candidate => candidate.Ordinal)
                .ToArray();
            foreach (var (ordinal, cost) in candidates)
            {
                if (cost > reserve)
                    continue;
                reserve -= cost;
                whole[ordinal] = true;
            }
        }

        var omitted = 0;
        var sets = new AgentCompactResultSet[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var take = whole[ordinal] ? caches[ordinal].Count : Math.Min(caches[ordinal].Count, rowLimit);
            var rows = caches[ordinal].Prefix(take, out var clipped, out var clippedCells);
            var setOmitted = caches[ordinal].Count - take;
            omitted += setOmitted + clipped;
            sets[ordinal] = rowless[ordinal] with
            {
                Rows = rows,
                OmittedRowCount = query.ResultSets[ordinal].OmittedRowCount + setOmitted,
                ClippedCells = clippedCells,
                ProjectionOmittedRowCount = setOmitted,
                SourceOmittedRowCount = query.ResultSets[ordinal].OmittedRowCount,
            };
        }

        omittedItems = omitted;
        return QueryShell(query, sets, 0, messages, warnings);
    }

    /// <summary>
    /// Budget-aware query fallback at detail limit 0. Like
    /// <see cref="BuildOmittedCellReferences"/> this is a conservative
    /// pre-filter against one third of the outer byte budget; the caller's
    /// real measurement stays the authority. It spends that budget in this
    /// order, and every step is bounded by the budget that is left:
    /// <list type="number">
    /// <item>the result-set inventory (ordinal and row/column counts) for as
    /// many leading sets as fit; trailing sets are counted;</item>
    /// <item>complete column schemas, cheapest schema first (ties by
    /// ordinal), whole schema or nothing;</item>
    /// <item>complete rows for sets whose full schema was kept, cheapest set
    /// first (ties by ordinal), whole set or nothing;</item>
    /// <item>a leading column prefix, in ordinal order, for each set whose
    /// schema did not fit whole (such a set carries no rows);</item>
    /// <item>a leading row prefix, in ordinal order, for each full-schema set
    /// whose complete rows did not fit.</item>
    /// </list>
    /// So small summary sets keep their complete schema and rows ahead of a
    /// wide detail set's column prefix or row sample. Nothing partial is ever
    /// unaccounted: every dropped set, column, and row is counted.
    /// </summary>
    private static AgentCompactQuery ProjectQueryInventory(
        SqlHarnessQueryReport query,
        IReadOnlyList<string?> messages,
        IReadOnlyList<string> warnings,
        int maximumCellCharacters,
        int maximumBytes,
        out int omittedItems)
    {
        long remaining = PreFilterBudget(maximumBytes) - SerializedLength(QueryShell(query, [], query.ResultSets.Count, messages, warnings));

        // 1. Inventory for a prefix of the result sets.
        var kept = 0;
        foreach (var set in query.ResultSets)
        {
            var inventory = new AgentCompactResultSet(kept, [], [], set.RowCount,
                set.OmittedRowCount + set.Rows.Count, set.Columns.Count, null, set.Rows.Count, set.OmittedRowCount);
            var cost = SerializedLength(inventory) + 1;
            if (cost > remaining)
                break;
            remaining -= cost;
            kept++;
        }

        var source = query.ResultSets;
        var columnCounts = new int[kept];
        var fullSchema = new bool[kept];

        // 2. Complete schemas, cheapest first.
        var schemaCosts = Enumerable.Range(0, kept)
            .Select(ordinal => (Ordinal: ordinal, Cost: source[ordinal].Columns.Sum(column => (long)SerializedLength(column) + 1)))
            .OrderBy(candidate => candidate.Cost)
            .ThenBy(candidate => candidate.Ordinal)
            .ToArray();
        foreach (var (ordinal, cost) in schemaCosts)
        {
            if (cost > remaining)
                continue;
            remaining -= cost;
            fullSchema[ordinal] = true;
            columnCounts[ordinal] = source[ordinal].Columns.Count;
        }

        // 3. Complete rows for full-schema sets, cheapest first.
        var caches = new QueryRowCache?[kept];
        var rowCounts = new int[kept];
        if (remaining > 0)
        {
            var rowCosts = Enumerable.Range(0, kept)
                .Where(ordinal => fullSchema[ordinal] && source[ordinal].Rows.Count > 0)
                .Select(ordinal =>
                {
                    caches[ordinal] = new QueryRowCache(source[ordinal], maximumCellCharacters);
                    return (Ordinal: ordinal, Cost: caches[ordinal]!.CompleteCost(remaining));
                })
                .OrderBy(candidate => candidate.Cost)
                .ThenBy(candidate => candidate.Ordinal)
                .ToArray();
            foreach (var (ordinal, cost) in rowCosts)
            {
                if (cost > remaining)
                    continue;
                remaining -= cost;
                rowCounts[ordinal] = source[ordinal].Rows.Count;
            }
        }

        // 4. Column prefixes for sets whose schema did not fit whole.
        for (var ordinal = 0; ordinal < kept; ordinal++)
        {
            if (fullSchema[ordinal])
                continue;
            var columns = source[ordinal].Columns;
            while (columnCounts[ordinal] < columns.Count)
            {
                var cost = SerializedLength(columns[columnCounts[ordinal]]) + 1;
                if (cost > remaining)
                    break;
                remaining -= cost;
                columnCounts[ordinal]++;
            }
        }

        // 5. Row prefixes for full-schema sets whose complete rows did not fit.
        for (var ordinal = 0; ordinal < kept; ordinal++)
        {
            if (caches[ordinal] is not { } cache || rowCounts[ordinal] == cache.Count)
                continue;
            while (rowCounts[ordinal] < cache.Count)
            {
                var cost = cache.Cost(rowCounts[ordinal]);
                if (cost > remaining)
                    break;
                remaining -= cost;
                rowCounts[ordinal]++;
            }
        }

        var omitted = source.Count - kept;
        var sets = new AgentCompactResultSet[kept];
        for (var ordinal = 0; ordinal < kept; ordinal++)
        {
            var set = source[ordinal];
            var clipped = 0;
            IReadOnlyList<AgentClippedCell>? retainedClippedCells = null;
            IReadOnlyList<IReadOnlyList<object?>> rows;
            if (caches[ordinal] is { } cache)
                rows = cache.Prefix(rowCounts[ordinal], out clipped, out retainedClippedCells);
            else
                rows = [];
            var omittedColumns = set.Columns.Count - columnCounts[ordinal];
            var omittedRows = set.Rows.Count - rows.Count;
            omitted += omittedColumns + omittedRows + clipped;
            sets[ordinal] = new AgentCompactResultSet(
                ordinal,
                set.Columns.Take(columnCounts[ordinal]).Select(CompactColumn).ToArray(),
                rows,
                set.RowCount,
                set.OmittedRowCount + omittedRows,
                omittedColumns,
                retainedClippedCells,
                omittedRows,
                set.OmittedRowCount);
        }

        omittedItems = omitted;
        return QueryShell(query, sets, source.Count - kept, messages, warnings);
    }

    /// <summary>
    /// Lazily projected (clipped) rows of one source result set with their
    /// serialized cost. Rows are projected at most once, in source order, and
    /// only as far as a caller asks, so costing is bounded by the budget the
    /// caller passes. Clipped cells are counted only for rows emitted.
    /// </summary>
    private sealed class QueryRowCache(SqlHarnessResultSetReport set, int maximumCellCharacters)
    {
        private readonly List<(IReadOnlyList<object?> Cells, int Clipped, int Cost, IReadOnlyList<AgentClippedCell> ClippedCells)> rows = [];

        public int Count => set.Rows.Count;

        public int Cost(int index) => Get(index).Cost;

        /// <summary>The cost of every row, or <paramref name="cap"/> + 1 once the running sum exceeds the cap.</summary>
        public long CompleteCost(long cap)
        {
            long total = 0;
            for (var index = 0; index < Count; index++)
            {
                total += Get(index).Cost;
                if (total > cap)
                    return cap + 1;
            }

            return total;
        }

        public IReadOnlyList<IReadOnlyList<object?>> Prefix(int length, out int clipped, out IReadOnlyList<AgentClippedCell>? clippedCells)
        {
            var selected = new IReadOnlyList<object?>[length];
            var metadata = new List<AgentClippedCell>();
            clipped = 0;
            for (var index = 0; index < length; index++)
            {
                var row = Get(index);
                selected[index] = row.Cells;
                clipped += row.Clipped;
                metadata.AddRange(row.ClippedCells);
            }

            clippedCells = metadata.Count == 0 ? null : metadata;
            return selected;
        }

        private (IReadOnlyList<object?> Cells, int Clipped, int Cost, IReadOnlyList<AgentClippedCell> ClippedCells) Get(int index)
        {
            while (rows.Count <= index)
            {
                var source = set.Rows[rows.Count];
                var clipped = 0;
                var clippedCells = new List<AgentClippedCell>();
                var cells = new object?[source.Count];
                for (var cell = 0; cell < cells.Length; cell++)
                {
                    var value = source[cell];
                    if (value is string text && text.Length > maximumCellCharacters)
                    {
                        var prefixLength = SafeTextPrefixLength(text, maximumCellCharacters);
                        cells[cell] = text[..prefixLength];
                        clippedCells.Add(new AgentClippedCell(rows.Count, cell, text.Length, prefixLength, "utf16-code-units"));
                        clipped++;
                    }
                    else if (value is byte[] bytes && ((long)bytes.Length + 2) / 3 * 4 > maximumCellCharacters)
                    {
                        var prefixLength = Math.Min(bytes.Length, maximumCellCharacters / 4 * 3);
                        cells[cell] = new AgentBinaryValue(bytes.LongLength, Convert.ToBase64String(bytes.AsSpan(0, prefixLength)), Truncated: true);
                        clippedCells.Add(new AgentClippedCell(rows.Count, cell, bytes.LongLength, prefixLength, "bytes"));
                        clipped++;
                    }
                    else
                    {
                        cells[cell] = ProjectCellValue(value, maximumCellCharacters, ref clipped);
                    }
                }
                rows.Add((cells, clipped, SerializedLength(cells) + 1, clippedCells));
            }

            return rows[index];
        }
    }

    /// <summary>Conservative prefilter that leaves room for envelope metadata and other fields before the final text-budget fit.</summary>
    private static int PreFilterBudget(int maximumBytes) => Math.Max(0, maximumBytes / 3 - 128);

    private static string? ClipText(string? value, int maximumCellCharacters, ref int clippedItems)
    {
        if (value is null || value.Length <= maximumCellCharacters) return value;
        clippedItems++;
        var length = maximumCellCharacters;
        length = SafeTextPrefixLength(value, length);
        return value[..length];
    }

    private static int SafeTextPrefixLength(string value, int maximumLength)
    {
        var length = Math.Min(value.Length, maximumLength);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
        return length;
    }

    private static object? ProjectCellValue(object? value, int maximumCellCharacters, ref int clippedItems)
    {
        switch (value)
        {
            case string text:
                return ClipText(text, maximumCellCharacters, ref clippedItems);
            case byte[] bytes:
                var fullBase64Length = ((long)bytes.Length + 2) / 3 * 4;
                if (fullBase64Length <= maximumCellCharacters)
                    return Convert.ToBase64String(bytes);
                clippedItems++;
                var prefixLength = Math.Min(bytes.Length, maximumCellCharacters / 4 * 3);
                var prefix = Convert.ToBase64String(bytes.AsSpan(0, prefixLength));
                return new AgentBinaryValue(bytes.LongLength, prefix, Truncated: true);
            default:
                return value;
        }
    }

    private static AgentCompactQuery QueryShell(
        SqlHarnessQueryReport query,
        IReadOnlyList<AgentCompactResultSet> sets,
        int omittedResultSets,
        IReadOnlyList<string?> messages,
        IReadOnlyList<string> warnings) =>
        new(query.Target.ActualDatabase, query.StatementClassification, sets, omittedResultSets,
            messages.Count == 0 ? null : messages,
            query.RecordsAffected < 0 ? null : query.RecordsAffected,
            query.DurationMilliseconds,
            warnings.Count == 0 ? null : warnings);

    private static AgentQueryColumn CompactColumn(SqlHarnessColumnReport column) =>
        new(column.Name, ValueTypeName(column.DataType), column.AllowNull);

    public static string ValueTypeName(string clrType) => clrType switch
    {
        "System.DateTime" or "DateTime" => "dateTime",
        "System.DateTimeOffset" or "DateTimeOffset" => "dateTimeOffset",
        "System.String" or "String" => "string",
        "System.Int16" or "Int16" => "int16",
        "System.Int32" or "Int32" => "int32",
        "System.Int64" or "Int64" => "int64",
        "System.Boolean" or "Boolean" => "boolean",
        "System.Decimal" or "Decimal" => "decimal",
        "System.Byte[]" or "Byte[]" => "bytes",
        _ => clrType,
    };

    private static int SerializedLength<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, ProjectionJson).Length;

    private static (DistilledPlan Report, int OmittedItems) ProjectPlan(DistilledPlan plan, int detailLimit, Func<string?, string?> clip, Func<IReadOnlyList<PlanNode>, IReadOnlyList<PlanNode>> takeNodes, int maximumNodes)
    {
        var omitted = 0;
        var nodesRemaining = maximumNodes;
        var statements = plan.Statements.Take(detailLimit).Select(statement => statement with
        {
            StatementText = clip(statement.StatementText),
            MissingIndexes = statement.MissingIndexes.Take(detailLimit).Select(index => index with
            {
                Table = clip(index.Table)!,
                EqualityColumns = index.EqualityColumns.Take(detailLimit).Select(value => clip(value)!).ToArray(),
                InequalityColumns = index.InequalityColumns.Take(detailLimit).Select(value => clip(value)!).ToArray(),
                IncludeColumns = index.IncludeColumns.Take(detailLimit).Select(value => clip(value)!).ToArray(),
            }).ToArray(),
            Root = ProjectNode(statement.Root, 0),
        }).ToArray();
        omitted += Math.Max(0, plan.Statements.Count - statements.Length);
        omitted += plan.Statements.Take(detailLimit).Sum(statement => Math.Max(0, statement.MissingIndexes.Count - detailLimit));
        return (new DistilledPlan(statements), omitted);

        PlanNode ProjectNode(PlanNode node, int depth)
        {
            if (nodesRemaining <= 0)
            {
                omitted += 1;
                omitted += node.Warnings.Count;
                return ClipPlanNode(node) with
                {
                    Warnings = [],
                    Children = [],
                    OmittedChildCount = node.Children.Count,
                    OmittedSubtreeCount = CountDescendants(node),
                };
            }
            nodesRemaining--;
            if (depth >= 6)
            {
                omitted += node.Children.Count;
                omitted += node.Warnings.Count;
                return ClipPlanNode(node) with
                {
                    Warnings = [],
                    Children = [],
                    OmittedChildCount = node.Children.Count,
                    OmittedSubtreeCount = CountDescendants(node),
                };
            }
            omitted += Math.Max(0, node.Warnings.Count - detailLimit);
            var selectedChildren = takeNodes(node.Children);
            var omittedChildren = node.Children.Skip(selectedChildren.Count).ToArray();
            var children = selectedChildren.Select(child => ProjectNode(child, depth + 1)).ToArray();
            var omittedSubtreeCount = omittedChildren.Sum(child => CountDescendants(child) + 1);
            return ClipPlanNode(node) with
            {
                Warnings = node.Warnings.Take(detailLimit).Select(value => clip(value)!).ToArray(),
                Children = children,
                OmittedChildCount = omittedChildren.Length,
                OmittedSubtreeCount = omittedSubtreeCount,
            };
        }

        PlanNode ClipPlanNode(PlanNode node) => node with
        {
            PhysicalOp = clip(node.PhysicalOp)!,
            LogicalOp = clip(node.LogicalOp),
            ObjectName = clip(node.ObjectName),
            IndexName = clip(node.IndexName),
            Predicate = clip(node.Predicate),
        };

        static long CountDescendants(PlanNode node) =>
            node.Children.Sum(child => 1 + CountDescendants(child));

    }

}