using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public sealed record SourcePreferences
{
    public static SourcePreferences Auto { get; } = new();

    public IReadOnlyDictionary<GpuMetricGroup, MetricSource> Preferred { get; init; } = new Dictionary<GpuMetricGroup, MetricSource>();

    public bool Strict { get; init; }

    public MetricSource? For(GpuMetricGroup group) => Preferred.TryGetValue(group, out var s) ? s : null;
    public MetricSource[] Apply(GpuMetricGroup group, MetricSource[] autoOrder)
    {
        if (For(group) is not { } p) return autoOrder;
        // DXGI totals belong to the Windows counter choice for memory.
        var chosen = group == GpuMetricGroup.Memory && p == MetricSource.WindowsCounters
            ? new[] { MetricSource.WindowsCounters, MetricSource.Dxgi }
            : new[] { p };
        return Strict ? chosen : [.. chosen, .. autoOrder.Where(s => !chosen.Contains(s))];
    }
}
public static class SourceCatalog
{
    public static readonly MetricSource[] Selectable =
        [MetricSource.Nvml, MetricSource.Adlx, MetricSource.Igcl, MetricSource.LibreHardwareMonitor];
    public static bool Supports(MetricSource source, GpuMetricGroup group) =>
        source is MetricSource.Nvml or MetricSource.Adlx or MetricSource.LibreHardwareMonitor
        || source == MetricSource.Igcl && group is GpuMetricGroup.Temperature or GpuMetricGroup.Power or GpuMetricGroup.Clocks;

    public static string DisplayName(MetricSource? source) => source switch
    {
        null => "Auto",
        MetricSource.WindowsCounters => "Windows performance counters",
        MetricSource.Nvml => "NVIDIA NVML",
        MetricSource.Adlx => "AMD ADLX",
        MetricSource.Igcl => "Intel IGCL",
        MetricSource.LibreHardwareMonitor => "LibreHardwareMonitor",
        _ => source.ToString()!,
    };
    public static SourcePreferences Build(MetricSource? main, IReadOnlyDictionary<GpuMetricGroup, MetricSource?> overrides, bool strict)
    {
        var map = new Dictionary<GpuMetricGroup, MetricSource>();
        foreach (var g in Enum.GetValues<GpuMetricGroup>())
        {
            var chosen = overrides.TryGetValue(g, out var o) && o is not null ? o : main;
            if (chosen is { } c && Supports(c, g)) map[g] = c;
        }
        return new SourcePreferences { Preferred = map, Strict = strict };
    }
    public static IReadOnlyList<MetricSource?> Options(GpuMetricGroup group, IReadOnlyCollection<MetricSource> available) =>
        [null, .. Selectable.Where(s => available.Contains(s) && Supports(s, group)).Cast<MetricSource?>()];
    public static MetricSource? Parse(string? value, IReadOnlyCollection<MetricSource> available) =>
        Enum.TryParse<MetricSource>(value, ignoreCase: true, out var s) && available.Contains(s) ? s : null;
}
