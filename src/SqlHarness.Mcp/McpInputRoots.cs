namespace SqlHarness.Mcp;

/// <summary>
/// Shared normalization and membership check for MCP input roots, used by
/// both startup validation (<see cref="McpScope"/>) and the guarded reader
/// (<see cref="McpInputReader"/>).
/// </summary>
internal static class McpInputRoots
{
    // Platform case choice, made once here and never per call site: Windows
    // keeps OrdinalIgnoreCase (status quo), while systems other than Windows
    // use Ordinal (conservative: no false admissions on case-sensitive
    // filesystems).
    //
    // Known limitation (accepted, not fixed in this plan): individual Windows
    // directories may opt into case sensitivity (per-directory case
    // sensitivity), so OrdinalIgnoreCase on Windows can falsely admit a
    // sibling that differs only in letter case inside such a directory.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Normalizes one input root: the full path with only redundant trailing
    /// separators removed. A filesystem root ("D:\", "/") is preserved as
    /// such and must be rejected explicitly by the caller, never admitted.
    /// </summary>
    internal static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal))
            return full;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>True when the normalized root is a filesystem root.</summary>
    internal static bool IsFilesystemRoot(string normalizedRoot) =>
        string.Equals(normalizedRoot, Path.GetPathRoot(normalizedRoot), StringComparison.Ordinal);

    /// <summary>
    /// Membership against one normalized root: equality or a
    /// <c>normalizedRoot + Separator</c> prefix. Appending the separator is
    /// safe exactly because normalization leaves no trailing separator,
    /// except on the filesystem root — which startup validation rejects, and
    /// which here fail-closes (root plus separator matches nothing).
    /// </summary>
    internal static bool IsUnderRoot(string full, string normalizedRoot) =>
        string.Equals(full, normalizedRoot, PathComparison) ||
        full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>Membership against any of the scope roots (each normalized here).</summary>
    internal static bool IsUnderAnyRoot(string full, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (IsUnderRoot(full, NormalizeRoot(root)))
                return true;
        }

        return false;
    }
}