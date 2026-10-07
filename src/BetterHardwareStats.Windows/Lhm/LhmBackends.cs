using System.Diagnostics;
using System.Runtime.CompilerServices;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Sensors;
using LibreHardwareMonitor.Hardware;

namespace BetterHardwareStats.Windows.Lhm;
public sealed class LhmGpuBackend : ILhmGpuBackend
{
    private readonly Computer _computer;
    private readonly List<(IHardware Hw, BackendDevice Device)> _gpus = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    public LhmGpuBackend()
    {
        _computer = new Computer { IsGpuEnabled = true };
        try
        {
            _computer.Open();
            foreach (var hw in _computer.Hardware)
            {
                var vendor = hw.HardwareType switch
                {
                    HardwareType.GpuNvidia => GpuVendor.Nvidia,
                    HardwareType.GpuAmd => GpuVendor.Amd,
                    HardwareType.GpuIntel => GpuVendor.Intel,
                    _ => (GpuVendor?)null,
                };
                if (vendor is null) continue;
                hw.Update();
                var id = hw.Identifier.ToString();
                var total = SensorMapping.MapGpu(Read(hw)).VramTotalBytes;
                _gpus.Add((hw, new BackendDevice($"lhm:{id}", vendor.Value, hw.Name, null, null, null, total, null, TrailingIndex(id))));
            }
            Devices = _gpus.Select(g => g.Device).ToList();
            Version = typeof(Computer).Assembly.GetName().Version?.ToString();
            Location = typeof(Computer).Assembly.Location;
        }
        catch
        {
            try { _computer.Close(); } catch { }
            throw;
        }
    }

    public IReadOnlyList<BackendDevice> Devices { get; }
    public string? Version { get; }
    public string? Location { get; }

    internal static int? TrailingIndex(string identifier)
    {
        var slash = identifier.LastIndexOf('/');
        return slash >= 0 && int.TryParse(identifier.AsSpan(slash + 1), out var n) ? n : null;
    }

    internal static List<SensorReading> Read(IHardware hw)
    {
        var list = new List<SensorReading>();
        void Add(IHardware h)
        {
            foreach (var s in h.Sensors)
            {
                try { list.Add(new SensorReading(s.SensorType.ToString(), s.Name, s.Value)); }
                catch { /* A failed sensor read leaves its value unavailable. */ }
            }
            foreach (var sub in h.SubHardware) Add(sub);
        }
        Add(hw);
        return list;
    }

    public BackendSample Sample()
    {
        var map = new Dictionary<string, PartialGpuMetrics>();
        foreach (var (hw, dev) in _gpus)
        {
            try
            {
                hw.Update();
                map[dev.BackendKey] = SensorMapping.MapGpu(Read(hw));
            }
            catch
            {
                // one GPU failing does not fail the pass
            }
        }
        return new BackendSample(Stopwatch.GetTimestamp(), map);
    }
    public IEnumerable<(string Hardware, string Type, string Name, string Identifier, float? Value)> Dump()
    {
        foreach (var (hw, _) in _gpus)
            foreach (var s in hw.Sensors.Concat(hw.SubHardware.SelectMany(x => x.Sensors)))
                yield return (hw.Name, s.SensorType.ToString(), s.Name, s.Identifier.ToString(), s.Value);
    }

    public void Dispose() => _computer.Close();
}
public sealed class LhmCpuSensors : ILhmCpuSensors
{
    private readonly Computer _computer;
    private readonly List<IHardware> _cpus;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public LhmCpuSensors()
    {
        _computer = new Computer { IsCpuEnabled = true };
        try
        {
            _computer.Open();
            _cpus = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToList();
        }
        catch
        {
            try { _computer.Close(); } catch { }
            throw;
        }
    }

    public int CpuCount => _cpus.Count;
    public (float? TemperatureC, float? PowerWatts) Read(int packageIndex)
    {
        if (_cpus.Count == 0) return (null, null);
        var hw = _cpus[Math.Clamp(packageIndex, 0, _cpus.Count - 1)];
        hw.Update();
        return SensorMapping.MapCpu(LhmGpuBackend.Read(hw));
    }

    public IEnumerable<(string Type, string Name, float? Value)> Dump() =>
        _cpus.SelectMany(h => h.Sensors).Select(s => (s.SensorType.ToString(), s.Name, s.Value));

    public void Dispose() => _computer.Close();
}
