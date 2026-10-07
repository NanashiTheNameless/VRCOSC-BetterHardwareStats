using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Tests;

internal static class G
{
    public const long MiB = 1024L * 1024;
    public const long GiB = 1024L * MiB;
    public const long Tps = 1000; // test ticks per second

    public static GpuIdentity Id(string key, GpuKind kind, long vram, GpuVendor vendor = GpuVendor.Nvidia, long luid = 1, string? name = null, long shared = 16 * GiB) =>
        new(key, new Luid(luid), vendor, 0x10DE, 0x1234, 0, 0, null, name ?? key, kind, kind == GpuKind.Integrated, vram, shared);

    public static GpuIdentity Dgpu(string key = "dgpu", long luid = 2) => Id(key, GpuKind.Discrete, 8 * GiB, GpuVendor.Nvidia, luid, "NVIDIA GeForce RTX 4070");

    public static GpuIdentity Igpu(string key = "igpu", long luid = 1) => Id(key, GpuKind.Integrated, 128 * MiB, GpuVendor.Intel, luid, "Intel(R) UHD Graphics");

    public static ResolverOptions Opts(SourcePreferences? sources = null, float smoothing = 0) =>
        new() { PollIntervalTicks = Tps, TicksPerSecond = Tps, Sources = sources ?? SourcePreferences.Auto, Smoothing = smoothing };

    public static SourcePreferences Prefer(MetricSource s, bool strict = false, params GpuMetricGroup[] groups) => new()
    {
        Preferred = (groups.Length == 0 ? Enum.GetValues<GpuMetricGroup>() : groups).ToDictionary(g => g, _ => s),
        Strict = strict,
    };

    public static GpuSnapshot Snap(GpuIdentity id, float? util, float? vrchat = null, long ts = 0) =>
        new(id, util is null ? null : new(util.Value, MetricSource.WindowsCounters, ts), new Dictionary<EngineKind, float>(),
            null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            vrchat is null ? null : new(vrchat.Value, MetricSource.WindowsCounters, ts));
}

public class LeaderTrackerTests
{
    private const long W = 5000;

    private static void Window(LeaderTracker t, long start, params (string Key, double V)[] samples)
    {
        foreach (var (k, v) in samples) t.Add(k, v, start);
    }

    [Fact]
    public void FirstLeaderIsAdoptedAfterOneWindow()
    {
        var t = new LeaderTracker(W, 3, 1);
        Window(t, 0, ("a", 50), ("b", 10));
        Assert.Null(t.Leader);
        Assert.True(t.Advance(W));
        Assert.Equal("a", t.Leader);
    }

    [Fact]
    public void SwitchNeedsThreeConsecutiveWindows()
    {
        var t = new LeaderTracker(W, 3, 1);
        Window(t, 0, ("a", 50), ("b", 10));
        t.Advance(W);
        Window(t, W, ("a", 10), ("b", 50));
        t.Advance(2 * W);
        Assert.Equal("a", t.Leader);
        Window(t, 2 * W, ("a", 10), ("b", 50));
        t.Advance(3 * W);
        Assert.Equal("a", t.Leader);
        Window(t, 3 * W, ("a", 10), ("b", 50));
        Assert.True(t.Advance(4 * W));
        Assert.Equal("b", t.Leader);
    }

    [Fact]
    public void InterruptedStreakResets()
    {
        var t = new LeaderTracker(W, 3, 1);
        Window(t, 0, ("a", 50));
        t.Advance(W);
        Window(t, W, ("b", 50), ("a", 1.5));
        t.Advance(2 * W);
        Window(t, 2 * W, ("b", 50), ("a", 1.5));
        t.Advance(3 * W);
        Window(t, 3 * W, ("a", 50), ("b", 1.5)); // a wins again, streak broken
        t.Advance(4 * W);
        Window(t, 4 * W, ("b", 50));
        t.Advance(5 * W);
        Assert.Equal("a", t.Leader);
    }

