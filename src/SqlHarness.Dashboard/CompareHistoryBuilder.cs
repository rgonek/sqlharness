using System.Text.Json;

namespace SqlHarness.Dashboard;

/// <summary>Lightweight compare metadata read from retained history before versioning and paging.</summary>
public sealed record CompareHistoryRow(
    long OperationId, long SessionId, string? Subject, string? Scope, string? BaselineHash,
    string? CandidateHash, string Status, string StartedAt);

/// <summary>Compact compare history entry. Versions describe retained hash occurrences only.</summary>
public sealed record CompareAttemptSummary(
    long OperationId, string? Subject, string? Scope, string? BaselineHash, string? CandidateHash,
    string? BaselineVersion, string? CandidateVersion, string Status, string StartedAt,
    JsonElement? Regression, IReadOnlyList<JsonElement>? Cases, JsonElement? Conditions, JsonElement? Summary);

public sealed record CompareSubjectSummary(
    string Subject, IReadOnlyList<string> Scopes, CompareAttemptSummary? LatestWhole,
    CompareAttemptSummary? LatestCompletedWhole, CompareAttemptSummary? StartToLatestWhole,
    bool VersionsFromRetainedHistory = true);

/// <summary>
/// Assigns stable-in-retained-history version labels. The full input must be supplied before paging;
/// baseline is observed before candidate for each attempt and hashes are never inferred from SQL text.
/// </summary>
public static class CompareHistoryBuilder
{
    public static IReadOnlyList<CompareAttemptSummary> Build(IEnumerable<CompareHistoryRow> rows)
    {
        var source = rows.OrderBy(row => row.StartedAt, StringComparer.Ordinal).ThenBy(row => row.OperationId).ToArray();
        var versions = new Dictionary<(long Session, string Subject, string Scope), Dictionary<string, string>>();
        var next = new Dictionary<(long Session, string Subject, string Scope), int>();
        var attempts = new List<CompareAttemptSummary>(source.Length);
        foreach (var row in source)
        {
            string? baselineVersion = null;
            string? candidateVersion = null;
            if (row.Subject is not null && row.Scope is not null)
            {
                var key = (row.SessionId, row.Subject, row.Scope);
                if (!versions.TryGetValue(key, out var map))
                    versions.Add(key, map = new Dictionary<string, string>(StringComparer.Ordinal));
                baselineVersion = Version(row.BaselineHash);
                candidateVersion = Version(row.CandidateHash);

                string? Version(string? hash)
                {
                    if (hash is null)
                        return null;
                    if (!map.TryGetValue(hash, out var version))
                    {
                        next.TryGetValue(key, out var ordinal);
                        version = $"v{ordinal}";
                        map.Add(hash, version);
                        next[key] = ordinal + 1;
                    }
                    return version;
                }
            }

            attempts.Add(new CompareAttemptSummary(row.OperationId, row.Subject, row.Scope, row.BaselineHash,
                row.CandidateHash, baselineVersion, candidateVersion, row.Status, row.StartedAt, null, null, null, null));
        }
        return attempts;
    }

    public static CompareAttemptSummary WithEvidence(
        CompareAttemptSummary attempt, JsonElement? summary, JsonElement? conditions)
    {
        JsonElement? regression = null;
        IReadOnlyList<JsonElement>? cases = null;
        if (summary is { ValueKind: JsonValueKind.Object } evidence)
        {
            if (evidence.TryGetProperty("regression", out var report)) regression = report.Clone();
            if (evidence.TryGetProperty("cases", out var caseArray) && caseArray.ValueKind == JsonValueKind.Array)
                cases = caseArray.EnumerateArray().Select(item => item.Clone()).ToArray();
        }
        return attempt with { Regression = regression, Cases = cases, Conditions = conditions, Summary = summary };
    }

    public static IReadOnlyList<CompareSubjectSummary> Subjects(IReadOnlyList<CompareAttemptSummary> attempts)
    {
        return attempts.Where(attempt => attempt.Subject is not null && attempt.Scope is not null)
            .GroupBy(attempt => attempt.Subject!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var whole = group.Where(attempt => attempt.Scope == "whole")
                    .OrderBy(attempt => attempt.StartedAt, StringComparer.Ordinal).ThenBy(attempt => attempt.OperationId).ToArray();
                var completed = whole.Where(attempt => attempt.Status is "succeeded" or "failed" or "rejected" or "abandoned").LastOrDefault();
                var start = completed is { BaselineVersion: "v0" } ? completed : null;
                return new CompareSubjectSummary(group.Key,
                    group.Select(attempt => attempt.Scope!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    whole.LastOrDefault(), completed, start);
            }).ToArray();
    }
}
