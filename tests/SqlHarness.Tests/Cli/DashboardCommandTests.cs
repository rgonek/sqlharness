using System.Diagnostics;
using System.Net;
using System.Net.Http;

using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Dashboard;
using SqlHarness.Tests.Dashboard;

namespace SqlHarness.Tests.Cli;

public sealed class DashboardCommandTests
{
    [Theory]
    [InlineData("--stop --restart")]
    [InlineData("--stop --background")]
    [InlineData("--restart --background")]
    [InlineData("--stop --no-open")]
    public async Task Invalid_lifecycle_flag_combinations_exit_with_safety(string arguments)
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        var lifecycle = new RecordingLifecycle(new DashboardStopResult(DashboardStopStatus.AlreadyStopped, null));
        var launcher = new RecordingLauncher();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Create(lifecycle, launcher, output, error)
            .RunAsync(["dashboard", .. arguments.Split(' ')]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.NotEmpty(error.ToString());
        Assert.Null(lifecycle.Home);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task Stop_of_an_already_stopped_dashboard_succeeds_without_launching()
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        var launcher = new RecordingLauncher();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output, planStdin: new MemoryStream(), mcpError: error, dashboardLauncher: launcher)
            .RunAsync(["dashboard", "--stop"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(launcher.Launches);
        Assert.Empty(error.ToString());
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard-open.html")));
    }

    [Theory]
    [InlineData("--stop")]
    [InlineData("--restart")]
    public async Task Stop_failure_exits_with_local_storage_and_does_not_launch(string option)
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        var lifecycle = new RecordingLifecycle(new DashboardStopResult(DashboardStopStatus.Failed, "Dashboard PID 123 could not be stopped."));
        var launcher = new RecordingLauncher();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Create(lifecycle, launcher, output, error).RunAsync(["dashboard", option]);

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exitCode);
        Assert.Empty(launcher.Launches);
        Assert.Empty(output.ToString());
        Assert.Contains("PID 123", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_after_successful_stop_does_not_launch()
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new RecordingLifecycle(
            new DashboardStopResult(DashboardStopStatus.Stopped, null), cancelOnStop: cancellation);
        var launcher = new RecordingLauncher();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Create(lifecycle, launcher, output, error)
            .RunAsync(["dashboard", "--restart", "--no-open"], cancellation.Token);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Empty(launcher.Launches);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Cancellation_after_publication_does_not_announce_or_open_browser()
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        using var cancellation = new CancellationTokenSource();
        var launchedAt = DateTimeOffset.UtcNow;
        var identity = new DashboardLaunchIdentity(8123, launchedAt);
        var lifecycle = new RecordingLifecycle(
            new DashboardStopResult(DashboardStopStatus.AlreadyStopped, null),
            new DashboardEndpoint(identity.Pid, launchedAt, 47800, "cancelled-test-token"),
            cancelOnWait: cancellation);
        var launcher = new RecordingLauncher(identity);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Create(lifecycle, launcher, output, error)
            .RunAsync(["dashboard", "--restart", "--no-open"], cancellation.Token);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        Assert.Single(launcher.Launches);
        Assert.Empty(output.ToString());
        Assert.Empty(error.ToString());
        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard-open.html")));
    }

    [Fact]
    public async Task Restart_launches_once_in_effective_home_and_announces_only_its_publication()
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        var launchedAt = DateTimeOffset.UtcNow;
        var identity = new DashboardLaunchIdentity(8123, launchedAt);
        var lifecycle = new RecordingLifecycle(
            new DashboardStopResult(DashboardStopStatus.AlreadyStopped, null),
            new DashboardEndpoint(identity.Pid, launchedAt.AddSeconds(1), 47800, "this-is-a-test-token-not-a-secret"));
        var launcher = new RecordingLauncher(identity);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Create(lifecycle, launcher, output, error).RunAsync(["dashboard", "--restart", "--no-open"]);

