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
}
