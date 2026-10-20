using System.Net;

using SqlHarness.Core;
using SqlHarness.Dashboard;
using SqlHarness.Dashboard.Transcripts;
using SqlHarness.Tests.Dashboard.Transcripts;

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

    private static DashboardHostOptions Options(TempHome home, TimeSpan? idle, TimeProvider time, Passes passes, SqlHarnessConfig? config = null, TextWriter? errors = null) =>
        new(home.Path, home.DatabasePath,
            new SqlHarnessConfigLoadResult(config ?? SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null),
            OpenBrowser: false, Quiet: true, TextWriter.Null, errors ?? TextWriter.Null,
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
    public async Task Own_retention_deletes_do_not_keep_the_background_dashboard_up()
    {
        using var home = new TempHome();
        // One operation per hour, so every hourly retention pass finds one more row past MaxAgeDays.
        var t0 = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        var writeTime = new Journal.FixedTimeProvider(t0);
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig(), TextWriter.Null, writeTime);
        for (var i = 0; i < 12; i++)
        {
            writeTime.Now = t0.AddHours(i);
            var handle = journal.Begin(Journal.JournalTestData.Session("cli:history"), Journal.JournalTestData.Start())!;
            journal.Complete(handle, Journal.JournalTestData.End());
        }

        var config = SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1 } },
        };
        var clock = new ManualClock(t0.AddDays(1).AddMinutes(30));
        var passes = new Passes();
        var run = DashboardHost.RunAsync(
            Options(home, TimeSpan.FromHours(3), clock, passes, config) with { RetentionInterval = TimeSpan.FromHours(1) },
            CancellationToken.None);
        await passes.MoreAsync();

        for (var step = 0; step < 8 && !run.IsCompleted; step++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await Task.WhenAny(run, passes.MoreAsync());
        }

        Assert.True(run.IsCompleted || await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))) == run,
            "retention deletes made by the dashboard itself are not activity");
        Assert.Equal(0, await run);
        var remaining = Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations").Count;
        Assert.InRange(remaining, 1, 10);
    }

    [Fact]
    public async Task Housekeeping_links_transcripts_when_the_setting_is_enabled()
    {
        using var home = new TempHome();
        using var agents = new TempHome();
        var homes = new AgentHomes(Path.Combine(agents.Path, "claude"), Path.Combine(agents.Path, "codex"));
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("mcp:transcript", "claude"), agent: new AgentCallMetadata("toolu_housekeep", null, null, null, null, null, null));
        var project = homes.ClaudeProjectDirectory("/work");
        Directory.CreateDirectory(project);
        File.WriteAllLines(Path.Combine(project, "conv-housekeep.jsonl"), [ClaudeFixtures.Assistant("conv-housekeep", "model", "high", ("toolu_housekeep", "mcp__sqlharness__query"))]);
        var config = SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with { AgentTranscripts = new AgentTranscriptsConfig { Enabled = true } },
        };
        var passes = new Passes();
        using var cts = new CancellationTokenSource();

        var run = DashboardHost.RunAsync(Options(home, idle: null, Clock(), passes, config) with { AgentHomes = homes }, cts.Token);
        await passes.MoreAsync();

        Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));
        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Journal_change_triggers_targeted_cycle_within_debounce()
    {
        using var home = new TempHome();
        using var agents = new TempHome();
        var homes = new AgentHomes(Path.Combine(agents.Path, "claude"), Path.Combine(agents.Path, "codex"));
        var seed = new JournalSeed(home.DatabasePath);
        var project = homes.ClaudeProjectDirectory("/work");
        Directory.CreateDirectory(project);
        File.WriteAllLines(Path.Combine(project, "conv-event.jsonl"), [ClaudeFixtures.Assistant("conv-event", "model", null, ("toolu_event", "mcp__sqlharness__query"))]);
        var config = SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with { AgentTranscripts = new AgentTranscriptsConfig { Enabled = true } },
        };
        var clock = Clock();
        var passes = new Passes();
        using var cts = new CancellationTokenSource();
        var run = DashboardHost.RunAsync(Options(home, idle: null, clock, passes, config) with { AgentHomes = homes }, cts.Token);
        await passes.MoreAsync();

        seed.Operation(JournalSeed.Session("mcp:event", "claude"), agent: new AgentCallMetadata("toolu_event", null, null, null, null, null, null));
        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await WaitForAsync(() => Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links").Count == 1);
        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Sweep_still_runs_every_60s()
    {
        using var home = new TempHome();
        using var agents = new TempHome();
        var homes = new AgentHomes(Path.Combine(agents.Path, "claude"), Path.Combine(agents.Path, "codex"));
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("mcp:sweep", "claude"), agent: new AgentCallMetadata("toolu_sweep", null, null, null, null, null, null));
        var config = SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with { AgentTranscripts = new AgentTranscriptsConfig { Enabled = true } },
        };
        var clock = Clock();
        var passes = new Passes();
        using var cts = new CancellationTokenSource();
        var run = DashboardHost.RunAsync(Options(home, idle: null, clock, passes, config) with { AgentHomes = homes }, cts.Token);
        await passes.MoreAsync();

        var project = homes.ClaudeProjectDirectory("/work");
        Directory.CreateDirectory(project);
        File.WriteAllLines(Path.Combine(project, "conv-sweep.jsonl"), [ClaudeFixtures.Assistant("conv-sweep", "model", null, ("toolu_sweep", "mcp__sqlharness__query"))]);
        clock.Advance(TimeSpan.FromSeconds(59));
        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links").Count == 1);
        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Journal_commit_between_version_sample_and_retention_snapshot_is_matched_and_refreshes_idle()
    {
        using var home = new TempHome();
        using var agents = new TempHome();
        var homes = new AgentHomes(Path.Combine(agents.Path, "claude"), Path.Combine(agents.Path, "codex"));
        var seed = new JournalSeed(home.DatabasePath);
        const string call = "toolu_retention_window";
        var project = homes.ClaudeProjectDirectory("/work");
        Directory.CreateDirectory(project);
        File.WriteAllLines(Path.Combine(project, "conv-retention-window.jsonl"),
            [ClaudeFixtures.Assistant("conv-retention-window", "model", null, (call, "mcp__sqlharness__query"))]);
        var config = SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with { AgentTranscripts = new AgentTranscriptsConfig { Enabled = true } },
            Journal = new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1 } },
        };
        var clock = Clock();
        var passes = new Passes();
        Action? armed = null;
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var run = DashboardHost.RunAsync(
            Options(home, TimeSpan.FromSeconds(55), clock, passes, config) with
            {
                AgentHomes = homes,
                RetentionInterval = TimeSpan.FromSeconds(30),
                BeforeRetentionSnapshot = () => Interlocked.Exchange(ref armed, null)?.Invoke(),
            },
            cts.Token);

        try
        {
            await passes.MoreAsync();
            Volatile.Write(ref armed, () =>
            {
                seed.Operation(JournalSeed.Session("mcp:retention-window", "claude"),
                    agent: new AgentCallMetadata(call, null, null, null, null, null, null));
                wrote.TrySetResult();
            });
            clock.Advance(TimeSpan.FromSeconds(50));
            await wrote.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await passes.MoreAsync();
            Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));

            clock.Advance(TimeSpan.FromMilliseconds(1500));
            await WaitForAsync(() => Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links").Count == 1);
            clock.Advance(TimeSpan.FromSeconds(4));
            await passes.MoreAsync();
            Assert.False(run.IsCompleted, "the commit during the snapshot window refreshes the idle deadline");
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public async Task Matcher_commits_do_not_keep_an_idle_dashboard_up()
    {
        using var home = new TempHome();
        using var agents = new TempHome();
        var homes = new AgentHomes(Path.Combine(agents.Path, "claude"), Path.Combine(agents.Path, "codex"));
        var seed = new JournalSeed(home.DatabasePath);
        var project = homes.ClaudeProjectDirectory("/work");
        Directory.CreateDirectory(project);
        var transcript = Path.Combine(project, "conv-idle.jsonl");
        var passCount = 0;
        var appendRequested = 1;
        const string call = "toolu_idle";
        void AppendTitle()
        {
            File.AppendAllLines(transcript, [ClaudeFixtures.Title("conv-idle", $"title-{Interlocked.Increment(ref passCount)}")]);
        }
        var session = JournalSeed.Session("mcp:idle-transcript", "claude");
        seed.Operation(session, agent: new AgentCallMetadata(call, null, null, null, null, null, null));
        seed.Operation(session, agent: new AgentCallMetadata("toolu_stays_pending", null, null, null, null, null, null));
        File.WriteAllLines(transcript, [ClaudeFixtures.Assistant("conv-idle", "model", null, (call, "mcp__sqlharness__query"))]);
        var config = SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with { AgentTranscripts = new AgentTranscriptsConfig { Enabled = true } },
        };
        var clock = Clock();
        var passes = new Passes();
        using var cts = new CancellationTokenSource();
        var run = DashboardHost.RunAsync(
            Options(home, TimeSpan.FromMinutes(2), clock, passes, config) with
            {
                AgentHomes = homes,
                Housekept = () =>
                {
                    if (Interlocked.Exchange(ref appendRequested, 0) != 0)
                        AppendTitle();
                    passes.Completed();
                },
            }, cts.Token);

        try
        {
            // The initial scan links the operation; only its following hook appends the first title.
            await WaitForAsync(() => Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links").Count == 1);
            await WaitForAsync(() => Volatile.Read(ref passCount) >= 1);

            // Housekeeping passes do not imply matcher scans. Move to the next 60-second sweep,
            // then wait for the committed value that proves it consumed the post-scan append.
            clock.Advance(TimeSpan.FromMinutes(1));
            await WaitForAsync(() =>
            {
                var conversations = Journal.JournalDb.Rows(home.DatabasePath, "SELECT title FROM agent_conversations");
                return conversations.Count == 1 && (string?)conversations[0]["title"] == "title-1";
            });
            Assert.False(run.IsCompleted, "the dashboard remains up before its original two-minute idle deadline");

            // Arm one more append after the observed commit; extra housekeeping passes do not
            // create title updates. The final eligible sweep commits it at the original deadline.
            Volatile.Write(ref appendRequested, 1);
            await WaitForAsync(() => Volatile.Read(ref passCount) >= 2);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal("title-2", (string?)Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT title FROM agent_conversations"))["title"]);
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public async Task External_journal_commit_during_matching_refreshes_the_idle_window()
    {
        using var home = new TempHome();
        using var agents = new TempHome();
        var homes = new AgentHomes(Path.Combine(agents.Path, "claude"), Path.Combine(agents.Path, "codex"));
        var seed = new JournalSeed(home.DatabasePath);
        var session = JournalSeed.Session("mcp:in-cycle", "claude");
        seed.Operation(session, agent: new AgentCallMetadata("toolu_stays_pending", null, null, null, null, null, null));
        var project = homes.ClaudeProjectDirectory("/work");
        Directory.CreateDirectory(project);
        File.WriteAllLines(Path.Combine(project, "conv-in-cycle.jsonl"), [ClaudeFixtures.Assistant("conv-in-cycle", "model", null, ("toolu_external_during_scan", "mcp__sqlharness__query"))]);

        var clock = Clock();
        var passes = new Passes();
        var triggerWrite = 0;
        var wroteDuringScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = 0;
        bool Probe(string directory)
        {
            if (Volatile.Read(ref triggerWrite) != 0 && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                seed.Operation(session, agent: new AgentCallMetadata("toolu_external_during_scan", null, null, null, null, null, null));
                wroteDuringScan.TrySetResult();
            }
            return Directory.Exists(directory);
        }

        var config = SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with { AgentTranscripts = new AgentTranscriptsConfig { Enabled = true } },
        };
        var errors = new StringWriter();
        var run = DashboardHost.RunAsync(Options(home, TimeSpan.FromMinutes(2), clock, passes, config, errors) with
        {
            AgentHomes = homes,
            AgentDirectoryProbe = Probe,
        }, CancellationToken.None);
        await passes.MoreAsync();
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(50)));
        Volatile.Write(ref triggerWrite, 1);
        await wroteDuringScan.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await passes.MoreAsync();
        Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));

        var nextPass = passes.MoreAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        var finished = await Task.WhenAny(run, nextPass);
        // Assert on the winner before awaiting nextPass: once the loop is down, MoreAsync can
        // never complete, so awaiting it first would time out and hide the host's own reason.
        var code = ReferenceEquals(finished, run) ? await run : -1;
        Assert.False(run.IsCompleted,
            $"the in-cycle external commit starts a new idle window; host exited with {code}: {errors}");
        await nextPass;

        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(50)));
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Disabled_matcher_does_not_probe_agent_directories()
    {
        using var home = new TempHome();
        using var agents = new TempHome();
        var homes = new AgentHomes(Path.Combine(agents.Path, "claude"), Path.Combine(agents.Path, "codex"));
        var passes = new Passes();
        using var cts = new CancellationTokenSource();
        var config = SqlHarnessConfig.Default with
        {
            Dashboard = SqlHarnessConfig.Default.Dashboard with { AgentTranscripts = new AgentTranscriptsConfig { Enabled = false } },
        };
        var run = DashboardHost.RunAsync(Options(home, idle: null, Clock(), passes, config) with
        {
            AgentHomes = homes,
            AgentDirectoryProbe = _ => throw new IOException("must not probe when disabled"),
        }, cts.Token);

        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));
        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task A_journal_write_just_before_a_retention_pass_still_counts_as_activity()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var config = SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true } },
        };
        var clock = Clock();
        var passes = new Passes();
        Action? armed = null;
        var run = DashboardHost.RunAsync(
            Options(home, Idle, clock, passes, config) with
            {
                RetentionInterval = TimeSpan.FromMinutes(30),
                // Runs on the housekeeping loop after its version check: the write lands after the
                // last check, and the advanced clock makes the very next pass run retention first.
                Housekept = () =>
                {
                    Interlocked.Exchange(ref armed, null)?.Invoke();
                    passes.Completed();
                },
            },
            CancellationToken.None);
        await passes.MoreAsync();

        Volatile.Write(ref armed, () =>
        {
            clock.Advance(TimeSpan.FromMinutes(50));
            seed.Operation(JournalSeed.Session("cli:busy"));
        });
        await passes.MoreAsync();
        Assert.Null(Volatile.Read(ref armed));

        clock.Advance(TimeSpan.FromMinutes(50));
        await Task.WhenAny(run, passes.MoreAsync());
        Assert.False(run.IsCompleted, "an agent write before a retention pass restarts the idle window");

        clock.Advance(TimeSpan.FromMinutes(10));
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

    [Fact]
    public async Task Retention_uses_settings_saved_after_start()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        seed.Operation(JournalSeed.Session("cli:old"));
        var configPath = Path.Combine(home.Path, "config.json");
        var clock = new ManualClock(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var passes = new Passes();
        using var cts = new CancellationTokenSource();

        // Started with retention off (defaults); the operator then enables it on the settings page.
        var run = DashboardHost.RunAsync(
            Options(home, idle: null, clock, passes) with { ConfigPath = configPath, RetentionInterval = TimeSpan.FromHours(1) },
            cts.Token);
        await passes.MoreAsync();
        Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));

        SqlHarnessConfigWriter.Write(configPath, SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 1 } },
        });
        clock.Advance(TimeSpan.FromHours(1));
        await passes.MoreAsync();
        Assert.Empty(Journal.JournalDb.Rows(home.DatabasePath, "SELECT id FROM operations"));

        cts.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
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