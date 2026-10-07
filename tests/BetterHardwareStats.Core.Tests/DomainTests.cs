using BetterHardwareStats.Core.Counters;
using BetterHardwareStats.Core.Cpu;
using BetterHardwareStats.Core.Disk;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Network;

namespace BetterHardwareStats.Core.Tests;

public class CpuUsageCalculatorTests
{
    private static readonly CpuPackage Single = new(0, "Test CPU", [new GroupAffinity(0, 0xF)]);

    private static ArrayCounterReader R(params (string, double)[] items) => new(items);

    [Theory]
    [InlineData("_Total", ProcessorInstanceKind.Total, -1, -1)]
    [InlineData("0,_Total", ProcessorInstanceKind.GroupTotal, 0, -1)]
    [InlineData("1,13", ProcessorInstanceKind.Logical, 1, 13)]
    public void ParsesInstances(string name, ProcessorInstanceKind kind, int group, int index)
    {
        Assert.True(CpuUsageCalculator.TryParseInstance(name, out var i));
        Assert.Equal(new ProcessorInstance(kind, group, index), i);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x,1")]
    [InlineData("0,")]
    [InlineData("0,a")]
    public void RejectsMalformed(string name) => Assert.False(CpuUsageCalculator.TryParseInstance(name, out _));

    [Fact]
    public void SinglePackageUsesTotalAndClamps()
    {
        var usage = R(("0,0", 10), ("0,1", 20), ("0,_Total", 15), ("_Total", 112));
        Assert.Equal(100, CpuUsageCalculator.Compute(Single, 1, usage, null, null).UsagePercent);
    }

    [Fact]
    public void SinglePackageWithoutTotalAveragesLogicals()
    {
        var usage = R(("0,0", 10), ("0,1", 30));
        Assert.Equal(20, CpuUsageCalculator.Compute(Single, 1, usage, null, null).UsagePercent);
    }

    [Fact]
    public void MultiPackageAveragesOwnLogicalProcessors()
    {
        var p0 = new CpuPackage(0, "A", [new GroupAffinity(0, 0b0011)]);
        var p1 = new CpuPackage(1, "B", [new GroupAffinity(0, 0b1100), new GroupAffinity(1, 0b1)]);
        var usage = R(("0,0", 10), ("0,1", 20), ("0,2", 60), ("0,3", 80), ("1,0", 100), ("_Total", 54));
        Assert.Equal(15, CpuUsageCalculator.Compute(p0, 2, usage, null, null).UsagePercent);
        Assert.Equal(80, CpuUsageCalculator.Compute(p1, 2, usage, null, null).UsagePercent);
    }

    [Fact]
    public void FrequencyIsNominalTimesPerformance()
    {
        var nominal = R(("_Total", 3000), ("0,0", 3000));
        var perf = R(("0,0", 150), ("_Total", 120));
        Assert.Equal(3600, CpuUsageCalculator.Compute(Single, 1, R(("_Total", 5)), nominal, perf).FrequencyMhz);
    }

    [Fact]
    public void MissingCountersAreNullNotZero()
    {
        var r = CpuUsageCalculator.Compute(Single, 1, ArrayCounterReader.Missing, ArrayCounterReader.Missing, null);
        Assert.Null(r.UsagePercent);
        Assert.Null(r.FrequencyMhz);
    }

    [Fact]
    public void InvalidIndexListsCpus()
    {
        var (idx, warn) = CpuUsageCalculator.ResolveIndex(3, [Single]);
        Assert.Equal(0, idx);
        Assert.Contains("0: Test CPU", warn);
        Assert.Null(CpuUsageCalculator.ResolveIndex(0, [Single]).Warning);
    }
}

public class DiskCalculatorTests
{
    [Theory]
    [InlineData("0 C:", 0, "C")]
    [InlineData("1 D: E:", 1, "DE")]
    [InlineData("2", 2, "")]
    [InlineData("3 f:", 3, "F")]
    public void ParsesInstances(string name, int index, string letters)
    {
        Assert.True(DiskCalculators.TryParseInstance(name, out var d));
        Assert.Equal(index, d.Index);
        Assert.Equal(letters, new string(d.Letters.ToArray()));
    }

    [Fact]
    public void RejectsTotal() => Assert.False(DiskCalculators.TryParseInstance("_Total", out _));

    [Fact]
    public void AggregateIsMaxActivityAndSummedThroughput()
    {
        var idle = new ArrayCounterReader([("0 C:", 90.0), ("1 D: E:", 20.0), ("2", 105.0), ("_Total", 50.0)]);
        var read = new ArrayCounterReader([("0 C:", 100.0), ("1 D: E:", 50.0), ("_Total", 150.0)]);
        var r = DiskCalculators.Aggregate(idle, read, ArrayCounterReader.Missing)!;
        Assert.Equal(80, r.ActivityPercent);
        Assert.Equal(150, r.ReadBytesPerSec);
        Assert.Null(r.WriteBytesPerSec);
        Assert.Equal(10, r.ActivityByLetter['C'], 3);
        Assert.Equal(80, r.ActivityByLetter['E']);
    }

