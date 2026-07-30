using System.Globalization;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public class WatchConditionTests
{
    [Theory]
    [InlineData("Count >= 10", 10, true)]
    [InlineData("Count < 10", 10, false)]
    [InlineData("Status = Completed", "Completed", true)]
    [InlineData("Status != Failed", "Completed", true)]
    public void Predicate_evaluates_first_row_column(
        string text, object value, bool expected)
    {
        var condition = WatchCondition.Parse(text);
        Assert.Equal(expected, condition.IsMet(Result("Count", value, alternateName: "Status")));
    }

    [Theory]
    [InlineData("Count <= 10", 10, true)]
    [InlineData("Count <= 10", 11, false)]
    [InlineData("Count < 10", 9, true)]
    [InlineData("Count > 10", 11, true)]
    [InlineData("Count > 10", 10, false)]
    [InlineData("Count >= 10", 9, false)]
    [InlineData("Count != 10", 11, true)]
    [InlineData("Count = 10", 10, true)]
    public void Numeric_operators_compare_invariant_decimals(string text, object value, bool expected)
    {
        var condition = WatchCondition.Parse(text);
        Assert.Equal(expected, condition.IsMet(SingleColumn("Count", value)));
    }

    [Fact]
    public void Parses_longer_operators_before_shorter_ones()
    {
        var lessOrEqual = WatchCondition.Parse("N<=5");
        Assert.Equal(WatchOperator.LessOrEqual, lessOrEqual.Operator);
        Assert.Equal("N", lessOrEqual.Column);
        Assert.Equal("5", lessOrEqual.Operand);

        var greaterOrEqual = WatchCondition.Parse("N>=5");
        Assert.Equal(WatchOperator.GreaterOrEqual, greaterOrEqual.Operator);

        var notEqual = WatchCondition.Parse("N!=5");
        Assert.Equal(WatchOperator.NotEqual, notEqual.Operator);

        var less = WatchCondition.Parse("N<5");
        Assert.Equal(WatchOperator.Less, less.Operator);
    }

    [Fact]
    public void Numeric_comparison_uses_invariant_culture_for_both_sides()
    {
        using var _ = new TemporaryCulture("pl-PL");

        var condition = WatchCondition.Parse("Amount >= 1234.56");
        Assert.True(condition.IsMet(SingleColumn("Amount", 1234.56m)));
        Assert.True(condition.IsMet(SingleColumn("Amount", "1234.56")));
        Assert.False(condition.IsMet(SingleColumn("Amount", 1234.55m)));
    }

    [Theory]
    [InlineData("Status = completed", "Completed", false)]
    [InlineData("Status = Completed", "Completed", true)]
    [InlineData("Status != Completed", "Failed", true)]
    [InlineData("Status != Completed", "Completed", false)]
    public void Text_equality_is_ordinal_case_sensitive(string text, object value, bool expected)
    {
        var condition = WatchCondition.Parse(text);
        Assert.Equal(expected, condition.IsMet(SingleColumn("Status", value)));
    }

    [Fact]
    public void Column_match_is_case_insensitive_and_requires_exactly_one()
    {
        var condition = WatchCondition.Parse("count >= 1");
        Assert.True(condition.IsMet(SingleColumn("Count", 1)));
    }

    [Fact]
    public void Rejects_missing_result_set()
    {
        var condition = WatchCondition.Parse("Count >= 1");
        Assert.Throws<SqlHarnessSafetyException>(() => { _ = condition.IsMet(null!); });
    }

    [Fact]
    public void Rejects_missing_first_row()
    {
        var condition = WatchCondition.Parse("Count >= 1");
        var empty = new SqlHarnessResultSetReport(
            [new SqlHarnessColumnReport(0, "Count", "System.Int32", false)],
            [],
            0,
            0);

        Assert.Throws<SqlHarnessSafetyException>(() => { _ = condition.IsMet(empty); });
    }

    [Fact]
    public void Rejects_missing_column()
    {
        var condition = WatchCondition.Parse("Missing >= 1");
        Assert.Throws<SqlHarnessSafetyException>(() => { _ = condition.IsMet(SingleColumn("Count", 1)); });
    }

    [Fact]
    public void Rejects_duplicate_column_names_case_insensitive()
    {
        var condition = WatchCondition.Parse("Count >= 1");
        var duplicate = new SqlHarnessResultSetReport(
            [
                new SqlHarnessColumnReport(0, "Count", "System.Int32", false),
                new SqlHarnessColumnReport(1, "count", "System.Int32", false),
            ],
            [[1, 2]],
            1,
            0);

        Assert.Throws<SqlHarnessSafetyException>(() => { _ = condition.IsMet(duplicate); });
    }

    [Fact]
    public void Rejects_null_condition_value()
    {
        var condition = WatchCondition.Parse("Count >= 1");
        Assert.Throws<SqlHarnessSafetyException>(() => { _ = condition.IsMet(SingleColumn("Count", null)); });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Count")]
    [InlineData(">= 10")]
    [InlineData("Count >=")]
    [InlineData("Count ~~ 10")]
    [InlineData("Count =")]
    public void Rejects_invalid_predicate_syntax(string text)
    {
        Assert.Throws<SqlHarnessSafetyException>(() => WatchCondition.Parse(text));
    }

    [Fact]
    public void Inequality_on_non_numeric_text_is_rejected()
    {
        var condition = WatchCondition.Parse("Status < Completed");
        Assert.Throws<SqlHarnessSafetyException>(() => { _ = condition.IsMet(SingleColumn("Status", "Active")); });
    }

    [Fact]
    public void Mixed_numeric_and_text_falls_back_to_text_equality_only()
    {
        var equal = WatchCondition.Parse("Code = 10x");
        Assert.True(equal.IsMet(SingleColumn("Code", "10x")));
        Assert.False(equal.IsMet(SingleColumn("Code", "10")));

        var notEqual = WatchCondition.Parse("Code != 10x");
        Assert.True(notEqual.IsMet(SingleColumn("Code", "10")));

        var less = WatchCondition.Parse("Code < 10x");
        Assert.Throws<SqlHarnessSafetyException>(() => { _ = less.IsMet(SingleColumn("Code", "9")); });
    }

    [Fact]
    public void Unchanged_tracker_stops_after_three_consecutive_unchanged_polls()
    {
        var tracker = new WatchUnchangedTracker(3);
        Assert.False(tracker.Observe("A"));
        Assert.False(tracker.Observe("A"));
        Assert.False(tracker.Observe("A"));
        Assert.True(tracker.Observe("A"));
    }

    [Fact]
    public void Unchanged_tracker_resets_after_a_changed_hash()
    {
        var tracker = new WatchUnchangedTracker(3);
        Assert.False(tracker.Observe("A"));
        Assert.False(tracker.Observe("A"));
        Assert.False(tracker.Observe("B"));
        Assert.False(tracker.Observe("B"));
        Assert.False(tracker.Observe("B"));
        Assert.True(tracker.Observe("B"));
    }

    [Fact]
    public void Unchanged_tracker_required_one_needs_one_poll_after_baseline()
    {
        var tracker = new WatchUnchangedTracker(1);
        Assert.False(tracker.Observe("A"));
        Assert.True(tracker.Observe("A"));
    }

    private static SqlHarnessResultSetReport Result(string name, object? value, string? alternateName = null)
    {
        if (alternateName is null)
            return SingleColumn(name, value);

        return new SqlHarnessResultSetReport(
            [
                new SqlHarnessColumnReport(0, name, TypeName(value), value is null),
                new SqlHarnessColumnReport(1, alternateName, TypeName(value), value is null),
            ],
            [[value, value]],
            1,
            0);
    }

    private static SqlHarnessResultSetReport SingleColumn(string name, object? value) =>
        new(
            [new SqlHarnessColumnReport(0, name, TypeName(value), value is null)],
            [[value]],
            1,
            0);

    private static string TypeName(object? value) =>
        value?.GetType().FullName ?? typeof(object).FullName!;

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo _previous;
        private readonly CultureInfo _previousUi;

        public TemporaryCulture(string name)
        {
            _previous = CultureInfo.CurrentCulture;
            _previousUi = CultureInfo.CurrentUICulture;
            var culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previous;
            CultureInfo.CurrentUICulture = _previousUi;
        }
    }
}