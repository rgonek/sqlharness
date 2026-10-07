using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SqlHarness.Core;

/// <param name="AlternateNames">Further names to classify by when <paramref name="Name"/> does not match (Linux: comm, argv[0]).</param>
/// <param name="StartIdentity">Clock-independent start identity used for session keys when present (Linux: boot id + start ticks).</param>
public sealed record ProcessSnapshot(
    int Pid,
    int? ParentPid,
    string Name,
    DateTimeOffset? StartedAt,
    string? CommandLine,
    IReadOnlyList<string>? AlternateNames = null,
    string? StartIdentity = null);

public interface IProcessInfo
{
    int CurrentPid { get; }

    /// <summary>Snapshot of one process, or null when it does not exist or is not readable.</summary>
    ProcessSnapshot? Get(int pid);
}

public static class ProcessInfo
{
    public static IProcessInfo Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsProcessInfo()
        : OperatingSystem.IsLinux() ? new LinuxProcessInfo()
        : new SelfOnlyProcessInfo();
}

/// <summary>Platforms without a parent-process reader (macOS): only the current process is known.</summary>
internal sealed class SelfOnlyProcessInfo : IProcessInfo
{
    public int CurrentPid => Environment.ProcessId;

    public ProcessSnapshot? Get(int pid)
    {
        if (pid != Environment.ProcessId)
            return null;
        using var self = Process.GetCurrentProcess();
        return new ProcessSnapshot(pid, null, self.ProcessName, new DateTimeOffset(self.StartTime), null);
    }
}

internal sealed class LinuxProcessInfo : IProcessInfo
{
    // USER_HZ is 100 on every mainstream Linux ABI; starttime is in USER_HZ ticks since boot.
    private const double TicksPerSecond = 100;
    private static readonly Lazy<long?> BootTimeSeconds = new(ReadBootTime);
    private static readonly Lazy<string?> BootId = new(() => ReadOptional("/proc/sys/kernel/random/boot_id"));

    public int CurrentPid => Environment.ProcessId;

    public ProcessSnapshot? Get(int pid)
    {
        try
        {
            var directory = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}";
            var parsed = ParseStat(File.ReadAllText(directory + "/stat"));
            if (parsed is null)
                return null;
            var rawCommandLine = File.ReadAllText(directory + "/cmdline");
            if (Names(ReadExeTarget(directory), ReadOptional(directory + "/comm"), rawCommandLine) is not { } names)
                return null;
            var commandLine = rawCommandLine.Replace('\0', ' ').Trim();
            DateTimeOffset? started = BootTimeSeconds.Value is { } boot
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)((boot + parsed.Value.StartTicks / TicksPerSecond) * 1000))
                : null;
            return new ProcessSnapshot(
                pid,
                parsed.Value.ParentPid,
                names.Name,
                started,
                commandLine.Length == 0 ? null : commandLine,
                names.Alternates,
                StartIdentity(BootId.Value, parsed.Value.StartTicks));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classification candidates: the exe link target's basename first (it may be
    /// version-named or carry " (deleted)"), then comm and argv[0] as fallbacks.
    /// </summary>
    internal static (string Name, IReadOnlyList<string> Alternates)? Names(string? exeTarget, string? comm, string? rawCommandLine)
    {
        var candidates = new List<string>(3);
        if (!string.IsNullOrEmpty(exeTarget))
            candidates.Add(Path.GetFileName(exeTarget));
        if (comm?.Trim() is { Length: > 0 } trimmedComm)
            candidates.Add(trimmedComm);
        if (rawCommandLine?.Split('\0')[0].Trim() is { Length: > 0 } argv0)
            candidates.Add(argv0);
        var distinct = candidates.Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0 ? null : (distinct[0], distinct.Skip(1).ToArray());
    }

    /// <summary>Start ticks are relative to boot; the boot id makes them unique across reboots and immune to wall-clock steps.</summary>
    internal static string StartIdentity(string? bootId, long startTicks)
    {
        var ticks = startTicks.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(bootId) ? "ticks:" + ticks : "boot:" + bootId.Trim() + ":" + ticks;
    }

    /// <summary>Parses /proc/[pid]/stat: comm may contain spaces and ')' so split after the last ')'.</summary>
    internal static (int ParentPid, long StartTicks)? ParseStat(string stat)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0] = state (field 3), fields[1] = ppid (field 4), fields[19] = starttime (field 22).
        if (fields.Length < 20
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            return null;
        return (parent, start);
    }

    private static string? ReadExeTarget(string directory)
    {
        try
        {
            return new FileInfo(directory + "/exe").LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadOptional(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadBootTime()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    return seconds;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}

internal sealed class WindowsProcessInfo : IProcessInfo
{
    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    public int CurrentPid => Environment.ProcessId;

    public ProcessSnapshot? Get(int pid)
    {
        var entry = Find(pid);
        if (entry is null)
            return null;
        DateTimeOffset? started = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            started = new DateTimeOffset(process.StartTime);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        // Command lines of other processes need PEB reads or WMI; node-hosted agents stay unclassified on Windows.
        return new ProcessSnapshot(pid, (int)entry.Value.th32ParentProcessID, entry.Value.szExeFile, started, null);
    }

    private static ProcessEntry32? Find(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
            return null;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
                return null;
            do
            {
                if (entry.th32ProcessID == (uint)pid)
                    return entry;
            }
            while (Process32NextW(snapshot, ref entry));
            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}