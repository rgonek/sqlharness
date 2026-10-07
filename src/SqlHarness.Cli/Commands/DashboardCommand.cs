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
            stop.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await DashboardHost.RunAsync(
                new DashboardHostOptions(
                    SqlHarnessPaths.Home,
                    SqlHarnessPaths.ActivityDatabase,
                    SqlHarnessConfigLoader.Load(),
                    OpenBrowser: !settings.NoOpen && !settings.Background,
                    Quiet: settings.Background,
                    Console.Out,
                    Console.Error,
                    ProcessInfo.Current,
                    new SystemBrowserLauncher()),
                stop.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}