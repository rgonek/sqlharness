using System.Net;
using System.Text;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public enum DashboardStopStatus
{
    AlreadyStopped,
    Stopped,
    Failed,
}

public sealed record DashboardStopResult(DashboardStopStatus Status, string? Error);

public interface IDashboardLifecycle
{
    Task<DashboardStopResult> StopAsync(string home, TimeSpan timeout, CancellationToken ct);
    Task<DashboardEndpoint?> WaitForRunningAsync(string home, TimeSpan timeout, CancellationToken ct);
}

public sealed class DashboardLifecycleService(IProcessInfo processes) : IDashboardLifecycle
{
    public Task<DashboardStopResult> StopAsync(string home, TimeSpan timeout, CancellationToken ct) =>
        DashboardLifecycle.StopAsync(home, processes, timeout, ct);

    public Task<DashboardEndpoint?> WaitForRunningAsync(string home, TimeSpan timeout, CancellationToken ct) =>
        DashboardLock.WaitForRunningAsync(home, processes, timeout, waitForLockAcquisition: true, ct);
}

public static class DashboardLifecycle
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static Task<DashboardStopResult> StopAsync(string home, IProcessInfo processes, TimeSpan timeout, CancellationToken ct) =>
        StopAsync(home, processes, timeout, handler: null, ct);

    internal static async Task<DashboardStopResult> StopAsync(
        string home,
        IProcessInfo processes,
        TimeSpan timeout,
        HttpMessageHandler? handler,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var started = TimeProvider.System.GetTimestamp();
        TimeSpan Remaining() => timeout - TimeProvider.System.GetElapsedTime(started);
        Task PauseAsync(CancellationToken token)
        {
            var remaining = Remaining();
            return remaining <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(remaining < PollInterval ? remaining : PollInterval, token);
        }
        using var client = new HttpClient(handler ?? CreateHandler(), disposeHandler: handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        DashboardEndpoint? endpoint = null;
        while (Remaining() > TimeSpan.Zero)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (deadline.IsCancellationRequested)
                break;
            try
            {
                endpoint = DashboardLock.ReadRunning(home, processes);
                if (endpoint is not null)
                    break;

                using var probe = TryAcquireProbe(home, Remaining());
                if (probe is not null)
                    return new(DashboardStopStatus.AlreadyStopped, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Failed("The dashboard state in the SQLHarness home could not be read or locked.");
            }
            catch (Exception)
            {
                return Failed("The dashboard state could not be safely inspected.");
            }

            if (Remaining() <= TimeSpan.Zero)
                break;
            try { await PauseAsync(deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }

        if (endpoint is null)
            return Failed("The dashboard lock is held, but no valid endpoint became available before the stop deadline.");

        ProcessSnapshot? captured;
        try
        {
            captured = processes.Get(endpoint.Pid);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failed($"The identity of dashboard PID {endpoint.Pid} could not be verified; it was left running.");
        }
        if (captured is null || endpoint.StartedAt is null || captured.StartedAt is null)
            return Failed($"The identity of dashboard PID {endpoint.Pid} could not be verified; it was left running.");
        if ((captured.StartedAt.Value - endpoint.StartedAt.Value).Duration() > TimeSpan.FromSeconds(2))
            return Failed($"Dashboard PID {endpoint.Pid} changed during discovery; no shutdown request was sent.");

        HttpStatusCode? responseStatus = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{endpoint.Port}/api/shutdown"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(DashboardSecurity.WriteHeader, "1");
            request.Headers.TryAddWithoutValidation("Cookie", $"{DashboardSecurity.CookieName}={endpoint.Token}");
            request.Headers.TryAddWithoutValidation(DashboardSecurity.ShutdownProcessIdHeader,
                endpoint.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(DashboardSecurity.ShutdownStartedAtTicksHeader,
                endpoint.StartedAt.Value.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            responseStatus = response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var current = DashboardLock.ReadRunning(home, processes);
                return current is not null && !SameEndpoint(endpoint, current)
                    ? Failed($"Dashboard PID {endpoint.Pid} was replaced during shutdown; the replacement was left running.")
                    : Failed($"Dashboard PID {endpoint.Pid} refused shutdown because its process identity changed.");
            }
            if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.NoContent))
            {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                    return Failed($"Dashboard PID {endpoint.Pid} does not support guarded shutdown (HTTP {(int)response.StatusCode}); stop it manually, then retry.");
                if (response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden)
                    return Failed($"Dashboard PID {endpoint.Pid} rejected shutdown (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The server may have stopped after sending the response even if the connection timed out.
        }
        catch (HttpRequestException)
        {
            // A disconnect can race with response delivery; confirm independently below.
        }
        catch (Exception)
        {
            // Do not expose transport exception text, which can contain the endpoint or token.
        }

        var accepted = responseStatus is HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.NoContent;
        while (Remaining() > TimeSpan.Zero)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (deadline.IsCancellationRequested)
                break;
            DashboardEndpoint? current;
            try
            {
                current = DashboardLock.ReadRunning(home, processes);
                if (current is not null && !SameEndpoint(endpoint, current))
                    return Failed($"Dashboard PID {endpoint.Pid} was replaced during shutdown; the replacement was left running.");

                using var probe = current is null ? TryAcquireProbe(home, Remaining()) : null;
                if (probe is not null)
                {
                    if (processes is SelfOnlyProcessInfo && endpoint.Pid != processes.CurrentPid)
                        return Failed($"The dashboard lock was released, but this process reader cannot verify that PID {endpoint.Pid} exited.");

                    var after = processes.Get(endpoint.Pid);
                    if (after is null || !SameProcess(captured, after))
                        return new(DashboardStopStatus.Stopped, null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Failed("The dashboard state in the SQLHarness home could not be confirmed after shutdown.");
            }
            catch (Exception)
            {
                return Failed("The dashboard stop could not be safely confirmed.");
            }

            try { await PauseAsync(deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }

        if (responseStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Failed($"Dashboard PID {endpoint.Pid} did not authenticate the shutdown request; it may still be running.");
        if (!accepted && responseStatus is null)
            return Failed($"Could not confirm shutdown of dashboard PID {endpoint.Pid}; it may still be running.");
        return Failed($"Dashboard PID {endpoint.Pid} did not release its lock and exit before the stop deadline.");
    }

    private static DashboardLock? TryAcquireProbe(string home, TimeSpan remaining)
    {
        // DashboardLock's normal retry waits 250 ms. Lifecycle uses a shorter wait to keep all
        // lock probes inside its one shared deadline.
        return DashboardLock.TryAcquire(home, () =>
        {
            var wait = remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50);
            if (wait > TimeSpan.Zero)
                Thread.Sleep(wait);
        });
    }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
    };

    private static bool SameEndpoint(DashboardEndpoint left, DashboardEndpoint right) =>
        left.Pid == right.Pid && left.StartedAt == right.StartedAt && left.Port == right.Port && left.Token == right.Token;

    private static bool SameProcess(ProcessSnapshot left, ProcessSnapshot right) =>
        left.Pid == right.Pid && left.StartedAt == right.StartedAt;

    private static DashboardStopResult Failed(string error) => new(DashboardStopStatus.Failed, error);
}