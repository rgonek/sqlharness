using SqlHarness.Cli.Commands;
using SqlHarness.Core;

namespace SqlHarness.Tests;

/// <summary>
/// 010/T3 characteristic pins: the numeric bounds and duration rules the CLI
/// and MCP adapters must share. These cases use only long-standing adapter
/// behavior (public window parser, capabilities limits) so they hold before
/// and after the OperationLimits extraction; the refactor must keep every
/// value, unit policy, and transport message identical.
/// </summary>
public sealed class OperationLimitsTests
{
    [Theory]
    [InlineData("1m", 1)]
    [InlineData("24h", 1440)]
    [InlineData("31d", 44640)]
    [InlineData("44640m", 44640)]
    public void Qstop_window_parses_minutes_within_one_to_44640(string window, int minutes) =>
        Assert.Equal(minutes, QueryStoreWindowParser.Parse(window));

    [Theory]
    [InlineData("0m")]
    [InlineData("44641m")]
    [InlineData("32d")]
    [InlineData("24")]
    [InlineData("1w")]
    public void Qstop_window_rejects_malformed_or_out_of_range(string window) =>
        Assert.Throws<ArgumentException>(() => QueryStoreWindowParser.Parse(window));

    [Theory]
    [InlineData("24H")]
    [InlineData("7D")]
    public void Qstop_window_units_stay_lowercase_only_on_cli(string window) =>
        // Deliberate transport difference: the MCP mapper folds case, the CLI
        // parser does not. The shared core takes an already-normalized unit.
        Assert.Throws<ArgumentException>(() => QueryStoreWindowParser.Parse(window));

    [Theory]
    [InlineData("queryTimeoutSeconds", 1, 300)]
    [InlineData("queryMaxRows", 0, 500)]
    [InlineData("repeat", 1, 100)]
    [InlineData("qstopTop", 1, 500)]
    [InlineData("qstopWindowMinutes", 1, 44640)]
    [InlineData("indexesTop", 1, 500)]
    public void Capabilities_limits_match_documented_operation_bounds(string name, int min, int max)
    {
        var (actualMin, actualMax) = LimitRange(name);
        Assert.Equal(min, actualMin);
        Assert.Equal(max, actualMax);
    }

    private static (int Min, int Max) LimitRange(string name)
    {
        var entry = SqlHarnessCapabilitiesProvider.Get().Limits[name];
        var type = entry.GetType();
        return (
            (int)type.GetProperty("min")!.GetValue(entry)!,
            (int)type.GetProperty("max")!.GetValue(entry)!);
    }

    [Fact]
    public void OperationLimits_pin_the_documented_values()
    {
        Assert.Equal(1, OperationLimits.QueryTimeoutSecondsMin);
        Assert.Equal(300, OperationLimits.QueryTimeoutSecondsMax);
        Assert.Equal(1, OperationLimits.TopMin);
        Assert.Equal(500, OperationLimits.TopMax);
        Assert.Equal(0, OperationLimits.MaxRowsMin);
        Assert.Equal(500, OperationLimits.MaxRowsMax);
        Assert.Equal(1, OperationLimits.RepeatMin);
        Assert.Equal(100, OperationLimits.RepeatMax);
        Assert.Equal(1, OperationLimits.QueryStoreWindowMinutesMin);
        Assert.Equal(44640, OperationLimits.QueryStoreWindowMinutesMax);
        Assert.Equal(1, OperationLimits.QueryStoreWindowMinuteFactor);
        Assert.Equal(60, OperationLimits.QueryStoreWindowHourFactor);
        Assert.Equal(1440, OperationLimits.QueryStoreWindowDayFactor);
        Assert.Equal(TimeSpan.FromHours(24), OperationLimits.WatchDurationMax);
    }