    [Fact]
    public void BelowThresholdIsNoneAndLosingLeaderNeedsHysteresis()
    {
        var t = new LeaderTracker(W, 3, 1);
        Window(t, 0, ("a", 0.5));
        t.Advance(W);
        Assert.Null(t.Leader);

        Window(t, W, ("a", 30));
        t.Advance(2 * W);
        Assert.Equal("a", t.Leader);
        for (var i = 2; i < 4; i++)
        {
            Window(t, i * W, ("a", 0));
            t.Advance((i + 1) * W);
            Assert.Equal("a", t.Leader);
        }
        Window(t, 4 * W, ("a", 0));
        t.Advance(5 * W);
        Assert.Null(t.Leader);
    }

    [Fact]
    public void RetainDropsGoneLeaderImmediately()
    {
        var t = new LeaderTracker(W, 3, 1);
        Window(t, 0, ("a", 50));
        t.Advance(W);
        Assert.True(t.Retain(["b"]));
        Assert.Null(t.Leader);
    }

    [Fact]
    public void TiesAreDeterministic()
    {
        var t = new LeaderTracker(W, 1, 0);
        Window(t, 0, ("b", 10), ("a", 10));
        t.Advance(W);
        Assert.Equal("a", t.Leader);
        Window(t, W, ("b", 10), ("a", 10), ("c", 10));
        t.Advance(2 * W);
        Assert.Equal("a", t.Leader); // current leader keeps a tie
    }
}

public class GpuMetricResolverTests
{
    private static SourcedMetrics S(MetricSource src, PartialGpuMetrics m, long ts = 0) => new(src, ts, m);

