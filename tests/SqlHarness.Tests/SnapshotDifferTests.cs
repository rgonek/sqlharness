using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SnapshotDifferTests
{
    [Fact]
    public void Identical_documents_report_zero_differences()
    {
        var document = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Name", "System.String")],
            rows: [[1, "alpha"], [1, "alpha"], [2, "beta"]]);

        var diff = SnapshotDiffer.Compare(document, document);

        Assert.Equal(0, diff.DifferenceCount);
        Assert.Empty(diff.Differences);
    }

    [Fact]
    public void Changed_cell_reports_location_without_values()
    {
        var before = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Name", "System.String")],
            rows: [[1, "secret-before"]]);
        var after = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Name", "System.String")],
            rows: [[1, "secret-after"]]);

        var diff = SnapshotDiffer.Compare(before, after);

        var change = Assert.Single(diff.Differences);
        Assert.Equal(new SqlHarnessSnapshotDifference(0, 0, 1, "cell-changed"), change);
        Assert.Equal(1, diff.DifferenceCount);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(diff), StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_cell_changes_are_positional()
    {
        var before = SnapshotFixture.Document(
            columns: [("A", "System.Int32"), ("B", "System.Int32")],
            rows: [[1, 2], [3, 4]]);
        var after = SnapshotFixture.Document(
            columns: [("A", "System.Int32"), ("B", "System.Int32")],
            rows: [[9, 2], [3, 8]]);

        var diff = SnapshotDiffer.Compare(before, after);

        Assert.Equal(2, diff.DifferenceCount);
        Assert.Equal(
            [
                new SqlHarnessSnapshotDifference(0, 0, 0, "cell-changed"),
                new SqlHarnessSnapshotDifference(0, 1, 1, "cell-changed"),
            ],
            diff.Differences);
    }

    [Fact]
    public void Row_order_change_is_reported_as_cell_changes()
    {
        var before = SnapshotFixture.Document(rows: [[1], [2]]);
        var after = SnapshotFixture.Document(rows: [[2], [1]]);

        var diff = SnapshotDiffer.Compare(before, after);

        Assert.Equal(2, diff.DifferenceCount);
        Assert.Equal(
            [
                new SqlHarnessSnapshotDifference(0, 0, 0, "cell-changed"),
                new SqlHarnessSnapshotDifference(0, 1, 0, "cell-changed"),
            ],
            diff.Differences);
    }

    [Fact]
    public void Duplicate_rows_compare_positionally()
    {
        var before = SnapshotFixture.Document(rows: [[1], [1], [2]]);
        var after = SnapshotFixture.Document(rows: [[1], [2], [2]]);

        var diff = SnapshotDiffer.Compare(before, after);

        // Only the middle position differs under ordered comparison (1 vs 2).
        Assert.Equal(1, diff.DifferenceCount);
        Assert.Equal(
            new SqlHarnessSnapshotDifference(0, 1, 0, "cell-changed"),
            Assert.Single(diff.Differences));

        var withExtraDuplicate = SnapshotDiffer.Compare(
            SnapshotFixture.Document(rows: [[1], [1]]),
            SnapshotFixture.Document(rows: [[1], [1], [1]]));
        Assert.Equal(
            new SqlHarnessSnapshotDifference(0, 2, null, "row-added"),
            Assert.Single(withExtraDuplicate.Differences));
    }

    [Fact]
    public void Added_and_removed_rows_report_without_values()
    {
        var before = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Secret", "System.String")],
            rows: [[1, "keep"], [2, "gone-secret"]]);
        var after = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Secret", "System.String")],
            rows: [[1, "keep"], [3, "added-secret"]]);

        var diff = SnapshotDiffer.Compare(before, after);

        Assert.Equal(2, diff.DifferenceCount);
        Assert.Equal(
            [
                new SqlHarnessSnapshotDifference(0, 1, 0, "cell-changed"),
                new SqlHarnessSnapshotDifference(0, 1, 1, "cell-changed"),
            ],
            diff.Differences);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(diff), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tail_row_added_and_removed_use_row_kinds()
    {
        var shorter = SnapshotFixture.Document(rows: [[1]]);
        var longer = SnapshotFixture.Document(rows: [[1], [2], [3]]);

        var removed = SnapshotDiffer.Compare(longer, shorter);
        Assert.Equal(2, removed.DifferenceCount);
        Assert.Equal(
            [
                new SqlHarnessSnapshotDifference(0, 1, null, "row-removed"),
                new SqlHarnessSnapshotDifference(0, 2, null, "row-removed"),
            ],
            removed.Differences);

        var added = SnapshotDiffer.Compare(shorter, longer);
        Assert.Equal(2, added.DifferenceCount);
        Assert.Equal(
            [
                new SqlHarnessSnapshotDifference(0, 1, null, "row-added"),
                new SqlHarnessSnapshotDifference(0, 2, null, "row-added"),
            ],
            added.Differences);
    }

    [Fact]
    public void Result_set_count_changes_report_added_and_removed_sets()
    {
        var one = SnapshotFixture.Document(rows: [[1]]);
        var two = SnapshotFixture.Multi(
            SnapshotFixture.ResultSet(rows: [[1]]),
            SnapshotFixture.ResultSet(rows: [[2]]));

        var removed = SnapshotDiffer.Compare(two, one);
        Assert.Equal(1, removed.DifferenceCount);
        Assert.Equal(
            new SqlHarnessSnapshotDifference(1, null, null, "result-set-removed"),
            Assert.Single(removed.Differences));

        var added = SnapshotDiffer.Compare(one, two);
        Assert.Equal(1, added.DifferenceCount);
        Assert.Equal(
            new SqlHarnessSnapshotDifference(1, null, null, "result-set-added"),
            Assert.Single(added.Differences));
    }

    [Fact]
    public void Column_count_change_throws_safety_exception()
    {
        var before = SnapshotFixture.Document(
            columns: [("Id", "System.Int32")],
            rows: [[1]]);
        var after = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Name", "System.String")],
            rows: [[1, "x"]]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(
            () => SnapshotDiffer.Compare(before, after));

        Assert.Equal("Snapshot result column shape changed.", exception.Message);
    }

    [Fact]
    public void Column_name_change_throws_safety_exception()
    {
        var before = SnapshotFixture.Document(columns: [("Value", "System.Int32")], rows: [[1]]);
        var after = SnapshotFixture.Document(columns: [("Other", "System.Int32")], rows: [[1]]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(
            () => SnapshotDiffer.Compare(before, after));

        Assert.Equal("Snapshot result column shape changed.", exception.Message);
    }

    [Fact]
    public void Column_type_change_throws_safety_exception()
    {
        var before = SnapshotFixture.Document(columns: [("Value", "System.Int32")], rows: [[1]]);
        var after = SnapshotFixture.Document(columns: [("Value", "System.Int64")], rows: [[1L]]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(
            () => SnapshotDiffer.Compare(before, after));

        Assert.Equal("Snapshot result column shape changed.", exception.Message);
    }

    [Fact]
    public void Column_nullability_change_throws_safety_exception()
    {
        var before = SnapshotFixture.Document(
            columns: [new SnapshotColumn("Value", "System.Int32", AllowNull: true)],
            rows: [[1]]);
        var after = SnapshotFixture.Document(
            columns: [new SnapshotColumn("Value", "System.Int32", AllowNull: false)],
            rows: [[1]]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(
            () => SnapshotDiffer.Compare(before, after));

        Assert.Equal("Snapshot result column shape changed.", exception.Message);
    }

    [Fact]
    public void Emission_cap_retains_full_difference_count_without_values()
    {
        var beforeRows = Enumerable.Range(0, 120).Select(i => new object?[] { i, $"secret-before-{i}" }).ToArray();
        var afterRows = Enumerable.Range(0, 120).Select(i => new object?[] { i, $"secret-after-{i}" }).ToArray();
        var before = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Name", "System.String")],
            rows: beforeRows);
        var after = SnapshotFixture.Document(
            columns: [("Id", "System.Int32"), ("Name", "System.String")],
            rows: afterRows);

        var diff = SnapshotDiffer.Compare(before, after);

        Assert.Equal(120, diff.DifferenceCount);
        Assert.Equal(100, diff.Differences.Count);
        Assert.All(diff.Differences, d =>
        {
            Assert.Equal("cell-changed", d.Kind);
            Assert.Equal(0, d.ResultSet);
            Assert.Equal(1, d.Column);
        });
        Assert.Equal(0, diff.Differences[0].Row);
        Assert.Equal(99, diff.Differences[99].Row);
        var json = JsonSerializer.Serialize(diff);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("before-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("after-", json, StringComparison.Ordinal);
    }

    private readonly record struct SnapshotColumn(string Name, string DataType, bool AllowNull = true);

    private static class SnapshotFixture
    {
        public static SnapshotDocument Document(
            SnapshotColumn[]? columns = null,
            object?[][]? rows = null,
            DateTimeOffset? createdAt = null,
            string resultHash = "HASH")
        {
            return Create([ResultSet(columns, rows)], createdAt, resultHash);
        }

        public static SnapshotDocument Document(
            (string Name, string DataType)[] columns,
            object?[][]? rows = null,
            DateTimeOffset? createdAt = null,
            string resultHash = "HASH")
        {
            return Document(
                columns.Select(c => new SnapshotColumn(c.Name, c.DataType)).ToArray(),
                rows,
                createdAt,
                resultHash);
        }

        public static SnapshotResultSet ResultSet(
            SnapshotColumn[]? columns = null,
            object?[][]? rows = null)
        {
            columns ??= [new SnapshotColumn("Value", "System.Int32")];
            rows ??= [[1]];
            var columnReports = columns
                .Select((column, ordinal) =>
                    new SqlHarnessColumnReport(ordinal, column.Name, column.DataType, column.AllowNull))
                .ToArray();
            var scalarRows = rows
                .Select(row => (IReadOnlyList<SnapshotScalar>)row.Select(SnapshotScalar.FromValue).ToArray())
                .ToArray();
            return new SnapshotResultSet(columnReports, scalarRows, scalarRows.Length);
        }

        public static SnapshotResultSet ResultSet(
            (string Name, string DataType)[]? columns,
            object?[][]? rows = null)
        {
            return ResultSet(
                columns?.Select(c => new SnapshotColumn(c.Name, c.DataType)).ToArray(),
                rows);
        }

        public static SnapshotDocument Multi(params SnapshotResultSet[] resultSets)
            => Create(resultSets);

        private static SnapshotDocument Create(
            IReadOnlyList<SnapshotResultSet> resultSets,
            DateTimeOffset? createdAt = null,
            string resultHash = "HASH")
        {
            return new SnapshotDocument(
                Version: 1,
                CreatedAt: createdAt ?? new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
                ResultSets: resultSets.ToArray(),
                ResultHash: resultHash);
        }
    }
}