using System.Text.Json;

namespace SqlHarness.Core;

public sealed record AgentBoundedOutput(string ReportType, int OmittedItems);
public sealed record AgentBinaryValue(long ByteLength, string Base64Prefix, bool Truncated);

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

    /// <summary>
    /// Candidate detail limits for the agent projection degradation loop.
    /// The first candidate is <see cref="int.MaxValue"/>, which projects the
    /// report without truncation. If that does not fit the byte budget, the
    /// loop descends through conservative estimates down to 1, then 0.
    /// </summary>
    public static IEnumerable<int> GetCandidateDetailLimits(int maximumBytes, int maximumCellCharacters)
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
        string? Clip(string? value)
        {
            if (value is null || value.Length <= maximumCellCharacters) return value;
            clippedItems++;
            var length = maximumCellCharacters;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value[..length];
        }
        object? ProjectCell(object? value) => value switch
        {
            string text => Clip(text),
            byte[] bytes => ProjectBinary(bytes),
            _ => value,
        };
        object ProjectBinary(byte[] bytes)
        {
            var fullBase64Length = ((long)bytes.Length + 2) / 3 * 4;
            if (fullBase64Length <= maximumCellCharacters)
                return Convert.ToBase64String(bytes);
            clippedItems++;
            var prefixLength = Math.Min(bytes.Length, maximumCellCharacters / 4 * 3);
            var prefix = Convert.ToBase64String(bytes.AsSpan(0, prefixLength));
            return new AgentBinaryValue(bytes.LongLength, prefix, Truncated: true);
        }
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
                    var sets = ProjectResultSets(query.ResultSets);
                    var messages = Take(query.Messages).Select(Clip).ToArray();
                    omissions += clippedItems;
                    return new { target = query.Target, statementClassification = query.StatementClassification, resultSets = sets, messages, query.RecordsAffected, query.DurationMilliseconds, query.ResultHash, query.RawFootprint };
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
                case SqlHarnessGainReport gain:
                    return gain;
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