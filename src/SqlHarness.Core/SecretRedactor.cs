using System.Text.RegularExpressions;

using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

public static partial class SecretRedactor
{
    public static string Redact(Exception exception, IReadOnlyList<string> knownSecrets)
    {
        if (PostgresReadOnlyFailure.ContainsReadOnlyViolation(exception))
            return PostgresReadOnlyFailure.RejectionMessage;

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
        safe = PasswordPattern().Replace(safe, "$1[REDACTED]");
        safe = SecretFieldPattern().Replace(safe, MaskFieldValue);
        safe = JwtPattern().Replace(safe, "[REDACTED]");
        return safe;
    }

    // Only the value of an explicit key/value field is masked; a field name or descriptive word alone is not a credential.
    // Group 1 is the key and separator; group 2 is the whole value: escaped-JSON, double- or single-quoted (with doubled quotes), or bare.
    // A quote that is never closed (a truncated diagnostic) masks the rest of the line rather than nothing.
    [GeneratedRegex("""(?i)(\\?["']?(?:password|pwd|access[_\s-]?token|client[_\s-]?secret|account[_\s-]?key|shared[_\s-]?access[_\s-]?key)\\?["']?\s*[:=]\s*)(\\"[^"\\]*\\"|"(?:\\.|""|[^"\\])*"|'(?:''|[^'])*'|\\?["'][^\r\n]*|[^"';&,}\s]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SecretFieldPattern();

    private static string MaskFieldValue(Match match)
    {
        var value = match.Groups[2].Value;
        var quote = value.StartsWith("\\\"", StringComparison.Ordinal) ? "\\\""
            : value[0] is '"' or '\'' ? value[..1]
            : string.Empty;
        var closing = value.Length > quote.Length && value.EndsWith(quote, StringComparison.Ordinal) ? quote : string.Empty;
        return match.Groups[1].Value + quote + "[REDACTED]" + closing;
    }

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