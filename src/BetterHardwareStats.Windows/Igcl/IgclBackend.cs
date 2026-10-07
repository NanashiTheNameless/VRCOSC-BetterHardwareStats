using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Windows.Igcl;
public sealed unsafe class IgclBackend
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct InitArgs { public uint Size; public byte Version; public uint AppVersion, Flags, SupportedVersion; public Guid ApplicationId; }
    [StructLayout(LayoutKind.Explicit, Size = 320)]
    internal struct DeviceProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public nint DeviceId;
        [FieldOffset(16)] public uint DeviceIdSize;
        [FieldOffset(20)] public uint DeviceType;
        [FieldOffset(64)] public uint VendorId;
        [FieldOffset(68)] public uint DeviceIdPci;
        [FieldOffset(88)] public fixed byte Name[100];
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TemperatureProperties { public uint Size; public byte Version; public int Type; public double Maximum; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FrequencyProperties { public uint Size; public byte Version; public int Type; public byte CanControl; public double Minimum, Maximum; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FrequencyState { public uint Size; public byte Version; public double Voltage, Requested, Tdp, Efficient, Actual; public uint Throttle; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct EnergyCounter { public uint Size; public byte Version; public ulong Energy, Timestamp; }

    private sealed class Adapter(BackendDevice device, nint temperature, nint memoryTemperature, nint clock, nint memoryClock, nint power)
    {
        public BackendDevice Device = device;
        public nint Temperature = temperature, MemoryTemperature = memoryTemperature, Clock = clock, MemoryClock = memoryClock, Power = power;
        public EnergyCounter? Previous;
    }
    private readonly List<Adapter> _adapters = [];
    private nint _library, _api;
    private delegate* unmanaged<nint, int> _close;
    private delegate* unmanaged<nint, double*, int> _temperature;
    private delegate* unmanaged<nint, FrequencyState*, int> _frequency;
    private delegate* unmanaged<nint, EnergyCounter*, int> _energy;
    public IReadOnlyList<BackendDevice> Devices { get; private set; } = [];

    private static string LibraryPath => Path.Combine(Environment.SystemDirectory, "ControlLib.dll");
    public static bool LibraryPresent() => Environment.Is64BitProcess && File.Exists(LibraryPath);

    public string? Initialize()
    {
        if (!Environment.Is64BitProcess) return "Intel IGCL telemetry requires a 64-bit process";
        if (!NativeLibrary.TryLoad(LibraryPath, out _library)) return "Intel ControlLib.dll not found in the system directory";
        nint E(string name) => NativeLibrary.TryGetExport(_library, name, out var address) ? address : 0;
        var init = (delegate* unmanaged<InitArgs*, nint*, int>)E("ctlInit");
        _close = (delegate* unmanaged<nint, int>)E("ctlClose");
        var enumerate = (delegate* unmanaged<nint, uint*, nint*, int>)E("ctlEnumerateDevices");
        var properties = (delegate* unmanaged<nint, DeviceProperties*, int>)E("ctlGetDeviceProperties");
        _temperature = (delegate* unmanaged<nint, double*, int>)E("ctlTemperatureGetState");
        _frequency = (delegate* unmanaged<nint, FrequencyState*, int>)E("ctlFrequencyGetState");
        _energy = (delegate* unmanaged<nint, EnergyCounter*, int>)E("ctlPowerGetEnergyCounter");
        if (init == null || _close == null || enumerate == null || properties == null) return "Intel IGCL is missing required exports";
        var args = new InitArgs { Size = (uint)sizeof(InitArgs), AppVersion = 1u << 16, Flags = 1 };
        nint api = 0;
        var status = init(&args, &api);
        _api = api;
        if (status != 0 || api == 0) return $"Intel IGCL initialization unavailable ({status:X8}); installed GPU/driver may not support telemetry";
        var tempProps = (delegate* unmanaged<nint, TemperatureProperties*, int>)E("ctlTemperatureGetProperties");
        var freqProps = (delegate* unmanaged<nint, FrequencyProperties*, int>)E("ctlFrequencyGetProperties");
        foreach (var handle in Enumerate(enumerate, api))
        {
            long luid = 0;
            var info = new DeviceProperties { Size = (uint)sizeof(DeviceProperties), DeviceId = (nint)(&luid), DeviceIdSize = 8 };
            if (properties(handle, &info) != 0 || info.DeviceType != 1 || info.VendorId != 0x8086) continue;
            var name = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(info.Name, 100)).Split('\0')[0];
            var device = new BackendDevice($"igcl:{luid:X16}", GpuVendor.Intel, name, null, checked((ushort)info.DeviceIdPci), null, null,
                luid != 0 ? new Luid(luid) : null, null);
            if (luid == 0) continue; // Never guess between Intel adapters or assign duplicate keys.
            nint temperature = 0, memoryTemperature = 0, globalTemperature = 0, clock = 0, memoryClock = 0;
            if (tempProps != null && _temperature != null)
                foreach (var sensor in Enumerate((delegate* unmanaged<nint, uint*, nint*, int>)E("ctlEnumTemperatureSensors"), handle))
                {
                    var prop = new TemperatureProperties { Size = (uint)sizeof(TemperatureProperties) };
                    if (tempProps(sensor, &prop) != 0) continue;
                    if (prop.Type == 1 && temperature == 0) temperature = sensor;
                    if (prop.Type == 0 && globalTemperature == 0) globalTemperature = sensor;
                    if (prop.Type == 2 && memoryTemperature == 0) memoryTemperature = sensor;
                }
            if (freqProps != null && _frequency != null)
                foreach (var domain in Enumerate((delegate* unmanaged<nint, uint*, nint*, int>)E("ctlEnumFrequencyDomains"), handle))
                {
                    var prop = new FrequencyProperties { Size = (uint)sizeof(FrequencyProperties) };
                    if (freqProps(domain, &prop) != 0) continue;
                    if (prop.Type == 0 && clock == 0) clock = domain;
                    if (prop.Type == 1 && memoryClock == 0) memoryClock = domain;
                }
            var powers = _energy == null ? [] : Enumerate((delegate* unmanaged<nint, uint*, nint*, int>)E("ctlEnumPowerDomains"), handle);
            _adapters.Add(new(device, temperature != 0 ? temperature : globalTemperature, memoryTemperature, clock, memoryClock,
                powers.Length == 1 ? powers[0] : 0)); // Multiple domains are ambiguous; do not sum/double-count them.
        }
        Devices = _adapters.Select(a => a.Device).ToArray();
        return Devices.Count == 0 ? "Intel IGCL found no Intel graphics adapters with a Windows LUID" : null;
    }

    private static nint[] Enumerate(delegate* unmanaged<nint, uint*, nint*, int> call, nint owner)
    {
        if (call == null) return [];
        uint count = 0;
        if (call(owner, &count, null) != 0 || count is 0 or > 256) return [];
        var handles = new nint[count];
        var capacity = count;
        fixed (nint* buffer = handles)
            if (call(owner, &count, buffer) != 0 || count > capacity) return [];
        return handles.Take((int)count).ToArray();
    }

    public BackendSample Sample()
    {
        var result = new Dictionary<string, PartialGpuMetrics>();
        foreach (var adapter in _adapters)
        {
            float? power = null;
            if (adapter.Power != 0 && _energy != null)
            {
                var reading = new EnergyCounter { Size = (uint)sizeof(EnergyCounter) };
                if (_energy(adapter.Power, &reading) == 0)
                {
                    if (adapter.Previous is { } previous) power = PowerDelta(previous.Energy, previous.Timestamp, reading.Energy, reading.Timestamp);
                    adapter.Previous = reading;
                }
                else adapter.Previous = null;
            }
            result[adapter.Device.BackendKey] = new PartialGpuMetrics
            {
                TemperatureC = Temperature(adapter.Temperature), MemoryTemperatureC = Temperature(adapter.MemoryTemperature),
                CoreClockMhz = Frequency(adapter.Clock), MemoryClockMhz = Frequency(adapter.MemoryClock), PowerWatts = power,
            };
        }
        return new(Stopwatch.GetTimestamp(), result);
    }

    internal static float? PowerDelta(ulong previousEnergy, ulong previousTime, ulong energy, ulong time) =>
        time > previousTime && energy >= previousEnergy ? (float)((double)(energy - previousEnergy) / (time - previousTime)) : null;

    private float? Temperature(nint handle)
    {
        double temperature;
        return handle != 0 && _temperature != null && _temperature(handle, &temperature) == 0 && double.IsFinite(temperature)
            && temperature >= -273.15 && temperature <= float.MaxValue ? (float)temperature : null;
    }
    private float? Frequency(nint handle)
    {
        var state = new FrequencyState { Size = (uint)sizeof(FrequencyState) };
        return handle != 0 && _frequency != null && _frequency(handle, &state) == 0 && double.IsFinite(state.Actual)
            && state.Actual >= 0 && state.Actual <= float.MaxValue ? (float)state.Actual : null;
    }
    public void Shutdown()
    {
        if (_api != 0 && _close != null) _close(_api);
        _api = 0;
        _adapters.Clear();
        Devices = [];
        if (_library != 0) NativeLibrary.Free(_library);
        _library = 0;
    }
}
