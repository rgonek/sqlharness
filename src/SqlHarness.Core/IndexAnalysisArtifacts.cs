using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

internal interface IIndexAnalysisArtifactWriter
{
    string Write(
        SqlHarnessIndexesReport report,
        IReadOnlyList<IndexCandidate> candidates,
        IReadOnlyList<ExistingIndex> indexes,
        IReadOnlyList<SensitiveExistingIndex> sensitiveIndexes,
        string target);
}

internal sealed partial class IndexAnalysisArtifactWriter : IIndexAnalysisArtifactWriter
{
    private const string IndexMismatch = "Index analysis artifacts do not match the existing indexes.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string, string, Encoding> _writeText;
    private readonly Action<string, string> _moveDirectory;
    private readonly Action<string> _deleteFile;
    private readonly Action<string, bool> _deleteDirectory;

    internal IndexAnalysisArtifactWriter()
        : this(SqlHarnessPaths.IndexAnalysisDir, () => DateTimeOffset.UtcNow)
    {
    }

    internal IndexAnalysisArtifactWriter(
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
        SqlHarnessIndexesReport report,
        IReadOnlyList<IndexCandidate> candidates,
        IReadOnlyList<ExistingIndex> indexes,
        IReadOnlyList<SensitiveExistingIndex> sensitiveIndexes,
        string target)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(sensitiveIndexes);
        ArgumentNullException.ThrowIfNull(target);
        var filters = RequireMatchingIndexes(indexes, sensitiveIndexes);

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
            _writeText(
                Path.Combine(staging, "candidates.jsonl"),
                JsonLines(candidates),
                new UTF8Encoding(false));
            _writeText(
                Path.Combine(staging, "existing-indexes.jsonl"),
                JsonLines(indexes.Select(index => ToArtifact(index, filters))),
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

    private static Dictionary<IndexKey, string?> RequireMatchingIndexes(
        IReadOnlyList<ExistingIndex> indexes,
        IReadOnlyList<SensitiveExistingIndex> sensitiveIndexes)
    {
        var filters = new Dictionary<IndexKey, string?>(sensitiveIndexes.Count, IndexKeyComparer.Instance);
        foreach (var sensitive in sensitiveIndexes)
        {
            if (!filters.TryAdd(Key(sensitive.Schema, sensitive.Table, sensitive.IndexId), sensitive.FilterDefinition))
                throw new InvalidOperationException(IndexMismatch);
        }

        var seen = new HashSet<IndexKey>(IndexKeyComparer.Instance);
        foreach (var index in indexes)
        {
            var key = Key(index.Schema, index.Table, index.IndexId);
            if (!seen.Add(key) || !filters.ContainsKey(key))
                throw new InvalidOperationException(IndexMismatch);
        }

        if (seen.Count != filters.Count)
            throw new InvalidOperationException(IndexMismatch);

        return filters;
    }

    private static string JsonLines<T>(IEnumerable<T> rows)
    {
        var lines = new StringBuilder();
        foreach (var row in rows)
        {
            lines.AppendLine(JsonSerializer.Serialize(row, JsonLineOptions));
        }

        return lines.ToString();
    }

    private static ExistingIndexArtifact ToArtifact(ExistingIndex index, Dictionary<IndexKey, string?> filters) =>
        new(
            index.Schema,
            index.Table,
            index.IndexId,
            index.Name,
            index.Type,
            index.KeyColumns,
            index.KeyDescending,
            index.IncludeColumns,
            index.Unique,
            index.PrimaryKey,
            index.UniqueConstraint,
            index.Disabled,
            index.HasFilter,
            index.FilterHash,
            index.Compression,
            filters[Key(index.Schema, index.Table, index.IndexId)]);

    private static IndexKey Key(string schema, string table, int indexId) => new(schema, table, indexId);

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

    private readonly record struct IndexKey(string Schema, string Table, int IndexId);

    private sealed class IndexKeyComparer : IEqualityComparer<IndexKey>
    {
        public static IndexKeyComparer Instance { get; } = new();

        public bool Equals(IndexKey x, IndexKey y) =>
            x.IndexId == y.IndexId
            && StringComparer.OrdinalIgnoreCase.Equals(x.Schema, y.Schema)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table);

        public int GetHashCode(IndexKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table),
                obj.IndexId);
    }

    private sealed record ExistingIndexArtifact(
        string Schema,
        string Table,
        int IndexId,
        string Name,
        string Type,
        IReadOnlyList<string> KeyColumns,
        IReadOnlyList<bool> KeyDescending,
        IReadOnlyList<string> IncludeColumns,
        bool Unique,
        bool PrimaryKey,
        bool UniqueConstraint,
        bool Disabled,
        bool HasFilter,
        string? FilterHash,
        string Compression,
        string? FilterDefinition);

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafePathCharacter();
}