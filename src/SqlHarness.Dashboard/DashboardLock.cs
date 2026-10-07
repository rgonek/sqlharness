using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

public sealed record DashboardEndpoint(int Pid, DateTimeOffset? StartedAt, int Port, string Token)
{
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public Uri OpenUri => new($"http://127.0.0.1:{Port}/?t={Token}");

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Pid = ").Append(Pid)
            .Append(", StartedAt = ").Append(StartedAt)
            .Append(", Port = ").Append(Port)
            .Append(", Token = <redacted>");
        return true;
    }
}

/// <summary>
/// One dashboard per SQLHarness home. The server holds an exclusive lock on
/// <c>dashboard.lock</c> for its whole lifetime (FileShare.None; an advisory
/// flock on Unix) and publishes its endpoint in <c>dashboard.json</c>
/// (owner-only), which other processes read because the lock file itself is
/// unreadable while held on Windows. The holder deletes any earlier
/// <c>dashboard.json</c> right after taking the lock, so a published file is
/// trusted only while the lock is held.
/// </summary>
public sealed class DashboardLock : IDisposable
{
    internal const string LockFileName = "dashboard.lock";
    internal const string InfoFileName = "dashboard.json";
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int DeleteAttempts = 5;
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AcquireRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(50);
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
        OwnerOnlyFiles.Directory(home);
        // A reader's ReadRunning probe holds the lock for an instant; one retry keeps a
        // starting server from mistaking that probe for another dashboard.
        if (TryAcquireOnce(home) is { } acquired)
            return acquired;
        Thread.Sleep(AcquireRetryDelay);
        return TryAcquireOnce(home);
    }

    private static DashboardLock? TryAcquireOnce(string home)
    {
        var lockPath = Path.Combine(home, LockFileName);
        FileStream? stream = null;
        try
        {
            stream = OpenExclusive(lockPath, FileMode.OpenOrCreate);
            OwnerOnlyFiles.File(lockPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }

        var infoPath = Path.Combine(home, InfoFileName);
        DeleteStaleInfo(infoPath);
        return new DashboardLock(stream, infoPath);
    }

    /// <summary>
    /// Whatever an earlier holder published is stale now that this process holds the lock.
    /// A concurrent reader can block the delete on Windows for a moment, so it is retried;
    /// if it still fails the lock is kept, because readers only trust the file while the
    /// lock is held and <see cref="Publish"/> overwrites it.
    /// </summary>
    private static void DeleteStaleInfo(string infoPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(infoPath);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                    return;
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    public void Publish(DashboardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var temp = _infoPath + ".tmp";
        File.Delete(temp);
        var content = JsonSerializer.Serialize(endpoint, Json);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(temp, content);
        }
        else
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerReadWrite };
            using var writer = new StreamWriter(temp, new UTF8Encoding(false), options);
            writer.Write(content);
        }

        OwnerOnlyFiles.File(temp);
        File.Move(temp, _infoPath, overwrite: true);
        _published = true;
    }

    public static DashboardEndpoint? ReadRunning(string home, IProcessInfo processes)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(processes);
        try
        {
            var path = Path.Combine(home, InfoFileName);
            if (!File.Exists(path))
                return null;
            var endpoint = JsonSerializer.Deserialize<DashboardEndpoint>(File.ReadAllText(path), Json);
            if (endpoint is null || endpoint.Pid <= 0 || endpoint.Port is < 1 or > 65535 || string.IsNullOrEmpty(endpoint.Token))
                return null;
            if (!IsHeldElsewhere(Path.Combine(home, LockFileName)))
                return null;
            var process = processes.Get(endpoint.Pid);
            if (process is null)
                // A reader that sees every process proves the publisher dead; a self-only reader
                // (macOS) cannot tell, so the held lock is the only evidence and is trusted.
                return processes is SelfOnlyProcessInfo ? endpoint : null;
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
        ArgumentException.ThrowIfNullOrEmpty(home);
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

    private static FileStream OpenExclusive(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows() && mode != FileMode.Open)
            options.UnixCreateMode = OwnerReadWrite;
        return new FileStream(path, options);
    }

    /// <summary>True when another holder has the lock; a free or missing lock file means nobody serves.</summary>
    private static bool IsHeldElsewhere(string lockPath)
    {
        try
        {
            using var probe = OpenExclusive(lockPath, FileMode.Open);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}