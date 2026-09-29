using System.Text;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

/// <summary>Safe rejection of an MCP file or inline input. Messages are constant and never echo values or SQL.</summary>
public sealed class McpInputException(string message) : Exception(message);

/// <summary>
/// Bounded file ingress for MCP tools (spec section 5). Inline input works
/// without filesystem access; file input is admitted only under the absolute
/// --input-root directories frozen at startup, and an empty root list means
/// no file inputs at all. Rejects traversal, UNC/network paths, NTFS
/// alternate streams, symlink/reparse escapes (including linked parent
/// directories), and files replaced while they are read. Opens each file
/// once, reads at most the stated bound plus one byte, then re-validates the
/// on-disk entry before accepting the bytes.
/// </summary>
public static class McpInputReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Resolves exactly one of inline sql or a rooted file, for SQL tools.</summary>
    public static Task<string> ReadSqlAsync(string? sql, string? file, McpScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var hasInline = !string.IsNullOrWhiteSpace(sql);
        var hasFile = !string.IsNullOrWhiteSpace(file);
        if (hasInline == hasFile)
            throw new McpInputException("Provide exactly one SQL source: inline sql or file.");
        if (hasInline)
        {
            ThrowIfInlineTooLarge(sql!);
            return Task.FromResult(sql!);
        }

        return ReadTextFileAsync(file!, scope.InputRoots, McpLimits.MaxSqlBytes, ct, kind: "SQL input");
    }

    /// <summary>Resolves exactly one of an inline plan document or a rooted file, for the plan tool.</summary>
    public static Task<string> ReadPlanAsync(string? content, string? file, McpScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var hasInline = !string.IsNullOrWhiteSpace(content);
        var hasFile = !string.IsNullOrWhiteSpace(file);
        if (hasInline == hasFile)
            throw new McpInputException("Provide exactly one plan source: inline content or file.");
        if (hasInline)
        {
            ThrowIfInlineTooLarge(content!);
            return Task.FromResult(content!);
        }

        return ReadTextFileAsync(file!, scope.InputRoots, McpLimits.MaxPlanBytes, ct, kind: "plan document");
    }

    /// <summary>
    /// Reads parameter-set files through the guarded reader and parses every
    /// file with the shared Core strict parser (64 KiB, no BOM/comments/
    /// trailing commas, only name and parameters). Only .sqljson files are
    /// admitted, and values never leave this path into reports.
    /// </summary>
    public static async Task<IReadOnlyList<SqlHarnessParameterSetInput>> ReadParameterSetsAsync(
        IReadOnlyList<string> paths,
        McpScope scope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(scope);
        var sets = new List<SqlHarnessParameterSetInput>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(Path.GetExtension(path), ".sqljson", StringComparison.OrdinalIgnoreCase))
                throw new McpInputException("Parameter-set files must use the .sqljson extension.");
            var bytes = await ReadBytesFileAsync(path, scope.InputRoots, McpLimits.MaxParameterSetBytes, ct, kind: "parameter set file");
            try
            {
                sets.Add(ParameterSetFileReader.Parse(bytes));
            }
            catch (ParameterSetFileException exception)
            {
                // The shared parser messages are constant and value-free.
                throw new McpInputException(exception.Message);
            }
        }

        return sets;
    }

    /// <summary>Rejects inline payloads above the MCP frame budget before further handling.</summary>
    public static void ThrowIfInlineTooLarge(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > McpLimits.MaxInlineBytes)
            throw new McpInputException("Inline input exceeds the 1 MiB frame. Place it in a file under an input root and pass its path.");
    }

    internal static Task<string> ReadTextFileAsync(
        string path,
        IReadOnlyList<string> roots,
        long maxBytes,
        CancellationToken ct,
        Action? afterOpen = null,
        string kind = "input") =>
        ReadFileAsync(path, roots, maxBytes, ct, afterOpen, DecodeText, kind);

    internal static Task<byte[]> ReadBytesFileAsync(
        string path,
        IReadOnlyList<string> roots,
        long maxBytes,
        CancellationToken ct,
        Action? afterOpen = null,
        string kind = "input") =>
        ReadFileAsync(path, roots, maxBytes, ct, afterOpen, bytes => bytes, kind);

    private static async Task<T> ReadFileAsync<T>(
        string path,
        IReadOnlyList<string> roots,
        long maxBytes,
        CancellationToken ct,
        Action? afterOpen,
        Func<byte[], T> decode,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
            throw new McpInputException("File inputs are not enabled for this server process.");
        if (string.IsNullOrWhiteSpace(path))
            throw new McpInputException("The input path is invalid.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new McpInputException("The input path is invalid.");

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path))
                throw new McpInputException("The input path is invalid.");
            full = Path.GetFullPath(path);
        }
        catch (McpInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new McpInputException("The input path is invalid.");
        }

        // NTFS alternate data streams hide behind a colon in the file name.
        // The drive-letter colon lives in the directory portion, never here.
        if (Path.GetFileName(full).Contains(':', StringComparison.Ordinal))
            throw new McpInputException("The input path is invalid.");
        if (!McpInputRoots.IsUnderAnyRoot(full, roots))
            throw new McpInputException("The input path is invalid.");
        if (IsLinkOrReparseChain(full))
            throw new McpInputException("The input path is invalid.");

        FileStream stream;
        try
        {
            if (new FileInfo(full).Length > maxBytes)
                throw new McpInputException(TooLargeMessage(kind, maxBytes));
            // FileShare.Delete lets the operator rotate files by rename while a
            // read holds the old entry; the post-read snapshot then rejects
            // the replaced file instead of mixing old and new bytes.
            stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (McpInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new McpInputException("The input file cannot be read.");
        }

        await using (stream)
        {
            var before = Snapshot(full);
            var bytes = await ReadBoundedAsync(stream, maxBytes, ct, kind);
            afterOpen?.Invoke();
            var after = Snapshot(full);
            if (!before.Equals(after) || !McpInputRoots.IsUnderAnyRoot(full, roots) || IsLinkOrReparseChain(full))
                throw new McpInputException("The input file changed while it was read.");
            try
            {
                return decode(bytes);
            }
            catch (McpInputException)
            {
                throw;
            }
            catch (DecoderFallbackException)
            {
                throw new McpInputException("The input file has invalid text encoding.");
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(FileStream stream, long maxBytes, CancellationToken ct, string kind)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= maxBytes)
        {
            var remaining = maxBytes + 1 - buffer.Length;
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), ct);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length > maxBytes)
            throw new McpInputException(TooLargeMessage(kind, maxBytes));
        return buffer.ToArray();
    }

    private static string DecodeText(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string TooLargeMessage(string kind, long maxBytes) => kind switch
    {
        "parameter set file" => "Parameter set file exceeds the 64 KiB limit.",
        "plan document" => "The plan document exceeds the 16 MiB limit.",
        "SQL input" => "SQL input exceeds the 16 MiB limit.",
        _ => "The input file exceeds the read limit.",
    };


    /// <summary>
    /// Fails closed on the file itself and on every parent directory up to the
    /// admitted root: a symlinked directory inside a root must not smuggle
    /// reads outside it.
    /// </summary>
    private static bool IsLinkOrReparseChain(string full)
    {
        var current = full;
        while (current is not null)
        {
            if (IsLinkOrReparse(current))
                return true;
            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static bool IsLinkOrReparse(string path)
    {
        try
        {
            FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
            if (!info.Exists)
                return false;
            return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static (long Length, DateTime Modified, bool IsLink) Snapshot(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
                return (-1, DateTime.MinValue, true);
            return (info.Length, info.LastWriteTimeUtc, IsLinkOrReparse(full));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (-1, DateTime.MinValue, true);
        }
    }
}
