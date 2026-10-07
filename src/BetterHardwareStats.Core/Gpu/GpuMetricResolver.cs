using System.Diagnostics;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public sealed record SourcedMetrics(MetricSource Source, long TimestampTicks, PartialGpuMetrics Metrics);

public sealed record ResolverOptions
{
    public long PollIntervalTicks { get; init; } = Stopwatch.Frequency;
    public long TicksPerSecond { get; init; } = Stopwatch.Frequency;
    public SourcePreferences Sources { get; init; } = SourcePreferences.Auto;
    public float Smoothing { get; init; }
    public IReadOnlySet<MetricSource> DisabledSources { get; init; } = new HashSet<MetricSource>();
    public long MaxAgeTicks => Math.Max(3 * PollIntervalTicks, 3 * TicksPerSecond);
}
public sealed class GpuMetricResolver
{
    private readonly Dictionary<string, float> _ema = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<EngineKind, float> NoEngines = new Dictionary<EngineKind, float>();

    public ResolverOptions Options { get; set; }

    public GpuMetricResolver(ResolverOptions? options = null) => Options = options ?? new ResolverOptions();
    public void Retain(IReadOnlyCollection<string> stableKeys)
    {
        foreach (var k in _ema.Keys.Where(k => !stableKeys.Contains(k)).ToList()) _ema.Remove(k);
    }

    public GpuSnapshot Resolve(GpuIdentity id, IReadOnlyList<SourcedMetrics> sources, long nowTicks)
    {
        var o = Options;
        var fresh = new Dictionary<MetricSource, SourcedMetrics>();
        foreach (var s in sources)
        {
            if (o.DisabledSources.Contains(s.Source)) continue;
            if (nowTicks - s.TimestampTicks > o.MaxAgeTicks) continue;
            if (!fresh.TryGetValue(s.Source, out var prev) || s.TimestampTicks > prev.TimestampTicks) fresh[s.Source] = s;
        }

        // DXGI totals are static facts from discovery, always fresh.
        if (!o.DisabledSources.Contains(MetricSource.Dxgi))
        {
            var dxgiTotal = id.IsUnifiedMemory ? id.DedicatedVramBytes + id.SharedSystemMemoryBytes : id.DedicatedVramBytes;
            if (dxgiTotal > 0)
                fresh[MetricSource.Dxgi] = new SourcedMetrics(MetricSource.Dxgi, nowTicks, new PartialGpuMetrics { VramTotalBytes = dxgiTotal });
        }

        var vendor = VendorSources(id.Vendor);
        MetricValue<float>? F(IEnumerable<MetricSource> order, Func<PartialGpuMetrics, float?> sel) => Pick(fresh, order, sel);
        MetricValue<long>? L(IEnumerable<MetricSource> order, Func<PartialGpuMetrics, long?> sel) => Pick(fresh, order, sel);

        var prefs = o.Sources;
        var autoSensors = vendor.Append(MetricSource.LibreHardwareMonitor).ToArray();
        var tempOrder = prefs.Apply(GpuMetricGroup.Temperature, autoSensors);
        var powerOrder = prefs.Apply(GpuMetricGroup.Power, autoSensors);
        var clockOrder = prefs.Apply(GpuMetricGroup.Clocks, autoSensors);
        var fanOrder = prefs.Apply(GpuMetricGroup.Fan, autoSensors);
        MetricSource[] win = [MetricSource.WindowsCounters];

        var util = Smooth(id.StableKey, F(prefs.Apply(GpuMetricGroup.Utilization, UtilizationOrder(id.Vendor)), m => m.UtilizationPercent));

        IReadOnlyDictionary<EngineKind, float> engines = NoEngines;
        if (fresh.TryGetValue(MetricSource.WindowsCounters, out var w) && w.Metrics.EnginePercent is { } ep) engines = ep;

        var videoOrder = id.Vendor == GpuVendor.Nvidia
            ? new[] { MetricSource.WindowsCounters, MetricSource.Nvml, MetricSource.LibreHardwareMonitor }
            : new[] { MetricSource.WindowsCounters, MetricSource.LibreHardwareMonitor };

        MetricValue<long>? used, total;
        MetricSource[] autoMemory = [.. vendor, MetricSource.WindowsCounters, MetricSource.LibreHardwareMonitor];
        var memoryChosen = prefs.For(GpuMetricGroup.Memory) is not null;
        var memoryOrder = prefs.Apply(GpuMetricGroup.Memory, autoMemory);
        if (!memoryChosen && id.IsUnifiedMemory && UnifiedUsed(fresh, id) is { } unifiedUsed)
        {
            used = unifiedUsed;
            total = L([MetricSource.Dxgi, .. vendor, MetricSource.LibreHardwareMonitor], m => m.VramTotalBytes);
        }
        else if (PairedVram(fresh, memoryOrder) is { } pair)
        {
            (used, total) = pair;
        }
        else if (memoryChosen && prefs.Strict)
        {
            used = total = null;
        }
        else
        {
            used = L(autoMemory, m => m.VramUsedBytes);
            total = L([.. vendor, MetricSource.Dxgi, MetricSource.LibreHardwareMonitor], m => m.VramTotalBytes);
        }

        return new GpuSnapshot(
            id,
            util,
            engines,
            F(videoOrder, m => m.VideoEncodePercent),
            F(videoOrder, m => m.VideoDecodePercent),
            used,
            total,
            L(win, m => m.SharedUsedBytes),
            F(tempOrder, m => m.TemperatureC),
            F(tempOrder, m => m.HotspotC),
            F(tempOrder, m => m.MemoryTemperatureC),
            F(powerOrder, m => m.PowerWatts),
            F(powerOrder, m => m.PowerLimitWatts),
            F(clockOrder, m => m.CoreClockMhz),
            F(clockOrder, m => m.MemoryClockMhz),
            F(fanOrder, m => m.FanPercent),
            F(fanOrder, m => m.FanRpm),
            F(win, m => m.VRChatUtilizationPercent))
        {
            Present = util is not null,
        };
    }

