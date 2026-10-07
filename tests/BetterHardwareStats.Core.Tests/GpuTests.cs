using BetterHardwareStats.Core.Counters;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Tests;

public class GpuInstanceParserTests
{
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1C3_phys_0_eng_0_engtype_3D", 1234, 0xD1C3L, 0, 0, EngineKind.Graphics3D)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_5_engtype_Compute_0", 8, 0xF00DL, 0, 5, EngineKind.Compute)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_2_engtype_Copy", 8, 0xF00DL, 0, 2, EngineKind.Copy)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_1_eng_3_engtype_VideoDecode", 8, 0xF00DL, 1, 3, EngineKind.VideoDecode)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_4_engtype_VideoEncode", 8, 0xF00DL, 0, 4, EngineKind.VideoEncode)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_6_engtype_VideoProcessing", 8, 0xF00DL, 0, 6, EngineKind.VideoProcessing)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_7_engtype_Cuda", 8, 0xF00DL, 0, 7, EngineKind.Compute)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_8_engtype_High Priority 3D", 8, 0xF00DL, 0, 8, EngineKind.Graphics3D)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_9_engtype_Security", 8, 0xF00DL, 0, 9, EngineKind.Other)]
    [InlineData("pid_8_luid_0x00000000_0x0000F00D_phys_0_eng_9_engtype_LegacyOverlay", 8, 0xF00DL, 0, 9, EngineKind.Other)]
    public void ParsesEngineNames(string name, int pid, long luid, int phys, int eng, EngineKind kind)
    {
        Assert.True(GpuInstanceParser.TryParseEngine(name, out var i));
        Assert.Equal(pid, i.Pid);
        Assert.Equal(luid, i.Luid.Value);
        Assert.Equal(phys, i.Phys);
        Assert.Equal(eng, i.Engine);
        Assert.Equal(kind, i.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("pid_x_luid_0x00000000_0x0000D1C3_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x0000_0x0000D1C3_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x00000000_0x0000D1C3_phys_0")]
    [InlineData("luid_0x00000000_0x0000D1C3_phys_0_eng_0")]
    public void MalformedNamesDoNotThrow(string name)
    {
        Assert.False(GpuInstanceParser.TryParseEngine(name, out _));
    }

    [Fact]
    public void LuidHighPartIsSigned()
    {
        Assert.True(GpuInstanceParser.TryParseMemory("luid_0xFFFFFFFF_0x00000001_phys_0", out var m));
        Assert.Equal(-1, m.Luid.HighPart);
        Assert.Equal(1u, m.Luid.LowPart);
        Assert.Equal(Luid.FromParts(-1, 1), m.Luid);
    }

    [Fact]
    public void ParsesMemoryNames()
    {
        Assert.True(GpuInstanceParser.TryParseMemory("luid_0x00000000_0x0000D1C3_phys_0", out var m));
        Assert.Equal(0xD1C3, m.Luid.Value);
        Assert.False(GpuInstanceParser.TryParseMemory("luid_0x00000000_0x0000D1C3_phys_0_extra", out _));
    }
}

public class GpuCounterAggregatorTests
{
    private static readonly Luid A = new(0xA);
    private static readonly Luid B = new(0xB);

    private static string E(int pid, Luid l, int eng, string type, int phys = 0) =>
        $"pid_{pid}_luid_0x{l.HighPart:X8}_0x{l.LowPart:X8}_phys_{phys}_eng_{eng}_engtype_{type}";

    private static string M(Luid l) => $"luid_0x{l.HighPart:X8}_0x{l.LowPart:X8}_phys_0";

    [Fact]
    public void SumsPidsPerEngineAndTakesMaxAcrossEngines()
    {
        var engine = new ArrayCounterReader([
            (E(1, A, 0, "3D"), 30.0), (E(2, A, 0, "3D"), 25.0),   // 3D engine = 55
            (E(1, A, 1, "Copy"), 40.0),                          // copy = 40
            (E(3, B, 0, "3D"), 5.0),
        ]);
        var r = new GpuCounterAggregator().Aggregate(engine, null, null, [], [A, B])!;
        Assert.Equal(55f, r[A].UtilizationPercent, 3);
        Assert.Equal(40f, r[A].EnginePercent[EngineKind.Copy], 3);
        Assert.Equal(5f, r[B].UtilizationPercent, 3);
    }