    [Fact]
    public void AutoUsesWindowsUtilizationAndVendorSensors()
    {
        var r = new GpuMetricResolver(G.Opts());
        var s = r.Resolve(G.Dgpu(), [
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 40 }),
            S(MetricSource.Nvml, new() { UtilizationPercent = 90, TemperatureC = 60, PowerWatts = 120 }),
            S(MetricSource.LibreHardwareMonitor, new() { TemperatureC = 61, FanRpm = 1200 }),
        ], 0);

        Assert.Equal(40, s.UtilizationPercent!.Value);
        Assert.Equal(MetricSource.WindowsCounters, s.UtilizationPercent.Source);
        Assert.Equal(MetricSource.Nvml, s.TemperatureC!.Source);
        Assert.Equal(120, s.PowerWatts!.Value);
        Assert.Equal(MetricSource.LibreHardwareMonitor, s.FanRpm!.Source);
        Assert.True(s.Present);
    }

    [Fact]
    public void AutoFallsBackToVendorWhenCountersHaveNothing()
    {
        var r = new GpuMetricResolver(G.Opts());
        var s = r.Resolve(G.Dgpu(), [S(MetricSource.Nvml, new() { UtilizationPercent = 90 })], 0);
        Assert.Equal(MetricSource.Nvml, s.UtilizationPercent!.Source);

        var strict = new GpuMetricResolver(G.Opts(G.Prefer(MetricSource.LibreHardwareMonitor, strict: true)));
        var s2 = strict.Resolve(G.Dgpu(), [S(MetricSource.Nvml, new() { UtilizationPercent = 90 })], 0);
        Assert.Null(s2.UtilizationPercent);
        Assert.False(s2.Present);
    }

    [Fact]
    public void VendorApiModePrefersVendor()
    {
        var r = new GpuMetricResolver(G.Opts(G.Prefer(MetricSource.Nvml)));
        var s = r.Resolve(G.Dgpu(), [
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 40 }),
            S(MetricSource.Nvml, new() { UtilizationPercent = 90 }),
        ], 0);
        Assert.Equal(90, s.UtilizationPercent!.Value);
    }

    [Fact]
    public void StaleSourceIsSkippedForFreshLowerPriority()
    {
        var r = new GpuMetricResolver(G.Opts());
        var now = 10 * G.Tps;
        var s = r.Resolve(G.Dgpu(), [
            S(MetricSource.Nvml, new() { TemperatureC = 60 }, ts: now - 4 * G.Tps), // older than max(3 x 1 s, 3 s)
            S(MetricSource.LibreHardwareMonitor, new() { TemperatureC = 70 }, ts: now - G.Tps),
        ], now);
        Assert.Equal(70, s.TemperatureC!.Value);

        var s2 = r.Resolve(G.Dgpu(), [S(MetricSource.Nvml, new() { TemperatureC = 60 }, ts: now - 3 * G.Tps)], now);
        Assert.Equal(60, s2.TemperatureC!.Value);
    }

    [Fact]
    public void DisabledSourceIsSkipped()
    {
        var o = G.Opts() with { DisabledSources = new HashSet<MetricSource> { MetricSource.Nvml } };
        var s = new GpuMetricResolver(o).Resolve(G.Dgpu(), [
            S(MetricSource.Nvml, new() { TemperatureC = 60 }),
            S(MetricSource.LibreHardwareMonitor, new() { TemperatureC = 70 }),
        ], 0);
        Assert.Equal(70, s.TemperatureC!.Value);
    }

    [Fact]
    public void DiscreteVramUsesCountersAndDxgiTotal()
    {
        var s = new GpuMetricResolver(G.Opts()).Resolve(G.Dgpu(), [
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 1, VramUsedBytes = 2 * G.GiB, SharedUsedBytes = G.GiB }),
        ], 0);
        Assert.Equal(2 * G.GiB, s.VramUsedBytes!.Value);
        Assert.Equal(8 * G.GiB, s.VramTotalBytes!.Value);
        Assert.Equal(MetricSource.Dxgi, s.VramTotalBytes.Source);
        Assert.Equal(G.GiB, s.SharedUsedBytes!.Value);
        Assert.Equal(0.25f, s.VramFraction);
    }

    [Fact]
    public void UnifiedMemoryAddsSharedAndDedicated()
    {
        var igpu = G.Igpu();
        var s = new GpuMetricResolver(G.Opts()).Resolve(igpu, [
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 5, VramUsedBytes = 64 * G.MiB, SharedUsedBytes = G.GiB }),
        ], 0);
        Assert.Equal(G.GiB + 64 * G.MiB, s.VramUsedBytes!.Value);
        Assert.Equal(128 * G.MiB + 16 * G.GiB, s.VramTotalBytes!.Value);
    }

    [Fact]
    public void NanIsTreatedAsMissing()
    {
        var s = new GpuMetricResolver(G.Opts()).Resolve(G.Dgpu(), [
            S(MetricSource.Nvml, new() { TemperatureC = float.NaN }),
            S(MetricSource.LibreHardwareMonitor, new() { TemperatureC = 55 }),
        ], 0);
        Assert.Equal(55, s.TemperatureC!.Value);
    }

    [Fact]
    public void SmoothingIsEmaAndResetsOnMissing()
    {
        var r = new GpuMetricResolver(G.Opts(smoothing: 0.5f));
        var id = G.Dgpu();
        Assert.Equal(100, r.Resolve(id, [S(MetricSource.WindowsCounters, new() { UtilizationPercent = 100 })], 0).UtilizationPercent!.Value);
        Assert.Equal(50, r.Resolve(id, [S(MetricSource.WindowsCounters, new() { UtilizationPercent = 0 })], 0).UtilizationPercent!.Value);
        Assert.Null(r.Resolve(id, [], 0).UtilizationPercent);
        Assert.Equal(80, r.Resolve(id, [S(MetricSource.WindowsCounters, new() { UtilizationPercent = 80 })], 0).UtilizationPercent!.Value);
    }
}

