using System.Diagnostics;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardLaunchCommand(string FileName, IReadOnlyList<string> Arguments)
{
    private static readonly string[] DashboardArguments = ["dashboard", "--background"];

    /// <summary>
    /// The command that runs this sqlharness again: the single-file executable itself, or
    /// <c>dotnet sqlharness.dll</c> during development (process path is the dotnet host).
    /// </summary>
    public static DashboardLaunchCommand For(string processPath, string? entryAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(processPath);
        // Either separator: a Windows path must classify the same way on every OS.
        var fileName = processPath[(processPath.LastIndexOfAny(['/', '\\']) + 1)..];
        var hostedByDotnet = string.Equals(Path.GetFileNameWithoutExtension(fileName), "dotnet", StringComparison.OrdinalIgnoreCase);
        return hostedByDotnet && !string.IsNullOrEmpty(entryAssemblyPath)
            ? new DashboardLaunchCommand(processPath, [entryAssemblyPath, .. DashboardArguments])
            : new DashboardLaunchCommand(processPath, DashboardArguments);
    }

    // Location is empty in a single-file app; that host is never dotnet, so For() ignores it there.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty location in a single-file app selects the executable itself.")]
    public static DashboardLaunchCommand Current() =>
        For(Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown."),
            System.Reflection.Assembly.GetEntryAssembly()?.Location);
}

public interface IDashboardLauncher
{
    void Launch(DashboardLaunchCommand command, string workingDirectory);
}

/// <summary>
/// Starts the dashboard as an independent process that holds none of this process's
/// standard handles: an MCP client must still see EOF on the server's stdout when the
/// server exits. Windows uses ShellExecuteEx (no handle inheritance, hidden window);
/// Unix redirects stdin/stdout/stderr to pipes this process closes immediately.
/// </summary>
public sealed class DetachedDashboardLauncher : IDashboardLauncher
{
    public void Launch(DashboardLaunchCommand command, string workingDirectory)
    {
        var windows = OperatingSystem.IsWindows();
        using var process = Process.Start(StartInfo(command, workingDirectory, windows))
            ?? throw new InvalidOperationException("The dashboard process did not start.");
        if (!windows)
        {
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
    }

    internal static ProcessStartInfo StartInfo(DashboardLaunchCommand command, string workingDirectory, bool windows)
    {
        if (windows)
        {
            return new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(" ", command.Arguments.Select(Quote)),
                WorkingDirectory = workingDirectory,
            };
        }

        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

public enum AutostartResult
{
    Disabled,
    AlreadyRunning,
    Launched,
    Failed,
}

public static class DashboardAutostart
{
    /// <summary>
    /// Launches a background dashboard when dashboard.autoStart is on and none is published
    /// for this home. Never throws and never waits for the dashboard: a dashboard that loses
    /// the lock race exits on its own, and a later mcp serve retries.
    /// </summary>
    public static AutostartResult TryStart(string home, SqlHarnessConfig config, IProcessInfo processes, IDashboardLauncher launcher, DashboardLaunchCommand command)
    {
        if (!config.Dashboard.AutoStart)
            return AutostartResult.Disabled;
        try
        {
            if (DashboardLock.ReadRunning(home, processes) is not null)
                return AutostartResult.AlreadyRunning;
            Directory.CreateDirectory(home);
            launcher.Launch(command, home);
            return AutostartResult.Launched;
        }
        catch (Exception)
        {
            return AutostartResult.Failed;
        }
    }
}