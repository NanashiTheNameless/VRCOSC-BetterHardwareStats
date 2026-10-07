using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Windows.Adlx;
public sealed unsafe class AdlxBackend
{
    // ADLX_RESULT
    private const int ADLX_OK = 0, ADLX_ALREADY_ENABLED = 1, ADLX_ALREADY_INITIALIZED = 2, ADLX_NOT_SUPPORTED = 12;

    // IADLXInterface
    private const int Slot_Release = 1, Slot_QueryInterface = 2;
    // IADLXSystem (no Acquire/Release)
    private const int Sys_GetGPUs = 1, Sys_GetPerformanceMonitoringServices = 9;
    // IADLXGPUList
    private const int List_Size = 3, List_At_GPUList = 11;
    // IADLXGPU
    private const int Gpu_VendorId = 3, Gpu_Type = 5, Gpu_Name = 7, Gpu_TotalVRAM = 11, Gpu_DeviceId = 14,
        Gpu_SubSystemId = 16, Gpu_SubSystemVendorId = 17, Gpu_UniqueId = 18;
    // IADLXGPU2
    private const int Gpu2_LUID = 34;
    // IADLXPerformanceMonitoringServices
    private const int Perf_GetCurrentGPUMetrics = 18, Perf_GetSupportedGPUMetrics = 21;
    // IADLXGPUMetricsSupport
    private const int Sup_Usage = 3, Sup_Clock = 4, Sup_VramClock = 5, Sup_Temp = 6, Sup_Hotspot = 7, Sup_Power = 8,
        Sup_TotalBoardPower = 9, Sup_Fan = 10, Sup_Vram = 11;
    // IADLXGPUMetrics
    private const int Met_Usage = 4, Met_Clock = 5, Met_VramClock = 6, Met_Temp = 7, Met_Hotspot = 8, Met_Power = 9,
        Met_TotalBoardPower = 10, Met_Fan = 11, Met_Vram = 12;

    [Flags]
    private enum Support { None = 0, Usage = 1, Clock = 2, VramClock = 4, Temp = 8, Hotspot = 16, Power = 32, Tbp = 64, Fan = 128, Vram = 256 }

    private nint _lib;
    private nint _system;
    private nint _perf;
    private delegate* unmanaged<int> _terminate;
    private readonly List<(nint Gpu, BackendDevice Device, Support Support)> _gpus = [];

    public IReadOnlyList<BackendDevice> Devices { get; private set; } = [];
    public ulong DriverAdlxVersion { get; private set; }
    public IReadOnlyDictionary<string, string> PowerSource => _gpus.ToDictionary(g => g.Device.BackendKey, g => (g.Support & Support.Tbp) != 0 ? "TotalBoardPower" : "GPUPower");

    public static bool LibraryPresent()
    {
        if (!NativeLibrary.TryLoad("amdadlx64.dll", out var h)) return false;
        var ok = NativeLibrary.TryGetExport(h, "ADLXQueryFullVersion", out _);
        NativeLibrary.Free(h);
        return ok;
    }

    private static nint Vt(nint obj, int slot) => (*(nint**)obj)[slot];