    [Fact]
    public void ClampsAt100()
    {
        var engine = new ArrayCounterReader([(E(1, A, 0, "3D"), 80.0), (E(2, A, 0, "3D"), 70.0)]);
        var r = new GpuCounterAggregator().Aggregate(engine, null, null, [], [A])!;
        Assert.Equal(100f, r[A].UtilizationPercent);
    }

    [Fact]
    public void ZeroInstancesIsZeroButMissingCounterSetIsNull()
    {
        var agg = new GpuCounterAggregator();
        var healthy = agg.Aggregate(new ArrayCounterReader(Array.Empty<(string, double)>()), null, null, [], [A])!;
        Assert.Equal(0f, healthy[A].UtilizationPercent);
        Assert.Equal(0, healthy[A].EngineInstanceCount);
        Assert.Null(agg.Aggregate(ArrayCounterReader.Missing, null, null, [], [A]));
    }

    [Fact]
    public void InvalidItemsIgnored()
    {
        var engine = new ArrayCounterReader([(E(1, A, 0, "3D"), 90.0, false), (E(1, A, 1, "Copy"), 10.0, true)]);
        var r = new GpuCounterAggregator().Aggregate(engine, null, null, [], [A])!;
        Assert.Equal(10f, r[A].UtilizationPercent);
    }

    [Fact]
    public void VRChatLoadOnlyCountsVRChatPids()
    {
        var engine = new ArrayCounterReader([(E(100, A, 0, "3D"), 60.0), (E(7, A, 0, "3D"), 20.0), (E(7, B, 0, "3D"), 50.0)]);
        var r = new GpuCounterAggregator().Aggregate(engine, null, null, [100], [A, B])!;
        Assert.Equal(60f, r[A].VRChatUtilizationPercent);
        Assert.Equal(0f, r[B].VRChatUtilizationPercent);
        Assert.Equal(80f, r[A].UtilizationPercent);
    }

    [Fact]
    public void ReadsMemoryCountersPerAdapter()
    {
        var engine = new ArrayCounterReader(Array.Empty<(string, double)>());
        var ded = new ArrayCounterReader([(M(A), 1_000_000.0), (M(B), 5.0)]);
        var sh = new ArrayCounterReader([(M(A), 2_000.0)]);
        var r = new GpuCounterAggregator().Aggregate(engine, ded, sh, [], [A, B])!;
        Assert.Equal(1_000_000L, r[A].DedicatedUsedBytes);
        Assert.Equal(2_000L, r[A].SharedUsedBytes);
        Assert.Null(r[B].SharedUsedBytes);
    }

    [Fact]
    public void VideoEngines()
    {
        var engine = new ArrayCounterReader([(E(1, A, 3, "VideoEncode"), 12.0), (E(1, A, 4, "VideoDecode"), 7.0)]);
        var r = new GpuCounterAggregator().Aggregate(engine, null, null, [], [A])!;
        Assert.Equal(12f, r[A].VideoEncodePercent);
        Assert.Equal(7f, r[A].VideoDecodePercent);
    }
}

