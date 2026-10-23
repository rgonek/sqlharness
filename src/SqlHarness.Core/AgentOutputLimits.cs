namespace SqlHarness.Core;

/// <summary>Shared bounds for agent response cell projection.</summary>
public static class AgentOutputLimits
{
    public const int DefaultCellCharacters = 2048;
    public const int MaximumCellCharacters = 4096;
    public const string CellCharactersDescription = "0..4096 chars, default 2048; redacted Message/Hint=max(limit,2048); path=limit; byte budget applies.";

    /// <summary>Applies the default diagnostic floor to a caller's cell limit.</summary>
    public static int ErrorCellCharacters(int requestedCellCharacters) =>
        Math.Max(requestedCellCharacters, DefaultCellCharacters);

    /// <summary>Clips error text by UTF-16 characters without splitting a surrogate pair.</summary>
    public static string PrefixErrorText(string value, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value);
        var length = Math.Min(value.Length, maximumCharacters);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }
}