    public static MetricSource[] VendorSources(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => [MetricSource.Nvml],
        GpuVendor.Amd => [MetricSource.Adlx],
        GpuVendor.Intel => [MetricSource.Igcl],
        _ => [],
    };
    public static MetricSource[] UtilizationOrder(GpuVendor v) =>
        [MetricSource.WindowsCounters, .. VendorSources(v), MetricSource.LibreHardwareMonitor];
    private static (MetricValue<long> Used, MetricValue<long> Total)? PairedVram(
        Dictionary<MetricSource, SourcedMetrics> fresh, MetricSource[] order)
    {
        fresh.TryGetValue(MetricSource.Dxgi, out var dxgi);
        foreach (var src in order)
        {
            if (!fresh.TryGetValue(src, out var s) || s.Metrics.VramUsedBytes is not { } u) continue;
            if (s.Metrics.VramTotalBytes is { } t and > 0)
                return (new(u, src, s.TimestampTicks), new(t, src, s.TimestampTicks));
            if (src == MetricSource.WindowsCounters && dxgi?.Metrics.VramTotalBytes is { } dt and > 0)
                return (new(u, src, s.TimestampTicks), new(dt, MetricSource.Dxgi, dxgi.TimestampTicks));
        }
        return null;
    }
    private static MetricValue<long>? UnifiedUsed(Dictionary<MetricSource, SourcedMetrics> fresh, GpuIdentity id)
    {
        if (!fresh.TryGetValue(MetricSource.WindowsCounters, out var w) || w.Metrics.SharedUsedBytes is not { } shared) return null;
        var dedicated = id.DedicatedVramBytes > 0 ? w.Metrics.VramUsedBytes ?? 0 : 0;
        return new MetricValue<long>(shared + dedicated, MetricSource.WindowsCounters, w.TimestampTicks);
    }

    private static MetricValue<T>? Pick<T>(
        Dictionary<MetricSource, SourcedMetrics> fresh, IEnumerable<MetricSource> order, Func<PartialGpuMetrics, T?> sel) where T : struct
    {
        foreach (var src in order)
        {
            if (!fresh.TryGetValue(src, out var s)) continue;
            if (sel(s.Metrics) is { } v && IsUsable(v)) return new MetricValue<T>(v, src, s.TimestampTicks);
        }
        return null;
    }

    private static bool IsUsable<T>(T v) => v switch
    {
        float f => float.IsFinite(f),
        double d => double.IsFinite(d),
        _ => true,
    };

    private MetricValue<float>? Smooth(string key, MetricValue<float>? value)
    {
        var s = Options.Smoothing;
        if (value is null)
        {
            _ema.Remove(key);
            return null;
        }
        if (s <= 0)
        {
            _ema.Remove(key);
            return value;
        }
        var a = 1f - Math.Clamp(s, 0f, 0.99f);
        var y = _ema.TryGetValue(key, out var prev) ? a * value.Value + (1 - a) * prev : value.Value;
        _ema[key] = y;
        return value with { Value = y };
    }
}
