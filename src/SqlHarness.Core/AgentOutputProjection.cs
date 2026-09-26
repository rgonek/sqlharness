namespace SqlHarness.Core;

/// <summary>Presentation-only bounds for the opt-in agent response.</summary>
public sealed record AgentOutputOptions(int MaximumBytes = 16 * 1024, int MaximumCellCharacters = 512);

/// <summary>Creates bounded, lower-priority-detail projections without changing source reports.</summary>
public static class AgentOutputProjection
{
    public static object? Project(object? report, int maximumCellCharacters, int detailLimit, out int omittedItems)
    {
        omittedItems = 0;
        var clippedItems = 0;
        string? Clip(string? value)
        {
            if (value is null || value.Length <= maximumCellCharacters) return value;
            clippedItems++;
            var length = maximumCellCharacters;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value[..length];
        }
        switch (report)
        {
            case SqlHarnessQueryReport query:
                var sets = query.ResultSets.Take(detailLimit).Select(set => new
                {
                    columns = set.Columns.Take(detailLimit).ToArray(),
                    rows = set.Rows.Take(detailLimit).Select(row => row.Select(value => value is string text ? Clip(text) : value).ToArray()).ToArray(),
                    rowCount = set.RowCount,
                    omittedRowCount = set.OmittedRowCount + Math.Max(0, set.Rows.Count - detailLimit),
                }).ToArray();
                var messages = query.Messages.Take(detailLimit).Select(Clip).ToArray();
                omittedItems += Math.Max(0, query.ResultSets.Count - sets.Length);
                omittedItems += query.ResultSets.Take(detailLimit).Sum(set => Math.Max(0, set.Columns.Count - detailLimit));
                omittedItems += query.ResultSets.Sum(set => Math.Max(0, set.Rows.Count - detailLimit));
                omittedItems += clippedItems;
                return new { target = query.Target, statementClassification = query.StatementClassification, resultSets = sets, messages, query.RecordsAffected, query.DurationMilliseconds, query.ResultHash, query.RawFootprint };
            case SqlHarnessCountsReport counts:
                var tables = counts.Tables.Take(detailLimit).ToArray();
                omittedItems += Math.Max(0, counts.Tables.Count - tables.Length);
                omittedItems += clippedItems;
                return new { counts.Target, tables, omitted = counts.Omitted + omittedItems };
            case SqlHarnessSchemaReport schema:
                var objects = schema.Objects.Take(detailLimit).Select(value => new
                {
                    value.Schema, value.Name, value.Kind,
                    columns = value.Columns.Take(detailLimit).ToArray(),
                    indexes = value.Indexes.Take(detailLimit).Select(i => new { i.Name, i.Unique, keys = i.Keys.Take(detailLimit), includes = i.Includes.Take(detailLimit), filter = Clip(i.Filter) }).ToArray(),
                    foreignKeys = value.ForeignKeys.Take(detailLimit).ToArray(),
                }).ToArray();
                omittedItems += Math.Max(0, schema.Objects.Count - objects.Length);
                omittedItems += schema.Objects.Take(detailLimit).Sum(value => Math.Max(0, value.Columns.Count - detailLimit) + Math.Max(0, value.Indexes.Count - detailLimit) + Math.Max(0, value.ForeignKeys.Count - detailLimit));
                omittedItems += clippedItems;
                return new { schema.Target, objects, omittedObjects = schema.OmittedObjects + omittedItems };
            case SqlHarnessMeasureSetReport setReport:
                var selectedSets = setReport.Sets.Take(detailLimit).Select(set => new MeasureParameterSetSummary(
                    Clip(set.Name)!, set.Parameters, set.ValueHash, set.ResultsStable,
                    set.Metrics.ElapsedTimeMilliseconds.Median, set.Metrics.CpuTimeMilliseconds.Median,
                    set.Metrics.LogicalReads.Median,
                    set.Metrics.Operators.Where(op => op.HasWarnings || op.HasSpill || op.HasImplicitConversion)
                        .OrderByDescending(op => op.HasWarnings || op.HasSpill || op.HasImplicitConversion)
                        .Take(10).Select(op => new NoteworthyOperatorSummary("query", op.NodeId, Clip(op.PhysicalOp)!, Clip(op.Object), op.HasWarnings, op.HasSpill, op.HasImplicitConversion)).ToArray())
                {
                    MetricReport = set.Metrics.MetricReport,
                }).ToArray();
                omittedItems += Math.Max(0, setReport.Sets.Count - selectedSets.Length);
                var planCacheWarning = Clip(setReport.PlanCacheWarning)!;
                var artifactDirectory = Clip(setReport.ArtifactDirectory);
                omittedItems += clippedItems;
                return new MeasureSetBenchmarkSummary(setReport.Target, setReport.MeasuredOrderRule, planCacheWarning, selectedSets, setReport.CrossSetSummary, artifactDirectory);
            case SqlHarnessCompareMatrixReport matrix:
                var nestedOmitted = 0;
                var cells = matrix.Cells.Take(detailLimit).Select(cell =>
                {
                    var projected = Project(cell.Compare, maximumCellCharacters, detailLimit, out var cellOmitted);
                    nestedOmitted += cellOmitted;
                    return new CompareMatrixCellSummary(cell.Index, Clip(cell.ParameterValue)!, (CompareBenchmarkSummary)projected!);
                }).ToArray();
                omittedItems += Math.Max(0, matrix.Cells.Count - cells.Length);
                omittedItems += nestedOmitted;
                var parameterName = Clip(matrix.ParameterName)!;
                var parameterType = Clip(matrix.ParameterType)!;
                omittedItems += clippedItems;
                return new CompareMatrixBenchmarkSummary(parameterName, parameterType, cells);
            case SqlHarnessCompareReport compare:
                return Project(BenchmarkSummaryProjector.Project(compare), maximumCellCharacters, detailLimit, out omittedItems);
            case SqlHarnessMeasureReport measure:
                return Project(BenchmarkSummaryProjector.Project(measure), maximumCellCharacters, detailLimit, out omittedItems);
            case CompareBenchmarkSummary summary:
                var compareProjection = summary with
                {
                    Baseline = ClipVariant(summary.Baseline), Candidate = ClipVariant(summary.Candidate),
                    NoteworthyOperators = summary.NoteworthyOperators.Take(10).Select(op => op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) }).ToArray(),
                    ArtifactDirectory = Clip(summary.ArtifactDirectory),
                };
                omittedItems += clippedItems;
                return compareProjection;
            case MeasureBenchmarkSummary summary:
                var measureProjection = summary with
                {
                    Query = ClipVariant(summary.Query),
                    NoteworthyOperators = summary.NoteworthyOperators.Take(10).Select(op => op with { PhysicalOp = Clip(op.PhysicalOp)!, Object = Clip(op.Object) }).ToArray(),
                    ArtifactDirectory = Clip(summary.ArtifactDirectory),
                };
                omittedItems += clippedItems;
                return measureProjection;
            default:
                return report;
        }

        BenchmarkVariantSummary ClipVariant(BenchmarkVariantSummary variant) => variant with
        {
            LogicalReadsByTable = variant.LogicalReadsByTable.Take(detailLimit).ToDictionary(p => Clip(p.Key)!, p => p.Value, StringComparer.Ordinal),
            Warnings = variant.Warnings.Take(detailLimit).Select(w => Clip(w)!).ToArray(),
        };
    }
}
