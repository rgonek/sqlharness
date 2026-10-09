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

    public const int TopMin = 1;
    public const int TopMax = 500;

    public const int MaxRowsMin = 0;
    public const int MaxRowsMax = 500;

    public const int RepeatMin = 1;
    public const int RepeatMax = 100;

    public const int QueryStoreWindowMinutesMin = 1;
    public const int QueryStoreWindowMinutesMax = 44640;
    public const long QueryStoreWindowMinuteFactor = 1;
    public const long QueryStoreWindowHourFactor = 60;
    public const long QueryStoreWindowDayFactor = 1440;

    public static readonly TimeSpan WatchDurationMax = TimeSpan.FromHours(24);

    public static bool IsQueryTimeoutSeconds(int value) =>
        value is >= QueryTimeoutSecondsMin and <= QueryTimeoutSecondsMax;

    public static bool IsTop(int value) =>
        value is >= TopMin and <= TopMax;

    public static bool IsMaxRows(int value) =>
        value is >= MaxRowsMin and <= MaxRowsMax;

    public static bool IsRepeat(int value) =>
        value is >= RepeatMin and <= RepeatMax;

    public static bool IsQueryStoreWindowMinutes(long value) =>
        value is >= QueryStoreWindowMinutesMin and <= QueryStoreWindowMinutesMax;

    public static bool IsWatchDuration(TimeSpan value) =>
        value > TimeSpan.Zero && value <= WatchDurationMax;

    /// <summary>
    /// Pure Query Store window conversion shared by both adapters: a positive
    /// magnitude with an already-normalized lowercase m/h/d unit becomes total
    /// minutes within 1..44640. Trimming, case folding, and suffix validation
    /// stay in the adapters; unknown units, non-positive magnitudes,
    /// overflowed products, and out-of-range totals fail without a message.
    /// </summary>
    public static bool TryConvertQueryStoreWindow(long magnitude, char unit, out int minutes)
    {
        minutes = 0;
        var factor = unit switch
        {
            'm' => QueryStoreWindowMinuteFactor,
            'h' => QueryStoreWindowHourFactor,
            'd' => QueryStoreWindowDayFactor,
            _ => 0L,
        };
        if (factor == 0 || magnitude <= 0)
            return false;

        long total;
        try
        {
            total = checked(magnitude * factor);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (!IsQueryStoreWindowMinutes(total))
            return false;

        minutes = (int)total;
        return true;
    }

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