    private static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged<nint, int>)Vt(obj, Slot_Release))(obj);
    }

    private static bool Ok(int r) => r is ADLX_OK or ADLX_ALREADY_ENABLED or ADLX_ALREADY_INITIALIZED;
    public string? Initialize()
    {
        if (!NativeLibrary.TryLoad("amdadlx64.dll", out _lib)) return "ADLX not found (no AMD driver installed)";
        nint E(string n) => NativeLibrary.TryGetExport(_lib, n, out var p) ? p : 0;

        var queryVersion = (delegate* unmanaged<ulong*, int>)E("ADLXQueryFullVersion");
        var init2 = (delegate* unmanaged<ulong, nint*, nint*, int>)E("ADLXInitialize2");
        var init = (delegate* unmanaged<ulong, nint*, int>)E("ADLXInitialize");
        _terminate = (delegate* unmanaged<int>)E("ADLXTerminate");
        if (queryVersion == null || (init2 == null && init == null) || _terminate == null) return "amdadlx64.dll is missing required exports";

        ulong ver;
        if (!Ok(queryVersion(&ver))) return "ADLXQueryFullVersion failed";
        DriverAdlxVersion = ver;

        nint sys = 0, mapping = 0;
        var r = init2 != null ? init2(ver, &sys, &mapping) : init(ver, &sys);
        if (!Ok(r) || sys == 0) return $"ADLXInitialize failed ({r}); driver ADLX version {FormatVersion(ver)}";
        _system = sys;

        nint perf;
        if (!Ok(((delegate* unmanaged<nint, nint*, int>)Vt(_system, Sys_GetPerformanceMonitoringServices))(_system, &perf)) || perf == 0)
            return "ADLX performance monitoring services unavailable";
        _perf = perf;

        nint list;
        if (!Ok(((delegate* unmanaged<nint, nint*, int>)Vt(_system, Sys_GetGPUs))(_system, &list)) || list == 0) return "ADLX GetGPUs failed";
        try
        {
            var n = ((delegate* unmanaged<nint, uint>)Vt(list, List_Size))(list);
            for (uint i = 0; i < n; i++)
            {
                nint gpu;
                if (!Ok(((delegate* unmanaged<nint, uint, nint*, int>)Vt(list, List_At_GPUList))(list, i, &gpu)) || gpu == 0) continue;
                try { _gpus.Add((gpu, Describe(gpu, (int)i), QuerySupport(gpu))); }
                catch { Release(gpu); throw; }
            }
        }
        finally
        {
            Release(list);
        }
        Devices = _gpus.Select(g => g.Device).ToList();
        return null;
    }

    public static string FormatVersion(ulong v) => $"{v >> 48}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";

    private BackendDevice Describe(nint gpu, int ordinal)
    {
        string? Str(int slot)
        {
            byte* p;
            return Ok(((delegate* unmanaged<nint, byte**, int>)Vt(gpu, slot))(gpu, &p)) && p != null ? Marshal.PtrToStringUTF8((nint)p) : null;
        }
        static uint? Hex(string? s) =>
            s is not null && uint.TryParse(s.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

        var name = Str(Gpu_Name);
        var deviceId = Hex(Str(Gpu_DeviceId));
        var sub = Hex(Str(Gpu_SubSystemId));
        var subVendor = Hex(Str(Gpu_SubSystemVendorId));
        // DXGI SubSysId packs subsystem id in the high word and subsystem vendor in the low word.
        uint? subsys = sub is { } s && subVendor is { } sv ? (s << 16) | (sv & 0xFFFF) : null;

        uint vramMb;
        long? vram = Ok(((delegate* unmanaged<nint, uint*, int>)Vt(gpu, Gpu_TotalVRAM))(gpu, &vramMb)) ? vramMb * 1024L * 1024 : null;

        int type;
        GpuKind? kind = Ok(((delegate* unmanaged<nint, int*, int>)Vt(gpu, Gpu_Type))(gpu, &type))
            ? type switch { 1 => GpuKind.Integrated, 2 => GpuKind.Discrete, _ => null }
            : null;

        int unique;
        var key = Ok(((delegate* unmanaged<nint, int*, int>)Vt(gpu, Gpu_UniqueId))(gpu, &unique)) ? $"adlx:{unique}" : $"adlx:#{ordinal}";

        // IADLXGPU2::LUID provides the strongest adapter identity match.
        Luid? luid = null;
        nint gpu2;
        fixed (char* iid = "IADLXGPU2")
        {
            if (Ok(((delegate* unmanaged<nint, char*, nint*, int>)Vt(gpu, Slot_QueryInterface))(gpu, iid, &gpu2)) && gpu2 != 0)
            {
                try
                {
                    ulong raw; // ADLX_LUID: lowPart (uint), highPart (int)
                    if (Ok(((delegate* unmanaged<nint, ulong*, int>)Vt(gpu2, Gpu2_LUID))(gpu2, &raw)))
                        luid = Luid.FromParts((int)(raw >> 32), (uint)raw);
                }
                finally
                {
                    Release(gpu2);
                }
            }
        }

        return new BackendDevice(key, GpuVendor.Amd, name, null, deviceId is { } d ? (ushort)d : null, subsys, vram, luid, ordinal, kind);
    }

    private Support QuerySupport(nint gpu)
    {
        nint supOut;
        if (!Ok(((delegate* unmanaged<nint, nint, nint*, int>)Vt(_perf, Perf_GetSupportedGPUMetrics))(_perf, gpu, &supOut)) || supOut == 0) return Support.None;
        var sup = supOut;
        try
        {
            var s = Support.None;
            void Check(int slot, Support flag)
            {
                byte b = 0; // adlx_bool is one byte
                if (Ok(((delegate* unmanaged<nint, byte*, int>)Vt(sup, slot))(sup, &b)) && b != 0) s |= flag;
            }
            Check(Sup_Usage, Support.Usage);
            Check(Sup_Clock, Support.Clock);
            Check(Sup_VramClock, Support.VramClock);
            Check(Sup_Temp, Support.Temp);
            Check(Sup_Hotspot, Support.Hotspot);
            Check(Sup_Power, Support.Power);
            Check(Sup_TotalBoardPower, Support.Tbp);
            Check(Sup_Fan, Support.Fan);
            Check(Sup_Vram, Support.Vram);
            return s;
        }
        finally
        {
            Release(sup);
        }
    }

    public BackendSample Sample()
    {
        var map = new Dictionary<string, PartialGpuMetrics>();
        foreach (var (gpu, dev, sup) in _gpus)
        {
            nint mOut;
            var r = ((delegate* unmanaged<nint, nint, nint*, int>)Vt(_perf, Perf_GetCurrentGPUMetrics))(_perf, gpu, &mOut);
            var m = mOut;
            if (r == ADLX_NOT_SUPPORTED || m == 0) continue;
            if (!Ok(r)) throw new InvalidOperationException($"ADLX GetCurrentGPUMetrics failed ({r}) for {dev.BackendKey}");
            try
            {
                double? D(Support f, int slot)
                {
                    if ((sup & f) == 0) return null;
                    double v;
                    return Ok(((delegate* unmanaged<nint, double*, int>)Vt(m, slot))(m, &v)) ? v : null;
                }
                int? I(Support f, int slot)
                {
                    if ((sup & f) == 0) return null;
                    int v;
                    return Ok(((delegate* unmanaged<nint, int*, int>)Vt(m, slot))(m, &v)) ? v : null;
                }

                var power = (sup & Support.Tbp) != 0 ? D(Support.Tbp, Met_TotalBoardPower) : D(Support.Power, Met_Power);
                var vramMb = I(Support.Vram, Met_Vram);
                map[dev.BackendKey] = new PartialGpuMetrics
                {
                    UtilizationPercent = (float?)D(Support.Usage, Met_Usage),
                    CoreClockMhz = I(Support.Clock, Met_Clock),
                    MemoryClockMhz = I(Support.VramClock, Met_VramClock),
                    TemperatureC = (float?)D(Support.Temp, Met_Temp),
                    HotspotC = (float?)D(Support.Hotspot, Met_Hotspot),
                    PowerWatts = (float?)power,
                    FanRpm = I(Support.Fan, Met_Fan),
                    VramUsedBytes = vramMb is { } mb ? mb * 1024L * 1024 : null,
                    VramTotalBytes = dev.DedicatedVramBytes,
                };
            }
            finally
            {
                Release(m);
            }
        }
        return new BackendSample(Stopwatch.GetTimestamp(), map);
    }

    public void Shutdown()
    {
        foreach (var (gpu, _, _) in _gpus) Release(gpu);
        _gpus.Clear();
        Release(_perf);
        _perf = 0;
        // IADLXSystem is owned by ADLX and has no Release; ADLXTerminate frees it.
        if (_terminate != null) _terminate();
        if (_lib != 0) NativeLibrary.Free(_lib);
        _lib = 0;
    }
}
