using System.Globalization;

namespace SqlHarness.Core;

internal enum WatchOperator
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

internal sealed record WatchCondition(string Column, WatchOperator Operator, string Operand, bool IsQuoted)
{
    private const string InvalidPredicateMessage = "Watch condition predicate is invalid.";
    private const string MissingResultSetMessage = "Watch condition requires a result set.";
    private const string MissingColumnMessage = "Watch condition column was not found.";
    private const string DuplicateColumnMessage = "Watch condition column matches more than once.";
    private const string NonNumericCompareMessage = "Watch condition comparison requires numeric values.";

    // Longer operators first so "<=" is not parsed as "<".
    private static readonly (string Token, WatchOperator Operator)[] Operators =
    [
        ("<=", WatchOperator.LessOrEqual),
        (">=", WatchOperator.GreaterOrEqual),
        ("!=", WatchOperator.NotEqual),
        ("=", WatchOperator.Equal),
        ("<", WatchOperator.Less),
        (">", WatchOperator.Greater),
    ];

    internal static WatchCondition Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SqlHarnessSafetyException(InvalidPredicateMessage);

        var span = text.AsSpan().Trim();
        var bestIndex = -1;
        var bestLength = 0;
        WatchOperator? bestOperator = null;

        // Leftmost operator wins; at the same index longer tokens win (<= before <).
        foreach (var (token, op) in Operators)
        {
            var index = span.IndexOf(token, StringComparison.Ordinal);
            if (index < 0)
                continue;
            if (bestIndex >= 0 && (index > bestIndex || (index == bestIndex && token.Length <= bestLength)))
                continue;

            bestIndex = index;
            bestLength = token.Length;
            bestOperator = op;
        }

        if (bestOperator is null || bestIndex < 0)
            throw new SqlHarnessSafetyException(InvalidPredicateMessage);

        var column = span[..bestIndex].Trim().ToString();
        var operand = span[(bestIndex + bestLength)..].Trim().ToString();
        if (column.Length == 0 || operand.Length == 0)
            throw new SqlHarnessSafetyException(InvalidPredicateMessage);

        var isQuoted = operand.Length >= 2
            && operand[0] is '\'' or '"'
            && operand[^1] == operand[0];
        if (isQuoted)
        {
            var quote = operand[0];
            operand = operand[1..^1].Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal);
        }
        else if (string.Equals(operand, "true", StringComparison.OrdinalIgnoreCase))
        {
            operand = "1";
        }
        else if (string.Equals(operand, "false", StringComparison.OrdinalIgnoreCase))
        {
            operand = "0";
        }

        return new WatchCondition(column, bestOperator.Value, operand, isQuoted);
    }

    internal bool IsMet(SqlHarnessResultSetReport firstResultSet)
    {
        if (firstResultSet is null)
            throw new SqlHarnessSafetyException(MissingResultSetMessage);

        var matchCount = 0;
        var ordinal = -1;
        for (var i = 0; i < firstResultSet.Columns.Count; i++)
        {
            if (!string.Equals(firstResultSet.Columns[i].Name, Column, StringComparison.OrdinalIgnoreCase))
                continue;
            matchCount++;
            ordinal = i;
        }

        if (matchCount == 0)
            throw new SqlHarnessSafetyException(MissingColumnMessage);
        if (matchCount > 1)
            throw new SqlHarnessSafetyException(DuplicateColumnMessage);

        if (firstResultSet.Rows.Count == 0)
            return false;

        var row = firstResultSet.Rows[0];
        if (ordinal < 0 || ordinal >= row.Count)
            throw new SqlHarnessSafetyException(MissingColumnMessage);

        var cell = row[ordinal];
        if (cell is null)
            return false;

        var leftText = FormatValue(cell);
        var rightText = Operand;

        if (IsQuoted)
            return CompareText(leftText, rightText, Operator);

        // Values outside decimal's range fail parsing and fall back to text comparison.
        var leftNumeric = decimal.TryParse(leftText, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = decimal.TryParse(rightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber);

        if (leftNumeric && rightNumeric)
            return CompareNumeric(leftNumber, rightNumber, Operator);

        return CompareText(leftText, rightText, Operator);
    }

    private static bool CompareNumeric(decimal left, decimal right, WatchOperator op) =>
        op switch
        {
            WatchOperator.Equal => left == right,
            WatchOperator.NotEqual => left != right,
            WatchOperator.Less => left < right,
            WatchOperator.LessOrEqual => left <= right,
            WatchOperator.Greater => left > right,
            WatchOperator.GreaterOrEqual => left >= right,
            _ => throw new SqlHarnessSafetyException(InvalidPredicateMessage),
        };

    private static bool CompareText(string left, string right, WatchOperator op) =>
        op switch
        {
            WatchOperator.Equal => string.Equals(left, right, StringComparison.Ordinal),
            WatchOperator.NotEqual => !string.Equals(left, right, StringComparison.Ordinal),
            _ => throw new SqlHarnessSafetyException(NonNumericCompareMessage),
        };

    private static string FormatValue(object value)
    {
        if (value is bool boolean)
            return boolean ? "1" : "0";
        if (value is string text)
            return text;
        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

internal sealed class WatchUnchangedTracker(int requiredUnchangedPolls)
{
    private string? _lastHash;
    private int _consecutiveUnchanged;

    internal bool Observe(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (_lastHash is null)
        {
            _lastHash = hash;
            _consecutiveUnchanged = 0;
            return false;
        }

        if (!string.Equals(_lastHash, hash, StringComparison.Ordinal))
        {
            _lastHash = hash;
            _consecutiveUnchanged = 0;
            return false;
        }

        _consecutiveUnchanged++;
        return _consecutiveUnchanged >= requiredUnchangedPolls;
    }
}