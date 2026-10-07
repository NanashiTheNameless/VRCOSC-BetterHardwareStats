using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BetterHardwareStats.Core.Cpu;
using BetterHardwareStats.Core.Disk;
using Microsoft.Win32;

namespace BetterHardwareStats.Windows.Native;
public static unsafe partial class MemoryStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(MEMORYSTATUSEX* buffer);

    public static (long Total, long Available)? Read()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        return GlobalMemoryStatusEx(&m) ? ((long)m.ullTotalPhys, (long)m.ullAvailPhys) : null;
    }
}
public static unsafe partial class CpuTopology
{
    private const int RelationProcessorPackage = 3;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, uint* returnedLength);

    public static IReadOnlyList<CpuPackage> ReadPackages()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorPackage, null, &len);
        if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER || len == 0) return [];
        var buf = new byte[len];
        var packages = new List<CpuPackage>();
        fixed (byte* p = buf)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorPackage, p, &len)) return [];
            uint offset = 0;
            while (offset < len)
            {
                var entry = p + offset;
                var relationship = *(int*)entry;
                var size = *(uint*)(entry + 4);
                if (size == 0) break;
                if (relationship == RelationProcessorPackage)
                {
                    // PROCESSOR_RELATIONSHIP at +8: Flags, EfficiencyClass, Reserved[20], GroupCount (WORD at +30), GroupMask[] at +32.
                    var groupCount = *(ushort*)(entry + 30);
                    var masks = new List<GroupAffinity>(groupCount);
                    for (var g = 0; g < groupCount; g++)
                    {
                        var ga = entry + 32 + g * 16; // GROUP_AFFINITY: KAFFINITY Mask (8), WORD Group, WORD Reserved[3]
                        masks.Add(new GroupAffinity(*(ushort*)(ga + 8), *(ulong*)ga));
                    }
                    packages.Add(new CpuPackage(packages.Count, NameFor(masks), masks));
                }
                offset += size;
            }
        }
        return packages;
    }
    private static string? NameFor(List<GroupAffinity> masks)
    {
        try
        {
            if (masks.Count == 0 || masks[0].Mask == 0) return null;
            var first = masks[0].Group * 64 + System.Numerics.BitOperations.TrailingZeroCount(masks[0].Mask);
            using var key = Registry.LocalMachine.OpenSubKey($@"HARDWARE\DESCRIPTION\System\CentralProcessor\{first}")
                            ?? Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch
        {
            return null;
        }
    }
}

public static class Elevation
{
    public static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
public static class VRChatProcesses
{
    public static readonly string[] Names = ["VRChat"];

    public static int[] Find()
    {
        var pids = new List<int>();
        foreach (var name in Names)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in ps)
            {
                try { pids.Add(p.Id); }
                catch { /* exited */ }
                finally { p.Dispose(); }
            }
        }
        return [.. pids];
    }
}
public sealed class DriveScanner
{
    private readonly Dictionary<char, (VolumeInfo Info, long ReadTicks)> _cache = new();

    public char? SystemDrive { get; } =
        Environment.GetEnvironmentVariable("SystemDrive") is { Length: >= 1 } s && char.IsAsciiLetter(s[0]) ? char.ToUpperInvariant(s[0]) : null;

    public IReadOnlyList<VolumeInfo> Scan(bool includeRemovable, long nowTicks)
    {
        var list = new List<VolumeInfo>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return list; }

        foreach (var d in drives)
        {
            char letter;
            DriveType type;
            try
            {
                letter = char.ToUpperInvariant(d.Name[0]);
                type = d.DriveType;
            }
            catch { continue; }
            if (type != DriveType.Fixed && !(includeRemovable && type == DriveType.Removable)) continue;

            var maxAge = (letter == SystemDrive ? 10 : 30) * Stopwatch.Frequency;
            if (_cache.TryGetValue(letter, out var c) && nowTicks - c.ReadTicks < maxAge)
            {
                list.Add(c.Info);
                continue;
            }

            VolumeInfo info;
            try
            {
                info = d.IsReady
                    ? new VolumeInfo(letter, type == DriveType.Fixed, type == DriveType.Removable, true, d.TotalSize, d.AvailableFreeSpace)
                    : new VolumeInfo(letter, type == DriveType.Fixed, type == DriveType.Removable, false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                info = new VolumeInfo(letter, type == DriveType.Fixed, type == DriveType.Removable, false);
            }
            _cache[letter] = (info, nowTicks);
            list.Add(info);
        }
        return list;
    }
}
