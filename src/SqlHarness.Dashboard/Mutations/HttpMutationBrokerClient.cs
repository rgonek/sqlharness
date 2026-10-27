using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SqlHarness.Core;
using SqlHarness.Core.Mutations;

namespace SqlHarness.Dashboard.Mutations;

/// <summary>Submits a prepared mutation to the local dashboard and waits for its terminal broker result.</summary>
public sealed class HttpMutationBrokerClient : IMutationBrokerClient
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupPoll = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<SqlHarnessConfig> _loadConfig;
    private readonly string _home;
    private readonly IProcessInfo _processes;
    private readonly IDashboardLauncher _launcher;
    private readonly DashboardLaunchCommand _command;
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public HttpMutationBrokerClient()
        : this(() => SqlHarnessConfigLoader.Load().Config, SqlHarnessPaths.Home, ProcessInfo.Current,
            new DetachedDashboardLauncher(), DashboardLaunchCommand.Current(), () => new SocketsHttpHandler())
    {
    }

    internal HttpMutationBrokerClient(
        Func<SqlHarnessConfig> loadConfig,
        string home,
        IProcessInfo processes,
        IDashboardLauncher launcher,
        DashboardLaunchCommand? command = null,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        _loadConfig = loadConfig;
        _home = home;
        _processes = processes;
        _launcher = launcher;
        _command = command ?? new DashboardLaunchCommand("sqlharness", ["dashboard", "--background"]);
        _handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler());
    }

    public async Task<MutationBrokerResult> SubmitAsync(MutationRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = _loadConfig();
        var endpoint = DashboardLock.ReadRunning(_home, _processes);
        if (endpoint is null && config.Dashboard.AutoStart)
        {
            try
            {
                SqlHarnessPaths.EnsureHomeFor(_home);
                Directory.CreateDirectory(_home);
                _launcher.Launch(_command, _home);
            }
            catch (Exception)
            {
                return Result(MutationStatus.Unavailable, null);
            }
            endpoint = await WaitForDashboardAsync(ct).ConfigureAwait(false);
        }
        if (endpoint is null)
            return Result(MutationStatus.Unavailable, null);
        string token;
        try
        {
            token = DashboardToken.LoadOrCreate(_home);
        }
        catch (Exception)
        {
            return Result(MutationStatus.Unavailable, null);
        }
        if (!TokenMatches(endpoint.Token, token))
            return Result(MutationStatus.Unavailable, null);

        using var handler = _handlerFactory();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(config.Mutations.ApprovalTimeoutSeconds + request.TimeoutSeconds + 30),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.OpenUri, "/api/mutations"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Content = new TrackedJsonContent(request);
        var content = (TrackedJsonContent)message.Content;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        try
        {
            while (!send.IsCompleted)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(5), ct);
                if (await Task.WhenAny(send, tick).ConfigureAwait(false) == send) break;
                progress?.Report("Waiting for mutation approval or execution.");
            }

            using var response = await send.ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
                    return Result(MutationStatus.Unavailable, null);
                var code = ReadErrorCode(error);
                return code is not null
                    ? Result(code, null)
                    : Result(MutationStatus.Failed, error.Length <= 512 ? error : error[..512]);
            }
            try
            {
                return await response.Content.ReadFromJsonAsync<MutationBrokerResult>(Json, ct).ConfigureAwait(false)
                    ?? Result(MutationStatus.OutcomeUnknown, "The dashboard returned an invalid result.");
            }
            catch (JsonException)
            {
                return Result(MutationStatus.OutcomeUnknown, "The dashboard returned an invalid result.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return Result(content.SerializationStarted ? MutationStatus.OutcomeUnknown : MutationStatus.Unavailable,
                content.SerializationStarted ? "The dashboard connection ended after submission; check mutation history before retrying." : null);
        }
    }

    private async Task<DashboardEndpoint?> WaitForDashboardAsync(CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < StartupTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (DashboardLock.ReadRunning(_home, _processes) is { } endpoint)
                return endpoint;
            var remaining = StartupTimeout - timer.Elapsed;
            await Task.Delay(remaining < StartupPoll ? remaining : StartupPoll, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static MutationBrokerResult Result(string status, string? message) => new(status, message, null);

    private static bool TokenMatches(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                var wireStatus = status.GetString();
                if (wireStatus is MutationStatus.Unavailable or MutationStatus.DatabaseMismatch or MutationStatus.NotAMutation or MutationStatus.PrincipalProtected)
                    return wireStatus;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class TrackedJsonContent(MutationRequest request) : HttpContent
    {
        private readonly byte[] _json = SerializeRequest(request);

        private static byte[] SerializeRequest(MutationRequest request)
        {
            var json = JsonSerializer.SerializeToNode(request, Json)!.AsObject();
            if (request.PreparationToken is { } token) json["preparationToken"] = token;
            return JsonSerializer.SerializeToUtf8Bytes(json, Json);
        }
        private int _serializationStarted;
        internal bool SerializationStarted => Volatile.Read(ref _serializationStarted) != 0;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Exchange(ref _serializationStarted, 1);
            await stream.WriteAsync(_json).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _serializationStarted, 1);
            return stream.WriteAsync(_json, cancellationToken).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _json.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Array.Clear(_json);
            base.Dispose(disposing);
        }
    }
}