public class SlotAssignerTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstRunUsesSortOrder()
    {
        var a = new SlotAssigner();
        var r = a.Assign([G.Igpu(), G.Id("small", GpuKind.Discrete, 4 * G.GiB), G.Dgpu(), G.Id("unk", GpuKind.Unknown, 0)], T0);
        Assert.Equal(["dgpu", "small", "igpu", "unk"], r.Slots);
        Assert.True(r.PersistenceChanged);
    }

    [Fact]
    public void PersistedSlotsSurviveAndNewGpuTakesLowestFree()
    {
        var a = new SlotAssigner([new SlotReservation("igpu", 0, T0), new SlotReservation("dgpu", 2, T0)]);
        var r = a.Assign([G.Dgpu(), G.Igpu(), G.Id("new", GpuKind.Discrete, 24 * G.GiB)], T0);
        Assert.Equal(["igpu", "new", "dgpu", null], r.Slots);
    }

    [Fact]
    public void UpgradedGpuTakesOverTheOldSlot()
    {
        var a = new SlotAssigner();
        a.Assign([G.Dgpu(), G.Igpu()], T0);

        var r = a.Assign([G.Igpu(), G.Id("new", GpuKind.Discrete, 16 * G.GiB, name: "RTX 5080")], T0.AddDays(1));
        Assert.Equal(["new", "igpu", null, null], r.Slots);
        Assert.Contains(r.Messages, m => m.Contains("slot 0") && m.Contains("dgpu"));
        Assert.DoesNotContain(a.Export(), x => x.StableKey == "dgpu");

        // A returning card gets a new slot when its reservation has been reassigned.
        var back = a.Assign([G.Igpu(), G.Id("new", GpuKind.Discrete, 16 * G.GiB), G.Dgpu()], T0.AddDays(2));
        Assert.Equal(["new", "igpu", "dgpu", null], back.Slots);
    }

    [Fact]
    public void TemporarilyMissingGpuGetsItsSlotBack()
    {
        var a = new SlotAssigner();
        a.Assign([G.Dgpu(), G.Igpu()], T0);
        var gone = a.Assign([G.Igpu()], T0.AddDays(3)); // eGPU unplugged, nothing new appeared
        Assert.Equal([null, "igpu", null, null], gone.Slots);
        Assert.Empty(gone.Messages);
        Assert.Equal(["dgpu", "igpu", null, null], a.Assign([G.Dgpu(), G.Igpu()], T0.AddDays(5)).Slots);
    }

    [Fact]
    public void ReservationExpiresAfterSevenDays()
    {
        var a = new SlotAssigner();
        a.Assign([G.Dgpu(), G.Igpu()], T0);
        a.Assign([G.Igpu()], T0.AddDays(8));
        Assert.DoesNotContain(a.Export(), x => x.StableKey == "dgpu");
    }

    [Fact]
    public void DifferentKindDoesNotTakeAHeldSlotWhileFreeSlotsExist()
    {
        var a = new SlotAssigner();
        a.Assign([G.Dgpu(), G.Igpu()], T0);
        var r = a.Assign([G.Dgpu(), G.Id("igpu2", GpuKind.Unknown, 0)], T0.AddDays(1));
        Assert.Equal(["dgpu", null, "igpu2", null], r.Slots); // igpu still has slot 1 held
    }

    [Fact]
    public void WhenAllSlotsAreHeldTheLongestMissingIsReplaced()
    {
        var a = new SlotAssigner();
        var d = Enumerable.Range(0, 4).Select(i => G.Id($"d{i}", GpuKind.Discrete, (10 - i) * G.GiB)).ToList();
        a.Assign(d, T0);
        a.Assign([d[0], d[1], d[3]], T0.AddDays(1)); // d2 gone at day 1
        a.Assign([d[0], d[1]], T0.AddDays(2));       // d3 gone at day 2

        // An integrated GPU appears: no same-kind hold, no free slot, so d2 (missing longest) yields slot 2.
        var r = a.Assign([d[0], d[1], G.Igpu()], T0.AddDays(3));
        Assert.Equal(["d0", "d1", "igpu", null], r.Slots);
        Assert.Empty(r.Overflow);
        Assert.Contains(a.Export(), x => x.StableKey == "d3" && x.Slot == 3);
    }

    [Fact]
    public void OverflowWarnsOnce()
    {
        var a = new SlotAssigner();
        var gpus = Enumerable.Range(0, 5).Select(i => G.Id($"g{i}", GpuKind.Discrete, (10 - i) * G.GiB)).ToList();
        var r = a.Assign(gpus, T0);
        Assert.Equal(["g4"], r.Overflow);
        Assert.NotNull(r.Warning);
        Assert.Null(a.Assign(gpus, T0).Warning);
    }

    [Fact]
    public void CorruptPersistenceIsIgnored()
    {
        var a = new SlotAssigner([
            new SlotReservation("x", 9, T0),
            new SlotReservation("a", 1, T0),
            new SlotReservation("b", 1, T0.AddDays(-1)),
        ]);
        Assert.Equal(["a"], a.Export().Select(r => r.StableKey));
    }
}

