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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AgentClippedCell>? ClippedCells = null);

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
    long DurationMilliseconds = 0);

/// <summary>The version shared by CLI and MCP agent response envelopes.</summary>
public static class AgentEnvelopeSchema
{
    public const int Version = 2;
}

/// <summary>Presentation-only bounds for the opt-in agent response.</summary>
public sealed record AgentOutputOptions(int MaximumBytes = 65_536, int MaximumCellCharacters = 512);

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

    public static object? Project(object? report, int maximumCellCharacters, int detailLimit, out int omittedItems, int maximumBytes = 65_536)
    {
        omittedItems = 0;
        var omissions = 0;
        var clippedItems = 0;
        string? Clip(string? value) => ClipText(value, maximumCellCharacters, ref clippedItems);
        object? ProjectCell(object? value) => ProjectCellValue(value, maximumCellCharacters, ref clippedItems);
        IReadOnlyList<T> Take<T>(IReadOnlyList<T> values)
        {
            var selected = values.Take(detailLimit).ToArray();
            omissions += Math.Max(0, values.Count - selected.Length);
            return selected;
        }
        IReadOnlyList<SqlHarnessResultSetReport> ProjectResultSets(IReadOnlyList<SqlHarnessResultSetReport> resultSets)
        {
            var selectedSets = Take(resultSets);
            return selectedSets.Select(set =>
            {
                var columns = Take(set.Columns);
                var rows = Take(set.Rows).Select(row => Take(row).Select(ProjectCell).ToArray()).ToArray();
                return new SqlHarnessResultSetReport(columns, rows, set.RowCount, set.OmittedRowCount + Math.Max(0, set.Rows.Count - rows.Length));
            }).ToArray();
        }
        try
        {
            switch (report)
            {
                case SqlHarnessQueryReport query:
                    {
                        var messages = Take(query.Messages).Select(Clip).ToArray();
                        var queryProjection = detailLimit > 0
                            ? ProjectQueryRowSample(query, messages, detailLimit, maximumCellCharacters, maximumBytes, out var queryOmitted)
                            : ProjectQueryInventory(query, messages, maximumCellCharacters, maximumBytes, out queryOmitted);
                        omissions += queryOmitted;
                        omissions += clippedItems;
                        return queryProjection;
                    }
                case SqlHarnessCountsReport counts:
                    var tables = Take(counts.Tables).Select(table => table with { Schema = Clip(table.Schema)!, Name = Clip(table.Name)! }).ToArray();
                    omissions += clippedItems;
                    return new { counts.Target, tables, omitted = counts.Omitted + omissions };
                case SqlHarnessSchemaReport schema:
                    var selectedObjects = Take(schema.Objects);
                    var omittedObjects = schema.OmittedObjects + schema.Objects.Count - selectedObjects.Count;
                    var objects = selectedObjects.Select(value => new
                    {
                        schema = Clip(value.Schema),
                        name = Clip(value.Name),
                        kind = Clip(value.Kind),
                        columns = Take(value.Columns).Select(column => column with { Name = Clip(column.Name)!, Type = Clip(column.Type)! }).ToArray(),
                        indexes = Take(value.Indexes).Select(i => new { name = Clip(i.Name), i.Unique, keys = Take(i.Keys).Select(Clip), includes = Take(i.Includes).Select(Clip), filter = Clip(i.Filter) }).ToArray(),
                        foreignKeys = Take(value.ForeignKeys).Select(fk => fk with { Name = Clip(fk.Name)!, Columns = Clip(fk.Columns)!, ReferencedTable = Clip(fk.ReferencedTable)!, ReferencedColumns = Clip(fk.ReferencedColumns)! }).ToArray(),
                    }).ToArray();
                    omissions += clippedItems;
                    return new { schema.Target, objects, omittedObjects, omittedItems = omissions };
                case SqlHarnessMeasureSetReport setReport:
                    var selectedSets = Take(setReport.Sets).Select(set => new MeasureParameterSetSummary(
                        Clip(set.Name)!, set.Parameters, set.ValueHash, set.ResultsStable,
                        set.Metrics.ElapsedTimeMilliseconds.Median, set.Metrics.CpuTimeMilliseconds.Median,
                        set.Metrics.LogicalReads.Median,
                        ProjectSetOperators(set.Metrics.Operators)
                            .Select(op => new NoteworthyOperatorSummary("query", op.NodeId, Clip(op.PhysicalOp)!, Clip(op.Object), op.HasWarnings, op.HasSpill, op.HasImplicitConversion)).ToArray())
                    {
                        MetricReport = set.Metrics.MetricReport,
                    }).ToArray();
                    var planCacheWarning = Clip(setReport.PlanCacheWarning)!;
                    var artifactDirectory = Clip(setReport.ArtifactDirectory);
                    omissions += clippedItems;
                    return new MeasureSetBenchmarkSummary(setReport.Target, setReport.MeasuredOrderRule, planCacheWarning, selectedSets, setReport.CrossSetSummary, artifactDirectory);
                case SqlHarnessCompareMatrixReport matrix:
                    var shared = BenchmarkSummaryProjector.Project(matrix);
                    var orderedMatrixCells = matrix.Cells.OrderBy(cell => cell.Index).ToArray();
                    var matrixArtifactId = shared.MatrixArtifactId;
                    var parameterName = Clip(matrix.ParameterName)!;
                    var parameterType = Clip(matrix.ParameterType)!;
                    var inventoryShell = shared with
                    {
                        ParameterName = parameterName,
                        ParameterType = parameterType,
                        Cases = [],
                        Details = [],
                        OmittedCaseCount = orderedMatrixCells.Length,
                        OmittedDetailCount = orderedMatrixCells.Length,
                        Continuation = null,
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
                        var candidate = inventoryShell with
                        {
                            Cases = candidateCases,
                            OmittedCaseCount = omittedCaseCount,
                            Continuation = omittedCaseCount > 0 && matrixArtifactId is not null ? candidateCases.Length : null,
                        };
                        // Leave room for the outer command/status/error/truncation envelope.
                        if (SerializedLength(candidate) + 384 > maximumBytes)
                            break;
                        cases.Add(item);
                    }

                    var omittedCases = orderedMatrixCells.Length - cases.Count;
                    var selectedDetails = new List<CompareMatrixCellDetail>();
                    var nestedOmitted = 0;
                    foreach (var cell in orderedMatrixCells.Take(cases.Count).Take(detailLimit))
                    {
                        var projected = Project(BenchmarkSummaryProjector.Project(cell.Compare), maximumCellCharacters, detailLimit,
                            out var cellOmitted, maximumBytes);
                        nestedOmitted += cellOmitted;
                        var summary = (CompareBenchmarkSummary)projected!;
                        selectedDetails.Add(new CompareMatrixCellDetail(cell.Index, new CompareMatrixCellDetailContent(
                            summary.Baseline, summary.Candidate, summary.Equivalence, summary.NoteworthyOperators, summary.Regression)));
                    }
                    var omittedDetails = orderedMatrixCells.Length - selectedDetails.Count;
                    omissions += omittedCases + omittedDetails + nestedOmitted + clippedItems;
                    return inventoryShell with
                    {
                        Cases = cases,
                        Details = selectedDetails,
                        OmittedCaseCount = omittedCases,
                        OmittedDetailCount = omittedDetails,
                        Continuation = omittedCases > 0 && matrixArtifactId is not null ? cases.Count : null,
                    };
                case SqlHarnessCompareReport compare:
                    {
                        var nestedProjection = Project(BenchmarkSummaryProjector.Project(compare), maximumCellCharacters, detailLimit, out var compareSummaryOmitted, maximumBytes);
                        omissions += compareSummaryOmitted;
                        return nestedProjection;
                    }
                case SqlHarnessMeasureReport measure:
                    {
                        var nestedProjection = Project(BenchmarkSummaryProjector.Project(measure), maximumCellCharacters, detailLimit, out var measureSummaryOmitted, maximumBytes);
                        omissions += measureSummaryOmitted;
                        return nestedProjection;
                    }
                case CompareBenchmarkSummary summary:
                    var compareProjection = summary with
                    {
                        Baseline = ClipVariant(summary.Baseline),
                        Candidate = ClipVariant(summary.Candidate),
                        NoteworthyOperators = Take(summary.NoteworthyOperators.Take(10).ToArray()).Select(op => op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) }).ToArray(),
                        ArtifactDirectory = Clip(summary.ArtifactDirectory),
                    };
                    omissions += clippedItems;
                    return compareProjection;
                case MeasureBenchmarkSummary summary:
                    var measureProjection = summary with
                    {
                        Query = ClipVariant(summary.Query),
                        NoteworthyOperators = Take(summary.NoteworthyOperators.Take(10).ToArray()).Select(op => op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) }).ToArray(),
                        ArtifactDirectory = Clip(summary.ArtifactDirectory),
                    };
                    omissions += clippedItems;
                    return measureProjection;
                case DistilledPlan plan:
                    var nodeCostEstimate = Math.Max(1024L, (long)Math.Max(1, maximumCellCharacters) * 36);
                    var planProjection = ProjectPlan(plan, detailLimit, Clip, Take, Math.Max(1, (int)(maximumBytes / nodeCostEstimate)));
                    omissions += planProjection.OmittedItems;
                    omissions += clippedItems;
                    return planProjection.Report;
                case SqlHarnessPingReport ping:
                    var pingProjection = ping with { Server = Clip(ping.Server)!, Database = Clip(ping.Database)!, Login = Clip(ping.Login)! };
                    omissions += clippedItems;
                    return pingProjection;
                case SqlHarnessSpaceReport space:
                    var files = Take(space.Files).Select(file => file with { LogicalName = Clip(file.LogicalName)!, Type = Clip(file.Type)!, PhysicalName = Clip(file.PhysicalName) }).ToArray();
                    var spaceTables = Take(space.Tables).Select(table => table with { Schema = Clip(table.Schema)!, Name = Clip(table.Name)! }).ToArray();
                    var indexes = Take(space.Indexes).Select(index => index with { Schema = Clip(index.Schema)!, Table = Clip(index.Table)!, Index = Clip(index.Index)!, Type = Clip(index.Type)!, Compression = Clip(index.Compression) }).ToArray();
                    omissions += clippedItems;
                    return new SqlHarnessSpaceReport(space.Target, files, space.Allocation, spaceTables, indexes);
                case SqlHarnessWatchReport watch:
                    var polls = Take(watch.EmittedPolls).Select(poll => new SqlHarnessWatchPoll(poll.Poll, poll.ElapsedMilliseconds, poll.ResultHash, ProjectResultSets(poll.ResultSets))).ToArray();
                    omissions += clippedItems;
                    return watch with { EmittedPolls = polls };
                case SqlHarnessSnapshotReport snapshot:
                    var snapshotName = Clip(snapshot.Name)!;
                    var differences = Take(snapshot.Differences);
                    omissions += clippedItems;
                    return snapshot with { Name = snapshotName, Differences = differences };
                case SqlHarnessQueryStoreTopReport queryStore:
                    var queryItems = Take(queryStore.Queries).Select(item => item with { ObjectName = Clip(item.ObjectName) }).ToArray();
                    var queryArtifacts = Clip(queryStore.ArtifactDirectory);
                    omissions += clippedItems;
                    return queryStore with { Queries = queryItems, ArtifactDirectory = queryArtifacts };
                case SqlHarnessIndexesReport indexesReport:
                    var candidates = Take(indexesReport.Candidates).Select(candidate => candidate with
                    {
                        Schema = Clip(candidate.Schema)!,
                        Table = Clip(candidate.Table)!,
                        EqualityColumns = Take(candidate.EqualityColumns).Select(value => Clip(value)!).ToArray(),
                        InequalityColumns = Take(candidate.InequalityColumns).Select(value => Clip(value)!).ToArray(),
                        IncludeColumns = Take(candidate.IncludeColumns).Select(value => Clip(value)!).ToArray(),
                        BestExistingIndex = Clip(candidate.BestExistingIndex),
                        MissingIncludeColumns = Take(candidate.MissingIncludeColumns).Select(value => Clip(value)!).ToArray(),
                    }).ToArray();
                    var indexWarnings = Take(indexesReport.Warnings).Select(value => Clip(value)!).ToArray();
                    var indexArtifact = Clip(indexesReport.ArtifactDirectory);
                    var objectFilter = Clip(indexesReport.ObjectFilter);
                    omissions += clippedItems;
                    return indexesReport with { Candidates = candidates, Warnings = indexWarnings, ArtifactDirectory = indexArtifact, ObjectFilter = objectFilter };
                case ArtifactMetricsSection metrics:
                    {
                        var variants = Take(metrics.Variants).Select(variant => variant with
                        {
                            Name = Clip(variant.Name)!,
                            LogicalReadsByTable = Take(BySignificance(variant.LogicalReadsByTable).ToArray()).ToDictionary(
                                pair => Clip(pair.Key)!, pair => pair.Value, StringComparer.Ordinal),
                            Warnings = Take(variant.Warnings).Select(warning => Clip(warning)!).ToArray(),
                        }).ToArray();
                        omissions += clippedItems;
                        return metrics with { Variants = variants };
                    }
                case ArtifactOperatorsSection operators:
                    {
                        var selected = Take(operators.Operators).Select(op => op with
                        {
                            PhysicalOp = Clip(op.PhysicalOp)!,
                            Object = Clip(op.Object),
                        }).ToArray();
                        omissions += clippedItems;
                        return operators with { Operators = selected };
                    }
                case ArtifactStatementsSection statements:
                    {
                        var selected = Take(statements.Statements).Select(statement => statement with
                        {
                            Variant = Clip(statement.Variant)!,
                            ParameterSet = Clip(statement.ParameterSet),
                            TopOperators = Take(statement.TopOperators).Select(op => op with
                            {
                                PhysicalOp = Clip(op.PhysicalOp)!,
                                Object = Clip(op.Object),
                                Index = Clip(op.Index),
                            }).ToArray(),
                        }).ToArray();
                        omissions += clippedItems;
                        return statements with
                        {
                            Statements = selected,
                            OmittedStatements = statements.OmittedStatements + Math.Max(0, statements.Statements.Count - selected.Length),
                            Continuation = selected.Length < statements.Statements.Count
                                ? statements.Cursor + selected.Length
                                : null,
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

        BenchmarkVariantSummary ClipVariant(BenchmarkVariantSummary variant)
        {
            var reads = Take(BySignificance(variant.LogicalReadsByTable).ToArray()).ToDictionary(p => Clip(p.Key)!, p => p.Value, StringComparer.Ordinal);
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
            rowless[ordinal] = new AgentCompactResultSet(ordinal, set.Columns.Select(CompactColumn).ToArray(), [], set.RowCount, set.OmittedRowCount + set.Rows.Count, 0);
        }

        long reserve = PreFilterBudget(maximumBytes) - SerializedLength(QueryShell(query, rowless, 0, messages));
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
            };
        }

        omittedItems = omitted;
        return QueryShell(query, sets, 0, messages);
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
        int maximumCellCharacters,
        int maximumBytes,
        out int omittedItems)
    {
        long remaining = PreFilterBudget(maximumBytes) - SerializedLength(QueryShell(query, [], query.ResultSets.Count, messages));

        // 1. Inventory for a prefix of the result sets.
        var kept = 0;
        foreach (var set in query.ResultSets)
        {
            var inventory = new AgentCompactResultSet(kept, [], [], set.RowCount, set.OmittedRowCount + set.Rows.Count, set.Columns.Count);
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
                retainedClippedCells);
        }

        omittedItems = omitted;
        return QueryShell(query, sets, source.Count - kept, messages);
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

    /// <summary>One third of the outer budget minus a fixed margin: MCP carries two representations plus metadata.</summary>
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
        IReadOnlyList<string?> messages) =>
        new(query.Target.ActualDatabase, query.StatementClassification, sets, omittedResultSets,
            messages.Count == 0 ? null : messages,
            query.RecordsAffected < 0 ? null : query.RecordsAffected,
            query.DurationMilliseconds);

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
                return node with { PhysicalOp = clip(node.PhysicalOp)!, LogicalOp = clip(node.LogicalOp), ObjectName = clip(node.ObjectName), IndexName = clip(node.IndexName), Predicate = clip(node.Predicate), Warnings = [], Children = [] };
            }
            nodesRemaining--;
            if (depth >= 6)
            {
                omitted += node.Children.Count;
                omitted += node.Warnings.Count;
                return node with { PhysicalOp = clip(node.PhysicalOp)!, LogicalOp = clip(node.LogicalOp), ObjectName = clip(node.ObjectName), IndexName = clip(node.IndexName), Predicate = clip(node.Predicate), Warnings = [], Children = [] };
            }
            omitted += Math.Max(0, node.Warnings.Count - detailLimit);
            var children = takeNodes(node.Children).Select(child => ProjectNode(child, depth + 1)).ToArray();
            return node with { PhysicalOp = clip(node.PhysicalOp)!, LogicalOp = clip(node.LogicalOp), ObjectName = clip(node.ObjectName), IndexName = clip(node.IndexName), Predicate = clip(node.Predicate), Warnings = node.Warnings.Take(detailLimit).Select(value => clip(value)!).ToArray(), Children = children };
        }

    }

    private static string? SafeArtifactId(string? directory)
    {
        var id = Path.GetFileName(directory);
        if (string.IsNullOrEmpty(id) || id.Length > 128 || !char.IsAsciiLetterOrDigit(id[0]))
            return null;
        return id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? id
            : null;
    }
}
