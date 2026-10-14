namespace SqlHarness.Core;

/// <summary>
/// Shared pure numeric bounds and duration rules for operation arguments.
/// The CLI and MCP adapters keep their own transport formats (case policy,
/// whitespace, suffix sets, defaults) and error text, and delegate only the
/// identical conversion-and-range core here. Capabilities reports the same
/// constants. Core execution re-validates these bounds independently at run
/// time; this type never throws transport errors.
/// </summary>
public static class OperationLimits
{
    public const int QueryTimeoutSecondsMin = 1;
    public const int QueryTimeoutSecondsMax = 300;

    public const int MaxRowsMin = 0;
    public const int MaxRowsMax = 500;

    public const int RepeatMin = 1;
    public const int RepeatMax = 100;

    public static readonly TimeSpan WatchDurationMax = TimeSpan.FromHours(24);

    public static bool IsQueryTimeoutSeconds(int value) =>
        value is >= QueryTimeoutSecondsMin and <= QueryTimeoutSecondsMax;

    public static bool IsMaxRows(int value) =>
        value is >= MaxRowsMin and <= MaxRowsMax;

    public static bool IsRepeat(int value) =>
        value is >= RepeatMin and <= RepeatMax;

    public static bool IsWatchDuration(TimeSpan value) =>
        value > TimeSpan.Zero && value <= WatchDurationMax;

    /// <summary>
    /// Pure watch duration conversion shared by both adapters: a positive
    /// integral value with an already-normalized unit (null or 's' = seconds,
    /// 'm' = minutes, 'h' = hours) becomes a TimeSpan within (0, 24h].
    /// Format validation and error text stay in the adapters; unknown units,
    /// non-positive values, overflowed spans, and totals above 24 hours fail
    /// without a message.
    /// </summary>
    public static bool TryCreateWatchDuration(long value, char? unit, out TimeSpan duration)
    {
        duration = default;
        if (value <= 0 || unit is not (null or 's' or 'm' or 'h'))
            return false;

        try
        {
            duration = unit switch
            {
                null or 's' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMinutes(value),
                _ => TimeSpan.FromHours(value),
            };
        }
        catch (OverflowException)
        {
            return false;
        }
        // .NET 10's long TimeSpan.FromSeconds / FromMinutes / FromHours overloads throw ArgumentOutOfRangeException instead of OverflowException.
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (!IsWatchDuration(duration))
        {
            duration = default;
            return false;
        }

        return true;
    }
}