        Assert.Equal((int)SqlHarnessExitCode.Success, exitCode);
        var launch = Assert.Single(launcher.Launches);
        Assert.Equal(DashboardLaunchCommand.Current(), launch.Command);
        Assert.Equal(["dashboard", "--background"], launch.Command.Arguments.TakeLast(2));
        Assert.Equal(home.Path, launch.WorkingDirectory);
        Assert.Equal(home.Path, lifecycle.Home);
        Assert.Equal(TimeSpan.FromSeconds(10), lifecycle.StopTimeout);
        Assert.InRange(lifecycle.WaitTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
        Assert.Contains("127.0.0.1:47800", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Restart_does_not_announce_a_concurrent_dashboard_publication()
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        var launcher = new RecordingLauncher(new DashboardLaunchIdentity(8123, DateTimeOffset.UtcNow));
        var lifecycle = new RecordingLifecycle(
            new DashboardStopResult(DashboardStopStatus.AlreadyStopped, null),
            new DashboardEndpoint(9999, DateTimeOffset.UtcNow, 47800, "concurrent-test-token"));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await Create(lifecycle, launcher, output, error).RunAsync(["dashboard", "--restart", "--no-open"]);

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("not the requested process", error.ToString(), StringComparison.Ordinal);
        Assert.Single(launcher.Launches);
    }

    [Fact]
    public async Task Restart_launch_failure_and_missing_publication_return_local_storage()
    {
        using var home = new TempHome();
        using var scope = new HomeScope(home.Path);
        var failedLauncher = new RecordingLauncher(failure: new InvalidOperationException("private launch detail"));
        var noPublishLifecycle = new RecordingLifecycle(new DashboardStopResult(DashboardStopStatus.AlreadyStopped, null));
        var failedOutput = new StringWriter();
        var failedError = new StringWriter();

        var launchExit = await Create(noPublishLifecycle, failedLauncher, failedOutput, failedError).RunAsync(["dashboard", "--restart", "--no-open"]);

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, launchExit);
        Assert.DoesNotContain("private launch detail", failedError.ToString(), StringComparison.Ordinal);

        var successfulLauncher = new RecordingLauncher(new DashboardLaunchIdentity(8123, DateTimeOffset.UtcNow));
        var noPublication = new RecordingLifecycle(new DashboardStopResult(DashboardStopStatus.AlreadyStopped, null));
        var output = new StringWriter();
        var error = new StringWriter();
        var publishExit = await Create(noPublication, successfulLauncher, output, error).RunAsync(["dashboard", "--restart", "--no-open"]);

        Assert.Equal((int)SqlHarnessExitCode.LocalStorage, publishExit);
        Assert.InRange(noPublication.WaitTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
        Assert.Empty(output.ToString());
        Assert.Contains("did not publish its address", error.ToString(), StringComparison.Ordinal);
    }

    [ProcessReaderFact]
    public async Task Restart_acceptance_replaces_only_the_isolated_home_instance_and_serves_authenticated_api()
    {
        using var home = new TempHome();
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        using var oldProcess = StartCli(cliAssembly, home.Path, "dashboard", "--background");
        try
        {
            var oldEndpoint = await WaitForEndpoint(home.Path, TimeSpan.FromSeconds(10));
            Assert.NotNull(oldEndpoint);

            using var restart = StartCli(cliAssembly, home.Path, "dashboard", "--restart", "--no-open");
            var stdout = await restart.StandardOutput.ReadToEndAsync();
            var stderr = await restart.StandardError.ReadToEndAsync();
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));

            Assert.True(restart.ExitCode == (int)SqlHarnessExitCode.Success, stderr);
            var newEndpoint = await WaitForEndpoint(home.Path, TimeSpan.FromSeconds(5));
            Assert.NotNull(newEndpoint);
            Assert.NotEqual(oldEndpoint!.Pid, newEndpoint!.Pid);
            Assert.NotEqual(oldEndpoint.StartedAt, newEndpoint.StartedAt);
            Assert.Contains($"127.0.0.1:{newEndpoint.Port}", stdout, StringComparison.Ordinal);

            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{newEndpoint.Port}") };
            using var exchange = await client.GetAsync($"/?t={newEndpoint.Token}");
            Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
            using var api = await client.GetAsync("/api/sessions");
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        }
        finally
        {
            await DashboardLifecycle.StopAsync(home.Path, ProcessInfo.Current, TimeSpan.FromSeconds(3), CancellationToken.None);
            if (!oldProcess.HasExited)
                await oldProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--json")]
    [InlineData("extra")]
    public async Task Invalid_dashboard_arguments_exit_with_safety(string argument)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output, planStdin: new MemoryStream(), mcpError: error)
            .RunAsync(["dashboard", argument]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal("sqlharness: Invalid command line arguments.", error.ToString().Trim());
    }

    private sealed class NoopModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
    }

    private sealed class RecordingLauncher : IDashboardLauncher
    {
        private readonly DashboardLaunchIdentity _identity;
        private readonly Exception? _failure;

        public RecordingLauncher(DashboardLaunchIdentity? identity = null, Exception? failure = null)
        {
            _identity = identity ?? new DashboardLaunchIdentity(Environment.ProcessId, DateTimeOffset.UtcNow);
            _failure = failure;
        }

        public List<(DashboardLaunchCommand Command, string WorkingDirectory)> Launches { get; } = [];

        public DashboardLaunchIdentity Launch(DashboardLaunchCommand command, string workingDirectory)
        {
            if (_failure is not null)
                throw _failure;
            Launches.Add((command, workingDirectory));
            return _identity;
        }
    }

    private sealed class RecordingLifecycle(
        DashboardStopResult stopResult,
        DashboardEndpoint? endpoint = null,
        CancellationTokenSource? cancelOnStop = null,
        CancellationTokenSource? cancelOnWait = null) : IDashboardLifecycle
    {
        public string? Home { get; private set; }
        public TimeSpan StopTimeout { get; private set; }
        public TimeSpan WaitTimeout { get; private set; }

        public Task<DashboardStopResult> StopAsync(string home, TimeSpan timeout, CancellationToken ct)
        {
            Home = home;
            StopTimeout = timeout;
            cancelOnStop?.Cancel();
            return Task.FromResult(stopResult);
        }

        public Task<DashboardEndpoint?> WaitForRunningAsync(string home, TimeSpan timeout, CancellationToken ct)
        {
            Home = home;
            WaitTimeout = timeout;
            cancelOnWait?.Cancel();
            return Task.FromResult(endpoint);
        }
    }

    private static SqlHarnessApp Create(RecordingLifecycle lifecycle, RecordingLauncher launcher, TextWriter output, TextWriter error) =>
        SqlHarnessCli.Create(new NoopModule(), output, planStdin: new MemoryStream(), mcpError: error,
            dashboardLauncher: launcher, dashboardLifecycle: lifecycle);

    private static Process StartCli(string assembly, string home, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["SQLHARNESS_HOME"] = home;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated CLI process.");
    }

    private static async Task<DashboardEndpoint?> WaitForEndpoint(string home, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (DashboardLock.ReadRunning(home, ProcessInfo.Current) is { } endpoint)
                return endpoint;
            await Task.Delay(50);
        }
        return null;
    }

    private sealed class HomeScope : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");

        public HomeScope(string home) => Environment.SetEnvironmentVariable("SQLHARNESS_HOME", home);

        public void Dispose() => Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _old);
    }
}