public class GpuCatalogTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    internal static RawAdapter Raw(
        string name, ushort vendor, long dedicated, long shared = 16 * GiB, PciAddress? pci = null,
        AdapterTypeFlags? flags = null, long luid = 1, ushort device = 0x1234, bool software = false) =>
        new(name, vendor, device, 0x11112222, 0xA1, dedicated, 0, shared, new Luid(luid), software, false, pci, flags);

    private static AdapterTypeFlags Flags(bool render = true, bool hd = false, bool hi = false, bool sw = false, bool ind = false, bool pv = false) =>
        new(render, true, sw, hd, hi, ind, pv);

    [Fact]
    public void IntelIGpuIsIntegratedByHeuristic()
    {
        var (kind, _) = GpuCatalogBuilder.Classify(Raw("Intel(R) UHD Graphics", 0x8086, 128 * MiB));
        Assert.Equal(GpuKind.Integrated, kind);
    }

    [Fact]
    public void AmdApuWithLargeCarveOutDefeatsHeuristicWithoutFlags()
    {
        var (kind, _) = GpuCatalogBuilder.Classify(Raw("AMD Radeon(TM) Graphics", 0x1002, 4 * GiB));
        Assert.Equal(GpuKind.Discrete, kind);
        var (kind2, reason) = GpuCatalogBuilder.Classify(Raw("AMD Radeon(TM) Graphics", 0x1002, 4 * GiB, flags: Flags(hi: true)));
        Assert.Equal(GpuKind.Integrated, kind2);
        Assert.Contains("HybridIntegrated", reason);
    }

    [Fact]
    public void NvidiaDGpuDiscrete()
    {
        Assert.Equal(GpuKind.Discrete, GpuCatalogBuilder.Classify(Raw("NVIDIA GeForce RTX 4070", 0x10DE, 8 * GiB)).Kind);
        Assert.Equal(GpuKind.Discrete, GpuCatalogBuilder.Classify(Raw("NVIDIA GeForce RTX 4070", 0x10DE, 256 * MiB, flags: Flags(hd: true))).Kind);
    }

    [Fact]
    public void BackendKindAppliesUnlessHybridFlagsDecided()
    {
        var id = GpuCatalogBuilder.Identify(Raw("AMD Radeon(TM) Graphics", 0x1002, 4 * GiB));
        var updated = GpuCatalogBuilder.WithBackendKind(id, GpuKind.Integrated);
        Assert.Equal(GpuKind.Integrated, updated.Kind);
        Assert.True(updated.IsUnifiedMemory);

        var flagged = GpuCatalogBuilder.Identify(Raw("x", 0x1002, 4 * GiB, flags: Flags(hd: true)));
        Assert.Equal(GpuKind.Discrete, GpuCatalogBuilder.WithBackendKind(flagged, GpuKind.Integrated).Kind);
    }

    [Fact]
    public void FiltersSoftwareMicrosoftVirtualAndDisplayOnly()
    {
        var raw = new[]
        {
            Raw("Microsoft Basic Render Driver", 0x1414, 0),
            Raw("WARP", 0x1234, 0, software: true),
            Raw("Virtual Display", 0x1234, 0, flags: Flags(ind: true)),
            Raw("VM", 0x1234, 0, flags: Flags(pv: true)),
            Raw("Display only", 0x1234, 0, flags: Flags(render: false)),
            Raw("NVIDIA GeForce RTX 4070", 0x10DE, 8 * GiB, flags: Flags()),
        };
        var cat = GpuCatalogBuilder.Build(raw, new CatalogOptions());
        Assert.Single(cat.Adapters);
        Assert.Equal(6, cat.Decisions.Count);
        Assert.Equal(2, GpuCatalogBuilder.Build(raw, new CatalogOptions(IncludeVirtualAdapters: true)).Adapters.Count(a => a.Name is "Virtual Display" or "VM") + 0);
    }

    [Fact]
    public void NeverReturnsEmptyWhenAdaptersExist()
    {
        var cat = GpuCatalogBuilder.Build([Raw("Microsoft Basic Render Driver", 0x1414, 0)], new CatalogOptions());
        Assert.Single(cat.Adapters);
        Assert.NotEmpty(cat.Warnings);
    }

    [Fact]
    public void IdenticalCardsSeparatedByPci()
    {
        var a = Raw("RTX", 0x10DE, 8 * GiB, pci: new PciAddress(1, 0, 0), luid: 5);
        var b = Raw("RTX", 0x10DE, 8 * GiB, pci: new PciAddress(2, 0, 0), luid: 3);
        var cat = GpuCatalogBuilder.Build([a, b], new CatalogOptions());
        Assert.Equal(2, cat.Adapters.Select(x => x.StableKey).Distinct().Count());
        Assert.EndsWith("1.0.0", cat.Adapters[0].StableKey);
        Assert.Empty(cat.Warnings);
    }

    [Fact]
    public void IdenticalCardsWithoutPciGetSuffixInLuidOrder()
    {
        var a = Raw("RTX", 0x10DE, 8 * GiB, luid: 5);
        var b = Raw("RTX", 0x10DE, 8 * GiB, luid: 3);
        var cat = GpuCatalogBuilder.Build([a, b], new CatalogOptions());
        Assert.EndsWith("nopci#2", cat.Adapters[0].StableKey);
        Assert.EndsWith("nopci#1", cat.Adapters[1].StableKey);
        Assert.NotEmpty(cat.Warnings);
    }

    [Fact]
    public void StableKeyFormat()
    {
        var k = GpuCatalogBuilder.BaseStableKey(Raw("x", 0x10DE, 0, pci: new PciAddress(1, 2, 3), device: 0x2786));
        Assert.Equal("10DE:2786:11112222:A1:1.2.3", k);
    }

    [Fact]
    public void IncludeIntegratedOffRemovesIGpu()
    {
        var cat = GpuCatalogBuilder.Build(
            [Raw("Intel(R) UHD", 0x8086, 128 * MiB, luid: 1), Raw("RTX", 0x10DE, 8 * GiB, luid: 2)],
            new CatalogOptions(IncludeIntegrated: false));
        Assert.Single(cat.Adapters);
        Assert.Equal(GpuKind.Discrete, cat.Adapters[0].Kind);
    }
}

