using System.Runtime.InteropServices;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Windows.Adlx;
using BetterHardwareStats.Windows.Native;
using BetterHardwareStats.Windows.Nvml;
using BetterHardwareStats.Windows.Service;

namespace BetterHardwareStats.Windows.Tests;
[Trait("Hardware", "Windows")]
public class HardwareTests(ITestOutputHelper output)
{
    [Fact]
    public void LayoutSizesMatchHeaders()
    {
        // Guards against accidental edits of the hand-written layouts.
        Assert.Equal(8, IntPtr.Size);
        var flags = D3dkmt.Decode(0b0010_0001);
        Assert.True(flags.RenderSupported);
        Assert.True(flags.HybridIntegrated);
        Assert.False(flags.HybridDiscrete);
        Assert.Equal(0u, NvmlBackend.ParseFunction("00000000:01:00.0"));
        Assert.Equal(1u, NvmlBackend.ParseFunction("00000000:01:00.1"));
    }

    [Fact]
    public void DxgiFindsAtLeastOneRealAdapter()
    {
        var errors = new List<string>();
        var raw = DxgiEnumerator.Enumerate(errors);
        foreach (var a in raw)
            output.WriteLine($"{a.Description} {a.VendorId:X4}:{a.DeviceId:X4} LUID {a.Luid} PCI {a.Pci?.ToString() ?? "n/a"} flags {a.TypeFlags} dedicated {a.DedicatedVideoMemory >> 20} MiB");
        foreach (var e in errors) output.WriteLine("error: " + e);
        var catalog = GpuCatalogBuilder.Build(raw, new CatalogOptions());
        Assert.NotEmpty(catalog.Adapters);
        Assert.DoesNotContain(catalog.Adapters, a => a.VendorId == 0x1414);
        Assert.All(catalog.Adapters, a => Assert.NotEqual(GpuKind.Unknown, a.Kind));
    }

    [Fact]
    public void D3dkmtReturnsPciForPhysicalAdapters()
    {
        var raw = DxgiEnumerator.Enumerate().Where(a => !a.IsSoftwareFlag && a.VendorId != 0x1414).ToList();
        Assert.NotEmpty(raw);
        Assert.All(raw, a => Assert.NotNull(a.TypeFlags));
        Assert.Contains(raw, a => a.Pci is not null);
    }

    [Fact]
    public void GpuCountersProduceUtilizationForEveryAdapter()
    {
        var consumer = new GpuCounterConsumer();
        var catalog = GpuCatalogBuilder.Build(DxgiEnumerator.Enumerate(), new CatalogOptions()).Adapters;
        consumer.SetKnownAdapters(catalog.Select(c => c.Luid).ToList());
        using var engine = new PdhEngine([consumer]);
        Assert.Empty(engine.AddFailures);
        engine.Collect(0);
        Thread.Sleep(1000);
        Assert.True(engine.Collect(System.Diagnostics.Stopwatch.GetTimestamp()));
        var sample = consumer.Latest?.Value;
        Assert.NotNull(sample);
        foreach (var c in catalog)
        {
            var m = sample!.ByBackendKey[WindowsCounterGpuSource.KeyFor(c.Luid)];
            output.WriteLine($"{c.Name}: util {m.UtilizationPercent} dedicated {m.VramUsedBytes >> 20} MiB shared {m.SharedUsedBytes >> 20} MiB");
            Assert.NotNull(m.UtilizationPercent);
        }
    }

    [Fact]
    public void CpuAndDiskCountersAndSystemApis()
    {
        var packages = CpuTopology.ReadPackages();
        Assert.NotEmpty(packages);
        output.WriteLine($"CPU: {packages[0].Name}, {packages.Count} package(s)");
        var cpu = new CpuCounterConsumer();
        cpu.Select(packages, 0);
        var disk = new DiskCounterConsumer();
        using var engine = new PdhEngine([cpu, disk]);
        engine.Collect(0);
        Thread.Sleep(1000);
        engine.Collect(1);
        Assert.NotNull(cpu.Latest?.Value.UsagePercent);
        output.WriteLine($"CPU usage {cpu.Latest!.Value.UsagePercent:F1}% at {cpu.Latest.Value.FrequencyMhz:F0} MHz; disk activity {disk.Latest?.Value.ActivityPercent:F1}%");

        var mem = MemoryStatus.Read();
        Assert.NotNull(mem);
        Assert.True(mem!.Value.Total > mem.Value.Available);
    }

