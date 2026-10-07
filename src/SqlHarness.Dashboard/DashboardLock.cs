using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardEndpoint(int Pid, DateTimeOffset? StartedAt, int Port, string Token)
{
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public Uri OpenUri => new($"http://127.0.0.1:{Port}/?t={Token}");
}

/// <summary>
/// One dashboard per SQLHarness home. The server holds an exclusive lock on
/// <c>dashboard.lock</c> for its whole lifetime (FileShare.None; an advisory
/// flock on Unix) and publishes its endpoint in <c>dashboard.json</c>
/// (owner-only), which other processes read because the lock file itself is
/// unreadable while held on Windows.
/// </summary>
public sealed class DashboardLock : IDisposable
{
    internal const string LockFileName = "dashboard.lock";
    internal const string InfoFileName = "dashboard.json";
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FileStream _lock;
    private readonly string _infoPath;
    private bool _published;

    private DashboardLock(FileStream lockStream, string infoPath)
    {
        _lock = lockStream;
        _infoPath = infoPath;
    }

    public static DashboardLock? TryAcquire(string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        Directory.CreateDirectory(home);
        try
        {
            var stream = new FileStream(Path.Combine(home, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            OwnerOnlyFiles.File(Path.Combine(home, LockFileName));
            return new DashboardLock(stream, Path.Combine(home, InfoFileName));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Publish(DashboardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var temp = _infoPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(endpoint, Json));
        OwnerOnlyFiles.File(temp);
        File.Move(temp, _infoPath, overwrite: true);
        _published = true;
    }

    public static DashboardEndpoint? ReadRunning(string home, IProcessInfo processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        try
        {
            var path = Path.Combine(home, InfoFileName);
            if (!File.Exists(path))
                return null;
            var endpoint = JsonSerializer.Deserialize<DashboardEndpoint>(File.ReadAllText(path), Json);
            if (endpoint is null || endpoint.Port is < 1 or > 65535 || string.IsNullOrEmpty(endpoint.Token))
                return null;
            var process = processes.Get(endpoint.Pid);
            if (process is null)
                return null;
            if (endpoint.StartedAt is { } published && process.StartedAt is { } actual
                && (actual - published).Duration() > StartTolerance)
                return null;
            return endpoint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task<DashboardEndpoint?> WaitForRunningAsync(string home, IProcessInfo processes, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (ReadRunning(home, processes) is { } endpoint)
                return endpoint;
            if (DateTimeOffset.UtcNow >= deadline)
                return null;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    public void Dispose()
    {
        if (_published)
        {
            try
            {
                File.Delete(_infoPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        _lock.Dispose();
    }
}