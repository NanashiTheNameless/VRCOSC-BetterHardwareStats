using System.Runtime.Loader;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Windows.Lhm;
using BetterHardwareStats.Windows.Service;

namespace BetterHardwareStats.Windows.Tests;

public class BundleAndDisabledTests
{
    [Fact]
    public void DisabledDomainsStartNoWorkersAndWriteNoLogs()
    {
        var logs = new List<string>();
        using var service = new HardwareService(new HardwareServiceOptions
        {
            EnableGpu = false, EnableCpu = false, EnableRam = false, EnableDisk = false, EnableNetwork = false,
            CpuSensorMode = CpuSensorMode.On,
        }, null, logs.Add);
        service.Start();
        Assert.Empty(service.State().Workers);
        Assert.Empty(service.State().Packages);
        Assert.Empty(service.State().Catalog.Adapters);
        Assert.Empty(logs);
        Assert.Same(HardwareSnapshot.Empty, service.Latest);
    }

    [Fact]
    public void LiveUpdatesKeepWorkersAndSessionTotals()
    {
        var options = new HardwareServiceOptions
        {
            EnableGpu = false, EnableCpu = false, EnableRam = true, EnableDisk = false, EnableNetwork = false,
        };
        var initial = HardwareSnapshot.Empty with
        {
            Sequence = 42,
            Network = NetworkSnapshot.Empty with { SessionDownloadBytes = 123, SessionUploadBytes = 456 },
        };
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var service = new HardwareService(options, null, logs.Enqueue, initial);
        service.Start();
        var workers = service.State().Workers.Select(w => w.Name).ToArray();
        Assert.True(service.TryUpdateOptions(options with { PollIntervalMs = 250, UtilizationSmoothing = 0.3f }));
        Assert.Equal(workers, service.State().Workers.Select(w => w.Name));
        Assert.True(SpinWait.SpinUntil(() => service.Latest.Sequence > 42, TimeSpan.FromSeconds(3)));
        Assert.Equal(123, service.Latest.Network.SessionDownloadBytes);
        Assert.Equal(456, service.Latest.Network.SessionUploadBytes);
        Assert.Single(logs, l => l.StartsWith("Started:"));
        Assert.False(service.TryUpdateOptions(options with { EnableGpu = true }));
        Assert.Equal(workers, service.State().Workers.Select(w => w.Name));
    }

    [Fact]
    public void FailedBridgeLoadCanRetryWithoutRetainingAContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "lhm"));
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "lhm", "LibreHardwareMonitorLib.dll"),
                Path.Combine(directory, "lhm", "LibreHardwareMonitorLib.dll"));
            File.WriteAllText(Path.Combine(directory, "BetterHardwareStats.Windows.dll"), "invalid assembly");
            using var bundle = new BundledLhm(directory);
            for (var i = 0; i < 2; i++)
            {
                Assert.Throws<BadImageFormatException>(() => bundle.CreateGpu());
                var field = typeof(BundledLhm).GetField("_context", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                Assert.Null(field.GetValue(bundle));
            }
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BundledGpuLibraryIsStableAndIsolatedFromDefaultContext()
    {
        using var bundle = new BundledLhm();
        using var gpu = bundle.CreateGpu();
        Assert.Equal("0.9.6.0", gpu.Version);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "lhm", "LibreHardwareMonitorLib.dll"), gpu.Location);
        var context = AssemblyLoadContext.GetLoadContext(gpu.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.True(context!.IsCollectible);
        Assert.Contains(context.Assemblies, a => a.GetName().Name == "LibreHardwareMonitorLib" && a.GetName().Version == new Version(0, 9, 6, 0));
        Assert.DoesNotContain(context.Assemblies, a => a.GetName().Name == "BetterHardwareStats.Core");
        gpu.Sample(); // The sample crosses the context through shared Core contracts.
    }

    [Fact]
    public void MissingLocalBundleNeverFallsBackToLoadedLibrary()
    {
        using var bundle = new BundledLhm(Path.Combine(AppContext.BaseDirectory, "missing-bundle"));
        Assert.Throws<FileNotFoundException>(() => bundle.CreateGpu());
    }
}