    [Fact]
    public void NetworkTableReadsInterfaces()
    {
        var rows = NetworkTable.Read();
        Assert.NotEmpty(rows);
        Assert.False(NetworkTable.LastReadUsedFallback);
        Assert.Contains(rows, r => r.IsLoopback);
        var route = NetworkTable.DefaultRouteKey();
        output.WriteLine($"{rows.Count} interfaces; default route: {rows.FirstOrDefault(r => r.Key == route)?.Alias ?? "none"}");
    }

    [Fact]
    public void NvmlEnumeratesWhenPresent()
    {
        if (NvmlBackend.FindLibrary() is null) { output.WriteLine("no NVML on this system"); return; }
        var nvml = new NvmlBackend();
        try
        {
            Assert.Null(nvml.Initialize());
            Assert.NotEmpty(nvml.Devices);
            var s = nvml.Sample();
            foreach (var d in nvml.Devices)
                output.WriteLine($"{d.Name} PCI {d.Pci}: util {s.ByBackendKey[d.BackendKey].UtilizationPercent} temp {s.ByBackendKey[d.BackendKey].TemperatureC}");
        }
        finally
        {
            nvml.Shutdown();
        }
    }

    [Fact]
    public void AdlxEnumeratesWhenPresent()
    {
        if (!AdlxBackend.LibraryPresent()) { output.WriteLine("no ADLX on this system"); return; }
        var adlx = new AdlxBackend();
        try
        {
            var reason = adlx.Initialize();
            Assert.True(reason is null, reason);
            Assert.NotEmpty(adlx.Devices);
            var s = adlx.Sample();
            foreach (var d in adlx.Devices)
            {
                s.ByBackendKey.TryGetValue(d.BackendKey, out var m);
                output.WriteLine($"{d.Name} {d.ReportedKind} LUID {d.Luid} VRAM {d.DedicatedVramBytes >> 20} MiB: util {m?.UtilizationPercent} temp {m?.TemperatureC} power {m?.PowerWatts}");
            }
            // Every ADLX GPU whose LUID is in the DXGI catalog must map to exactly that adapter by LUID (rule 1). ADLX can
            // also list GPUs Windows does not present (an iGPU without a WDDM adapter); those must stay unmapped, never
            // borrow another GPU's slot.
            var catalog = GpuCatalogBuilder.Build(DxgiEnumerator.Enumerate(), new CatalogOptions()).Adapters;
            var results = BackendCorrelator.Correlate(catalog, adlx.Devices);
            foreach (var c in results) output.WriteLine($"{c.Device.BackendKey} ({c.Device.Name}) -> {c.StableKey ?? "unmapped"} rule {c.Rule}");
            Assert.Contains(results, c => c.StableKey is not null);
            foreach (var c in results)
            {
                var byLuid = catalog.FirstOrDefault(a => c.Device.Luid is { } l && a.Luid == l);
                if (byLuid is not null) { Assert.Equal(byLuid.StableKey, c.StableKey); Assert.Equal(1, c.Rule); }
            }
            var mapped = results.Where(c => c.StableKey is not null).Select(c => c.StableKey).ToList();
            Assert.Equal(mapped.Count, mapped.Distinct().Count());
        }
        finally
        {
            adlx.Shutdown();
        }
    }

    [Fact]
    public void ServiceStartsQuicklyAndPublishes()
    {
        using var svc = new HardwareService(new HardwareServiceOptions(), null, output.WriteLine);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        svc.Start();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"start took {sw.ElapsedMilliseconds} ms (AC-6)");
        Thread.Sleep(4000);
        var s = svc.Latest;
        Assert.True(s.Sequence > 0);
        Assert.NotEmpty(s.Gpu.Adapters);
        Assert.NotNull(s.Gpu.SelectedStableKey);
        Assert.NotNull(s.Ram.TotalBytes);
        output.WriteLine(svc.DiagnosticsReport());
    }

    [Fact]
    public void SourceDetectionListsVendorToolsForPresentGpus()
    {
        var d = SourceDetection.Detect();
        output.WriteLine($"available: {string.Join(", ", d.Available)}; {string.Join("; ", d.Reasons.Select(r => $"{r.Key}: {r.Value}"))}");
        Assert.Contains(MetricSource.WindowsCounters, d.Available);
        Assert.True(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
    }
}
