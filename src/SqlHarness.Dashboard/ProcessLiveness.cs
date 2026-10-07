using System.Globalization;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

/// <summary>
/// Read-time <c>abandoned</c> detection: a <c>running</c> row whose host process
/// (pid plus start time, because pids are reused) no longer exists. Platforms
/// without a process reader (macOS, <see cref="SelfOnlyProcessInfo"/>) report
/// rows as stored rather than guessing.
/// </summary>
internal sealed class ProcessLiveness(IProcessInfo processes)
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);
    private readonly Dictionary<(long Pid, string? Started), bool> _cache = new();

    internal string Status(string stored, long hostPid, string? hostStartedAt) =>
        stored == "running" && !IsAlive(hostPid, hostStartedAt) ? "abandoned" : stored;

    private bool IsAlive(long pid, string? startedAt)
    {
        if (processes is SelfOnlyProcessInfo)
            return true;
        if (_cache.TryGetValue((pid, startedAt), out var known))
            return known;

        ProcessSnapshot? snapshot;
        try
        {
            snapshot = pid is > 0 and <= int.MaxValue ? processes.Get((int)pid) : null;
        }
        catch (Exception)
        {
            snapshot = null;
        }

        var alive = snapshot is not null
            && (startedAt is null
                || snapshot.StartedAt is null
                || !DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stored)
                || (snapshot.StartedAt.Value - stored).Duration() <= StartTolerance);
        _cache[(pid, startedAt)] = alive;
        return alive;
    }
}