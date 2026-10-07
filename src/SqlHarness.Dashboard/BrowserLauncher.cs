using System.ComponentModel;
using System.Diagnostics;

namespace SqlHarness.Dashboard;

public interface IBrowserLauncher
{
    void Open(Uri uri);
}

/// <summary>
/// Opens the default browser; failures are ignored because the URL is also printed.
/// The dashboard passes a local redirect page here, never the token URL, because a
/// launched process's arguments are visible to other local users.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    // sh redirects the opener's standard streams to /dev/null, so browser chatter never
    // reaches the dashboard's terminal and no pipe is left for the browser to write into.
    internal const string DiscardOutputScript = "exec \"$1\" \"$2\" </dev/null >/dev/null 2>&1";

    private readonly Func<ProcessStartInfo, IDisposable?> _start;

    public SystemBrowserLauncher()
        : this(Process.Start)
    {
    }

    internal SystemBrowserLauncher(Func<ProcessStartInfo, IDisposable?> start) => _start = start;

    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(uri.ToString()) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(DiscardOutputScript);
                start.ArgumentList.Add("sh");
                start.ArgumentList.Add(OperatingSystem.IsMacOS() ? "open" : "xdg-open");
                start.ArgumentList.Add(uri.ToString());
            }

            using var _ = _start(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
        }
    }
}