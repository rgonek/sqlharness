using System.Text;
using System.Text.RegularExpressions;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

/// <summary>
/// Safe rejection of an MCP file or inline input. Messages name the reason and may echo the caller-supplied
/// path (credential-filtered and length-bounded) and the configured input roots; they never echo SQL, file
/// content, or any other filesystem path.
/// </summary>
public sealed class McpInputException(string message) : Exception(message);

/// <summary>
/// Bounded file ingress for MCP tools (spec section 5). Inline input works
/// without filesystem access; file input is admitted only under the absolute
/// --input-root directories frozen at startup, and an empty root list means
/// no file inputs at all. Rejects UNC/network paths. Absolute paths must be
/// lexically under an admitted root.
/// Opens each file once and reads at most the stated bound plus one byte.
/// </summary>
public static partial class McpInputReader
{
    /// <summary>Longest caller-supplied path echoed in a rejection message, in characters.</summary>
    internal const int MaxEchoedPathCharacters = 120;

    /// <summary>Most configured roots listed in an outside-root rejection.</summary>
    internal const int MaxListedRoots = 8;

    /// <summary>Largest total root-string length listed in an outside-root rejection.</summary>
    internal const int MaxListedRootCharacters = 150;

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
        return ReadPlanAsync(content, file, scope.InputRoots, ct);
    }

    public static Task<string> ReadPlanAsync(string? content, string? file, IReadOnlyList<string> inputRoots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputRoots);
        var hasInline = !string.IsNullOrWhiteSpace(content);
        var hasFile = !string.IsNullOrWhiteSpace(file);
        if (hasInline == hasFile)
            throw new McpInputException("Provide exactly one plan source: inline content or file.");
        if (hasInline)
        {
            ThrowIfInlineTooLarge(content!);
            return Task.FromResult(content!);
        }

        return ReadTextFileAsync(file!, inputRoots, McpLimits.MaxPlanBytes, ct, kind: "plan document");
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
        string kind = "input") =>
        ReadFileAsync(path, roots, maxBytes, ct, DecodeText, kind);

    internal static Task<byte[]> ReadBytesFileAsync(
        string path,
        IReadOnlyList<string> roots,
        long maxBytes,
        CancellationToken ct,
        string kind = "input") =>
        ReadFileAsync(path, roots, maxBytes, ct, bytes => bytes, kind);

    private static async Task<T> ReadFileAsync<T>(
        string path,
        IReadOnlyList<string> roots,
        long maxBytes,
        CancellationToken ct,
        Func<byte[], T> decode,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
            throw new McpInputException("File inputs are disabled: no --input-root is configured (sqlharness_capabilities inputRoots is empty). Pass the content inline instead.");
        if (string.IsNullOrWhiteSpace(path))
            throw new McpInputException("The input path is blank. Provide an absolute path under a configured input root (see sqlharness_capabilities inputRoots).");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw Rejected("UNC and network paths are not accepted", path);

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path))
                throw Rejected("not an absolute path", path);
            full = Path.GetFullPath(path);
        }
        catch (McpInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Rejected("not a valid path", path);
        }

        if (!McpInputRoots.IsUnderAnyRoot(full, roots))
            throw OutsideRootsException(roots, path);

        FileStream stream;
        try
        {
            if (new FileInfo(full).Length > maxBytes)
                throw new McpInputException(TooLargeMessage(kind, maxBytes));
            stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (McpInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Rejected("the file cannot be read", path);
        }

        await using (stream)
        {
            var bytes = await ReadBoundedAsync(stream, maxBytes, ct, kind);
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


    private static McpInputException Rejected(string reason, string path) =>
        new($"The input path was rejected: {reason}. Path: {EchoPath(path)}");

    private static McpInputException OutsideRootsException(IReadOnlyList<string> roots, string path) =>
        new($"The input path is outside every configured input root. {DescribeRoots(roots)} Path: {EchoPath(path)}");

    private static string DescribeRoots(IReadOnlyList<string> roots)
    {
        const string Pointer = "sqlharness_capabilities inputRoots lists them";
        var listed = 0;
        var characters = 0;
        while (listed < roots.Count && listed < MaxListedRoots && characters + roots[listed].Length <= MaxListedRootCharacters)
        {
            characters += roots[listed].Length;
            listed++;
        }

        if (listed == roots.Count)
            return $"Configured roots ({Pointer}): {string.Join("; ", roots)}.";
        if (listed == 0)
            return $"{roots.Count} input roots are configured; page sqlharness_capabilities inputRoots (inputRootCursor) to list them.";
        return $"First {listed} of {roots.Count} configured roots: {string.Join("; ", roots.Take(listed))}. Page sqlharness_capabilities inputRoots (inputRootCursor) for the rest.";
    }

    /// <summary>The caller's own path, credential-filtered, control characters flattened, and bounded.</summary>
    [GeneratedRegex(@"(//|\\\\)[^/\\@\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    private static string EchoPath(string path)
    {
        // URL/UNC userinfo (user:password@host) is masked first, before redaction and truncation.
        var safe = SecretRedactor.Redact(UserInfoPattern().Replace(path, "$1[REDACTED]@"), []);
        safe = string.Create(safe.Length, safe, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
        });
        return safe.Length <= MaxEchoedPathCharacters
            ? safe
            : $"{safe[..MaxEchoedPathCharacters]}... [truncated, {safe.Length} characters]";
    }

}