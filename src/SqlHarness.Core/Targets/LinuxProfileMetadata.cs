using System.Runtime.InteropServices;

namespace SqlHarness.Core.Targets;

// A Linux save is allowed only when ordinary owner/group/mode fully describe both
// files. Unknown or extended metadata is rejected, never silently discarded.
internal static class LinuxProfileMetadata
{
    private const int CurrentDirectory = -100;
    private const int NoFollow = 0x100;
    private const uint Required = 0x1b; // type, mode, uid, gid
    private const ushort TypeMask = 0xf000;
    private const ushort RegularFile = 0x8000;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint User;
        [FieldOffset(24)] public uint Group;
        [FieldOffset(28)] public ushort Mode;
    }

    internal readonly record struct Metadata(uint User, uint Group, ushort Mode);

    internal static bool CanReplace(Metadata original, Metadata latest, Metadata temporary) =>
        original == latest && original == temporary;

    internal static bool TryRead(string path, out Metadata metadata)
    {
        metadata = default;
        try
        {
            if (!OperatingSystem.IsLinux() || Statx(CurrentDirectory, path, NoFollow, Required, out var data) != 0 ||
                (data.Mask & Required) != Required || (data.Mode & TypeMask) != RegularFile ||
                ListXattr(path, IntPtr.Zero, 0) != 0)
                return false;
            metadata = new Metadata(data.User, data.Group, data.Mode);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out StatxData data);

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern nint ListXattr(string path, IntPtr list, nuint size);
}
