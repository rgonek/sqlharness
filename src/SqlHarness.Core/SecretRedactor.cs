using System.Text.RegularExpressions;

using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

public static partial class SecretRedactor
{
    public static string Redact(Exception exception, IReadOnlyList<string> knownSecrets)
    {
        if (exception is SqlHarnessSafetyException parameterError && parameterError.IsParameterValue)
            return RedactPreserving(parameterError.Message, knownSecrets, parameterError.PreservedTokens());

        var messages = new List<string>();
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;

            messages.Add(current.Message);
            // Validation failures keep diagnostics off the user text, even if an inner exception still carries the value.
            if (current is SqlHarnessSafetyException)
                continue;
            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                    pending.Push(aggregate.InnerExceptions[index]);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
        return Redact(string.Join(" | ", messages), knownSecrets);
    }

    public static string Redact(string value, IReadOnlyList<string> knownSecrets)
    {
        var secrets = knownSecrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)
            .ThenBy(secret => secret, StringComparer.Ordinal)
            .ToArray();
        var safe = secrets.Length == 0
            ? value
            : new Regex(
                string.Join("|", secrets.Select(Regex.Escape)),
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
                .Replace(value, "[REDACTED]");
        safe = SecretFieldPattern().Replace(safe, "$1[REDACTED]$3");
        safe = PasswordPattern().Replace(safe, "$1[REDACTED]");
        safe = JwtPattern().Replace(safe, "[REDACTED]");
        return safe;
    }

    private static string RedactPreserving(string value, IReadOnlyList<string> knownSecrets, IReadOnlyList<string> preserved)
    {
        var working = value;
        var restores = new List<(string Token, string Original)>();
        foreach (var token in preserved
            .Where(token => !string.IsNullOrEmpty(token))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(token => token.Length))
        {
            if (!working.Contains(token, StringComparison.Ordinal))
                continue;
            var placeholder = PrivatePlaceholder(working, knownSecrets, restores.Count);
            working = working.Replace(token, placeholder, StringComparison.Ordinal);
            restores.Add((placeholder, token));
        }

        working = Redact(working, knownSecrets);
        foreach (var (placeholder, original) in restores)
            working = working.Replace(placeholder, original, StringComparison.Ordinal);
        return working;
    }

    // Digits would let a value of "0" eat the placeholder before the name or type is restored.
    private static string PrivatePlaceholder(string working, IReadOnlyList<string> knownSecrets, int salt)
    {
        var index = salt;
        while (true)
        {
            var placeholder = $"\uE000{(char)(0xE010 + index)}\uE001";
            var conflict = working.Contains(placeholder, StringComparison.Ordinal)
                || knownSecrets.Any(secret => secret?.Contains(placeholder, StringComparison.Ordinal) == true);
            if (!conflict)
                return placeholder;
            index++;
        }
    }

    // Only the value of an explicit key/value field is masked; a field name or descriptive word alone is not a credential.
    [GeneratedRegex("""(?i)(["']?(?:access[_\s-]?token|client[_\s-]?secret|account[_\s-]?key|shared[_\s-]?access[_\s-]?key)["']?\s*[:=]\s*["']?)([^"';&,}\s]+)(["']?)""", RegexOptions.CultureInvariant)]
    private static partial Regex SecretFieldPattern();

    // Server, database and user fields are target diagnostics and stay readable.
    [GeneratedRegex("""(?i)((?:password|pwd)\s*=\s*)(?:"(?:""|[^"])*"|'(?:''|[^'])*'|[^;\r\n]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    [GeneratedRegex("""\beyJ[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+){1,2}\b""", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();
}

/// <summary>Registers operator-held credentials for error redaction once a target is resolved.</summary>
internal static class OperationSecrets
{
    internal static void AddCredentials(List<string> knownSecrets, ResolvedTarget target)
    {
        if (target.Auth.PasswordForRedaction() is { Length: > 0 } password)
            knownSecrets.Add(password);
    }
}