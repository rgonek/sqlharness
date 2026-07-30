namespace SqlHarness.Core;

internal sealed record SnapshotDiffResult(
    int DifferenceCount,
    IReadOnlyList<SqlHarnessSnapshotDifference> Differences);

internal static class SnapshotDiffer
{
    private const int MaxEmittedDifferences = 100;
    private const string ColumnShapeChangedMessage = "Snapshot result column shape changed.";

    internal static SnapshotDiffResult Compare(
        SnapshotDocument baseline,
        SnapshotDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline.ResultSets);
        ArgumentNullException.ThrowIfNull(candidate.ResultSets);

        var baselineSets = baseline.ResultSets;
        var candidateSets = candidate.ResultSets;
        var sharedSetCount = Math.Min(baselineSets.Count, candidateSets.Count);

        for (var resultSet = 0; resultSet < sharedSetCount; resultSet++)
        {
            if (!ColumnsEqual(baselineSets[resultSet].Columns, candidateSets[resultSet].Columns))
                throw new SqlHarnessSafetyException(ColumnShapeChangedMessage);
        }

        var differences = new List<SqlHarnessSnapshotDifference>(MaxEmittedDifferences);
        var differenceCount = 0;

        for (var resultSet = 0; resultSet < sharedSetCount; resultSet++)
            DiffResultSet(baselineSets[resultSet], candidateSets[resultSet], resultSet, ref differenceCount, differences);

        for (var resultSet = sharedSetCount; resultSet < baselineSets.Count; resultSet++)
            Add(new SqlHarnessSnapshotDifference(resultSet, null, null, "result-set-removed"), ref differenceCount, differences);

        for (var resultSet = sharedSetCount; resultSet < candidateSets.Count; resultSet++)
            Add(new SqlHarnessSnapshotDifference(resultSet, null, null, "result-set-added"), ref differenceCount, differences);

        return new SnapshotDiffResult(differenceCount, differences);
    }

    private static void DiffResultSet(
        SnapshotResultSet baseline,
        SnapshotResultSet candidate,
        int resultSet,
        ref int differenceCount,
        List<SqlHarnessSnapshotDifference> differences)
    {
        var baselineRows = baseline.Rows ?? Array.Empty<IReadOnlyList<SnapshotScalar>>();
        var candidateRows = candidate.Rows ?? Array.Empty<IReadOnlyList<SnapshotScalar>>();
        var sharedRowCount = Math.Min(baselineRows.Count, candidateRows.Count);
        var columnCount = baseline.Columns?.Count ?? 0;

        for (var row = 0; row < sharedRowCount; row++)
        {
            var baselineRow = baselineRows[row];
            var candidateRow = candidateRows[row];
            for (var column = 0; column < columnCount; column++)
            {
                if (!CellEqual(baselineRow, candidateRow, column))
                {
                    Add(
                        new SqlHarnessSnapshotDifference(resultSet, row, column, "cell-changed"),
                        ref differenceCount,
                        differences);
                }
            }
        }

        for (var row = sharedRowCount; row < baselineRows.Count; row++)
            Add(new SqlHarnessSnapshotDifference(resultSet, row, null, "row-removed"), ref differenceCount, differences);

        for (var row = sharedRowCount; row < candidateRows.Count; row++)
            Add(new SqlHarnessSnapshotDifference(resultSet, row, null, "row-added"), ref differenceCount, differences);
    }

    private static void Add(
        SqlHarnessSnapshotDifference difference,
        ref int differenceCount,
        List<SqlHarnessSnapshotDifference> differences)
    {
        differenceCount++;
        if (differences.Count < MaxEmittedDifferences)
            differences.Add(difference);
    }

    private static bool ColumnsEqual(
        IReadOnlyList<SqlHarnessColumnReport>? baseline,
        IReadOnlyList<SqlHarnessColumnReport>? candidate)
    {
        baseline ??= Array.Empty<SqlHarnessColumnReport>();
        candidate ??= Array.Empty<SqlHarnessColumnReport>();
        if (baseline.Count != candidate.Count)
            return false;

        for (var i = 0; i < baseline.Count; i++)
        {
            var left = baseline[i];
            var right = candidate[i];
            if (left.Ordinal != right.Ordinal ||
                !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                !string.Equals(left.DataType, right.DataType, StringComparison.Ordinal) ||
                left.AllowNull != right.AllowNull)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CellEqual(
        IReadOnlyList<SnapshotScalar>? baselineRow,
        IReadOnlyList<SnapshotScalar>? candidateRow,
        int column)
    {
        var left = GetCell(baselineRow, column);
        var right = GetCell(candidateRow, column);
        if (left is null || right is null)
            return left is null && right is null;

        // JsonElement.Equals is unreliable across independently cloned values;
        // compare canonical type/null flags and raw JSON text instead.
        return string.Equals(left.Type, right.Type, StringComparison.Ordinal)
            && left.IsNull == right.IsNull
            && string.Equals(left.Value.GetRawText(), right.Value.GetRawText(), StringComparison.Ordinal);
    }

    private static SnapshotScalar? GetCell(IReadOnlyList<SnapshotScalar>? row, int column)
    {
        if (row is null || column < 0 || column >= row.Count)
            return null;
        return row[column];
    }
}