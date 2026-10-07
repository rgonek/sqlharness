namespace SqlHarness.Core;

/// <summary>Owner-only modes for journal files on Unix; Windows inherits the user-profile ACL.</summary>
internal static class OwnerOnlyFiles
{
    internal static void Directory(string path)
    {
        if (!OperatingSystem.IsWindows() && System.IO.Directory.Exists(path))
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void File(string path)
    {
        if (!OperatingSystem.IsWindows() && System.IO.File.Exists(path))
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}