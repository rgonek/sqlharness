using System.Net.Sockets;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardHostOptions(
    string Home,
    string DatabasePath,
    SqlHarnessConfigLoadResult Config,
    bool OpenBrowser,
    bool Quiet,
    TextWriter Output,
    TextWriter Error,
    IProcessInfo Processes,
    IBrowserLauncher Browser)
{
    /// <summary>Overrides config dashboard.port; 0 binds an ephemeral port (tests).</summary>
    public int? PortOverride { get; init; }

    public TimeSpan LivePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Test hook invoked once the server is listening and published.</summary>
    public Action<RunningDashboard>? Started { get; init; }
}

/// <summary>
/// One <c>sqlharness dashboard</c> invocation: reuse a running dashboard of this
/// home, or acquire the lock, migrate the journal, serve on loopback, publish the
/// endpoint, and run until cancelled. Local storage failures exit 6 and
/// cancellation (Ctrl+C) exits 0; neither escapes as an unhandled exception.
/// </summary>
public static class DashboardHost
{
    public static readonly TimeSpan ExistingInstanceWait = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(DashboardHostOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Config.Warning is not null)
            options.Error.WriteLine(options.Config.Warning);

        try
        {
            return await RunCoreAsync(options, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (int)SqlHarnessExitCode.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            options.Error.WriteLine("sqlharness: the dashboard could not use its lock or address file in the SQLHarness home.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }
    }

    private static async Task<int> RunCoreAsync(DashboardHostOptions options, CancellationToken ct)
    {
        using var held = DashboardLock.TryAcquire(options.Home);
        if (held is null)
        {
            var existing = await DashboardLock.WaitForRunningAsync(options.Home, options.Processes, ExistingInstanceWait, ct);
            if (existing is null)
            {
                options.Error.WriteLine("sqlharness: another dashboard holds the lock but has not published its address.");
                return (int)SqlHarnessExitCode.LocalStorage;
            }

            Announce(options, existing.OpenUri);
            return (int)SqlHarnessExitCode.Success;
        }

        if (options.Config.Config.Journal.Enabled)
            _ = ActivityJournal.Open(options.DatabasePath, options.Config.Config.Journal, TextWriter.Null, TimeProvider.System);
        var reader = new JournalReader(options.DatabasePath, options.Processes);
        long? version;
        try
        {
            version = reader.SchemaVersion();
        }
        catch (SqliteException)
        {
            options.Error.WriteLine("sqlharness: the dashboard could not read the activity journal.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }

        if (version > JournalSchema.CurrentVersion)
        {
            options.Error.WriteLine("sqlharness: the activity journal schema is newer than this sqlharness; upgrade sqlharness to open the dashboard.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }

        RunningDashboard running;
        try
        {
            running = await DashboardServer.StartAsync(
                new DashboardServerOptions(options.DatabasePath, options.PortOverride ?? options.Config.Config.Dashboard.Port, options.Processes)
                {
                    LivePollInterval = options.LivePollInterval,
                },
                ct);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            options.Error.WriteLine("sqlharness: the dashboard could not bind a loopback port.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }

        await using (running)
        {
            var self = options.Processes.Get(Environment.ProcessId);
            try
            {
                held.Publish(new DashboardEndpoint(Environment.ProcessId, self?.StartedAt, running.Port, running.Token));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                options.Error.WriteLine("sqlharness: the dashboard could not publish its address in the SQLHarness home.");
                return (int)SqlHarnessExitCode.LocalStorage;
            }

            Announce(options, running.OpenUri);
            options.Started?.Invoke(running);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return (int)SqlHarnessExitCode.Success;
    }

    private static void Announce(DashboardHostOptions options, Uri uri)
    {
        if (!options.Quiet)
        {
            options.Output.WriteLine($"SQLHarness dashboard: {uri}");
            options.Output.Flush();
        }

        if (options.OpenBrowser)
            options.Browser.Open(uri);
    }
}