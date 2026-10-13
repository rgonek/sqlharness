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
    [InlineData("queryTimeoutSeconds", 1, 300)]
    [InlineData("queryMaxRows", 0, 500)]
    [InlineData("repeat", 1, 100)]
    public void Capabilities_limits_match_documented_operation_bounds(string name, int min, int max)
    {
        var (actualMin, actualMax) = LimitRange(name);
        Assert.Equal(min, actualMin);
        Assert.Equal(max, actualMax);
    }

    private static (int Min, int Max) LimitRange(string name)
    {
        var entry = SqlHarnessCapabilitiesProvider.Get(SqlEngine.SqlServer, "p").Limits[name];
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
        Assert.Equal(TimeSpan.FromHours(24), OperationLimits.WatchDurationMax);
    }

    [Theory]
    [InlineData("queryTimeoutSeconds", 1, 300)]
    [InlineData("queryMaxRows", 0, 500)]
    [InlineData("repeat", 1, 100)]
    public void Capabilities_limits_follow_the_shared_constants(string name, int min, int max)
    {
        var (actualMin, actualMax) = LimitRange(name);
        var (expectedMin, expectedMax) = name switch
        {
            "queryTimeoutSeconds" => (OperationLimits.QueryTimeoutSecondsMin, OperationLimits.QueryTimeoutSecondsMax),
            "queryMaxRows" => (OperationLimits.MaxRowsMin, OperationLimits.MaxRowsMax),
            _ => (OperationLimits.RepeatMin, OperationLimits.RepeatMax),
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