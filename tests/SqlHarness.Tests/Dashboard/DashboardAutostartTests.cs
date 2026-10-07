using System.Diagnostics;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardAutostartTests
{
    private sealed class RecordingLauncher(Exception? failure = null) : IDashboardLauncher
    {
        public List<(DashboardLaunchCommand Command, string WorkingDirectory)> Launches { get; } = [];
        public void Launch(DashboardLaunchCommand command, string workingDirectory)
        {
            if (failure is not null)
                throw failure;
            Launches.Add((command, workingDirectory));
        }
    }

    private static readonly DashboardLaunchCommand Command = DashboardLaunchCommand.For("/opt/sqlharness/sqlharness", null);

    private static SqlHarnessConfig AutoStart(bool enabled) =>
        SqlHarnessConfig.Default with { Dashboard = new DashboardConfig { AutoStart = enabled } };

    [Fact]
    public void Disabled_autostart_launches_nothing()
    {
        using var home = new TempHome();
        var launcher = new RecordingLauncher();

        Assert.Equal(AutostartResult.Disabled, DashboardAutostart.TryStart(home.Path, AutoStart(false), new FakeProcesses(), launcher, Command));
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public void Running_dashboard_is_not_launched_again()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, null, 47800, "tok"));
        var launcher = new RecordingLauncher();

        var result = DashboardAutostart.TryStart(home.Path, AutoStart(true), new FakeProcesses().Alive(4242, null), launcher, Command);

        Assert.Equal(AutostartResult.AlreadyRunning, result);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public void Missing_dashboard_is_launched_in_the_home_directory()
    {
        using var home = new TempHome();
        var launcher = new RecordingLauncher();

        Assert.Equal(AutostartResult.Launched, DashboardAutostart.TryStart(home.Path, AutoStart(true), new FakeProcesses(), launcher, Command));
        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(home.Path, launch.WorkingDirectory);
        Assert.Equal(["dashboard", "--background"], launch.Command.Arguments);
    }

    [Fact]
    public void Launch_failure_is_reported_not_thrown()
    {
        using var home = new TempHome();

        Assert.Equal(AutostartResult.Failed,
            DashboardAutostart.TryStart(home.Path, AutoStart(true), new FakeProcesses(), new RecordingLauncher(new InvalidOperationException("x")), Command));
    }

    [Theory]
    [InlineData("C:\\tools\\sqlharness.exe", "", "C:\\tools\\sqlharness.exe")]
    [InlineData("/usr/local/bin/sqlharness", null, "/usr/local/bin/sqlharness")]
    public void Launch_command_uses_the_executable_when_self_hosted(string processPath, string? entry, string fileName)
    {
        var command = DashboardLaunchCommand.For(processPath, entry);

        Assert.Equal(fileName, command.FileName);
        Assert.Equal(["dashboard", "--background"], command.Arguments);
    }

    [Theory]
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe", "D:\\src\\bin\\sqlharness.dll")]
    [InlineData("/usr/share/dotnet/dotnet", "/src/bin/sqlharness.dll")]
    public void Launch_command_uses_the_dll_when_hosted_by_dotnet(string processPath, string entry)
    {
        var command = DashboardLaunchCommand.For(processPath, entry);

        Assert.Equal(processPath, command.FileName);
        Assert.Equal([entry, "dashboard", "--background"], command.Arguments);
    }

    [Fact]
    public void Windows_launch_uses_shell_execute_without_handle_inheritance()
    {
        var start = DetachedDashboardLauncher.StartInfo(
            DashboardLaunchCommand.For("C:\\Program Files\\dotnet\\dotnet.exe", "D:\\my src\\sqlharness.dll"), "C:\\home", windows: true);

        // ShellExecuteEx never inherits handles, so the MCP server's stdio pipe stays private.
        Assert.True(start.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal("\"D:\\my src\\sqlharness.dll\" dashboard --background", start.Arguments);
        Assert.Equal("C:\\home", start.WorkingDirectory);
    }

    [Fact]
    public void Unix_launch_redirects_and_closes_every_standard_stream()
    {
        var start = DetachedDashboardLauncher.StartInfo(DashboardLaunchCommand.For("/usr/local/bin/sqlharness", null), "/home/u/.sqlharness", windows: false);

        // Redirected streams replace fds 0-2 in the child; .NET marks its other fds close-on-exec.
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(["dashboard", "--background"], start.ArgumentList);
        Assert.Equal("/home/u/.sqlharness", start.WorkingDirectory);
    }
}