using System.ComponentModel;
using System.Diagnostics;

namespace SqlHarness.Dashboard;

public interface IBrowserLauncher
{
    void Open(Uri uri);
}

/// <summary>Opens the default browser; failures are ignored because the URL is also printed.</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
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
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(uri.ToString());
            }

            using var _ = Process.Start(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
        }
    }
}