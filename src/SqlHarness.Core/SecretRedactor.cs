using System.Text.RegularExpressions;

namespace SqlHarness.Core;

public static partial class SecretRedactor
{
    public static string Redact(Exception exception, IReadOnlyList<string> knownSecrets)
    {
        var messages = new List<string>();
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;

            messages.Add(current.Message);
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
        var safe = value;
        foreach (var secret in knownSecrets.Where(secret => !string.IsNullOrEmpty(secret)))
            safe = safe.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        safe = AccessTokenPattern().Replace(safe, "$1[REDACTED]$3");
        safe = ConnectionSecretPattern().Replace(safe, "$1[REDACTED]");
        safe = JwtPattern().Replace(safe, "[REDACTED]");
        safe = TokenLikePattern().Replace(safe, "[REDACTED]");
        return safe;
    }

    [GeneratedRegex("(?i)([\\\"']?(?:access_?token)[\\\"']?\\s*[:=]\\s*[\\\"']?)([^\\\"';&,}\\s]+)([\\\"']?)", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenPattern();

    [GeneratedRegex("(?i)((?:password|pwd|user\\s*id|uid|data\\s*source|server|initial\\s*catalog|database)\\s*=\\s*)(?:\"(?:\"\"|[^\"])*\"|'(?:''|[^'])*'|[^;\\r\\n]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionSecretPattern();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]+(?:\\.[A-Za-z0-9_-]+){1,2}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex("\\b[A-Za-z0-9_-]*access-token[A-Za-z0-9_-]*\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenLikePattern();
}
