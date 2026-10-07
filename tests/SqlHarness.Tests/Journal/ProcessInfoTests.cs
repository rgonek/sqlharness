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
    public void Linux_names_keep_exe_basename_first_then_comm_and_argv0()
    {
        var names = LinuxProcessInfo.Names(
            "/home/u/.local/share/claude/versions/2.1.3 (deleted)",
            "claude\n",
            "/home/u/.local/share/claude/versions/2.1.3\0--resume\0");

        Assert.NotNull(names);
        Assert.Equal("2.1.3 (deleted)", names!.Value.Name);
        Assert.Equal(["claude", "/home/u/.local/share/claude/versions/2.1.3"], names.Value.Alternates);
    }

    [Fact]
    public void Linux_names_fall_back_to_comm_and_drop_duplicates()
    {
        var names = LinuxProcessInfo.Names(null, "bash", "bash\0-c\0x");

        Assert.Equal("bash", names!.Value.Name);
        Assert.Empty(names.Value.Alternates);
        Assert.Null(LinuxProcessInfo.Names(null, null, ""));
    }

    [Fact]
    public void Linux_start_identity_combines_boot_id_and_ticks()
    {
        Assert.Equal("boot:abc:987654", LinuxProcessInfo.StartIdentity("abc\n", 987654));
        Assert.Equal("ticks:987654", LinuxProcessInfo.StartIdentity(null, 987654));
        Assert.Equal("ticks:987654", LinuxProcessInfo.StartIdentity("  ", 987654));
    }

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