public class CorrelatorTests
{
    private const long GiB = 1L << 30;

    private static GpuIdentity Id(string name, ushort vendor, long vram, long luid, PciAddress? pci = null, ushort device = 0x1234) =>
        GpuCatalogBuilder.Identify(GpuCatalogTests.Raw(name, vendor, vram, pci: pci, luid: luid, device: device));

    [Fact]
    public void LuidWins()
    {
        var cat = new[] { Id("A", 0x10DE, 8 * GiB, 1), Id("B", 0x10DE, 8 * GiB, 2) };
        var r = BackendCorrelator.CorrelateOne(cat, new BackendDevice("k", GpuVendor.Nvidia, null, null, null, null, null, new Luid(2), null));
        Assert.Equal(1, r.Rule);
        Assert.Equal(cat[1].StableKey, r.StableKey);
    }

    [Fact]
    public void PciForNvml()
    {
        var cat = new[] { Id("A", 0x10DE, 8 * GiB, 1, new PciAddress(1, 0, 0)), Id("B", 0x10DE, 8 * GiB, 2, new PciAddress(2, 0, 0)) };
        var r = BackendCorrelator.CorrelateOne(cat, new BackendDevice("uuid", GpuVendor.Nvidia, "B", new PciAddress(2, 0, 0), null, null, null, null, null));
        Assert.Equal(2, r.Rule);
        Assert.Equal(cat[1].StableKey, r.StableKey);
    }

    [Fact]
    public void TwoIdenticalAmdAdaptersAreNeverGuessed()
    {
        var cat = new[] { Id("AMD Radeon RX 7800 XT", 0x1002, 16 * GiB, 1), Id("AMD Radeon RX 7800 XT", 0x1002, 16 * GiB, 2) };
        var d = new BackendDevice("adlx0", GpuVendor.Amd, "AMD Radeon RX 7800 XT", null, 0x1234, 0x11112222, 16 * GiB, null, 0);
        var r = BackendCorrelator.CorrelateOne(cat, d);
        Assert.Null(r.StableKey);
        Assert.Contains("unmapped", r.Trace);
    }

    [Fact]
    public void AmbiguousRuleFallsThroughToNext()
    {
        // Same device id + VRAM (rule 3 ambiguous), distinguishable by name (rule 4).
        var cat = new[] { Id("Radeon RX 7600", 0x1002, 8 * GiB, 1), Id("Radeon RX 7600 XT", 0x1002, 8 * GiB, 2) };
        var d = new BackendDevice("adlx1", GpuVendor.Amd, "AMD Radeon RX 7600 XT", null, 0x1234, 0x11112222, 8 * GiB, null, 1);
        var r = BackendCorrelator.CorrelateOne(cat, d);
        Assert.Equal(4, r.Rule);
        Assert.Equal(cat[1].StableKey, r.StableKey);
    }

