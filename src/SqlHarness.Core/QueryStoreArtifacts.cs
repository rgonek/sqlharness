using System.Text;
using System.Text.Json;

namespace SqlHarness.Core;

internal interface IQueryStoreArtifactWriter
{
    string Write(
        SqlHarnessQueryStoreTopReport report,
        IReadOnlyList<SensitiveQueryStoreText> texts,
        string target);
}

internal sealed class QueryStoreArtifactWriter : IQueryStoreArtifactWriter
{
    private const string TextMismatch = "Query Store artifact texts do not match the report queries.";

    private readonly ArtifactDirectoryPublisher _publisher;

    internal QueryStoreArtifactWriter()
        : this(SqlHarnessPaths.QueryStoreDir, () => DateTimeOffset.UtcNow)
    {
    }

    internal QueryStoreArtifactWriter(
        string root,
        Func<DateTimeOffset> utcNow,
        Action<string, string, Encoding>? writeText = null,
        Action<string, string>? moveDirectory = null,
        Action<string>? deleteFile = null,
        Action<string, bool>? deleteDirectory = null)
    {
        _publisher = new ArtifactDirectoryPublisher(
            root, utcNow, writeText, moveDirectory, null, deleteFile, deleteDirectory);
    }

    public string Write(
        SqlHarnessQueryStoreTopReport report,
        IReadOnlyList<SensitiveQueryStoreText> texts,
        string target)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(target);
        RequireMatchingTexts(report.Queries, texts);

        return _publisher.Publish(target, (staging, directory) =>
        {
            var persisted = report with { ArtifactDirectory = directory };
            _publisher.WriteText(
                Path.Combine(staging, "report.json"),
                JsonSerializer.Serialize(persisted, ArtifactDirectoryPublisher.JsonOptions),
                new UTF8Encoding(false));
            var lines = new StringBuilder();
            foreach (var text in texts)
            {
                lines.AppendLine(JsonSerializer.Serialize(
                    new QueryTextArtifact(text.QueryId, text.QueryHash, text.QuerySqlText),
                    ArtifactDirectoryPublisher.JsonLineOptions));
            }

            _publisher.WriteText(
                Path.Combine(staging, "queries.jsonl"),
                lines.ToString(),
                new UTF8Encoding(false));
        });
    }

    private static void RequireMatchingTexts(
        IReadOnlyList<QueryStoreTopItemReport> queries,
        IReadOnlyList<SensitiveQueryStoreText> texts)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var metricIds = new HashSet<long>();
        foreach (var query in queries)
        {
            if (!metricIds.Add(query.QueryId))
                throw new InvalidOperationException(TextMismatch);
        }

        var byId = new Dictionary<long, string>(texts.Count);
        foreach (var text in texts)
        {
            if (!byId.TryAdd(text.QueryId, text.QueryHash))
                throw new InvalidOperationException(TextMismatch);
        }

        if (byId.Count != queries.Count)
            throw new InvalidOperationException(TextMismatch);

        foreach (var query in queries)
        {
            if (!byId.TryGetValue(query.QueryId, out var hash)
                || !string.Equals(hash, query.QueryHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(TextMismatch);
            }
        }
    }

    private sealed record QueryTextArtifact(long QueryId, string QueryHash, string QuerySqlText);

}