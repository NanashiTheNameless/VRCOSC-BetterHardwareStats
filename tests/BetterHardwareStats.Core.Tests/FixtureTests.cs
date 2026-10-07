using System.Globalization;
using BetterHardwareStats.Core.Counters;
using BetterHardwareStats.Core.Cpu;
using BetterHardwareStats.Core.Disk;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Tests;
public class FixtureTests
{
    private const string Dir = "Fixtures/amd-rx7900xt-r9-7950x";
    private static readonly Luid Gpu = new(0x008CFDA1);
    private static readonly Luid BasicRender = new(0x0001706B);

    private static ArrayCounterReader Load(string file) =>
        new(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, Dir, file))
            .Where(l => l.Length > 0)
            .Select(l => l.Split('\t'))
            .Select(p => (p[0], double.Parse(p[1], CultureInfo.InvariantCulture), bool.Parse(p[2]))));

    [Fact]
    public void EveryEngineInstanceParses()
    {
        var r = Load("GPUEngineUtilizationPercentage.txt");
        Assert.Equal(538, r.Count);
        var kinds = new HashSet<EngineKind>();
        for (var i = 0; i < r.Count; i++)
        {
            Assert.True(GpuInstanceParser.TryParseEngine(r.GetName(i), out var inst), r.GetName(i).ToString());
            Assert.True(inst.Luid == Gpu || inst.Luid == BasicRender);
            kinds.Add(inst.Kind);
        }
        Assert.Contains(EngineKind.Graphics3D, kinds);
        Assert.Contains(EngineKind.Compute, kinds);   // "Compute 0"
        Assert.Contains(EngineKind.Copy, kinds);
        Assert.Contains(EngineKind.VideoCodec, kinds); // "Video Codec Engine" (AMD VCN)
    }

    [Fact]
    public void AggregatesTheRealGpu()
    {
        var results = new GpuCounterAggregator().Aggregate(
            Load("GPUEngineUtilizationPercentage.txt"), Load("GPUAdapterMemoryDedicatedUsage.txt"), Load("GPUAdapterMemorySharedUsage.txt"),
            [], [Gpu])!;
        var g = results[Gpu];
        Assert.InRange(g.UtilizationPercent, 0, 100);
        Assert.Equal(1895555072, g.DedicatedUsedBytes);
        Assert.Equal(208142336, g.SharedUsedBytes);
        Assert.True(g.EngineInstanceCount > 0);
        Assert.NotNull(g.VideoEncodePercent); // VCN counts for both
        Assert.NotNull(g.VideoDecodePercent);
    }

    [Fact]
    public void CpuInstancesAndUsage()
    {
        var util = Load("ProcessorInformationProcessorUtility.txt");
        var names = Enumerable.Range(0, util.Count).Select(i => util.GetName(i).ToString()).ToList();
        Assert.All(names, n => Assert.True(CpuUsageCalculator.TryParseInstance(n, out _), n));
        Assert.Contains("_Total", names);
        Assert.Contains("0,_Total", names);

        var pkg = new CpuPackage(0, "AMD Ryzen 9 7950X", [new GroupAffinity(0, 0xFFFFFFFF)]);
        var r = CpuUsageCalculator.Compute(pkg, 1, util, Load("ProcessorInformationProcessorFrequency.txt"), Load("ProcessorInformationProcessorPerformance.txt"));
        Assert.InRange(r.UsagePercent!.Value, 0, 100);
        Assert.InRange(r.FrequencyMhz!.Value, 1000, 6000);

        // Multi-package path over the same data must agree closely with _Total for a single package.
        var multi = CpuUsageCalculator.Compute(pkg, 2, util, null, null);
        Assert.InRange(Math.Abs(multi.UsagePercent!.Value - r.UsagePercent.Value), 0, 1);
    }

    [Fact]
    public void DiskInstances()
    {
        var r = DiskCalculators.Aggregate(Load("PhysicalDiskIdleTime.txt"), Load("PhysicalDiskDiskReadBytessec.txt"), Load("PhysicalDiskDiskWriteBytessec.txt"))!;
        Assert.Contains('C', r.ActivityByLetter.Keys);
        Assert.Contains('X', r.ActivityByLetter.Keys);
        Assert.Equal(97401.70952365667, r.WriteBytesPerSec!.Value, 6); // _Total excluded, no double count
        Assert.InRange(r.ActivityPercent, 0, 100);
    }
}