public class GpuSelectionEngineTests
{
    private const long W = 5 * G.Tps;

    [Fact]
    public void AutoVRChatFallsBackToDiscreteThenFollowsVRChat()
    {
        var dgpu = G.Dgpu();
        var igpu = G.Igpu();
        var ids = new[] { dgpu, igpu };
        var e = new GpuSelectionEngine(W);

        var r0 = e.Select(ids, new());
        Assert.Equal("dgpu", r0.StableKey);
        Assert.StartsWith("fallback", r0.Reason);

        // VRChat renders on the iGPU.
        for (long t = 0; t <= W; t += G.Tps)
            e.Observe([G.Snap(dgpu, 5, 0), G.Snap(igpu, 60, 55)], t);
        var r1 = e.Select(ids, new());
        Assert.Equal("igpu", r1.StableKey);
        Assert.True(r1.Changed);
        Assert.Equal("VRChat GPU", r1.Reason);
    }

    [Fact]
    public void IncludeIntegratedOffNeverSelectsIgpuWhenDiscreteExists()
    {
        var ids = new[] { G.Dgpu(), G.Igpu() };
        var e = new GpuSelectionEngine(W);
        Assert.Equal("dgpu", e.Select(ids, new(GpuSelectionMode.IntegratedFirst, IncludeIntegrated: false)).StableKey);
        Assert.Equal("igpu", e.Select(ids, new(GpuSelectionMode.IntegratedFirst)).StableKey);
    }

    private static GpuIdentity Real(string name, ushort device, string pci, GpuKind kind = GpuKind.Discrete, long vram = 12 * G.GiB) =>
        G.Id($"10DE:{device:X4}:00000000:A1:{pci}", kind, vram, GpuVendor.Nvidia, name: name);

