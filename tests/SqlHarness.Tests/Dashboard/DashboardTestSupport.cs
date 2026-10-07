using SqlHarness.Core;

namespace SqlHarness.Tests.Dashboard;

internal sealed class FakeProcesses : IProcessInfo
{
    private readonly Dictionary<int, ProcessSnapshot> _alive = new();

    public int CurrentPid => Environment.ProcessId;

    public FakeProcesses Alive(int pid, DateTimeOffset? startedAt)
    {
        _alive[pid] = new ProcessSnapshot(pid, null, "proc", startedAt, null);
        return this;
    }

    public void Clear() => _alive.Clear();

    public ProcessSnapshot? Get(int pid) => _alive.GetValueOrDefault(pid);
}

internal sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlharness-dash-" + Guid.NewGuid().ToString("N"));

    public TempHome() => Directory.CreateDirectory(Path);

    public string DatabasePath => System.IO.Path.Combine(Path, "data", "activity.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // A just-stopped server may still release a handle; temp cleanup is best-effort.
        }
    }
}