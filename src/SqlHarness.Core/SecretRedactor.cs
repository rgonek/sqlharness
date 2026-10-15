using System.Text.RegularExpressions;

using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

public static class SecretRedactor
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

    public static string Redact(string value, IReadOnlyList<string> knownSecrets) =>
        For(knownSecrets).Apply(value);

    /// <summary>
    /// Builds a redactor for one set of known secrets. Compiling the pattern is by far the
    /// most expensive part of redaction, so callers that redact many strings under the same
    /// secrets (a result set's messages, one error's fields) build this once and reuse it.
    /// </summary>
    public static SecretRedaction For(IReadOnlyList<string> knownSecrets)
    {
        ArgumentNullException.ThrowIfNull(knownSecrets);
        var secrets = knownSecrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            // Longest first so a secret that contains another is replaced whole, not in pieces.
            .OrderByDescending(secret => secret.Length)
            .ThenBy(secret => secret, StringComparer.Ordinal)
            .ToArray();
        return new SecretRedaction(secrets);
    }
}

/// <summary>A set of known secrets compiled once and applied to as many strings as needed.</summary>
public sealed class SecretRedaction
{
    private const string Placeholder = "[REDACTED]";

    private readonly Regex? _pattern;

    internal SecretRedaction(string[] secrets) =>
        // NonBacktracking keeps the pattern linear and avoids the compiled-regex probe.
        _pattern = secrets.Length == 0
            ? null
            : new Regex(
                string.Join("|", secrets.Select(Regex.Escape)),
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string Apply(string value) => _pattern is null ? value : _pattern.Replace(value, Placeholder);
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