    [Fact]
    public void SpecificSelectsTheChosenGpuEvenWhenIntegratedIsExcluded()
    {
        var ids = new[] { G.Dgpu(), G.Igpu() };
        var e = new GpuSelectionEngine(W);
        var r = e.Select(ids, new(GpuSelectionMode.Specific, "igpu", IncludeIntegrated: false));
        Assert.Equal("igpu", r.StableKey);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void SpecificFollowsTheSameModelToANewPciSlot()
    {
        var moved = Real("NVIDIA GeForce RTX 4070", 0x2786, "2.0.0");
        var e = new GpuSelectionEngine(W);
        var r = e.Select([moved, G.Igpu()], new(GpuSelectionMode.Specific, "10DE:2786:00000000:A1:1.0.0"));
        Assert.Equal(moved.StableKey, r.StableKey);
        Assert.Contains("same model", r.Warning);
    }

    [Fact]
    public void SpecificMissingFallsBackToAutoAndWarnsOnce()
    {
        // Two matching models make the saved choice ambiguous.
        var a = Real("NVIDIA GeForce RTX 4070", 0x2786, "3.0.0");
        var b = Real("NVIDIA GeForce RTX 4070", 0x2786, "4.0.0");
        var e = new GpuSelectionEngine(W);
        var opts = new SelectionOptions(GpuSelectionMode.Specific, "10DE:2786:00000000:A1:1.0.0");
        var r = e.Select([a, b], opts);
        Assert.NotNull(r.StableKey); // discrete-first fallback, never nothing
        Assert.Contains("Auto (VRChat's GPU)", r.Warning);
        Assert.Contains("NVIDIA GeForce RTX 4070", r.Warning);
        Assert.Null(e.Select([a, b], opts).Warning);
    }

    [Fact]
    public void ChoicesListAutoModesThenGpusByFriendlyNameAndNumberTwins()
    {
        var twin1 = Real("NVIDIA GeForce RTX 4070", 0x2786, "1.0.0");
        var twin2 = Real("NVIDIA GeForce RTX 4070", 0x2786, "2.0.0");
        var igpu = G.Igpu();
        var choices = GpuChoices.Build([igpu, twin2, twin1]);
        Assert.Equal(GpuChoices.Automatic.Select(c => c.Title), choices.Take(4).Select(c => c.Title));
        Assert.Equal([$"NVIDIA GeForce RTX 4070 #1 [{twin1.StableKey}]", $"NVIDIA GeForce RTX 4070 #2 [{twin2.StableKey}]", $"Intel(R) UHD Graphics [{igpu.StableKey}]"], choices.Skip(4).Select(c => c.Title));
        Assert.Equal((GpuSelectionMode.Specific, twin1.StableKey), GpuChoices.Parse(choices[4].Value));
        Assert.Equal(choices.Count, choices.Select(c => c.Value).Distinct().Count());
    }

    [Theory]
    [InlineData(null, GpuSelectionMode.AutoVRChat)]
    [InlineData("", GpuSelectionMode.AutoVRChat)]
    [InlineData("gpu:", GpuSelectionMode.AutoVRChat)]
    [InlineData("nonsense", GpuSelectionMode.AutoVRChat)]
    [InlineData(GpuChoices.AutoHighestLoad, GpuSelectionMode.AutoHighestLoad)]
    [InlineData(GpuChoices.DiscreteFirst, GpuSelectionMode.DiscreteFirst)]
    [InlineData(GpuChoices.IntegratedFirst, GpuSelectionMode.IntegratedFirst)]
    public void ParseMapsSavedValues(string? value, GpuSelectionMode mode) => Assert.Equal(mode, GpuChoices.Parse(value).Mode);

    [Fact]
    public void ModelKeyDropsThePciLocation()
    {
        Assert.Equal("1002:744C:471E1DA2:CC", GpuChoices.ModelKey("1002:744C:471E1DA2:CC:3.0.0"));
        Assert.Equal("1002:744C:471E1DA2:CC", GpuChoices.ModelKey("1002:744C:471E1DA2:CC:nopci#2"));
    }

    [Fact]
    public void DiscreteFirstFallbackIsHighestVram()
    {
        var a = G.Id("a", GpuKind.Unknown, 2 * G.GiB);
        var b = G.Id("b", GpuKind.Integrated, 512 * G.MiB);
        Assert.Equal("a", GpuSelectionEngine.DiscreteFirst([b, a]).Key);
    }
}

public class GpuMergerTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MergesWindowsAndNvmlByCorrelation()
    {
        var dgpu = G.Dgpu() with { Pci = new PciAddress(1, 0, 0) };
        var igpu = G.Igpu();
        IReadOnlyList<GpuIdentity> catalog = [dgpu, igpu];

        var win = new GpuSourceInput(
            new BackendStatus("WindowsCounters", BackendState.Running, null),
            MetricSource.WindowsCounters,
            WindowsCounterGpuSource.DevicesFor(catalog),
            new BackendSample(0, new Dictionary<string, PartialGpuMetrics>
            {
                [WindowsCounterGpuSource.KeyFor(dgpu.Luid)] = new() { UtilizationPercent = 70 },
                [WindowsCounterGpuSource.KeyFor(igpu.Luid)] = new() { UtilizationPercent = 3 },
            }));
        var nvml = new GpuSourceInput(
            new BackendStatus("NVML", BackendState.Running, null),
            MetricSource.Nvml,
            [new BackendDevice("nvml:0", GpuVendor.Nvidia, "RTX", new PciAddress(1, 0, 0), null, null, null, null, 0)],
            new BackendSample(0, new Dictionary<string, PartialGpuMetrics> { ["nvml:0"] = new() { TemperatureC = 65 } }));
        var disabled = new GpuSourceInput(
            new BackendStatus("ADLX", BackendState.Disabled, "no AMD GPU"), MetricSource.Adlx, [], null);

