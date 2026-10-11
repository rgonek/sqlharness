using System.Text.Json;

namespace SqlHarness.Core;

public sealed record AgentBoundedOutput(string ReportType, int OmittedItems);
public sealed record AgentBinaryValue(long ByteLength, string Base64Prefix, bool Truncated);

/// <summary>
/// One query result set in the agent projection. <c>Ordinal</c> is the
/// zero-based position in the source report (the snapshot-diff convention).
/// Every emitted row has exactly <c>Columns.Count</c> cells. Rows not emitted
/// are counted in <c>OmittedRowCount</c>; columns dropped by the detail-limit-0
/// fallback (always the trailing ones) are counted in <c>OmittedColumnCount</c>,
/// and a set with omitted columns carries no rows.
/// </summary>
public sealed record AgentQueryResultSet(
    int Ordinal,
    IReadOnlyList<SqlHarnessColumnReport> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    long RowCount,
    long OmittedRowCount,
    int OmittedColumnCount);

/// <summary>
/// Agent projection of a query report. <c>ResultSets</c> is a prefix of the
/// source result sets; <c>OmittedResultSetCount</c> counts the trailing sets
/// that did not fit.
/// </summary>
public sealed record AgentQueryProjection(
    SqlHarnessTargetIdentityReport Target,
    string StatementClassification,
    IReadOnlyList<AgentQueryResultSet> ResultSets,
    int OmittedResultSetCount,
    IReadOnlyList<string?> Messages,
    int RecordsAffected,
    long DurationMilliseconds,
    string ResultHash,
    OutputFootprint RawFootprint);

/// <summary>Presentation-only bounds for the opt-in agent response.</summary>
public sealed record AgentOutputOptions(int MaximumBytes = 16 * 1024, int MaximumCellCharacters = 512);

/// <summary>Creates bounded, lower-priority-detail projections without changing source reports.</summary>
public static class AgentOutputProjection
{
    private static readonly JsonSerializerOptions ProjectionJson = new(JsonSerializerDefaults.Web);

    public static int CalculateDetailLimit(int maximumBytes, int maximumCellCharacters)
    {
        var perItemEstimate = Math.Max(128L, (long)Math.Max(1, maximumCellCharacters) * 16);
        perItemEstimate = Math.Max(perItemEstimate, 128L);
        var estimate = Math.Max(1d, maximumBytes / (double)perItemEstimate);
        return Math.Clamp((int)Math.Pow(estimate, 0.2), 1, 128);
    }

    private static readonly int[] QueryRowSampleLadder = [128, 64, 32, 16, 8, 4, 2, 1, 0];

    /// <summary>
    /// Candidate detail limits for the agent projection degradation loop.
    /// The first candidate is <see cref="int.MaxValue"/>, which projects the
    /// report without truncation. If that does not fit the byte budget, the
    /// loop descends through conservative estimates down to 1, then 0.
    /// A query report instead descends through bounded per-result-set row
    /// samples (128, 64, 32, 16, 8, 4, 2, 1) and ends with the budget-aware
    /// inventory, schema and row allocation at 0.
    /// </summary>
    public static IEnumerable<int> GetCandidateDetailLimits(object? report, int maximumBytes, int maximumCellCharacters) =>
        report is SqlHarnessQueryReport
            ? QueryCandidateDetailLimits()
            : DefaultCandidateDetailLimits(maximumBytes, maximumCellCharacters);

    private static IEnumerable<int> QueryCandidateDetailLimits()
    {
        yield return int.MaxValue;
        foreach (var level in QueryRowSampleLadder)
            yield return level;
    }

    private static IEnumerable<int> DefaultCandidateDetailLimits(int maximumBytes, int maximumCellCharacters)
    {
        yield return int.MaxValue;
        var level = CalculateDetailLimit(maximumBytes, maximumCellCharacters);
        while (level > 0)
        {
            yield return level;
            if (level == 1)
                break;
            level = Math.Max(1, level / 2);
        }

        yield return 0;
    }

