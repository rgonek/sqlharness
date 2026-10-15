using System.ComponentModel;
using System.Diagnostics;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Cli.Commands;

[Description("Serve the local activity dashboard on 127.0.0.1.")]
public sealed class DashboardCommand(IDashboardLauncher launcher, IDashboardLifecycle lifecycle, OutputContext output, McpHostConsole console) : AsyncCommand<DashboardCommand.Settings>
{
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(10);

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-open")]
        [Description("Print the URL without opening a browser.")]
        public bool NoOpen { get; set; }

        [CommandOption("--background")]
        [Description("Serve quietly without opening a browser (used by autostart).")]
        public bool Background { get; set; }

        [CommandOption("--stop")]
        [Description("Stop the dashboard for this SQLHarness home.")]
        public bool Stop { get; set; }

        [CommandOption("--restart")]
        [Description("Replace the dashboard with this sqlharness version.")]
        public bool Restart { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Stop && settings.Restart
            || (settings.Stop || settings.Restart) && settings.Background
            || settings.Stop && settings.NoOpen)
            return Fail("--stop and --restart cannot be combined; lifecycle options cannot be combined with --background; --stop cannot be combined with --no-open.", SqlHarnessExitCode.Safety);

        if (settings.Stop || settings.Restart)
            return await ExecuteLifecycleAsync(settings, ct);

        // Ctrl+C stops the server gracefully so the lock and published address are released.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            args.Cancel = true;
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ctrl+C raced the command's return; nothing is left to stop.
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var config = SqlHarnessConfigLoader.Load();
            // Background mode writes nothing: the autostart launcher closes the standard streams.
            var quiet = settings.Background;
            return await DashboardHost.RunAsync(
                new DashboardHostOptions(
                    SqlHarnessPaths.Home,
                    SqlHarnessPaths.ActivityDatabase,
                    config,
                    OpenBrowser: !settings.NoOpen && !settings.Background,
                    Quiet: quiet,
                    quiet ? TextWriter.Null : Console.Out,
                    quiet ? TextWriter.Null : Console.Error,
                    ProcessInfo.Current,
                    new SystemBrowserLauncher())
                {
                    ConfigPath = SqlHarnessPaths.ConfigFile,
                    // Only the autostarted instance idle-exits; a dashboard started by hand runs until Ctrl+C.
                    IdleShutdown = settings.Background ? TimeSpan.FromHours(config.Config.Dashboard.IdleShutdownHours) : null,
                },
                stop.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private async Task<int> ExecuteLifecycleAsync(Settings settings, CancellationToken ct)
    {
        var home = SqlHarnessPaths.Home;
        DashboardStopResult stopped;
        try
        {
            stopped = await lifecycle.StopAsync(home, LifecycleTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (int)SqlHarnessExitCode.Success;
        }
        catch (Exception)
        {
            return Fail("The dashboard could not be safely stopped; it may still be running.", SqlHarnessExitCode.LocalStorage);
        }

        if (ct.IsCancellationRequested)
            return (int)SqlHarnessExitCode.Success;

        if (stopped.Status == DashboardStopStatus.Failed)
            return Fail(stopped.Error ?? "The dashboard could not be safely stopped; it may still be running.", SqlHarnessExitCode.LocalStorage);

        if (settings.Stop)
        {
            output.Capture.WriteLine(stopped.Status == DashboardStopStatus.AlreadyStopped
                ? "SQLHarness dashboard is already stopped."
                : "SQLHarness dashboard stopped.");
            return (int)SqlHarnessExitCode.Success;
        }

        var launchStarted = Stopwatch.GetTimestamp();
        DashboardLaunchIdentity identity;
        try
        {
            identity = launcher.Launch(DashboardLaunchCommand.Current(), home);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (int)SqlHarnessExitCode.Success;
        }
        catch (Exception)
        {
            return Fail("The replacement dashboard could not be launched.", SqlHarnessExitCode.LocalStorage);
        }

        var remaining = LifecycleTimeout - Stopwatch.GetElapsedTime(launchStarted);
        if (remaining <= TimeSpan.Zero)
            return Fail("The replacement dashboard did not publish its address before the startup deadline.", SqlHarnessExitCode.LocalStorage);

        DashboardEndpoint? endpoint;
        try
        {
            endpoint = await lifecycle.WaitForRunningAsync(home, remaining, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (int)SqlHarnessExitCode.Success;
        }
        catch (Exception)
        {
            return Fail("The replacement dashboard address could not be confirmed.", SqlHarnessExitCode.LocalStorage);
        }

        if (ct.IsCancellationRequested)
            return (int)SqlHarnessExitCode.Success;

        if (endpoint is null)
            return Fail("The replacement dashboard did not publish its address before the startup deadline.", SqlHarnessExitCode.LocalStorage);
        if (!identity.Matches(endpoint))
            return Fail($"Dashboard PID {endpoint.Pid} published during restart, but it is not the requested process; no success was announced.", SqlHarnessExitCode.LocalStorage);

        if (ct.IsCancellationRequested)
            return (int)SqlHarnessExitCode.Success;

        DashboardAnnouncement.Announce(home, endpoint.OpenUri, output.Capture, console.Error,
            openBrowser: !settings.NoOpen, new SystemBrowserLauncher());
        return (int)SqlHarnessExitCode.Success;
    }

    private int Fail(string message, SqlHarnessExitCode exitCode)
    {
        console.Error.WriteLine($"sqlharness: {message}");
        return (int)exitCode;
    }
}