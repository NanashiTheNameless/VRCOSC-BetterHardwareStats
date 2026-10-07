using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Cpu;
public sealed record CpuSensorReading(float? TemperatureC, float? PowerWatts, long TimestampTicks, MetricSource Source);
public static class CpuSensorPolicy
{
    public static (bool Start, string Reason) Decide(CpuSensorMode mode, bool elevated) => mode switch
    {
        CpuSensorMode.Off => (false, "CpuSensorMode is Off"),
        CpuSensorMode.HwInfo => (false, "External HWiNFO sensors; do not load the LibreHardwareMonitor CPU driver"),
        CpuSensorMode.AutoIfElevated => elevated
            ? (true, "AutoIfElevated: running as administrator")
            : (false, "AutoIfElevated: not running as administrator"),
        CpuSensorMode.On => (true, elevated ? "On" : "On without administrator rights; sensors may be unavailable"),
        _ => (false, "unknown mode"),
    };
}
public static class CpuSnapshotBuilder
{
    public static CpuSnapshot Build(
        CpuPackage? package,
        int selectedIndex,
        int packageCount,
        CpuCounterResult? counters,
        long countersTicks,
        CpuSensorReading? sensors,
        bool sensorsEnabled,
        string? sensorDetail,
        long nowTicks,
        long maxAgeTicks)
    {
        var countersFresh = counters is not null && nowTicks - countersTicks <= maxAgeTicks;
        var sensorsFresh = sensorsEnabled && sensors is not null && nowTicks - sensors.TimestampTicks <= maxAgeTicks;

        MetricValue<float>? C(float? v) => countersFresh && v is { } x ? new(x, MetricSource.WindowsCounters, countersTicks) : null;
        MetricValue<float>? S(float? v) => sensorsFresh && v is { } x && float.IsFinite(x) ? new(x, sensors!.Source, sensors.TimestampTicks) : null;

        var temp = S(sensors?.TemperatureC);
        var power = S(sensors?.PowerWatts);
        return new CpuSnapshot(
            package?.Name,
            selectedIndex,
            packageCount,
            C(counters?.UsagePercent),
            C(counters?.FrequencyMhz),
            temp,
            power,
            temp is not null || power is not null,
            sensorDetail);
    }
}
