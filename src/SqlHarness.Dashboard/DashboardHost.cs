using System.Net.Sockets;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Core.Journal;
using SqlHarness.Dashboard.Transcripts;

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

    /// <summary>How long to wait for another dashboard to publish before taking the lock over.</summary>
    public TimeSpan ExistingInstanceWait { get; init; } = DashboardHost.ExistingInstanceWait;

    /// <summary>Test hook invoked once the server is listening and published.</summary>
    public Action<RunningDashboard>? Started { get; init; }

    /// <summary>Exit after this long without journal writes, requests or open live streams; null never (foreground).</summary>
    public TimeSpan? IdleShutdown { get; init; }

    public TimeSpan HousekeepingInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan RetentionInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>config.json to re-read before each retention pass; null keeps <see cref="Config"/> (tests).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Clock for retention ages, idle decisions and housekeeping delays.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Test hook invoked after each housekeeping pass that did not end the dashboard.</summary>
    internal Action? Housekept { get; init; }

    internal AgentHomes? AgentHomes { get; init; }

    internal Func<string, bool>? AgentDirectoryProbe { get; init; }
}

/// <summary>
/// One <c>sqlharness dashboard</c> invocation: reuse a running dashboard of this
/// home, or acquire the lock, migrate the journal, serve on loopback, publish the
/// endpoint, and run until cancelled. Local storage failures exit 6 and
/// cancellation (Ctrl+C) exits 0; neither escapes as an unhandled exception.
/// </summary>
public static class DashboardHost
{
    /// <summary>Default budget for another dashboard of the same home to publish its address.</summary>
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
        catch (Exception exception)
        {
            // Last resort: report and exit instead of crashing. Only the type name, because
            // messages can carry paths or the token URL.
            options.Error.WriteLine($"sqlharness: the dashboard stopped unexpectedly ({exception.GetType().Name}).");
            return (int)SqlHarnessExitCode.LocalStorage;
        }
    }

    private static async Task<int> RunCoreAsync(DashboardHostOptions options, CancellationToken ct)
    {
        var acquired = DashboardLock.TryAcquire(options.Home);
        if (acquired is null)
        {
            var existing = await DashboardLock.WaitForRunningAsync(options.Home, options.Processes, options.ExistingInstanceWait, ct);
            if (existing is not null)
            {
                DashboardAnnouncement.Announce(options.Home, existing.OpenUri, options.Output, options.Error, options.OpenBrowser, options.Browser, options.Quiet);
                return (int)SqlHarnessExitCode.Success;
            }

            // The holder may have exited during the wait without publishing; take over if so.
            acquired = DashboardLock.TryAcquire(options.Home);
            if (acquired is null)
            {
                options.Error.WriteLine("sqlharness: another dashboard holds the lock but has not published its address.");
                return (int)SqlHarnessExitCode.LocalStorage;
            }
        }

        using var held = acquired;

        var mutationJournal = options.Config.Config.Journal.Enabled
            ? ActivityJournal.Open(options.DatabasePath, options.Config.Config.Journal, TextWriter.Null, options.Time)
            : NullActivityJournal.Instance;
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

        // Older means the journal was not migrated (disabled, or the migration failed): every
        // read would fail, so refuse instead of serving a dashboard whose API only returns 500.
        if (version < JournalSchema.CurrentVersion)
        {
            options.Error.WriteLine(options.Config.Config.Journal.Enabled
                ? "sqlharness: the activity journal schema is older than this sqlharness and could not be upgraded; the dashboard cannot read it."
                : "sqlharness: the activity journal schema is older than this sqlharness and was not upgraded because the journal is disabled; enable the journal to upgrade it.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }

        string token;
        try
        {
            token = DashboardToken.LoadOrCreate(options.Home);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            options.Error.WriteLine("sqlharness: the dashboard could not read or create its token in the SQLHarness home.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }

        var activity = new DashboardActivity(options.Time);
        RunningDashboard running;
        try
        {
            running = await DashboardServer.StartAsync(
                new DashboardServerOptions(options.DatabasePath, options.PortOverride ?? options.Config.Config.Dashboard.Port, options.Processes)
                {
                    LivePollInterval = options.LivePollInterval,
                    Activity = activity,
                    Token = token,
                    ConfigPath = options.ConfigPath ?? Path.Combine(options.Home, "config.json"),
                    TargetsPath = Path.Combine(options.Home, "targets.json"),
                    PricesPath = Path.Combine(options.Home, "data", "model-prices.json"),
                    Time = options.Time,
                    AgentHomes = options.AgentHomes ?? AgentHomes.Current(),
                    AgentDirectoryProbe = options.AgentDirectoryProbe,
                    MutationJournal = mutationJournal,
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
            try
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

                DashboardAnnouncement.Announce(options.Home, running.OpenUri, options.Output, options.Error, options.OpenBrowser, options.Browser, options.Quiet);
                options.Started?.Invoke(running);

                // Ctrl+C cancels ct; SIGTERM/SIGQUIT reach the host lifetime and signal Stopping.
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, running.Stopping);
                try
                {
                    await HousekeepAsync(options, activity, running, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
            finally
            {
                // The page holds the token, so it lives no longer than this server.
                DashboardOpenPage.Delete(options.Home);
            }
        }

        return (int)SqlHarnessExitCode.Success;
    }

    /// <summary>
    /// Runs retention at start and every RetentionInterval, and in background mode returns
    /// once nothing happened for IdleShutdown: no journal commit other than its own retention
    /// (PRAGMA data_version on a held journal connection), no authenticated request, and no
    /// open live stream.
    /// </summary>
    private static async Task HousekeepAsync(DashboardHostOptions options, DashboardActivity activity, RunningDashboard running, CancellationToken ct)
    {
        DateTimeOffset? lastRetention = null;
        var lastJournalChange = options.Time.GetUtcNow();
        long? lastVersion = null;
        SqliteConnection? watch = null;

        // PRAGMA data_version changes for other connections. Matching uses this same connection,
        // so SQLite excludes matcher-owned commits while retaining external commits during a cycle.
        long? ReadVersion()
        {
            try
            {
                watch ??= running.Transcripts.OpenJournalConnection();
                if (watch is null)
                    return null;
                using var command = watch.CreateCommand();
                command.CommandText = "PRAGMA data_version;";
                return (long)command.ExecuteScalar()!;
            }
            catch (SqliteException)
            {
                // An unreadable journal is no activity; reopen on the next pass.
                watch?.Dispose();
                watch = null;
                return null;
            }
        }

        try
        {
            while (true)
            {
                var now = options.Time.GetUtcNow();
                // Settings saved from the settings page apply from the next pass; an invalid file means defaults.
                var config = options.ConfigPath is null ? options.Config.Config : SqlHarnessConfigLoader.Load(options.ConfigPath).Config;
                if (lastRetention is not { } last || now - last >= options.RetentionInterval)
                {
                    lastRetention = now;
                    var journal = config.Journal;
                    if (journal.Enabled && journal.Retention.Enabled)
                    {
                        // Agent commits since the last check would be hidden by the post-retention
                        // baseline: count them before retention runs.
                        var before = ReadVersion();
                        if (before is not null && lastVersion is not null && before != lastVersion)
                            lastJournalChange = now;
                        // Never throws; a failed pass is retried at the next interval. Ctrl+C waits for an
                        // in-flight pass, which its short delete batches keep brief.
                        await Task.Run(() => JournalRetention.Run(options.DatabasePath, journal, options.Processes, options.Time), ct);
                        // Retention's own commits are not activity: take the version after it as the baseline.
                        lastVersion = ReadVersion();
                    }
                }

                if (config.Dashboard.AgentTranscripts.Enabled)
                {
                    // Count agent commits since the last check before the matcher's own commits hide them.
                    var beforeMatching = ReadVersion();
                    if (beforeMatching is not null && lastVersion is not null && beforeMatching != lastVersion)
                        lastJournalChange = now;
                    if (watch is not null)
                        await Task.Run(() => running.Transcripts.RunCycle(enabled: true, journalConnection: watch), ct);
                }

                var version = ReadVersion();
                if (version is not null && lastVersion is not null && version != lastVersion)
                    lastJournalChange = now;
                lastVersion = version;

                if (options.IdleShutdown is { } idle && activity.OpenStreams == 0)
                {
                    var lastActivity = lastJournalChange > activity.LastRequest ? lastJournalChange : activity.LastRequest;
                    if (now - lastActivity >= idle)
                        return;
                }

                options.Housekept?.Invoke();
                await Task.Delay(options.HousekeepingInterval, options.Time, ct);
            }
        }
        finally
        {
            watch?.Dispose();
        }
    }

}

public static class DashboardAnnouncement
{
    public static void Announce(string home, Uri uri, TextWriter output, TextWriter error, bool openBrowser, IBrowserLauncher browser, bool quiet = false)
    {
        if (!quiet)
        {
            output.WriteLine($"SQLHarness dashboard: {uri}");
            output.Flush();
        }

        if (!openBrowser)
            return;
        Uri page;
        try
        {
            page = DashboardOpenPage.Write(home, uri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Never fall back to launching the token URL: its arguments are visible to other users.
            error.WriteLine("sqlharness: could not prepare the browser page; open the URL above.");
            return;
        }

        browser.Open(page);
    }
}
