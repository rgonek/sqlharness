using System.Diagnostics;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class BrowserLauncherTests
{
    [Fact]
    public void Launcher_passes_only_the_given_uri_and_discards_output()
    {
        var started = new List<ProcessStartInfo>();
        var launcher = new SystemBrowserLauncher(start =>
        {
            started.Add(start);
            return null;
        });
        var page = new Uri(Path.Combine(Path.GetTempPath(), "dashboard-open.html"));

        launcher.Open(page);

        var start = Assert.Single(started);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(page.ToString(), start.FileName);
            Assert.True(start.UseShellExecute);
        }
        else
        {
            Assert.Equal("/bin/sh", start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.Equal(
                ["-c", "exec \"$1\" \"$2\" </dev/null >/dev/null 2>&1", "sh", OperatingSystem.IsMacOS() ? "open" : "xdg-open", page.ToString()],
                start.ArgumentList);
        }
    }

    [Fact]
    public void Launcher_ignores_start_failures()
    {
        var launcher = new SystemBrowserLauncher(_ => throw new System.ComponentModel.Win32Exception());

        launcher.Open(new Uri("file:///tmp/dashboard-open.html"));
    }
}