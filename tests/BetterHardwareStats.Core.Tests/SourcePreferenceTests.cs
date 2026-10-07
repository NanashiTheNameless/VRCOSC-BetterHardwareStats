using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Tests;

public class SourcePreferenceTests
{
    [Fact]
    public void IntelAutoUsesIgclSensorsAndKeepsWindowsUtilization()
    {
        var gpu = G.Id("intel", GpuKind.Discrete, 8 * G.GiB, GpuVendor.Intel);
        var snapshot = new GpuMetricResolver(G.Opts()).Resolve(gpu,
        [
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 30 }),
            S(MetricSource.Igcl, new() { TemperatureC = 55, PowerWatts = 80, CoreClockMhz = 1500 }),
            S(MetricSource.LibreHardwareMonitor, new() { TemperatureC = 60 }),
        ], 0);
        Assert.Equal(MetricSource.Igcl, snapshot.TemperatureC!.Source);
        Assert.Equal(55, snapshot.TemperatureC.Value);
        Assert.Equal(MetricSource.Igcl, snapshot.PowerWatts!.Source);
        Assert.Equal(1500, snapshot.CoreClockMhz!.Value);
        Assert.Equal(MetricSource.WindowsCounters, snapshot.UtilizationPercent!.Source);
    }

    [Fact]
    public void IgclIsOnlyOfferedForSupportedGroups()
    {
        Assert.Equal([null, MetricSource.Igcl], SourceCatalog.Options(GpuMetricGroup.Temperature, [MetricSource.Igcl]));
        Assert.Equal([null, MetricSource.Igcl], SourceCatalog.Options(GpuMetricGroup.Power, [MetricSource.Igcl]));
        Assert.Equal([null, MetricSource.Igcl], SourceCatalog.Options(GpuMetricGroup.Clocks, [MetricSource.Igcl]));
        Assert.Equal([null], SourceCatalog.Options(GpuMetricGroup.Utilization, [MetricSource.Igcl]));
        Assert.Equal([null], SourceCatalog.Options(GpuMetricGroup.Memory, [MetricSource.Igcl]));
        Assert.Equal([null], SourceCatalog.Options(GpuMetricGroup.Fan, [MetricSource.Igcl]));
        var preferences = SourceCatalog.Build(MetricSource.Igcl, new Dictionary<GpuMetricGroup, MetricSource?>(), false);
        Assert.Equal(MetricSource.Igcl, preferences.For(GpuMetricGroup.Temperature));
        Assert.Null(preferences.For(GpuMetricGroup.Utilization));
    }

    private static SourcedMetrics S(MetricSource src, PartialGpuMetrics m) => new(src, 0, m);

    private static readonly SourcedMetrics[] All =
    [
        S(MetricSource.WindowsCounters, new() { UtilizationPercent = 40, VramUsedBytes = 3 * G.GiB }),
        S(MetricSource.Nvml, new() { UtilizationPercent = 90, TemperatureC = 60, VramUsedBytes = 2 * G.GiB, VramTotalBytes = 8 * G.GiB }),
        S(MetricSource.LibreHardwareMonitor, new() { UtilizationPercent = 0, TemperatureC = 61, PowerWatts = 100, VramUsedBytes = G.GiB, VramTotalBytes = 8 * G.GiB }),
    ];

    [Fact]
    public void ChosenToolIsUsedForEveryGroup()
    {
        var s = new GpuMetricResolver(G.Opts(G.Prefer(MetricSource.LibreHardwareMonitor))).Resolve(G.Dgpu(), All, 0);
        Assert.Equal(MetricSource.LibreHardwareMonitor, s.UtilizationPercent!.Source);
        Assert.Equal(61, s.TemperatureC!.Value);
        Assert.Equal(G.GiB, s.VramUsedBytes!.Value);
        Assert.Equal(MetricSource.LibreHardwareMonitor, s.VramTotalBytes!.Source);
    }

    [Fact]
    public void NonStrictFallsBackWhereTheToolDoesNotApply()
    {
        // NVML chosen, but the AMD iGPU has no NVML data: it still reports through the Auto order.
        var amd = G.Id("apu", GpuKind.Integrated, 512 * G.MiB, GpuVendor.Amd);
        var s = new GpuMetricResolver(G.Opts(G.Prefer(MetricSource.Nvml))).Resolve(amd, [
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 7 }),
            S(MetricSource.Adlx, new() { TemperatureC = 55 }),
        ], 0);
        Assert.Equal(7, s.UtilizationPercent!.Value);
        Assert.Equal(MetricSource.Adlx, s.TemperatureC!.Source);
    }

    [Fact]
    public void StrictLeavesMissingInsteadOfFallingBack()
    {
        var s = new GpuMetricResolver(G.Opts(G.Prefer(MetricSource.Adlx, strict: true))).Resolve(G.Dgpu(), All, 0);
        Assert.Null(s.UtilizationPercent);
        Assert.Null(s.TemperatureC);
        Assert.Null(s.VramUsedBytes);
    }

    [Fact]
    public void ChoosingAToolForMemoryReplacesTheSharedMemoryRuleOnIntegratedGpus()
    {
        var apu = G.Id("apu", GpuKind.Integrated, 512 * G.MiB, GpuVendor.Amd);
        SourcedMetrics[] src =
        [
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 5, VramUsedBytes = 100 * G.MiB, SharedUsedBytes = 2 * G.GiB }),
            S(MetricSource.Adlx, new() { VramUsedBytes = 300 * G.MiB, VramTotalBytes = 512 * G.MiB }),
        ];
        var auto = new GpuMetricResolver(G.Opts()).Resolve(apu, src, 0);
        Assert.Equal(2 * G.GiB + 100 * G.MiB, auto.VramUsedBytes!.Value);
        var adlx = new GpuMetricResolver(G.Opts(G.Prefer(MetricSource.Adlx))).Resolve(apu, src, 0);
        Assert.Equal(300 * G.MiB, adlx.VramUsedBytes!.Value);
        Assert.Equal(512 * G.MiB, adlx.VramTotalBytes!.Value);
    }

    [Fact]
    public void BuildCombinesMainChoiceAndOverrides()
    {
        var p = SourceCatalog.Build(MetricSource.Nvml,
            new Dictionary<GpuMetricGroup, MetricSource?> { [GpuMetricGroup.Fan] = MetricSource.LibreHardwareMonitor, [GpuMetricGroup.Power] = null }, false);
        Assert.Equal(MetricSource.Nvml, p.For(GpuMetricGroup.Power));
        Assert.Equal(MetricSource.LibreHardwareMonitor, p.For(GpuMetricGroup.Fan));
        Assert.Empty(SourceCatalog.Build(null, new Dictionary<GpuMetricGroup, MetricSource?>(), false).Preferred);
    }

    [Fact]
    public void OptionsOnlyListToolsThisSystemHas()
    {
        var amdOnly = new[] { MetricSource.WindowsCounters, MetricSource.Adlx, MetricSource.LibreHardwareMonitor };
        Assert.Equal([null, MetricSource.Adlx, MetricSource.LibreHardwareMonitor], SourceCatalog.Options(GpuMetricGroup.Temperature, amdOnly));
        Assert.Equal([null], SourceCatalog.Options(GpuMetricGroup.Utilization, [MetricSource.WindowsCounters]));
        Assert.Null(SourceCatalog.Parse("Nvml", amdOnly)); // saved choice no longer available: Auto
        Assert.Equal(MetricSource.Adlx, SourceCatalog.Parse("Adlx", amdOnly));
    }
}

public class SlotStateCodecTests
{
    [Fact]
    public void RoundTripsAndSkipsGarbage()
    {
        var t = new DateTime(2026, 10, 7, 1, 2, 3, DateTimeKind.Utc);
        var rs = new[] { new SlotReservation("10DE:2786:12345678:A1:1.0.0", 0, t, GpuKind.Discrete), new SlotReservation("1002:164E:00000000:C1:nopci#1", 1, t, GpuKind.Integrated) };
        var s = SlotStateCodec.Encode(rs);
        Assert.Equal(rs, SlotStateCodec.Decode(s));
        Assert.Equal(2, SlotStateCodec.Decode(s + ";junk;a|b|c|d;|1|2|Discrete").Count);
        Assert.Empty(SlotStateCodec.Decode(null));
    }
}
