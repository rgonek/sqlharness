using System.Net;
using System.Net.Sockets;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardLifecycleTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private sealed class NoopBrowser : IBrowserLauncher
    {
        public void Open(Uri uri) { }
    }

    [Fact]
    public async Task No_holder_returns_AlreadyStopped()
    {
        using var home = new TempHome();

        var result = await DashboardLifecycle.StopAsync(home.Path, new FakeProcesses(), TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.AlreadyStopped, result.Status);
    }

    [Fact]
    public async Task Published_host_returns_Stopped_and_releases_the_lock()
    {
        using var home = new TempHome();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processes = new FakeProcesses().Alive(Environment.ProcessId, new DateTimeOffset(self.StartTime));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<RunningDashboard>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = DashboardHost.RunAsync(
            new DashboardHostOptions(home.Path, home.DatabasePath,
                new SqlHarnessConfigLoadResult(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null),
                OpenBrowser: false, Quiet: true, TextWriter.Null, TextWriter.Null, processes,
                new NoopBrowser())
            {
                PortOverride = 0,
                Started = server =>
                {
                    server.Stopping.Register(processes.Clear);
                    started.SetResult(server);
                },
            }, cancellation.Token);

        try
        {
            _ = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(3), CancellationToken.None);
            var exit = await host.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(DashboardStopStatus.Stopped, result.Status);
            Assert.Equal((int)SqlHarnessExitCode.Success, exit);
            using var reacquired = DashboardLock.TryAcquire(home.Path);
            Assert.NotNull(reacquired);
        }
        finally
        {
            cancellation.Cancel();
            await host;
        }
    }

    [Fact]
    public async Task Held_unpublished_lock_returns_Failed_by_timeout()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;

        var result = await DashboardLifecycle.StopAsync(home.Path, new FakeProcesses(), TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Corrupt_metadata_with_held_lock_returns_Failed_by_timeout()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        File.WriteAllText(Path.Combine(home.Path, "dashboard.json"), "not-json");

        var result = await DashboardLifecycle.StopAsync(home.Path, new FakeProcesses(), TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Stale_pid_with_held_lock_fails_without_contacting_its_endpoint()
    {
        using var home = new TempHome();
        using var endpointServer = new StatusServer(HttpStatusCode.Accepted);
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, endpointServer.Port, "stale-token"));

        var result = await DashboardLifecycle.StopAsync(home.Path, new FakeProcesses(), TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.Empty(endpointServer.RequestText);
    }

    [Fact]
    public async Task Stale_start_time_with_held_lock_fails_without_contacting_its_endpoint()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started.AddHours(1));
        using var endpointServer = new StatusServer(HttpStatusCode.Accepted);
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, endpointServer.Port, "stale-start-token"));

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.Empty(endpointServer.RequestText);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Old_or_unsupported_endpoint_returns_Failed_with_pid_guidance(HttpStatusCode status)
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started);
        using var responseServer = new StatusServer(status);
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, responseServer.Port, "lifecycle-test-token"));

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.Contains("4242", result.Error);
        Assert.Contains("manually", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unauthorized_shutdown_returns_Failed_without_leaking_token()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started);
        await using var dashboard = await StartPublishedAsync(home, processes, port: 0, token: "actual-secret-token", publishedToken: "wrong-secret-token");

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.DoesNotContain("wrong-secret-token", result.Error);
    }

    [Fact]
    public async Task Connection_failure_returns_Failed_without_leaking_token()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started);
        var port = UnusedPort();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, port, "never-in-url-token"));

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.DoesNotContain("never-in-url-token", result.Error);
    }

    [Fact]
    public async Task Shutdown_uses_guarded_loopback_post_without_token_in_url_and_does_not_follow_redirects()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        using var redirect = new StatusServer(HttpStatusCode.Redirect, location: $"http://127.0.0.1:{destinationPort}/stolen");
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, redirect.Port, "private-stop-token"));

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.DoesNotContain("private-stop-token", redirect.RequestText.Split("\r\n", StringSplitOptions.None)[0]);
        Assert.Contains("POST /api/shutdown HTTP/1.1", redirect.RequestText);
        Assert.Contains($"Host: 127.0.0.1:{redirect.Port}", redirect.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cookie: sqlharness_dashboard=private-stop-token", redirect.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-SqlHarness-Dashboard: 1", redirect.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-SqlHarness-Dashboard-Pid: 4242", redirect.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"X-SqlHarness-Dashboard-StartedAtTicks: {Started.UtcTicks}", redirect.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Type: application/json", redirect.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Length: 2", redirect.RequestText, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("{}", redirect.RequestText);
        Assert.DoesNotContain("private-stop-token", result.Error);
        Assert.False(destination.Pending());
    }

    [Fact]
    public async Task Request_timeout_returns_Failed_within_the_shared_deadline()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started);
        using var slowServer = new StatusServer(HttpStatusCode.Accepted, delay: TimeSpan.FromSeconds(2));
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, slowServer.Port, "timeout-token"));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("timeout-token", result.Error);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_during_the_shutdown_request()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started);
        using var slowServer = new StatusServer(HttpStatusCode.Accepted, delay: TimeSpan.FromSeconds(2));
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, slowServer.Port, "cancel-token"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(3), cancellation.Token));
    }

    [Fact]
    public void Production_http_handler_disables_proxy_and_redirects()
    {
        using var handler = DashboardLifecycle.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Replacement_endpoint_returns_Failed_and_is_not_contacted()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started).Alive(4343, Started.AddMinutes(1));
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, 30001, "captured-token"));
        DashboardLock? replacementLock = null;
        using var handler = new CallbackHandler(() =>
        {
            held.Dispose();
            replacementLock = DashboardLock.TryAcquire(home.Path)!;
            replacementLock.Publish(new DashboardEndpoint(4343, Started.AddMinutes(1), 30002, "replacement-token"));
        });

        DashboardStopResult result;
        try
        {
            result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(1), handler, CancellationToken.None);
        }
        finally
        {
            replacementLock?.Dispose();
        }

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
        Assert.Contains("replacement", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Reused_port_replacement_rejects_captured_identity_and_remains_live()
    {
        using var home = new TempHome();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var currentPid = Environment.ProcessId;
        var originalStarted = new DateTimeOffset(self.StartTime);
        var replacementStarted = originalStarted.AddMinutes(1);
        var processes = new FakeProcesses().Alive(currentPid, originalStarted);
        const string token = "same-persistent-home-token";
        var original = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, 0, processes) { Token = token }, CancellationToken.None);
        DashboardLock? originalLock = DashboardLock.TryAcquire(home.Path);
        originalLock!.Publish(new DashboardEndpoint(currentPid, originalStarted, original.Port, token));
        RunningDashboard? replacement = null;
        DashboardLock? replacementLock = null;
        using var handler = new ReusedPortReplacementHandler(async () =>
        {
            await original.DisposeAsync();
            originalLock.Dispose();
            originalLock = null;

            processes.Alive(currentPid, replacementStarted);
            replacement = await DashboardServer.StartAsync(
                new DashboardServerOptions(home.DatabasePath, original.Port, processes) { Token = token }, CancellationToken.None);
            replacementLock = DashboardLock.TryAcquire(home.Path)!;
            replacementLock.Publish(new DashboardEndpoint(currentPid, replacementStarted, replacement.Port, token));
        });

        try
        {
            var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(3), handler, CancellationToken.None);

            Assert.Equal(DashboardStopStatus.Failed, result.Status);
            Assert.Contains("replacement", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(replacement);
            Assert.Equal(original.Port, replacement!.Port);
            Assert.True(handler.ResponseStatus.HasValue, handler.Failure?.ToString() ?? "The replacement request received no HTTP response.");
            Assert.Equal(HttpStatusCode.Conflict, handler.ResponseStatus);
            Assert.False(replacement!.Stopping.IsCancellationRequested);
            Assert.False(replacement.Stopping.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)));
            using var verify = new HttpClient { BaseAddress = replacement.BaseUri };
            verify.DefaultRequestHeaders.Add("Cookie", $"{DashboardSecurity.CookieName}={token}");
            Assert.Equal(HttpStatusCode.OK, (await verify.GetAsync("/api/agents")).StatusCode);
        }
        finally
        {
            if (replacement is not null)
                await replacement.DisposeAsync();
            replacementLock?.Dispose();
            originalLock?.Dispose();
            await original.DisposeAsync();
        }
    }

    [Fact]
    public async Task Lost_shutdown_response_returns_Stopped_after_lock_and_process_exit_are_confirmed()
    {
        using var home = new TempHome();
        var processes = new FakeProcesses().Alive(4242, Started);
        var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, 30001, "lost-response-token"));
        using var handler = new LostResponseHandler(() =>
        {
            held.Dispose();
            processes.Clear();
        });

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromSeconds(1), handler, CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Stopped, result.Status);
        Assert.Null(result.Error);
        using var reacquired = DashboardLock.TryAcquire(home.Path);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task Self_only_reader_cannot_claim_captured_process_stopped()
    {
        using var home = new TempHome();
        var processes = new SelfOnlyProcesses();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, 1, "self-only-token"));

        var result = await DashboardLifecycle.StopAsync(home.Path, processes, TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(DashboardStopStatus.Failed, result.Status);
    }

    private static async Task<RunningDashboard> StartPublishedAsync(TempHome home, FakeProcesses processes, int port, string token, string? publishedToken = null)
    {
        var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, port, processes) { Token = token }, CancellationToken.None);
        var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, server.Port, publishedToken ?? token));
        server.Stopping.Register(() =>
        {
            processes.Clear();
            held.Dispose();
        });
        return server;
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class CallbackHandler(Action onSend) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private sealed class LostResponseHandler(Action stopCapturedInstance) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            stopCapturedInstance();
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection lost after shutdown"));
        }
    }

    private sealed class ReusedPortReplacementHandler(Func<Task> replace) : HttpMessageHandler
    {
        private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        public HttpStatusCode? ResponseStatus { get; private set; }
        public Exception? Failure { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await replace();
            try
            {
                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
                {
                    Version = request.Version,
                    VersionPolicy = request.VersionPolicy,
                };
                foreach (var header in request.Headers)
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (request.Content is not null)
                {
                    var content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                    foreach (var header in request.Content.Headers)
                        content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    forwarded.Content = content;
                }

                var response = await _client.SendAsync(forwarded, cancellationToken);
                ResponseStatus = response.StatusCode;
                return response;
            }
            catch (Exception exception)
            {
                Failure = exception;
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _client.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class SelfOnlyProcesses : IProcessInfo
    {
        public int CurrentPid => 9999;
        public ProcessSnapshot? Get(int pid) => null;
    }

    private sealed class StatusServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string RequestText => _request.Task.IsCompleted ? _request.Task.GetAwaiter().GetResult() : string.Empty;

        public StatusServer(HttpStatusCode status, TimeSpan? delay = null, string? location = null)
        {
            _listener.Start();
            _run = RunAsync(status, delay ?? TimeSpan.Zero, location);
        }

        private async Task RunAsync(HttpStatusCode status, TimeSpan delay, string? location)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                var request = new System.Text.StringBuilder();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                    request.AppendLine(line);
                var contentLength = request.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .Select(header => header.Split(':', 2))
                    .Where(parts => parts.Length == 2 && string.Equals(parts[0], "Content-Length", StringComparison.OrdinalIgnoreCase))
                    .Select(parts => int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                    .SingleOrDefault();
                if (contentLength > 0)
                {
                    var body = new char[contentLength];
                    var read = await reader.ReadBlockAsync(body.AsMemory(), _stop.Token);
                    request.Append(new string(body, 0, read));
                }
                _request.TrySetResult(request.ToString());
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _stop.Token);
                var extra = location is null ? string.Empty : $"Location: {location}\r\n";
                var response = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} {status}\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, _stop.Token);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _run.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}