    public static object? Project(object? report, int maximumCellCharacters, int detailLimit, out int omittedItems, int maximumBytes = 16 * 1024)
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
                    var nestedOmitted = 0;
                    var cells = Take(matrix.Cells).Select(cell =>
                    {
                        var projected = Project(cell.Compare, maximumCellCharacters, detailLimit, out var cellOmitted, maximumBytes);
                        nestedOmitted += cellOmitted;
                        var compareSummary = (CompareBenchmarkSummary)projected!;
                        return new CompareMatrixCellSummary(cell.Index, null,
                            compareSummary with { ArtifactDirectory = SafeArtifactId(cell.Compare.ArtifactDirectory) });
                    }).ToArray();
                    omissions += nestedOmitted;
                    var parameterName = Clip(matrix.ParameterName)!;
                    var parameterType = Clip(matrix.ParameterType)!;
                    var omittedCellReferences = BuildOmittedCellReferences(
                        matrix.Cells, cells, parameterName, parameterType, maximumBytes, out var referenceOmissions, out var continuation);
                    omissions += referenceOmissions;
                    omissions += clippedItems;
                    var matrixArtifactId = matrix.Cells.Count == 0
                        ? null
                        : SafeArtifactId(matrix.Cells[0].Compare.ArtifactDirectory);
                    return new CompareMatrixBenchmarkSummary(parameterName, parameterType, cells, omittedCellReferences, continuation, matrixArtifactId);
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
                            LogicalReadsByTable = Take(variant.LogicalReadsByTable.ToArray()).ToDictionary(
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
                        };
                    }
                case ArtifactMatrixCellsSection matrixCells:
                    {
                        var selected = Take(matrixCells.Cells);
                        omissions += clippedItems;
                        return matrixCells with
                        {
                            Cells = selected,
                            Continuation = selected.Count < matrixCells.Cells.Count
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
            var reads = Take(variant.LogicalReadsByTable.ToArray()).ToDictionary(p => Clip(p.Key)!, p => p.Value, StringComparer.Ordinal);
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
    private static AgentQueryProjection ProjectQueryRowSample(
        SqlHarnessQueryReport query,
        IReadOnlyList<string?> messages,
        int rowLimit,
        int maximumCellCharacters,
        int maximumBytes,
        out int omittedItems)
    {
        var count = query.ResultSets.Count;
        var caches = query.ResultSets.Select(set => new QueryRowCache(set, maximumCellCharacters)).ToArray();
        var rowless = new AgentQueryResultSet[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var set = query.ResultSets[ordinal];
            rowless[ordinal] = new AgentQueryResultSet(ordinal, set.Columns.ToArray(), [], set.RowCount, set.OmittedRowCount + set.Rows.Count, 0);
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
        var sets = new AgentQueryResultSet[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var take = whole[ordinal] ? caches[ordinal].Count : Math.Min(caches[ordinal].Count, rowLimit);
            var rows = caches[ordinal].Prefix(take, out var clipped);
            var setOmitted = caches[ordinal].Count - take;
            omitted += setOmitted + clipped;
            sets[ordinal] = rowless[ordinal] with
            {
                Rows = rows,
                OmittedRowCount = query.ResultSets[ordinal].OmittedRowCount + setOmitted,
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
    private static AgentQueryProjection ProjectQueryInventory(
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
            var inventory = new AgentQueryResultSet(kept, [], [], set.RowCount, set.OmittedRowCount + set.Rows.Count, set.Columns.Count);
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
        var sets = new AgentQueryResultSet[kept];
        for (var ordinal = 0; ordinal < kept; ordinal++)
        {
            var set = source[ordinal];
            var clipped = 0;
            IReadOnlyList<IReadOnlyList<object?>> rows = caches[ordinal] is { } cache
                ? cache.Prefix(rowCounts[ordinal], out clipped)
                : [];
            var omittedColumns = set.Columns.Count - columnCounts[ordinal];
            var omittedRows = set.Rows.Count - rows.Count;
            omitted += omittedColumns + omittedRows + clipped;
            sets[ordinal] = new AgentQueryResultSet(
                ordinal,
                set.Columns.Take(columnCounts[ordinal]).ToArray(),
                rows,
                set.RowCount,
                set.OmittedRowCount + omittedRows,
                omittedColumns);
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
        private readonly List<(IReadOnlyList<object?> Cells, int Clipped, int Cost)> rows = [];

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

        public IReadOnlyList<IReadOnlyList<object?>> Prefix(int length, out int clipped)
        {
            var selected = new IReadOnlyList<object?>[length];
            clipped = 0;
            for (var index = 0; index < length; index++)
            {
                var row = Get(index);
                selected[index] = row.Cells;
                clipped += row.Clipped;
            }

            return selected;
        }

        private (IReadOnlyList<object?> Cells, int Clipped, int Cost) Get(int index)
        {
            while (rows.Count <= index)
            {
                var source = set.Rows[rows.Count];
                var clipped = 0;
                var cells = new object?[source.Count];
                for (var cell = 0; cell < cells.Length; cell++)
                    cells[cell] = ProjectCellValue(source[cell], maximumCellCharacters, ref clipped);
                rows.Add((cells, clipped, SerializedLength(cells) + 1));
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
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
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

    private static AgentQueryProjection QueryShell(
        SqlHarnessQueryReport query,
        IReadOnlyList<AgentQueryResultSet> sets,
        int omittedResultSets,
        IReadOnlyList<string?> messages) =>
        new(query.Target, query.StatementClassification, sets, omittedResultSets, messages,
            query.RecordsAffected, query.DurationMilliseconds, query.ResultHash, query.RawFootprint);

    private static int SerializedLength<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, ProjectionJson).Length;

    private static IReadOnlyList<CompareMatrixCellReference>? BuildOmittedCellReferences(
        IReadOnlyList<CompareMatrixCellReport> allCells,
        IReadOnlyList<CompareMatrixCellSummary> selectedCells,
        string parameterName,
        string parameterType,
        int maximumBytes,
        out int omittedReferences,
        out int? continuation)
    {
        omittedReferences = 0;
        continuation = null;
        if (selectedCells.Count >= allCells.Count)
            return null;

        var selectedIndexes = selectedCells.Select(cell => cell.Index).ToHashSet();
        var references = allCells
            .Where(cell => !selectedIndexes.Contains(cell.Index))
            .Select(cell => new CompareMatrixCellReference(cell.Index, null, SafeArtifactId(cell.Compare.ArtifactDirectory)))
            .ToArray();

        // Keep the reference list itself inside the byte budget. The full envelope is
        // measured by the caller, so this is a conservative pre-filter that lets the
        // degradation loop land on a references-only projection (detailLimit=0) when
        // full cells do not fit. If references still do not fit, page them with an
        // explicit continuation marker so the caller never assumes completeness.
        // MCP serializes both text and structured content plus SDK metadata.
        // Reserve one third of the outer wire budget for this summary estimate.
        var availableForSummary = Math.Max(0, maximumBytes / 3 - 128);
        var emptySummary = new CompareMatrixBenchmarkSummary(parameterName, parameterType, selectedCells);
        var emptyBytes = JsonSerializer.SerializeToUtf8Bytes(emptySummary, ProjectionJson).Length;
        var remaining = Math.Max(0, availableForSummary - emptyBytes);
        if (references.Length == 0)
            return references;

        if (remaining <= 0)
        {
            omittedReferences = references.Length;
            continuation = selectedCells.Count;
            return [];
        }

        var sampleBytes = JsonSerializer.SerializeToUtf8Bytes(references[0], ProjectionJson).Length;
        var estimatedPerReference = sampleBytes + 8;
        var maxReferences = Math.Max(0, remaining / Math.Max(estimatedPerReference, 1));
        if (references.Length <= maxReferences)
            return references;

        omittedReferences = references.Length - maxReferences;
        continuation = selectedCells.Count + maxReferences;
        return references.Take(maxReferences).ToArray();
    }

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