    [Fact]
    public void MissingCounterSetIsNull() => Assert.Null(DiskCalculators.Aggregate(ArrayCounterReader.Missing, null, null));

    [Fact]
    public void ParseLettersAcceptsCommonForms()
    {
        Assert.Equal("CDE", new string(DiskCalculators.ParseLetters(@"c:\, D: e d").ToArray()));
        Assert.Equal("ABCD", new string(DiskCalculators.ParseLetters("A B C D E").ToArray()));
        Assert.Empty(DiskCalculators.ParseLetters("  "));
    }

    [Fact]
    public void DefaultSlotsPutSystemDriveFirst()
    {
        VolumeInfo[] vols = [new('E', true, false, true), new('C', true, false, true), new('D', true, false, true),
                             new('F', false, true, true), new('G', true, false, false)];
        Assert.Equal(['C', 'D', 'E', null], DiskCalculators.PlanSlots(null, 'C', vols, false));
        Assert.Equal(['D', 'C', 'E', 'F'], DiskCalculators.PlanSlots("", 'D', vols, true));
        Assert.Equal(['Z', 'C', null, null], DiskCalculators.PlanSlots("Z,C", 'C', vols, false));
    }

    [Fact]
    public void DropdownSlotsReserveExplicitChoicesAndPreserveEmptySlots()
    {
        VolumeInfo[] volumes = [new('C', true, false, true), new('D', true, false, true), new('E', true, false, true)];
        Assert.Equal([null, 'D', 'C', 'E'], DiskCalculators.PlanSelectedSlots(["None", "D", "Auto", "Auto"], 'C', volumes, false));
        Assert.Equal(['D', null, 'C', 'E'], DiskCalculators.PlanSelectedSlots(["D", "d", "Auto", "Auto"], 'C', volumes, false));
        Assert.Equal(['Z', 'C', null, 'D'], DiskCalculators.PlanSelectedSlots(["Z", "Auto", "None", "Auto"], 'C', volumes, false));
        Assert.Equal(['C', 'D', 'E', null], DiskCalculators.PlanSelectedSlots([], 'C', volumes, false));
    }

    [Fact]
    public void DropdownSlotsHandleRemovedLockedAndRemovableVolumes()
    {
        VolumeInfo[] volumes = [new('C', true, false, true, 1000, 250), new('D', true, false, false), new('R', false, true, true, 1000, 500)];
        var choices = new string?[] { "D", "R", "Auto", "None" };
        var plan = DiskCalculators.PlanSelectedSlots(choices, 'C', volumes, false);
        Assert.Equal(['D', 'R', 'C', null], plan);
        var monitored = volumes.Where(v => !v.IsRemovable).ToArray();
        var missing = DiskCalculators.BuildSnapshot(null, plan, monitored, 0);
        Assert.False(missing.Slots[0]!.Present);
        Assert.False(missing.Slots[1]!.Present);
        Assert.True(missing.Slots[2]!.Present);
        var returned = DiskCalculators.BuildSnapshot(null, plan, [volumes[0], volumes[1] with { IsReady = true, TotalBytes = 1000 }, volumes[2]], 1);
        Assert.True(returned.Slots[0]!.Present);
        Assert.True(returned.Slots[1]!.Present);
        Assert.Equal("D", choices[0]);
        Assert.Equal(['C', null, null, null], DiskCalculators.PlanSelectedSlots(["Auto", "Auto", "Auto", "Auto"], 'C', volumes, false));
        Assert.Equal(['C', 'R', null, null], DiskCalculators.PlanSelectedSlots(["Auto", "Auto", "Auto", "Auto"], 'C', volumes, true));
    }

    [Fact]
    public void SnapshotJoinsActivityByLetter()
    {
        var counters = new DiskCounterResult(40, 1, 2, new Dictionary<char, float> { ['C'] = 40 });
        VolumeInfo[] vols = [new('C', true, false, true, 1000, 250), new('D', true, false, true, 500, 100)];
        var s = DiskCalculators.BuildSnapshot(counters, ['C', 'D', 'Z', null], vols, 7);
        Assert.True(s.Present);
        Assert.Equal(0.75f, s.Slots[0]!.UsedFraction);
        Assert.Equal(40, s.Slots[0]!.ActivityPercent);
        Assert.Null(s.Slots[1]!.ActivityPercent); // letter not in any instance
        Assert.False(s.Slots[2]!.Present);
        Assert.Null(s.Slots[3]);
    }
}

public class NetworkTests
{
    private const long Tps = 1000;

    private static InterfaceRow Row(string key, ulong rx, ulong tx, bool hw = true, bool up = true, ulong link = 1_000_000_000, string? alias = null) =>
        new(key, alias ?? key, key + " adapter", up, false, hw, false, link, link, rx, tx);

