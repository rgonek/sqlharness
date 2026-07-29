using SqlHarness.Core;

namespace SqlHarness.Tests;

public class ResultEquivalenceTests
{
    [Fact]
    public void Comparison_capture_preserves_order_and_duplicates()
    {
        using var capture = new CanonicalComparisonAccumulator();
        capture.BeginResultSet([new(0, "Id", "System.Int32", false)]);
        capture.AddRow([1]);
        capture.AddRow([1]);
        capture.AddRow([2]);
        capture.EndResultSet();

        var result = capture.Complete();

        Assert.Equal(3, result.OrderedRows.Count);
        Assert.Equal(result.OrderedRows[0], result.OrderedRows[1]);
        Assert.NotEqual(result.OrderedRows[1], result.OrderedRows[2]);
    }

    [Fact]
    public void Comparison_schema_hash_reflects_column_metadata()
    {
        using var originalCapture = new CanonicalComparisonAccumulator();
        originalCapture.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        originalCapture.AddRow([1]);
        originalCapture.EndResultSet();
        var original = originalCapture.Complete();

        using var renamedCapture = new CanonicalComparisonAccumulator();
        renamedCapture.BeginResultSet([new CanonicalColumn(0, "Other", "System.Int32", false)]);
        renamedCapture.AddRow([1]);
        renamedCapture.EndResultSet();
        var renamed = renamedCapture.Complete();

        using var retypedCapture = new CanonicalComparisonAccumulator();
        retypedCapture.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int64", false)]);
        retypedCapture.AddRow([1]);
        retypedCapture.EndResultSet();
        var retyped = retypedCapture.Complete();

        using var nullableCapture = new CanonicalComparisonAccumulator();
        nullableCapture.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", true)]);
        nullableCapture.AddRow([1]);
        nullableCapture.EndResultSet();
        var nullable = nullableCapture.Complete();

        Assert.NotEqual(original.SchemaHash, renamed.SchemaHash);
        Assert.NotEqual(original.SchemaHash, retyped.SchemaHash);
        Assert.NotEqual(original.SchemaHash, nullable.SchemaHash);
        Assert.Equal(original.OrderedRows, renamed.OrderedRows);
    }

    [Fact]
    public void Comparison_row_fingerprints_include_result_set_ordinal()
    {
        using var oneSet = new CanonicalComparisonAccumulator();
        oneSet.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        oneSet.AddRow([1]);
        oneSet.AddRow([2]);
        oneSet.EndResultSet();
        var combined = oneSet.Complete();

        using var twoSets = new CanonicalComparisonAccumulator();
        twoSets.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        twoSets.AddRow([1]);
        twoSets.EndResultSet();
        twoSets.BeginResultSet([new CanonicalColumn(0, "Value", "System.Int32", false)]);
        twoSets.AddRow([2]);
        twoSets.EndResultSet();
        var split = twoSets.Complete();

        Assert.NotEqual(combined.SchemaHash, split.SchemaHash);
        Assert.Equal(2, combined.OrderedRows.Count);
        Assert.Equal(2, split.OrderedRows.Count);
        Assert.NotEqual(combined.OrderedRows[1], split.OrderedRows[1]);
        Assert.Equal(combined.OrderedRows[0], split.OrderedRows[0]);
    }

    [Fact]
    public void Comparison_capture_rejects_rows_beyond_configured_limit()
    {
        using var capture = new CanonicalComparisonAccumulator(maximumRows: 2);
        capture.BeginResultSet([new CanonicalColumn(0, "Id", "System.Int32", false)]);
        capture.AddRow([1]);
        capture.AddRow([2]);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() => capture.AddRow([3]));

