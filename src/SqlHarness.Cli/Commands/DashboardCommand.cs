using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Cli.Commands;

[Description("Serve the local activity dashboard on 127.0.0.1.")]
public sealed class DashboardCommand : AsyncCommand<DashboardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-open")]
        [Description("Print the URL without opening a browser.")]
        public bool NoOpen { get; set; }

        [CommandOption("--background")]
        [Description("Serve quietly without opening a browser (used by autostart).")]
        public bool Background { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
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
}