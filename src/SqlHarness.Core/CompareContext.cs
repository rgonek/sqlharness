namespace SqlHarness.Core;

public sealed record CompareContext(string Subject, string Scope)
{
    public static CompareContext? Parse(string? subject, string? scope, bool required)
    {
        if (subject is null && scope is null)
        {
            if (!required)
                return null;
            throw new ArgumentException("Both --subject and --scope are required.");
        }

        if (subject is null || scope is null)
            throw new ArgumentException("Provide both --subject and --scope, or omit both.");

        if (!IsValidLabel(subject) || !IsValidLabel(scope))
            throw new ArgumentException("Compare context labels must be 1..64 lowercase ASCII letters, digits, '.', '_' or '-', starting with a lowercase letter or digit.");

        return new CompareContext(subject!, scope!);
    }

    private static bool IsValidLabel(string? value)
    {
        if (value is null || value.Length is < 1 or > 64 || !IsInitial(value[0]))
            return false;
        return value.Skip(1).All(character => IsInitial(character) || character is '.' or '_' or '-');
    }

    private static bool IsInitial(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
}