        Assert.Equal(
            "Result comparison exceeds the 1000000-row limit.",
            exception.Message);
    }

    [Fact]
    public void Comparison_capture_does_not_store_messages_or_values()
    {
        using var capture = new CanonicalComparisonAccumulator();
        capture.BeginResultSet([new CanonicalColumn(0, "Secret", "System.String", false)]);
        capture.AddRow(["never-expose-me"]);
        capture.EndResultSet();

        var result = capture.Complete();

        Assert.All(result.OrderedRows, fingerprint =>
        {
            Assert.DoesNotContain("never-expose-me", fingerprint, StringComparison.Ordinal);
            Assert.Matches("^[0-9A-F]{64}$", fingerprint);
        });
        Assert.DoesNotContain("never-expose-me", result.SchemaHash, StringComparison.Ordinal);
        Assert.Matches("^[0-9A-F]{64}$", result.SchemaHash);
    }

    [Fact]
    public void Maximum_compared_rows_constant_is_one_million()
    {
        Assert.Equal(1_000_000, CanonicalComparisonAccumulator.MaximumComparedRows);
    }

    [Theory]
    [InlineData(ResultComparisonMode.Ordered, false)]
    [InlineData(ResultComparisonMode.Multiset, true)]
    [InlineData(ResultComparisonMode.Set, true)]
    public void Modes_apply_order_and_duplicate_semantics(ResultComparisonMode mode, bool expected)
    {
        var report = ResultComparer.Compare(mode, [Capture("A", "A", "B")], [Capture("B", "A", "A")]);
        Assert.Equal(expected, report.Equivalent);
    }

    [Theory]
    [InlineData(ResultComparisonMode.Multiset, false)]
    [InlineData(ResultComparisonMode.Set, true)]
    public void Modes_apply_duplicate_multiplicity_semantics(ResultComparisonMode mode, bool expected)
    {
        var report = ResultComparer.Compare(mode, [Capture("A", "A", "B")], [Capture("A", "B")]);
        Assert.Equal(expected, report.Equivalent);
    }

    [Fact]
    public void Ordered_order_only_change_reports_positions_with_zero_directional_counts()
    {
        var report = ResultComparer.Compare(
            ResultComparisonMode.Ordered,
            [Capture("A", "A", "B")],
            [Capture("B", "A", "A")]);

        Assert.False(report.Equivalent);
        Assert.True(report.DifferingPositions > 0);
        Assert.Equal(0, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
    }

    [Fact]
    public void Multiset_missing_duplicate_reports_baseline_only_count()
    {
        var report = ResultComparer.Compare(
            ResultComparisonMode.Multiset,
            [Capture("A", "A", "B")],
            [Capture("A", "B")]);

        Assert.False(report.Equivalent);
        Assert.Equal(1, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
        Assert.Null(report.DifferingPositions);
    }

    [Fact]
    public void Schema_mismatch_is_not_equivalent()
    {
        var baseline = new CanonicalComparisonResult("schema-a", ["A"]);
        var candidate = new CanonicalComparisonResult("schema-b", ["A"]);

        var ordered = ResultComparer.Compare(ResultComparisonMode.Ordered, [baseline], [candidate]);
        var multiset = ResultComparer.Compare(ResultComparisonMode.Multiset, [baseline], [candidate]);
        var set = ResultComparer.Compare(ResultComparisonMode.Set, [baseline], [candidate]);

        Assert.False(ordered.Equivalent);
        Assert.False(multiset.Equivalent);
        Assert.False(set.Equivalent);
    }

    [Fact]
    public void Multiple_repetitions_require_every_run_to_match_first_baseline()
    {
        var stable = Capture("A", "A", "B");
        var changed = Capture("A", "B", "B");

        var report = ResultComparer.Compare(
            ResultComparisonMode.Ordered,
            [stable, stable, changed],
            [stable, stable]);

        Assert.False(report.Equivalent);
        Assert.Equal(ResultComparisonMode.Ordered, report.Mode);
    }

    [Fact]
    public void Multiple_repetitions_report_maximum_observed_pair_counts()
    {
        var match = Capture("A", "A", "B");
        var reordered = Capture("B", "A", "A");
        var missingDuplicate = Capture("A", "B");

        var report = ResultComparer.Compare(
            ResultComparisonMode.Ordered,
            [match, match],
            [reordered, missingDuplicate]);

        Assert.False(report.Equivalent);
        // reordered pair: positions differ, directional 0; missing-duplicate pair: directional baseline-only 1
        Assert.True(report.DifferingPositions >= 2);
        Assert.Equal(1, report.BaselineOnlyCount);
        Assert.Equal(0, report.CandidateOnlyCount);
    }

    [Fact]
    public void Off_mode_nulls_all_result_fields_except_mode()
    {
        var report = ResultComparer.Compare(
            ResultComparisonMode.Off,
            [Capture("A")],
            [Capture("B")]);

        Assert.Equal(ResultComparisonMode.Off, report.Mode);
        Assert.Null(report.Equivalent);
        Assert.Null(report.DifferingPositions);
        Assert.Null(report.BaselineOnlyCount);
        Assert.Null(report.CandidateOnlyCount);
    }

    [Fact]
    public void Set_mode_reports_unique_directional_counts()
    {
        var report = ResultComparer.Compare(
            ResultComparisonMode.Set,
            [Capture("A", "A", "B")],
            [Capture("B", "C")]);

        Assert.False(report.Equivalent);
        Assert.Equal(1, report.BaselineOnlyCount); // unique A
        Assert.Equal(1, report.CandidateOnlyCount); // unique C
        Assert.Null(report.DifferingPositions);
    }

    private static CanonicalComparisonResult Capture(params string[] rows) =>
        new("schema", rows);
}