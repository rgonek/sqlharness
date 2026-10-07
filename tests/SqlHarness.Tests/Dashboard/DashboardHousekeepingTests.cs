using System.Net;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

/// <summary>
/// Idle decisions read a manual clock that only the test advances, so no test waits
/// for real idle windows. Real time only paces the housekeeping passes (milliseconds),
/// and tests wait for whole passes through the host's pass hook instead of sleeping.
/// </summary>
public sealed class DashboardHousekeepingTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private sealed class NoBrowser : IBrowserLauncher
    {
        public void Open(Uri uri) { }
    }

    /// <summary>A thread-safe clock that moves only when the test advances it; timers stay real.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
                _now += by;
        }
    }

    /// <summary>Counts completed housekeeping passes so a test can wait for passes that started after an event.</summary>
    private sealed class Passes
    {
        private readonly object _gate = new();
        private readonly List<(int Target, TaskCompletionSource Done)> _waiters = [];
        private int _count;

        public void Completed()
        {
            lock (_gate)
            {
                _count++;
                foreach (var waiter in _waiters.Where(w => _count >= w.Target).ToArray())
                {
                    waiter.Done.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
        }

        /// <summary>Waits for two more passes: one may already be running, the second starts after this call.</summary>
        public Task MoreAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _waiters.Add((_count + 2, done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private static DashboardHostOptions Options(TempHome home, TimeSpan? idle, TimeProvider time, Passes passes, SqlHarnessConfig? config = null) =>
        new(home.Path, home.DatabasePath,
            new SqlHarnessConfigLoadResult(config ?? SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null),
            OpenBrowser: false, Quiet: true, TextWriter.Null, TextWriter.Null,
            new FakeProcesses().Alive(Environment.ProcessId, null), new NoBrowser())
        {
            PortOverride = 0,
            IdleShutdown = idle,
            HousekeepingInterval = TimeSpan.FromMilliseconds(10),
            LivePollInterval = TimeSpan.FromMilliseconds(50),
            Time = time,
            Housekept = passes.Completed,
        };

    private static ManualClock Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Background_dashboard_exits_after_the_idle_window()
    {
        using var home = new TempHome();
        var clock = Clock();
        var passes = new Passes();

        var run = DashboardHost.RunAsync(Options(home, Idle, clock, passes), CancellationToken.None);
        await passes.MoreAsync();
        clock.Advance(Idle - TimeSpan.FromMinutes(1));
        await passes.MoreAsync();
        Assert.False(run.IsCompleted, "inside the idle window the dashboard stays up");

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
        using var again = DashboardLock.TryAcquire(home.Path);
        Assert.NotNull(again);
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public async Task Background_dashboard_stays_up_while_a_live_stream_is_open()
    {
        using var home = new TempHome();
        var clock = Clock();
        var passes = new Passes();
        RunningDashboard? server = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = DashboardHost.RunAsync(Options(home, Idle, clock, passes) with
        {
            Started = s => { server = s; started.SetResult(); },
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = server!.BaseUri, Timeout = Timeout.InfiniteTimeSpan };
        (await client.GetAsync($"/?t={server.Token}")).Dispose();
        var stream = await client.GetAsync("/api/live", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        await WaitForAsync(() => server.Activity.OpenStreams == 1);

        clock.Advance(Idle * 3);
        await passes.MoreAsync();
        Assert.False(run.IsCompleted, "an open live stream keeps the dashboard up");

        stream.Dispose();
        client.Dispose();
        await WaitForAsync(() => server.Activity.OpenStreams == 0);
        await passes.MoreAsync();
        Assert.False(run.IsCompleted, "closing the stream is activity and restarts the idle window");

        clock.Advance(Idle);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Requests_keep_the_background_dashboard_up()
    {
        using var home = new TempHome();
        var clock = Clock();
        var passes = new Passes();
        RunningDashboard? server = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = DashboardHost.RunAsync(Options(home, Idle, clock, passes) with
        {
            Started = s => { server = s; started.SetResult(); },
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        clock.Advance(TimeSpan.FromMinutes(50));
        using (var anonymous = new HttpClient { BaseAddress = server!.BaseUri })
            (await anonymous.GetAsync("/api/stats")).Dispose();
        await passes.MoreAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        // An unauthenticated request is not activity: the window still ends one hour after start.
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));

        using var home2 = new TempHome();
        clock = Clock();
        passes = new Passes();
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run = DashboardHost.RunAsync(Options(home2, Idle, clock, passes) with
        {
            Started = s => { server = s; started.SetResult(); },
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        clock.Advance(TimeSpan.FromMinutes(50));
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        using (var client = new HttpClient(handler) { BaseAddress = server!.BaseUri })
        {
            (await client.GetAsync($"/?t={server.Token}")).Dispose();
            using var stats = await client.GetAsync("/api/stats");
            Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        }

        clock.Advance(TimeSpan.FromMinutes(50));
        await passes.MoreAsync();
        Assert.False(run.IsCompleted, "an authenticated request restarts the idle window");

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Journal_writes_keep_the_background_dashboard_up()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var clock = Clock();
        var passes = new Passes();
        var run = DashboardHost.RunAsync(Options(home, Idle, clock, passes), CancellationToken.None);
        await passes.MoreAsync();

        clock.Advance(TimeSpan.FromMinutes(50));
        seed.Operation(JournalSeed.Session("cli:busy"));
        await passes.MoreAsync();
        clock.Advance(TimeSpan.FromMinutes(50));
        await passes.MoreAsync();
        Assert.False(run.IsCompleted, "journal writes count as activity");

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Foreground_dashboard_never_idle_exits()
    {
        using var home = new TempHome();
        var clock = Clock();
        var passes = new Passes();
        using var cts = new CancellationTokenSource();

        var run = DashboardHost.RunAsync(Options(home, idle: null, clock, passes), cts.Token);
        await passes.MoreAsync();
        clock.Advance(TimeSpan.FromDays(400));
        await passes.MoreAsync();

        Assert.False(run.IsCompleted);
        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Housekeeping_runs_retention_at_start_and_every_interval()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:old"));
        var config = SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1 } },
        };
        // JournalSeed writes at 2026-10-07; with MaxAgeDays = 1 a clock at 2026-12-01 makes those rows old.
        var clock = new ManualClock(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var passes = new Passes();
        using var cts = new CancellationTokenSource();

        var run = DashboardHost.RunAsync(Options(home, idle: null, clock, passes, config), cts.Token);
        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));

        seed.Operation(JournalSeed.Session("cli:old"));
        await passes.MoreAsync();
        Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));

        clock.Advance(TimeSpan.FromHours(1));
        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));

        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Housekeeping_skips_retention_when_it_is_disabled()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session("cli:old"));
        var clock = new ManualClock(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var passes = new Passes();
        using var cts = new CancellationTokenSource();

        var run = DashboardHost.RunAsync(Options(home, idle: null, clock, passes), cts.Token);
        await passes.MoreAsync();
        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            await Task.Delay(10);
        }
    }
}