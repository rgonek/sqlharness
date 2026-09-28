using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

/// <summary>
/// Shared root/staging/publish/cleanup mechanics for domain artifact writers
/// (compare/measure, Query Store, index analysis). The publisher owns only the
/// directory lifecycle: safe naming under a fixed root, staging writes, publish
/// via move, and best-effort rollback that never masks the primary failure
/// (same rule as <see cref="OperationFailureMapper.CompleteCleanup"/>: when the
/// main path already failed, a cleanup failure is swallowed so the original
/// cause propagates). Content policy — what is written and which sensitive
/// payloads may accompany a report — stays in the domain writer.
/// SnapshotStore/GainStore do not use this contract.
/// </summary>
internal sealed partial class ArtifactDirectoryPublisher
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string, string, Encoding> _writeText;
    private readonly Action<string, string> _moveDirectory;
    private readonly Action<string, string> _moveFile;
    private readonly Action<string> _deleteFile;
    private readonly Action<string, bool> _deleteDirectory;

    internal ArtifactDirectoryPublisher(
        string root,
        Func<DateTimeOffset> utcNow,
        Action<string, string, Encoding>? writeText = null,
        Action<string, string>? moveDirectory = null,
        Action<string, string>? moveFile = null,
        Action<string>? deleteFile = null,
        Action<string, bool>? deleteDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _writeText = writeText ?? File.WriteAllText;
        _moveDirectory = moveDirectory ?? ((source, destination) => Directory.Move(source, destination));
        _moveFile = moveFile ?? File.Move;
        _deleteFile = deleteFile ?? File.Delete;
        _deleteDirectory = deleteDirectory ?? Directory.Delete;
    }

    /// <summary>
    /// Reserves a unique final directory under the root, runs
    /// <paramref name="writeStaging"/> against an isolated staging directory,
    /// then publishes by moving staging to the final directory. The final
    /// directory never appears before the move completes; any failure leaves
    /// neither a partial final directory nor staged files behind.
    /// Holds no mutable state, so concurrent publishes are isolated by GUID.
    /// </summary>
    internal string Publish(string target, Action<string, string> writeStaging)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(writeStaging);
        var safeTarget = SanitizeTarget(target);

        Directory.CreateDirectory(_root);
        string directory;
        do
        {
            directory = Path.Combine(_root, $"{_utcNow():yyyyMMddTHHmmssfffZ}-{safeTarget}-{Guid.NewGuid():N}");
        } while (Directory.Exists(directory));

        var staging = directory + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            writeStaging(staging, directory);
            _moveDirectory(staging, directory);
        }
        catch (Exception primary)
        {
            // Preserve the primary failure even when rollback also fails.
            OperationFailureMapper.CompleteCleanup(primary, () => Cleanup(staging));
            OperationFailureMapper.CompleteCleanup(primary, () => Cleanup(directory));
            throw;
        }

        return directory;
    }

    internal void WriteText(string path, string content, Encoding encoding) =>
        _writeText(path, content, encoding);

    internal void MoveFile(string source, string destination) =>
        _moveFile(source, destination);

    internal static string SanitizeTarget(string target)
    {
        var segment = SanitizePathSegment(target);
        return string.IsNullOrEmpty(segment) ? "target" : segment;
    }

    internal static string SanitizePathSegment(string value) =>
        UnsafePathCharacter().Replace(value, "-").Trim('-');

    private void Cleanup(string directory)
    {
        if (!Directory.Exists(directory)) return;
        string[] files;
        try { files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories); }
        catch { files = []; }
        foreach (var file in files) Try(() => _deleteFile(file));

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

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafePathCharacter();
}