        var m = new GpuMerger(new SlotAssigner(), new GpuSelectionEngine(5 * G.Tps));
        var r = m.Merge(catalog, [win, nvml, disabled], new GpuMergeOptions(G.Opts(), new()), 0, Now, 1);

        Assert.Equal(70, r.Set.Find("dgpu")!.UtilizationPercent!.Value);
        Assert.Equal(65, r.Set.Find("dgpu")!.TemperatureC!.Value);
        Assert.Null(r.Set.Find("igpu")!.TemperatureC);
        Assert.Equal("dgpu", r.Set.SelectedStableKey);
        Assert.Equal(["dgpu", "igpu", null, null], r.Set.SlotStableKeys);
        Assert.Equal(3, r.Set.Backends.Count);
        Assert.Contains(r.NewCorrelations, c => c.Device.BackendKey == "nvml:0" && c.Rule == 2);

        // Second tick with the same device lists reuses the cached correlation.
        var r2 = m.Merge(catalog, [win, nvml, disabled], new GpuMergeOptions(G.Opts(), new()), 0, Now, 2);
        Assert.Empty(r2.NewCorrelations);
        Assert.False(r2.SelectionChanged);
    }

    [Fact]
    public void BackendReportedKindRefinesUnknown()
    {
        var unk = G.Id("u", GpuKind.Unknown, 512 * G.MiB, GpuVendor.Amd, 9);
        var dev = new BackendDevice("adlx:0", GpuVendor.Amd, null, null, null, null, null, new Luid(9), 0, GpuKind.Integrated);
        var src = new GpuSourceInput(new BackendStatus("ADLX", BackendState.Running, null), MetricSource.Adlx, [dev], null);
        var r = new GpuMerger(new SlotAssigner()).Merge([unk], [src], new GpuMergeOptions(G.Opts(), new()), 0, Now, 1);
        Assert.Equal(GpuKind.Integrated, r.Set.Adapters[0].Identity.Kind);
    }
}

public class WindowsCounterGpuSourceTests
{
    private static GpuCounterResult R(float util, int engines, long? ded = null) =>
        new(util, new Dictionary<EngineKind, float>(), null, null, 0, ded, null, engines);

    [Fact]
    public void AdapterWithMemoryInstanceButNoEnginesIsIdleNotMissing()
    {
        var s = new WindowsCounterGpuSource(10 * G.Tps);
        var a = new Luid(1);
        var b = new Luid(2);
        for (long t = 0; t <= 20 * G.Tps; t += G.Tps)
        {
            var sample = s.Convert(new Dictionary<Luid, GpuCounterResult> { [a] = R(50, 3, 1), [b] = R(0, 0, 0) }, t)!;
            Assert.Equal(0, sample.ByBackendKey[WindowsCounterGpuSource.KeyFor(b)].UtilizationPercent);
        }
    }

    [Fact]
    public void AdapterNeverInCountersGoesNullAfterGrace()
    {
        var s = new WindowsCounterGpuSource(10 * G.Tps);
        var a = new Luid(1);
        var b = new Luid(2);
        BackendSample Tick(long t) => s.Convert(new Dictionary<Luid, GpuCounterResult> { [a] = R(50, 3, 1), [b] = R(0, 0) }, t)!;

        Assert.Equal(0, Tick(0).ByBackendKey[WindowsCounterGpuSource.KeyFor(b)].UtilizationPercent);
        Assert.Equal(0, Tick(9 * G.Tps).ByBackendKey[WindowsCounterGpuSource.KeyFor(b)].UtilizationPercent);
        Assert.Null(Tick(10 * G.Tps).ByBackendKey[WindowsCounterGpuSource.KeyFor(b)].UtilizationPercent);
        Assert.Contains(b, s.AbsentAdapters);
    }

    [Fact]
    public void MissingCounterSetIsNull()
    {
        Assert.Null(new WindowsCounterGpuSource().Convert(null, 0));
    }
}
