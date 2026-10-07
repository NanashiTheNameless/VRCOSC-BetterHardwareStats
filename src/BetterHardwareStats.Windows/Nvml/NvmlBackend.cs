using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Windows.Nvml;
public sealed unsafe class NvmlBackend
{
    private const int NVML_SUCCESS = 0, NOT_SUPPORTED = 3, NO_PERMISSION = 4, FUNCTION_NOT_FOUND = 13, GPU_IS_LOST = 15;
    private const uint MemoryV2Version = 40u | (2u << 24);

    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        public fixed byte BusId[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryV2 { public uint Version; public ulong Total, Reserved, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryV1 { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu, Memory; }

    private nint _lib;
    private delegate* unmanaged<int> _init, _shutdown;
    private delegate* unmanaged<uint*, int> _count;
    private delegate* unmanaged<uint, nint*, int> _handle;
    private delegate* unmanaged<nint, byte*, uint, int> _name, _uuid;
    private delegate* unmanaged<nint, PciInfo*, int> _pci;
    private delegate* unmanaged<nint, Utilization*, int> _util;
    private delegate* unmanaged<nint, MemoryV2*, int> _memV2;
    private delegate* unmanaged<nint, MemoryV1*, int> _memV1;
    private delegate* unmanaged<nint, int, uint*, int> _temp, _clock;
    private delegate* unmanaged<nint, uint*, int> _power, _powerLimit, _fan;
    private delegate* unmanaged<nint, uint*, uint*, int> _enc, _dec;
    private delegate* unmanaged<byte*, uint, int> _driverVersion;

    private readonly List<(nint Handle, BackendDevice Device)> _devices = [];
    private readonly Dictionary<(string Key, string Metric), bool> _unsupported = new();
    private readonly Dictionary<string, (PartialGpuMetrics Metrics, long Ticks)> _last = new();

    public IReadOnlyList<BackendDevice> Devices { get; private set; } = [];
    public string? DriverVersion { get; private set; }
    public Func<BackendDevice, bool>? IsActive { get; set; }

    public static string? FindLibrary()
    {
        if (NativeLibrary.TryLoad("nvml.dll", out var h)) { NativeLibrary.Free(h); return "nvml.dll"; }
        var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(nvsmi) ? nvsmi : null;
    }
    public string? Initialize()
    {
        var path = FindLibrary();
        if (path is null || !NativeLibrary.TryLoad(path, out _lib)) return "nvml.dll not found (no NVIDIA driver installed)";

        nint E(string name) => NativeLibrary.TryGetExport(_lib, name, out var p) ? p : 0;
        _init = (delegate* unmanaged<int>)E("nvmlInit_v2");
        _shutdown = (delegate* unmanaged<int>)E("nvmlShutdown");
        _count = (delegate* unmanaged<uint*, int>)E("nvmlDeviceGetCount_v2");
        _handle = (delegate* unmanaged<uint, nint*, int>)E("nvmlDeviceGetHandleByIndex_v2");
        if (_init == null || _shutdown == null || _count == null || _handle == null) return "nvml.dll is missing required exports (driver too old)";
        _name = (delegate* unmanaged<nint, byte*, uint, int>)E("nvmlDeviceGetName");
        _uuid = (delegate* unmanaged<nint, byte*, uint, int>)E("nvmlDeviceGetUUID");
        _pci = (delegate* unmanaged<nint, PciInfo*, int>)E("nvmlDeviceGetPciInfo_v3");
        _util = (delegate* unmanaged<nint, Utilization*, int>)E("nvmlDeviceGetUtilizationRates");
        _memV2 = (delegate* unmanaged<nint, MemoryV2*, int>)E("nvmlDeviceGetMemoryInfo_v2");
        _memV1 = (delegate* unmanaged<nint, MemoryV1*, int>)E("nvmlDeviceGetMemoryInfo");
        _temp = (delegate* unmanaged<nint, int, uint*, int>)E("nvmlDeviceGetTemperature");
        _clock = (delegate* unmanaged<nint, int, uint*, int>)E("nvmlDeviceGetClockInfo");
        _power = (delegate* unmanaged<nint, uint*, int>)E("nvmlDeviceGetPowerUsage");
        _powerLimit = (delegate* unmanaged<nint, uint*, int>)E("nvmlDeviceGetEnforcedPowerLimit");
        _fan = (delegate* unmanaged<nint, uint*, int>)E("nvmlDeviceGetFanSpeed");
        _enc = (delegate* unmanaged<nint, uint*, uint*, int>)E("nvmlDeviceGetEncoderUtilization");
        _dec = (delegate* unmanaged<nint, uint*, uint*, int>)E("nvmlDeviceGetDecoderUtilization");
        _driverVersion = (delegate* unmanaged<byte*, uint, int>)E("nvmlSystemGetDriverVersion");

        var rc = _init();
        if (rc != NVML_SUCCESS) return $"nvmlInit_v2 failed ({rc})";

        if (_driverVersion != null)
        {
            var b = stackalloc byte[80];
            if (_driverVersion(b, 80) == NVML_SUCCESS) DriverVersion = Utf8(b, 80);
        }

        uint n;
        if (_count(&n) != NVML_SUCCESS) return "nvmlDeviceGetCount_v2 failed";
        for (uint i = 0; i < n; i++)
        {
            nint h;
            if (_handle(i, &h) != NVML_SUCCESS) continue;
            _devices.Add((h, Describe(h, (int)i)));
        }
        Devices = _devices.Select(d => d.Device).ToList();
        return null;
    }

    private BackendDevice Describe(nint h, int ordinal)
    {
        var buf = stackalloc byte[96];
        string? name = _name != null && _name(h, buf, 96) == NVML_SUCCESS ? Utf8(buf, 96) : null;
        var key = _uuid != null && _uuid(h, buf, 96) == NVML_SUCCESS ? "nvml:" + Utf8(buf, 96) : $"nvml:{ordinal}";

        PciAddress? pci = null;
        ushort? deviceId = null;
        uint? subsys = null;
        PciInfo p;
        if (_pci != null && _pci(h, &p) == NVML_SUCCESS)
        {
            pci = new PciAddress(p.Bus, p.Device, ParseFunction(Utf8(p.BusId, 32)));
            deviceId = (ushort)(p.PciDeviceId >> 16);
            subsys = p.PciSubSystemId;
        }

        long? vram = null;
        MemoryV2 m2 = new() { Version = MemoryV2Version };
        MemoryV1 m1;
        if (_memV2 != null && _memV2(h, &m2) == NVML_SUCCESS) vram = (long)m2.Total;
        else if (_memV1 != null && _memV1(h, &m1) == NVML_SUCCESS) vram = (long)m1.Total;

        // NVIDIA GPUs on Windows are discrete (laptop dGPUs included); classification uses this only if D3DKMT did not decide.
        return new BackendDevice(key, GpuVendor.Nvidia, name, pci, deviceId, subsys, vram, null, ordinal, GpuKind.Discrete);
    }
    internal static uint ParseFunction(string busId)
    {
        var dot = busId.LastIndexOf('.');
        return dot >= 0 && uint.TryParse(busId.AsSpan(dot + 1), System.Globalization.NumberStyles.HexNumber, null, out var f) ? f : 0;
    }

    public BackendSample Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var map = new Dictionary<string, PartialGpuMetrics>();
        foreach (var (h, d) in _devices)
        {
            var active = IsActive?.Invoke(d) ?? true;
            if (!active && _last.TryGetValue(d.BackendKey, out var last) && now - last.Ticks < 10 * Stopwatch.Frequency)
            {
                map[d.BackendKey] = last.Metrics; // carried forward, at most 10 s old
                continue;
            }
            var m = Read(h, d.BackendKey);
            if (m is null) continue;
            map[d.BackendKey] = m;
            _last[d.BackendKey] = (m, now);
        }
        return new BackendSample(now, map);
    }

    private bool Skip(string key, string metric) => _unsupported.ContainsKey((key, metric));
    private bool Ok(string key, string metric, int rc)
    {
        if (rc == NVML_SUCCESS) return true;
        if (rc is NOT_SUPPORTED or NO_PERMISSION or FUNCTION_NOT_FOUND) _unsupported[(key, metric)] = true;
        else if (rc == GPU_IS_LOST) throw new InvalidOperationException($"NVML: GPU {key} is lost");
        else if (rc is 1 or 9 or 18) throw new InvalidOperationException($"NVML fatal error {rc}");
        return false;
    }

    private PartialGpuMetrics? Read(nint h, string key)
    {
        float? util = null, enc = null, dec = null, temp = null, power = null, limit = null, core = null, mem = null, fan = null;
        long? used = null, total = null;
        Utilization u;
        uint v, period;
        MemoryV2 m2 = new() { Version = MemoryV2Version };
        MemoryV1 m1;

        if (_util != null && !Skip(key, "util") && Ok(key, "util", _util(h, &u))) util = u.Gpu;
        if (_memV2 != null && !Skip(key, "memv2") && Ok(key, "memv2", _memV2(h, &m2))) { used = (long)m2.Used; total = (long)m2.Total; }
        else if (_memV1 != null && !Skip(key, "memv1") && Ok(key, "memv1", _memV1(h, &m1))) { used = (long)m1.Used; total = (long)m1.Total; }
        if (_temp != null && !Skip(key, "temp") && Ok(key, "temp", _temp(h, 0, &v))) temp = v;
        if (_power != null && !Skip(key, "power") && Ok(key, "power", _power(h, &v))) power = v / 1000f;
        if (_powerLimit != null && !Skip(key, "plimit") && Ok(key, "plimit", _powerLimit(h, &v))) limit = v / 1000f;
        if (_clock != null && !Skip(key, "gclk") && Ok(key, "gclk", _clock(h, 0, &v))) core = v;
        if (_clock != null && !Skip(key, "mclk") && Ok(key, "mclk", _clock(h, 2, &v))) mem = v;
        if (_fan != null && !Skip(key, "fan") && Ok(key, "fan", _fan(h, &v))) fan = v;
        if (_enc != null && !Skip(key, "enc") && Ok(key, "enc", _enc(h, &v, &period))) enc = v;
        if (_dec != null && !Skip(key, "dec") && Ok(key, "dec", _dec(h, &v, &period))) dec = v;

        return new PartialGpuMetrics
        {
            UtilizationPercent = util, VramUsedBytes = used, VramTotalBytes = total, TemperatureC = temp,
            PowerWatts = power, PowerLimitWatts = limit, CoreClockMhz = core, MemoryClockMhz = mem, FanPercent = fan,
            VideoEncodePercent = enc, VideoDecodePercent = dec,
        };
    }

    public void Shutdown()
    {
        if (_lib == 0) return;
        try { if (_shutdown != null) _shutdown(); } catch { /* best effort */ }
        NativeLibrary.Free(_lib);
        _lib = 0;
    }

    private static string Utf8(byte* p, int max)
    {
        var len = 0;
        while (len < max && p[len] != 0) len++;
        return Encoding.UTF8.GetString(p, len);
    }
}