    [Fact]
    public void LhmNameMatchAndOrdinalFallback()
    {
        var cat = new[] { Id("Intel(R) UHD Graphics 770", 0x8086, 128L << 20, 1), Id("NVIDIA GeForce RTX 4070", 0x10DE, 12 * GiB, 2) };
        var byName = BackendCorrelator.CorrelateOne(cat, new BackendDevice("/gpu-nvidia/0", GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", null, null, null, 12 * GiB, null, 0));
        Assert.Equal(4, byName.Rule);
        var byOrdinal = BackendCorrelator.CorrelateOne(cat, new BackendDevice("/gpu-intel-integrated/0", GpuVendor.Intel, "Intel UHD", null, null, null, null, null, 0));
        Assert.Equal(5, byOrdinal.Rule);
        Assert.Equal(cat[0].StableKey, byOrdinal.StableKey);
    }

    [Fact]
    public void BackendIgpuWindowsDoesNotListCannotStealTheLuidMatch()
    {
        // ADLX lists an uncatalogued iGPU before a discrete GPU with a matching LUID.
        // The weaker vendor match must not claim the discrete adapter.
        var cat = new[] { Id("AMD Radeon RX 7900 XT", 0x1002, 20 * GiB, 0x165F0) };
        var igpu = new BackendDevice("adlx:28160", GpuVendor.Amd, "AMD Radeon(TM) Graphics", null, 0x164E, null, 512L << 20, null, 0, GpuKind.Integrated);
        var dgpu = new BackendDevice("adlx:768", GpuVendor.Amd, "AMD Radeon RX 7900 XT", null, 0x744C, null, 20 * GiB, new Luid(0x165F0), 1, GpuKind.Discrete);
        var r = BackendCorrelator.Correlate(cat, [igpu, dgpu]);
        Assert.Null(r[0].StableKey);
        Assert.Equal(cat[0].StableKey, r[1].StableKey);
        Assert.Equal(1, r[1].Rule);
    }

    [Fact]
    public void UniqueVendorOrdinalNeedsUniquenessOnBothSidesAndAgreeingVram()
    {
        var cat = new[] { Id("AMD Radeon RX 7900 XT", 0x1002, 20 * GiB, 1) };
        var a = new BackendDevice("/gpu-amd/0", GpuVendor.Amd, "AMD Radeon(TM) Graphics", null, null, null, null, null, 0);
        var b = new BackendDevice("/gpu-amd/1", GpuVendor.Amd, "Something else", null, null, null, null, null, 1);
        Assert.All(BackendCorrelator.Correlate(cat, [a, b]), x => Assert.Null(x.StableKey));
        // Alone it may use rule 5, unless the VRAM sizes disagree.
        Assert.Equal(5, BackendCorrelator.CorrelateOne(cat, a).Rule);
        Assert.Null(BackendCorrelator.CorrelateOne(cat, a with { DedicatedVramBytes = 512L << 20 }).StableKey);
    }

    [Fact]
    public void TwoDevicesClaimingOneAdapterUnderTheSameRuleBothStayUnmapped()
    {
        var cat = new[] { Id("NVIDIA GeForce RTX 4070", 0x10DE, 12 * GiB, 1), Id("Intel(R) UHD Graphics 770", 0x8086, 128L << 20, 2) };
        var x = new BackendDevice("x", GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070", null, null, null, 12 * GiB, null, null);
        var y = x with { BackendKey = "y" };
        Assert.All(BackendCorrelator.Correlate(cat, [x, y]), r => Assert.Null(r.StableKey));
    }

    [Fact]
    public void NameNormalisation()
    {
        Assert.Equal("rtx 4070", BackendCorrelator.NormalizeName("NVIDIA GeForce RTX 4070"));
        Assert.Equal("uhd graphics 770", BackendCorrelator.NormalizeName("Intel(R) UHD Graphics   770"));
    }
}
