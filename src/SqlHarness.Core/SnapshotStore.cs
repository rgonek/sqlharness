using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

internal sealed record SnapshotDocument(
    int Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SnapshotResultSet> ResultSets,
    string ResultHash)
{
    internal const int CurrentVersion = 1;

    public static SnapshotDocument Create(
        DateTimeOffset createdAt,
        IReadOnlyList<SqlHarnessResultSetReport> resultSets,
        string resultHash)
    {
        ArgumentNullException.ThrowIfNull(resultSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultHash);

        return new SnapshotDocument(
            CurrentVersion,
            createdAt,
            resultSets.Select(ToSnapshotResultSet).ToArray(),
            resultHash);
    }

    private static SnapshotResultSet ToSnapshotResultSet(SqlHarnessResultSetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = report.Rows
            .Select(row => (IReadOnlyList<SnapshotScalar>)row.Select(SnapshotScalar.FromValue).ToArray())
            .ToArray();
        return new SnapshotResultSet(report.Columns, rows, report.RowCount);
    }
}

internal sealed record SnapshotResultSet(
    IReadOnlyList<SqlHarnessColumnReport> Columns,
    IReadOnlyList<IReadOnlyList<SnapshotScalar>> Rows,
    long RowCount);

internal sealed record SnapshotScalar(string Type, bool IsNull, JsonElement Value)
{
    public static SnapshotScalar FromValue(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            CanonicalScalarCodec.Write(writer, CanonicalScalarCodec.Prepare(value));

        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        return new SnapshotScalar(
            root.GetProperty("type").GetString()
                ?? throw new InvalidOperationException("Canonical scalar is missing type."),
            root.GetProperty("isNull").GetBoolean(),
            root.GetProperty("value").Clone());
    }
}

internal interface ISnapshotStore
{
    void Save(string name, SnapshotDocument document, bool force);
    SnapshotDocument Load(string name);
}

internal sealed partial class SnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _root;
    private readonly Action<string, byte[]> _writeAllBytes;
    private readonly Action<string, string, bool> _move;
    private readonly Action<string> _delete;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, byte[]> _readAllBytes;
    private readonly Action<string> _createDirectory;

    public SnapshotStore()
        : this(SqlHarnessPaths.SnapshotsDir)
    {
    }

    internal SnapshotStore(string root)
        : this(
            root,
            File.WriteAllBytes,
            (source, destination, overwrite) => File.Move(source, destination, overwrite),
            File.Delete,
            File.Exists,
            File.ReadAllBytes,
            path => Directory.CreateDirectory(path))
    {
    }

    internal SnapshotStore(
        string root,
        Action<string, byte[]> writeAllBytes,
        Action<string, string, bool>? move = null,
        Action<string>? delete = null,
        Func<string, bool>? exists = null,
        Func<string, byte[]>? readAllBytes = null,
        Action<string>? createDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _writeAllBytes = writeAllBytes ?? throw new ArgumentNullException(nameof(writeAllBytes));
        _move = move ?? ((source, destination, overwrite) => File.Move(source, destination, overwrite));
        _delete = delete ?? File.Delete;
        _exists = exists ?? File.Exists;
        _readAllBytes = readAllBytes ?? File.ReadAllBytes;
        _createDirectory = createDirectory ?? (path => Directory.CreateDirectory(path));
    }

    public void Save(string name, SnapshotDocument document, bool force)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != SnapshotDocument.CurrentVersion)
        {
            throw new ArgumentException(
                $"Snapshot version must be {SnapshotDocument.CurrentVersion}.",
                nameof(document));
        }

        ArgumentNullException.ThrowIfNull(document.ResultSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.ResultHash);

        var path = ResolvePath(name);
        if (!force && _exists(path))
            throw new IOException($"Snapshot '{name}' already exists.");

        _createDirectory(_root);

        string? tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            _writeAllBytes(tempPath, bytes);
            _move(tempPath, path, force);
            tempPath = null;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public SnapshotDocument Load(string name)
    {
        var path = ResolvePath(name);
        if (!_exists(path))
            throw new FileNotFoundException($"Snapshot '{name}' was not found.", path);

        try
        {
            var bytes = _readAllBytes(path);
            var document = JsonSerializer.Deserialize<SnapshotDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException($"Snapshot '{name}' was empty.");

            if (document.Version != SnapshotDocument.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported snapshot version {document.Version.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (document.ResultSets is null)
                throw new InvalidDataException($"Snapshot '{name}' is missing result sets.");
            if (string.IsNullOrWhiteSpace(document.ResultHash))
                throw new InvalidDataException($"Snapshot '{name}' is missing a result hash.");

            return document;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException($"Snapshot '{name}' is malformed.", exception);
        }
    }

    private string ResolvePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !NamePattern().IsMatch(name))
        {
            throw new ArgumentException(
                "Snapshot name must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$.",
                nameof(name));
        }

        // Defense in depth: labels must be a single path segment under the store root.
        if (name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(name))
        {
            throw new ArgumentException("Snapshot name must not contain path separators or traversal.", nameof(name));
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, name + ".json"));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snapshot path must remain under the configured root.", nameof(name));
        }

        var parent = Path.GetDirectoryName(candidate);
        if (!string.Equals(parent, _root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Snapshot path must remain directly under the configured root.", nameof(name));

        return candidate;
    }

    private void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try { _delete(path); }
        catch { /* best effort cleanup */ }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}