using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class ProcessInfoTests
{
    [Fact]
    public void Linux_stat_parsing_handles_parentheses_in_comm()
    {
        // pid (comm) state ppid, then fields 5..21, then starttime (field 22).
        var fields = string.Join(' ', Enumerable.Range(5, 17).Select(i => i.ToString()));
        var stat = $"123 (we ird) x) S 45 {fields} 987654 rest";

        var parsed = LinuxProcessInfo.ParseStat(stat);

        Assert.Equal((45, 987654L), parsed);
    }

    [Fact]
    public void Linux_stat_parsing_rejects_garbage() => Assert.Null(LinuxProcessInfo.ParseStat("garbage"));

    [Fact]
    public void Current_process_is_visible_on_windows_and_linux()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return;

        var self = ProcessInfo.Current.Get(Environment.ProcessId);

        Assert.NotNull(self);
        Assert.Equal(Environment.ProcessId, self!.Pid);
        Assert.False(string.IsNullOrWhiteSpace(self.Name));
        Assert.NotNull(self.ParentPid);
        Assert.NotNull(self.StartedAt);
    }
}