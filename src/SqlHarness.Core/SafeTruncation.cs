namespace SqlHarness.Core;

/// <summary>Length caps that never leave half of a UTF-16 surrogate pair behind.</summary>
public static class SafeTruncation
{
    /// <summary>
    /// Cuts <paramref name="value"/> to at most <paramref name="limit"/> UTF-16 units. When the cut
    /// would keep a lone high surrogate, the result is one unit shorter.
    /// </summary>
    public static string Cap(string value, int limit)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (value.Length <= limit)
            return value;
        if (limit > 0 && char.IsHighSurrogate(value[limit - 1]))
            limit--;
        return value[..limit];
    }
}