    [Fact]
    public void RatesNeedBaselineAndHandleReset()
    {
        var t = new NetworkRateTracker(Tps);
        t.Update([Row("eth", 1000, 0)], 0);
        Assert.Empty(t.Rates);
        t.Update([Row("eth", 3000, 500)], 2 * Tps);
        Assert.Equal(new InterfaceRate(1000, 250), t.Rates["eth"]);
        t.Update([Row("eth", 10, 600)], 3 * Tps); // counter reset
        Assert.Empty(t.Rates);
        t.Update([Row("eth", 1010, 600)], 4 * Tps);
        Assert.Equal(1000, t.Rates["eth"].DownBytesPerSec);
        Assert.Equal((3000L, 500L), t.Session("eth"));
    }

    [Fact]
    public void CapacityFallsBackTo1Gbps()
    {
        Assert.Equal(1e9, NetworkSelector.Capacity(0, 0));
        Assert.Equal(1e9, NetworkSelector.Capacity(ulong.MaxValue, 0));
        Assert.Equal(2.5e9, NetworkSelector.Capacity(2_500_000_000, 0));
        Assert.Equal(100e6, NetworkSelector.Capacity(2_500_000_000, 100));
    }

    [Fact]
    public void DefaultRouteSelectedAndPresentAfterBaseline()
    {
        var t = new NetworkRateTracker(Tps);
        var sel = new NetworkSelector(5 * Tps);
        var o = new NetworkOptions();
        InterfaceRow[] rows0 = [Row("wifi", 0, 0, alias: "Wi-Fi"), Row("vpn", 0, 0, hw: false)];
        t.Update(rows0, 0);
        var s0 = sel.Select(rows0, t, "vpn", o, 0);
        Assert.False(s0.Present); // baseline only

        InterfaceRow[] rows1 = [Row("wifi", 5000, 100, alias: "Wi-Fi"), Row("vpn", 1000, 50, hw: false)];
        t.Update(rows1, 5 * Tps);
        var s1 = sel.Select(rows1, t, "vpn", o, 5 * Tps);
        Assert.Equal("vpn", s1.AdapterName);
        Assert.True(s1.Present);
        Assert.Equal(200, s1.DownloadBytesPerSec);
    }

    [Fact]
    public void ByNameAndAggregate()
    {
        var t = new NetworkRateTracker(Tps);
        var sel = new NetworkSelector(5 * Tps);
        InterfaceRow[] r0 = [Row("eth", 0, 0, alias: "Ethernet"), Row("wifi", 0, 0, alias: "Wi-Fi"), Row("vpn", 0, 0, hw: false)];
        InterfaceRow[] r1 = [Row("eth", 1000, 0, alias: "Ethernet"), Row("wifi", 3000, 0, alias: "Wi-Fi"), Row("vpn", 900, 0, hw: false)];
        t.Update(r0, 0);
        t.Update(r1, Tps);

        Assert.Equal("Wi-Fi", sel.Select(r1, t, null, new(NetworkSelectionMode.ByName, "wi-fi"), Tps).AdapterName);

        var agg = sel.Select(r1, t, null, new(NetworkSelectionMode.Aggregate), Tps);
        Assert.Equal(4000, agg.DownloadBytesPerSec); // vpn excluded
        Assert.Equal(2e9, agg.DownloadCapacityBitsPerSec);
    }

    [Fact]
    public void SpecificAdapterUsesIdentityDespiteRenameAndDuplicateNames()
    {
        InterfaceRow[] rows = [Row("a", 0, 0, alias: "Ethernet"), Row("b", 0, 0, alias: "Renamed")];
        var rates = new NetworkRateTracker(Tps);
        rates.Update(rows, 0);
        var selected = new NetworkSelector().Select(rows, rates, "a", new(NetworkSelectionMode.ByKey, "b"), 0);
        Assert.Equal("Renamed", selected.AdapterName);
    }

    [Fact]
    public void MissingOrDisconnectedSpecificAdapterFallsBackToRouteAndReturnsWhenAvailable()
    {
        var rates = new NetworkRateTracker(Tps);
        var selector = new NetworkSelector();
        var options = new NetworkOptions(NetworkSelectionMode.ByKey, "b");
        InterfaceRow[] rows = [Row("a", 0, 0, alias: "Route"), Row("b", 0, 0, up: false)];
        rates.Update(rows, 0);
        Assert.Equal("Route", selector.Select(rows, rates, "a", options, 0).AdapterName);
        Assert.Equal("Route", selector.Select(rows[..1], rates, "a", options, 0).AdapterName);
        rows[1] = Row("b", 0, 0, alias: "Chosen");
        Assert.Equal("Chosen", selector.Select(rows, rates, "a", options, 0).AdapterName);
        Assert.Equal("b", options.Name);
        Assert.False(selector.Select([], rates, null, options, 0).Present);
    }

    [Fact]
    public void NoUpInterfaceIsNotPresent()
    {
        var t = new NetworkRateTracker(Tps);
        var s = new NetworkSelector(5 * Tps).Select([Row("eth", 0, 0, up: false)], t, null, new(), 0);
        Assert.False(s.Present);
        Assert.Null(s.AdapterName);
    }
}
