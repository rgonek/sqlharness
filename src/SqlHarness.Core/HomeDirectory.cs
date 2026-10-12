namespace SqlHarness.Core;

/// <summary>
/// The SQLHarness home directory. On Unix it is created with mode 0700: no other user
/// can traverse it, so one private home protects every writer below it without per-file
/// creation modes. An existing home keeps the mode its owner gave it — SQLHarness never
/// re-chmods a directory it did not create. Windows inherits the user-profile ACL.
/// </summary>
internal static class HomeDirectory
{
    internal const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Creates the home when a writer is about to create <paramref name="path"/> under it.
    /// No-op when the path is outside the home. Only SQLHarness sees the creation itself:
    /// setting the mode on an already existing home would change a directory SQLHarness
    /// did not create, so <see cref="Directory.CreateDirectory(string, UnixFileMode)"/>
    /// is relied upon to leave that one untouched.
    /// </summary>
    internal static void EnsureFor(string path)
    {
        if (!IsUnderHome(path))
            return;

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(SqlHarnessPaths.Home);
            return;
        }

        Directory.CreateDirectory(SqlHarnessPaths.Home, OwnerOnly);
    }

    /// <summary>Null off Unix or when the home is missing; otherwise the home's Unix mode.</summary>
    internal static UnixFileMode? Mode()
    {
        if (OperatingSystem.IsWindows())
            return null;

        var home = SqlHarnessPaths.Home;
        if (!Directory.Exists(home))
            return null;

        try
        {
            return File.GetUnixFileMode(home);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the mode grants nothing to group or other; 0700 and 0600 are owner-only.</summary>
    internal static bool IsOwnerOnly(UnixFileMode mode) => (mode & GroupOrOther) == 0;

    internal static bool IsUnderHome(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string home;
        string full;
        try
        {
            home = Path.GetFullPath(SqlHarnessPaths.Home);
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return string.Equals(full, home, Comparison)
            || full.StartsWith(home + Path.DirectorySeparatorChar, Comparison);
    }
}