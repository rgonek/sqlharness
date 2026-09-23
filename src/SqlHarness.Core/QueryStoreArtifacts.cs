using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

internal interface IQueryStoreArtifactWriter
{
    string Write(
        SqlHarnessQueryStoreTopReport report,
        IReadOnlyList<SensitiveQueryStoreText> texts,
        string target);
}

internal sealed partial class QueryStoreArtifactWriter : IQueryStoreArtifactWriter
{
    private const string TextMismatch = "Query Store artifact texts do not match the report queries.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string, string, Encoding> _writeText;
    private readonly Action<string, string> _moveDirectory;
    private readonly Action<string> _deleteFile;
    private readonly Action<string, bool> _deleteDirectory;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _writeText = writeText ?? File.WriteAllText;
        _moveDirectory = moveDirectory ?? ((source, destination) => Directory.Move(source, destination));
        _deleteFile = deleteFile ?? File.Delete;
        _deleteDirectory = deleteDirectory ?? Directory.Delete;
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

        var safeTarget = UnsafePathCharacter().Replace(target, "-").Trim('-');
        if (string.IsNullOrEmpty(safeTarget))
            safeTarget = "target";

        Directory.CreateDirectory(_root);
        string directory;
        do
        {
            directory = Path.Combine(_root, $"{_utcNow():yyyyMMddTHHmmssfffZ}-{safeTarget}-{Guid.NewGuid():N}");
        }
        while (Directory.Exists(directory));

        var staging = directory + ".staging-" + Guid.NewGuid().ToString("N");
        var persisted = report with { ArtifactDirectory = directory };
        try
        {
            Directory.CreateDirectory(staging);
            _writeText(
                Path.Combine(staging, "report.json"),
                JsonSerializer.Serialize(persisted, JsonOptions),
                new UTF8Encoding(false));
            var lines = new StringBuilder();
            foreach (var text in texts)
            {
                lines.AppendLine(JsonSerializer.Serialize(
                    new QueryTextArtifact(text.QueryId, text.QueryHash, text.QuerySqlText),
                    JsonLineOptions));
            }

            _writeText(
                Path.Combine(staging, "queries.jsonl"),
                lines.ToString(),
                new UTF8Encoding(false));
            _moveDirectory(staging, directory);
        }
        catch
        {
            try
            {
                Cleanup(staging);
                Cleanup(directory);
            }
            catch
            {
                // Keep the original publish failure even when cleanup throws.
            }

            throw;
        }

        return directory;
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

    private void Cleanup(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        string[] files;
        try { files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories); }
        catch { files = []; }
        foreach (var file in files)
            Try(() => _deleteFile(file));

        string[] directories;
        try { directories = Directory.GetDirectories(directory, "*", SearchOption.AllDirectories); }
        catch { directories = []; }
        foreach (var child in directories.OrderByDescending(path => path.Length))
            Try(() => _deleteDirectory(child, false));
        Try(() => _deleteDirectory(directory, false));
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch { }
    }

    private sealed record QueryTextArtifact(long QueryId, string QueryHash, string QuerySqlText);

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafePathCharacter();
}