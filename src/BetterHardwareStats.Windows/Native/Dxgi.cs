using System.Runtime.InteropServices;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Windows.Native;
public static unsafe partial class DxgiEnumerator
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_REMOTE = 1;
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, nint* factory);
    public static IReadOnlyList<RawAdapter> Enumerate(List<string>? errors = null)
    {
        var result = new List<RawAdapter>();
        nint factory = 0;
        try
        {
            var iid = IID_IDXGIFactory1;
            var hr = CreateDXGIFactory1(&iid, &factory);
            if (hr < 0 || factory == 0)
            {
                errors?.Add($"CreateDXGIFactory1 failed: 0x{hr:X8}");
                return result;
            }

            var fvt = *(nint**)factory;
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)fvt[12];
            for (uint i = 0; ; i++)
            {
                nint adapter = 0;
                hr = enumAdapters1(factory, i, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                if (hr < 0 || adapter == 0)
                {
                    errors?.Add($"EnumAdapters1({i}) failed: 0x{hr:X8}");
                    break;
                }
                try
                {
                    var avt = *(nint**)adapter;
                    DXGI_ADAPTER_DESC1 d;
                    hr = ((delegate* unmanaged<nint, DXGI_ADAPTER_DESC1*, int>)avt[10])(adapter, &d);
                    if (hr < 0)
                    {
                        errors?.Add($"GetDesc1({i}) failed: 0x{hr:X8}");
                        continue;
                    }
                    var luid = Luid.FromParts(d.LuidHigh, d.LuidLow);
                    var name = new string(d.Description).TrimEnd('\0');
                    var (pci, flags) = D3dkmt.Query(luid, errors);
                    result.Add(new RawAdapter(
                        name, (ushort)d.VendorId, (ushort)d.DeviceId, d.SubSysId, (byte)d.Revision,
                        (long)d.DedicatedVideoMemory, (long)d.DedicatedSystemMemory, (long)d.SharedSystemMemory,
                        luid, (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0, (d.Flags & DXGI_ADAPTER_FLAG_REMOTE) != 0, pci, flags));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        catch (Exception e)
        {
            errors?.Add($"DXGI enumeration failed: {e.Message}");
        }
        finally
        {
            if (factory != 0) Release(factory);
        }
        return result;
    }

    private static void Release(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);
}
internal static unsafe partial class D3dkmt
{
    private const int KMTQAITYPE_ADAPTERADDRESS = 6;
    private const int KMTQAITYPE_ADAPTERTYPE = 15;

    [StructLayout(LayoutKind.Sequential)]
    private struct OPENADAPTERFROMLUID
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public void* pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADAPTERADDRESS
    {
        public uint BusNumber, DeviceNumber, FunctionNumber;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTOpenAdapterFromLuid(OPENADAPTERFROMLUID* p);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(QUERYADAPTERINFO* p);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(uint* hAdapter);

    public static (PciAddress? Pci, AdapterTypeFlags? Flags) Query(Luid luid, List<string>? errors)
    {
        try
        {
            var open = new OPENADAPTERFROMLUID { LuidLow = luid.LowPart, LuidHigh = luid.HighPart };
            var st = D3DKMTOpenAdapterFromLuid(&open);
            if (st != 0)
            {
                errors?.Add($"D3DKMTOpenAdapterFromLuid({luid}) failed: 0x{st:X8}");
                return (null, null);
            }
            try
            {
                PciAddress? pci = null;
                ADAPTERADDRESS addr;
                var q = new QUERYADAPTERINFO { hAdapter = open.hAdapter, Type = KMTQAITYPE_ADAPTERADDRESS, pPrivateDriverData = &addr, PrivateDriverDataSize = (uint)sizeof(ADAPTERADDRESS) };
                // Virtual (indirect display) adapters answer with 0xFFFFFFFF in every field: no PCI location.
                if (D3DKMTQueryAdapterInfo(&q) == 0 && addr.BusNumber != uint.MaxValue)
                    pci = new PciAddress(addr.BusNumber, addr.DeviceNumber, addr.FunctionNumber);

                AdapterTypeFlags? flags = null;
                uint bits;
                q = new QUERYADAPTERINFO { hAdapter = open.hAdapter, Type = KMTQAITYPE_ADAPTERTYPE, pPrivateDriverData = &bits, PrivateDriverDataSize = sizeof(uint) };
                if (D3DKMTQueryAdapterInfo(&q) == 0) flags = Decode(bits);
                return (pci, flags);
            }
            finally
            {
                var h = open.hAdapter;
                D3DKMTCloseAdapter(&h);
            }
        }
        catch (Exception e)
        {
            errors?.Add($"D3DKMT query failed for {luid}: {e.Message}");
            return (null, null);
        }
    }
    public static AdapterTypeFlags Decode(uint v) => new(
        RenderSupported: (v & 1) != 0,
        DisplaySupported: (v & 2) != 0,
        SoftwareDevice: (v & 4) != 0,
        HybridDiscrete: (v & 16) != 0,
        HybridIntegrated: (v & 32) != 0,
        IndirectDisplayDevice: (v & 64) != 0,
        Paravirtualized: (v & 128) != 0);
}