    [Theory]
    [InlineData("queryTimeoutSeconds", 1, 300)]
    [InlineData("queryMaxRows", 0, 500)]
    [InlineData("repeat", 1, 100)]
    [InlineData("qstopTop", 1, 500)]
    [InlineData("qstopWindowMinutes", 1, 44640)]
    [InlineData("indexesTop", 1, 500)]
    public void Capabilities_limits_follow_the_shared_constants(string name, int min, int max)
    {
        var (actualMin, actualMax) = LimitRange(name);
        var (expectedMin, expectedMax) = name switch
        {
            "queryTimeoutSeconds" => (OperationLimits.QueryTimeoutSecondsMin, OperationLimits.QueryTimeoutSecondsMax),
            "queryMaxRows" => (OperationLimits.MaxRowsMin, OperationLimits.MaxRowsMax),
            "repeat" => (OperationLimits.RepeatMin, OperationLimits.RepeatMax),
            "qstopTop" or "indexesTop" => (OperationLimits.TopMin, OperationLimits.TopMax),
            _ => (OperationLimits.QueryStoreWindowMinutesMin, OperationLimits.QueryStoreWindowMinutesMax),
        };
        Assert.Equal(expectedMin, actualMin);
        Assert.Equal(expectedMax, actualMax);
        Assert.Equal(min, actualMin);
        Assert.Equal(max, actualMax);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(300, true)]
    [InlineData(0, false)]
    [InlineData(301, false)]
    public void QueryTimeout_predicate_enforces_one_to_three_hundred(int value, bool expected) =>
        Assert.Equal(expected, OperationLimits.IsQueryTimeoutSeconds(value));

    [Theory]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(0, false)]
    [InlineData(501, false)]
    public void Top_predicate_enforces_one_to_five_hundred(int value, bool expected) =>
        Assert.Equal(expected, OperationLimits.IsTop(value));

    [Theory]
    [InlineData(0, true)]
    [InlineData(500, true)]
    [InlineData(-1, false)]
    [InlineData(501, false)]
    public void MaxRows_predicate_enforces_zero_to_five_hundred(int value, bool expected) =>
        Assert.Equal(expected, OperationLimits.IsMaxRows(value));

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    public void Repeat_predicate_enforces_one_to_one_hundred(int value, bool expected) =>
        Assert.Equal(expected, OperationLimits.IsRepeat(value));

    [Theory]
    [InlineData(1L, 'm', 1)]
    [InlineData(24L, 'h', 1440)]
    [InlineData(31L, 'd', 44640)]
    [InlineData(44640L, 'm', 44640)]
    public void QueryStoreWindow_conversion_matches_cli_parser(long magnitude, char unit, int minutes)
    {
        Assert.True(OperationLimits.TryConvertQueryStoreWindow(magnitude, unit, out var converted));
        Assert.Equal(minutes, converted);
    }

    [Theory]
    [InlineData(0L, 'm')]
    [InlineData(-1L, 'h')]
    [InlineData(44641L, 'm')]
    [InlineData(32L, 'd')]
    [InlineData(1L, 'H')]
    [InlineData(1L, 'w')]
    [InlineData(long.MaxValue, 'd')]
    [InlineData(71582789L, 'h')]
    public void QueryStoreWindow_conversion_rejects_non_positive_unknown_overflowed_or_out_of_range(
        long magnitude, char unit) =>
        Assert.False(OperationLimits.TryConvertQueryStoreWindow(magnitude, unit, out _));

    [Theory]
    [InlineData("1m", 1L, 'm', 1)]
    [InlineData("24h", 24L, 'h', 1440)]
    [InlineData("31d", 31L, 'd', 44640)]
    public void Cli_window_parser_agrees_with_core_on_shared_inputs(
        string window, long magnitude, char unit, int minutes)
    {
        Assert.True(OperationLimits.TryConvertQueryStoreWindow(magnitude, unit, out var converted));
        Assert.Equal(minutes, converted);
        Assert.Equal(minutes, QueryStoreWindowParser.Parse(window));
    }

    [Theory]
    [InlineData(10L, null, 10)]
    [InlineData(5L, 's', 5)]
    [InlineData(90L, 'm', 5400)]
    [InlineData(24L, 'h', 86400)]
    public void WatchDuration_conversion_applies_suffix_with_24h_cap(long value, char? unit, long seconds)
    {
        Assert.True(OperationLimits.TryCreateWatchDuration(value, unit, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
        Assert.True(OperationLimits.IsWatchDuration(duration));
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(0L, 's')]
    [InlineData(-1L, 'h')]
    [InlineData(25L, 'h')]
    [InlineData(1441L, 'm')]
    [InlineData(1L, 'd')]
    [InlineData(1L, 'M')]
    [InlineData(long.MaxValue, 's')]
    public void WatchDuration_conversion_rejects_non_positive_unknown_overflowed_or_over_cap(
        long value, char? unit) =>
        Assert.False(OperationLimits.TryCreateWatchDuration(value, unit, out _));
}
