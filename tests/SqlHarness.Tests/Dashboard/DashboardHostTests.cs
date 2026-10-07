using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardHostTests
{
    private sealed class RecordingBrowser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];

        /// <summary>Content of each opened local page, read at open time.</summary>
        public List<string> Pages { get; } = [];

        public void Open(Uri uri)
        {
            Opened.Add(uri);
            Pages.Add(uri.IsFile ? File.ReadAllText(uri.LocalPath) : string.Empty);
        }
    }

    private sealed class ThrowingBrowser : IBrowserLauncher
    {
        public void Open(Uri uri) => throw new InvalidOperationException("browser failed for " + uri);
    }

    private static string PagePath(TempHome home) => Path.Combine(home.Path, "dashboard-open.html");

    private static void AssertOpenedThroughPage(TempHome home, RecordingBrowser browser, Uri target)
    {
        var opened = Assert.Single(browser.Opened);
        Assert.True(opened.IsFile);
        Assert.Equal(Path.GetFullPath(PagePath(home)), Path.GetFullPath(opened.LocalPath));
        Assert.DoesNotContain(target.Query, opened.ToString());
        Assert.Contains(target.ToString(), Assert.Single(browser.Pages));
    }

    private static DashboardHostOptions Options(TempHome home, StringWriter output, StringWriter error, IBrowserLauncher browser,
        bool openBrowser = true, bool quiet = false, SqlHarnessConfigLoadResult? config = null) =>
        new(home.Path, home.DatabasePath, config ?? new SqlHarnessConfigLoadResult(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null),
            openBrowser, quiet, output, error, new FakeProcesses().Alive(Environment.ProcessId, null), browser)
        {
            PortOverride = 0,
        };

    [Fact]
    public async Task Run_starts_publishes_prints_opens_and_stops_on_cancel()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        var browser = new RecordingBrowser();
        using var cts = new CancellationTokenSource();
        RunningDashboard? started = null;

        var run = DashboardHost.RunAsync(Options(home, output, new StringWriter(), browser) with { Started = s => { started = s; cts.Cancel(); } }, cts.Token);
        var exit = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.NotNull(started);
        Assert.Contains(started!.OpenUri.ToString(), output.ToString());
        AssertOpenedThroughPage(home, browser, started.OpenUri);
        Assert.True(File.Exists(home.DatabasePath));
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
        Assert.False(File.Exists(PagePath(home)));
    }

    [Fact]
    public async Task Background_mode_prints_nothing_and_opens_nothing()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        var browser = new RecordingBrowser();
        using var cts = new CancellationTokenSource();

        var exit = await DashboardHost.RunAsync(
            Options(home, output, new StringWriter(), browser, openBrowser: false, quiet: true) with { Started = _ => cts.Cancel() },
            cts.Token).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task Second_invocation_reuses_the_running_dashboard()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var endpoint = new DashboardEndpoint(Environment.ProcessId, null, 47811, "existing-token");
        held.Publish(endpoint);
        var output = new StringWriter();
        var browser = new RecordingBrowser();

        var exit = await DashboardHost.RunAsync(Options(home, output, new StringWriter(), browser), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(endpoint.OpenUri.ToString(), output.ToString());
        AssertOpenedThroughPage(home, browser, endpoint.OpenUri);
        Assert.False(File.Exists(home.DatabasePath));
    }

    [Fact]
    public async Task Held_lock_without_a_published_address_exits_with_local_storage()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var error = new StringWriter();

        var exit = await DashboardHost.RunAsync(Options(home, new StringWriter(), error, new RecordingBrowser()), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("dashboard", error.ToString());
    }

    [Fact]
    public async Task Newer_journal_schema_refuses_to_serve()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var error = new StringWriter();

        var exit = await DashboardHost.RunAsync(Options(home, new StringWriter(), error, new RecordingBrowser()), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("newer", error.ToString());
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public async Task Older_journal_schema_with_the_journal_disabled_refuses_to_serve()
    {
        using var home = new TempHome();
        new JournalSeed(home.DatabasePath);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {JournalSchema.CurrentVersion - 1};";
            command.ExecuteNonQuery();
        }

        var error = new StringWriter();
        var browser = new RecordingBrowser();
        var config = new SqlHarnessConfigLoadResult(
            SqlHarnessConfig.Default with { Journal = new JournalConfig { Enabled = false } }, SqlHarnessConfigStatus.Valid, null);

        var exit = await DashboardHost.RunAsync(Options(home, new StringWriter(), error, browser, config: config), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("older", error.ToString());
        Assert.Empty(browser.Opened);
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public async Task Disabled_journal_still_serves_without_creating_the_database()
    {
        using var home = new TempHome();
        using var cts = new CancellationTokenSource();
        var config = new SqlHarnessConfigLoadResult(
            SqlHarnessConfig.Default with { Journal = new JournalConfig { Enabled = false } }, SqlHarnessConfigStatus.Valid, null);

        var exit = await DashboardHost.RunAsync(
            Options(home, new StringWriter(), new StringWriter(), new RecordingBrowser(), config: config) with { Started = _ => cts.Cancel() },
            cts.Token).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.False(File.Exists(home.DatabasePath));
    }

    [Fact]
    public async Task Publish_failure_exits_with_local_storage()
    {
        using var home = new TempHome();
        // A directory where the publish temp file goes makes Publish fail after the server started.
        var temp = Path.Combine(home.Path, "dashboard.json.tmp");
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, "blocker"), "x");
        var error = new StringWriter();
        var browser = new RecordingBrowser();

        var exit = await DashboardHost.RunAsync(Options(home, new StringWriter(), error, browser), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("dashboard", error.ToString());
        Assert.Empty(browser.Opened);
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
        using var again = DashboardLock.TryAcquire(home.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Cancel_while_waiting_for_the_running_dashboard_exits_normally()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var browser = new RecordingBrowser();

        var exit = await DashboardHost.RunAsync(Options(home, new StringWriter(), new StringWriter(), browser), cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.Success, exit);
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task Unusable_home_exits_with_local_storage()
    {
        using var home = new TempHome();
        // A file where the home directory should be cannot hold the lock.
        var blocked = Path.Combine(home.Path, "not-a-directory");
        File.WriteAllText(blocked, "x");
        var error = new StringWriter();
        var options = Options(home, new StringWriter(), error, new RecordingBrowser()) with { Home = blocked };

        var exit = await DashboardHost.RunAsync(options, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Contains("dashboard", error.ToString());
    }

    [Fact]
    public async Task Open_page_is_owner_only_and_redirects_to_the_token_url()
    {
        using var home = new TempHome();
        using var cts = new CancellationTokenSource();
        string? content = null;
        UnixFileMode? mode = null;

        var exit = await DashboardHost.RunAsync(
            Options(home, new StringWriter(), new StringWriter(), new RecordingBrowser()) with
            {
                Started = s =>
                {
                    content = File.ReadAllText(PagePath(home));
                    if (!OperatingSystem.IsWindows())
                        mode = File.GetUnixFileMode(PagePath(home));
                    Assert.Contains($"location.replace(\"{s.OpenUri}\")", content);
                    Assert.Contains($"<meta http-equiv=\"refresh\" content=\"0;url={s.OpenUri}\">", content);
                    cts.Cancel();
                },
            },
            cts.Token).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.NotNull(content);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        Assert.False(File.Exists(PagePath(home)));
    }

    [Fact]
    public async Task Host_shutdown_stops_the_dashboard_and_releases_it()
    {
        using var home = new TempHome();

        var exit = await DashboardHost.RunAsync(
            Options(home, new StringWriter(), new StringWriter(), new RecordingBrowser()) with { Started = s => s.RequestStop() },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
        Assert.False(File.Exists(PagePath(home)));
        using var again = DashboardLock.TryAcquire(home.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Lock_released_during_the_wait_is_taken_over()
    {
        using var home = new TempHome();
        var held = DashboardLock.TryAcquire(home.Path)!;
        using var cts = new CancellationTokenSource();
        RunningDashboard? started = null;
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            held.Dispose();
        });

        var exit = await DashboardHost.RunAsync(
            Options(home, new StringWriter(), new StringWriter(), new RecordingBrowser()) with { Started = s => { started = s; cts.Cancel(); } },
            cts.Token).WaitAsync(TimeSpan.FromSeconds(20));
        await release;

        Assert.Equal(0, exit);
        Assert.NotNull(started);
    }

    [Fact]
    public async Task Unexpected_failure_exits_with_local_storage_without_details()
    {
        using var home = new TempHome();
        var error = new StringWriter();
        RunningDashboard? started = null;

        var exit = await DashboardHost.RunAsync(
            Options(home, new StringWriter(), error, new ThrowingBrowser()) with { Started = s => started = s },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exit);
        Assert.Null(started);
        Assert.Contains("InvalidOperationException", error.ToString());
        Assert.DoesNotContain("browser failed", error.ToString());
        Assert.DoesNotContain("?t=", error.ToString());
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
        Assert.False(File.Exists(PagePath(home)));
    }
}