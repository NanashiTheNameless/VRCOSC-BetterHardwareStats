using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Sensors;
public readonly record struct SensorReading(string Type, string Name, float? Value);
public static class SensorMapping
{
    private const float MiB = 1024f * 1024f;

    public static readonly IReadOnlyList<(string Field, string Type, string[] Names)> GpuTable =
    [
        ("Utilization", "Load", ["GPU Core", "D3D 3D"]),
        ("VideoEncode", "Load", ["GPU Video Engine"]),
        ("VramUsedMB", "SmallData", ["GPU Memory Used", "D3D Dedicated Memory Used"]),
        ("VramTotalMB", "SmallData", ["GPU Memory Total"]),
        ("SharedUsedMB", "SmallData", ["D3D Shared Memory Used"]),
        ("Temperature", "Temperature", ["GPU Core"]),
        ("Hotspot", "Temperature", ["GPU Hot Spot"]),
        ("MemoryTemperature", "Temperature", ["GPU Memory Junction", "GPU Memory"]),
        ("Power", "Power", ["GPU Package", "GPU Power"]),
        ("CoreClock", "Clock", ["GPU Core"]),
        ("MemoryClock", "Clock", ["GPU Memory"]),
        ("FanPercent", "Control", ["GPU Fan"]),
        ("FanRpm", "Fan", ["GPU Fan"]),
    ];

    public static PartialGpuMetrics MapGpu(IReadOnlyList<SensorReading> sensors)
    {
        float? F(string field) => Find(sensors, GpuTable.First(t => t.Field == field));
        long? Bytes(string field) => F(field) is { } mb && mb >= 0 ? (long)(mb * MiB) : null;

        var temp = F("Temperature");
        if (temp is null)
        {
            // Use the temperature reading when there is only one sensor.
            var temps = sensors.Where(s => Is(s.Type, "Temperature") && s.Value is { } v && float.IsFinite(v)).ToList();
            if (temps.Count == 1) temp = temps[0].Value;
        }

        return new PartialGpuMetrics
        {
            UtilizationPercent = F("Utilization"),
            VideoEncodePercent = F("VideoEncode"),
            VramUsedBytes = Bytes("VramUsedMB"),
            VramTotalBytes = Bytes("VramTotalMB"),
            SharedUsedBytes = Bytes("SharedUsedMB"),
            TemperatureC = temp,
            HotspotC = F("Hotspot"),
            MemoryTemperatureC = F("MemoryTemperature"),
            PowerWatts = F("Power"),
            CoreClockMhz = F("CoreClock"),
            MemoryClockMhz = F("MemoryClock"),
            FanPercent = F("FanPercent"),
            FanRpm = F("FanRpm"),
        };
    }

    public static readonly string[] CpuTemperatureNames = ["CPU Package", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie"];
    public static readonly string[] CpuCoreTemperaturePrefixes = ["CPU Core #", "Core Max", "Core #"];
    public static readonly string[] CpuPowerNames = ["CPU Package", "Package"];
    public static (float? TemperatureC, float? PowerWatts) MapCpu(IReadOnlyList<SensorReading> sensors)
    {
        var temp = FindExact(sensors, "Temperature", CpuTemperatureNames);
        if (temp is null)
        {
            var cores = sensors.Where(s => Is(s.Type, "Temperature") && s.Value is { } v && float.IsFinite(v)
                                           && CpuCoreTemperaturePrefixes.Any(p => s.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                               .Select(s => s.Value!.Value).ToList();
            if (cores.Count > 0) temp = cores.Max();
        }
        return (temp, FindExact(sensors, "Power", CpuPowerNames));
    }

    private static float? Find(IReadOnlyList<SensorReading> sensors, (string Field, string Type, string[] Names) row)
    {
        foreach (var name in row.Names)
        {
            foreach (var s in sensors)
                if (Is(s.Type, row.Type) && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Usable(s.Value)) return s.Value;
            foreach (var s in sensors)
                if (Is(s.Type, row.Type) && s.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && Usable(s.Value)) return s.Value;
        }
        return null;
    }

    private static float? FindExact(IReadOnlyList<SensorReading> sensors, string type, string[] names)
    {
        foreach (var name in names)
            foreach (var s in sensors)
                if (Is(s.Type, type) && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Usable(s.Value)) return s.Value;
        return null;
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Usable(float? v) => v is { } x && float.IsFinite(x);
}
