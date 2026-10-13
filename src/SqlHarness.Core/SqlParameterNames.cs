using System.Text.RegularExpressions;

namespace SqlHarness.Core;

internal static partial class SqlParameterNames
{
    internal static string Normalize(string? name)
    {
        var bare = name is { Length: > 1 } && name[0] == '@' ? name[1..] : name;
        if (bare is null || !BareName().IsMatch(bare))
            throw new SqlHarnessSafetyException($"Invalid SQL parameter name '{name}'.");
        return bare;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareName();
}
