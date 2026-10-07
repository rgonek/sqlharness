using System.Collections.Concurrent;
using System.Globalization;

namespace SqlHarness.Core;

/// <summary>
/// Host processes known to have exited, shared by every read of one
/// <c>JournalReader</c> (Dashboard) or retention run. A (pid, start time) pair never comes back to
/// life, so once a lookup finds it gone no later read looks it up again; this
/// keeps abandoned rows from costing a process lookup per live-feed tick.
/// The set is bounded and simply starts over when full.
/// </summary>
internal sealed class DeadProcesses
{
    internal const int Capacity = 4096;
    private readonly ConcurrentDictionary<(long Pid, string? Started), byte> _dead = new();

    internal int Count => _dead.Count;

    internal bool Contains(long pid, string? startedAt) => _dead.ContainsKey((pid, startedAt));

    internal void Add(long pid, string? startedAt)
    {
        if (_dead.Count >= Capacity)
            _dead.Clear();
        _dead.TryAdd((pid, startedAt), 0);
    }
}

/// <summary>
/// Read-time <c>abandoned</c> detection: a <c>running</c> row whose host process
/// (pid plus start time, because pids are reused) no longer exists. Platforms
/// without a process reader (macOS, <see cref="SelfOnlyProcessInfo"/>) report
/// rows as stored rather than guessing. Live results are cached for one read
/// only; dead results go to the shared <see cref="DeadProcesses"/>. Retention
/// passes <paramref name="failedLookupIsAlive"/> so a lookup that throws never
/// makes a possibly live row deletable.
/// </summary>
internal sealed class ProcessLiveness(IProcessInfo processes, DeadProcesses dead, bool failedLookupIsAlive = false)
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
    private readonly Dictionary<(long Pid, string? Started), bool> _cache = new();

    internal string Status(string stored, long hostPid, string? hostStartedAt) =>
        stored == "running" && !IsAlive(hostPid, hostStartedAt) ? "abandoned" : stored;

    /// <summary>True when the host process of a running row still exists (pid plus start time).</summary>
    internal bool IsRunningAlive(long hostPid, string? hostStartedAt) => Status("running", hostPid, hostStartedAt) == "running";

    private bool IsAlive(long pid, string? startedAt)
    {
        if (processes is SelfOnlyProcessInfo)
            return true;
        if (dead.Contains(pid, startedAt))
            return false;
        if (_cache.TryGetValue((pid, startedAt), out var known))
            return known;

        ProcessSnapshot? snapshot;
        var failed = false;
        try
        {
            snapshot = pid is > 0 and <= int.MaxValue ? processes.Get((int)pid) : null;
        }
        catch (Exception)
        {
            snapshot = null;
            failed = true;
        }

        var alive = (failed && failedLookupIsAlive) || (snapshot is not null
            && (startedAt is null
                || snapshot.StartedAt is null
                || !DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stored)
                || (snapshot.StartedAt.Value - stored).Duration() <= StartTolerance));
        _cache[(pid, startedAt)] = alive;
        // A failed lookup proves nothing lasting; only a completed one marks the pair dead for good.
        if (!alive && !failed)
            dead.Add(pid, startedAt);
        return alive;
    }
}