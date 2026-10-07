using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using BetterHardwareStats.Core.Network;

namespace BetterHardwareStats.Windows.Native;
public static unsafe partial class NetworkTable
{
    private const int RowSize = 1352;
    private const int OffLuid = 0, OffIndex = 8, OffAlias = 28, OffDescription = 542, OffType = 1128;
    private const int OffFlags = 1152, OffOperStatus = 1156;
    private const int OffTxSpeed = 1192, OffRxSpeed = 1200, OffInOctets = 1208, OffOutOctets = 1280;
    private const int StringChars = 257;
    private const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    private const int IfOperStatusUp = 1;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIfTable2(byte** table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(void* memory);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterfaceEx(byte* destAddr, uint* bestIfIndex);

    private static readonly object TableLock = new();

    public static bool LastReadUsedFallback { get; private set; }

    public static IReadOnlyList<InterfaceRow> Read()
    {
        lock (TableLock) return ReadLocked();
    }

    private static IReadOnlyList<InterfaceRow> ReadLocked()
    {
        try
        {
            var rows = ReadNative();
            LastReadUsedFallback = false;
            return rows;
        }
        catch
        {
            LastReadUsedFallback = true;
            return ReadManaged();
        }
    }

    private static List<InterfaceRow> ReadNative()
    {
        byte* table = null;
        var st = GetIfTable2(&table);
        if (st != 0 || table == null) throw new InvalidOperationException($"GetIfTable2 failed: {st}");
        try
        {
            var n = *(uint*)table;
            var rows = new List<InterfaceRow>((int)n);
            for (var i = 0; i < n; i++)
            {
                var r = table + 8 + i * RowSize; // MIB_IF_TABLE2: NumEntries, then Table[] aligned to 8
                var flags = *(r + OffFlags);
                rows.Add(new InterfaceRow(
                    Key: (*(ulong*)(r + OffLuid)).ToString("X16"),
                    Alias: Str(r + OffAlias),
                    Description: Str(r + OffDescription),
                    IsUp: *(int*)(r + OffOperStatus) == IfOperStatusUp,
                    IsLoopback: *(uint*)(r + OffType) == IF_TYPE_SOFTWARE_LOOPBACK,
                    IsHardware: (flags & 1) != 0,
                    IsFilter: (flags & 2) != 0,
                    ReceiveLinkBps: *(ulong*)(r + OffRxSpeed),
                    TransmitLinkBps: *(ulong*)(r + OffTxSpeed),
                    InOctets: *(ulong*)(r + OffInOctets),
                    OutOctets: *(ulong*)(r + OffOutOctets)));
                IndexToKey[*(uint*)(r + OffIndex)] = rows[^1].Key;
            }
            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static readonly Dictionary<uint, string> IndexToKey = new();

    private static string Str(byte* p) => new string((char*)p, 0, StringChars).Split('\0')[0];
    public static string? DefaultRouteKey()
    {
        lock (TableLock) return DefaultRouteKeyLocked();
    }

    private static string? DefaultRouteKeyLocked()
    {
        try
        {
            var sa = stackalloc byte[16]; // sockaddr_in: family, port, 4-byte address, zero padding
            new Span<byte>(sa, 16).Clear();
            *(ushort*)sa = 2; // AF_INET
            sa[4] = 1; sa[5] = 1; sa[6] = 1; sa[7] = 1; // 1.1.1.1
            uint index;
            return GetBestInterfaceEx(sa, &index) == 0 && IndexToKey.TryGetValue(index, out var key) ? key : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<InterfaceRow> ReadManaged()
    {
        var rows = new List<InterfaceRow>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var stats = ni.GetIPStatistics();
                var speed = ni.Speed > 0 ? (ulong)ni.Speed : 0;
                rows.Add(new InterfaceRow(ni.Id, ni.Name, ni.Description, ni.OperationalStatus == OperationalStatus.Up,
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                    ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet,
                    false, speed, speed, (ulong)stats.BytesReceived, (ulong)stats.BytesSent));
            }
            catch
            {
                // interface vanished during enumeration
            }
        }
        return rows;
    }
}
