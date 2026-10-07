using System.Diagnostics;
using BetterHardwareStats.Core.Cpu;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Network;
using BetterHardwareStats.Core.Output;
using BetterHardwareStats.Core.Sensors;
using BetterHardwareStats.Core.Service;

namespace BetterHardwareStats.Core.Tests;

public class ReviewFixTests
{
    private static SourcedMetrics S(MetricSource src, PartialGpuMetrics m) => new(src, 0, m);

    [Fact]
    public void VramUsedAndTotalComeFromOneSource()
    {
        var s = new GpuMetricResolver(G.Opts()).Resolve(G.Dgpu(), [
            S(MetricSource.Nvml, new() { VramUsedBytes = 2 * G.GiB, VramTotalBytes = 7 * G.GiB }),
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 1, VramUsedBytes = 3 * G.GiB }),
        ], 0);
        Assert.Equal(MetricSource.Nvml, s.VramUsedBytes!.Source);
        Assert.Equal(MetricSource.Nvml, s.VramTotalBytes!.Source);
        Assert.Equal(7 * G.GiB, s.VramTotalBytes.Value);

        // Vendor has only "used": pair Windows used with the DXGI total instead of mixing sources.
        var s2 = new GpuMetricResolver(G.Opts()).Resolve(G.Dgpu(), [
            S(MetricSource.Nvml, new() { VramUsedBytes = 2 * G.GiB }),
            S(MetricSource.WindowsCounters, new() { UtilizationPercent = 1, VramUsedBytes = 3 * G.GiB }),
        ], 0);
        Assert.Equal(MetricSource.WindowsCounters, s2.VramUsedBytes!.Source);
        Assert.Equal(MetricSource.Dxgi, s2.VramTotalBytes!.Source);
    }

    [Fact]
    public void NewGpusWaitForSlotsWhileDeferred()
    {
        var a = new SlotAssigner([new SlotReservation("dgpu", 0, DateTime.UnixEpoch, GpuKind.Discrete)]);
        var r = a.Assign([G.Dgpu(), G.Igpu()], DateTime.UnixEpoch, assignNew: false);
        Assert.Equal(["dgpu", null, null, null], r.Slots);
        Assert.Equal(["igpu"], r.Pending);
        Assert.Equal(["dgpu", "igpu", null, null], a.Assign([G.Dgpu(), G.Igpu()], DateTime.UnixEpoch).Slots);
    }

    [Fact]
    public void UnmappedBackendDeviceIsWarnedOncePerCorrelation()
    {
        var src = new GpuSourceInput(new BackendStatus("NVML", BackendState.Running, null), MetricSource.Nvml,
            [new BackendDevice("nvml:9", GpuVendor.Nvidia, "Mystery", new PciAddress(9, 0, 0), null, null, null, null, null)], null);
        var m = new GpuMerger(new SlotAssigner());
        IReadOnlyList<GpuIdentity> cat = [G.Igpu()];
        var r1 = m.Merge(cat, [src], new GpuMergeOptions(G.Opts(), new()), 0, DateTime.UnixEpoch, 1);
        Assert.Contains(r1.Warnings, w => w.Contains("Mystery") && w.Contains("matches no GPU"));
        var r2 = m.Merge(cat, [src], new GpuMergeOptions(G.Opts(), new()), 0, DateTime.UnixEpoch, 2);
        Assert.DoesNotContain(r2.Warnings, w => w.Contains("Mystery"));
    }

    [Fact]
    public void NetworkSessionTotalsFollowTheSelectionWithoutJumping()
    {
        const long Tps = 1000;
        InterfaceRow Row(string k, ulong rx) => new(k, k, k, true, false, true, false, 1_000_000_000, 1_000_000_000, rx, 0);
        var t = new NetworkRateTracker(Tps);
        var sel = new NetworkSelector(5 * Tps);

        t.Update([Row("a", 0), Row("b", 1_000_000)], 0);
        sel.Select([Row("a", 0), Row("b", 1_000_000)], t, null, new(NetworkSelectionMode.ByName, "a"), 0);
        t.Update([Row("a", 100), Row("b", 2_000_000)], Tps);
        Assert.Equal(100, sel.Select([Row("a", 100), Row("b", 2_000_000)], t, null, new(NetworkSelectionMode.ByName, "a"), Tps).SessionDownloadBytes);

        // Switch to b: total continues from 100 with b's new traffic, not b's whole history.
        t.Update([Row("a", 100), Row("b", 2_000_500)], 2 * Tps);
        Assert.Equal(600, sel.Select([Row("a", 100), Row("b", 2_000_500)], t, null, new(NetworkSelectionMode.ByName, "b"), 2 * Tps).SessionDownloadBytes);
    }
}

public class CpuLogicTests
{
    [Theory]
    [InlineData(CpuSensorMode.Off, true, false)]
    [InlineData(CpuSensorMode.AutoIfElevated, false, false)]
    [InlineData(CpuSensorMode.AutoIfElevated, true, true)]
    [InlineData(CpuSensorMode.On, false, true)]
    public void SensorPolicyMatrix(CpuSensorMode mode, bool elevated, bool start) =>
        Assert.Equal(start, CpuSensorPolicy.Decide(mode, elevated).Start);

    [Fact]
    public void SnapshotSensorsOnlyWhenEnabledAndFresh()
    {
        var pkg = new CpuPackage(0, "Ryzen", [new GroupAffinity(0, 0xFF)]);
        var counters = new CpuCounterResult(25, 4500);
        var sensors = new CpuSensorReading(70, 90, 0, MetricSource.LibreHardwareMonitor);

        var off = CpuSnapshotBuilder.Build(pkg, 0, 1, counters, 0, sensors, false, "Off", 0, 3000);
        Assert.Equal(25, off.UsagePercent!.Value);
        Assert.Null(off.TemperatureC);
        Assert.False(off.SensorsAvailable);

        var on = CpuSnapshotBuilder.Build(pkg, 0, 1, counters, 0, sensors, true, null, 0, 3000);
        Assert.True(on.SensorsAvailable);
        Assert.Equal(90, on.PowerWatts!.Value);

        var stale = CpuSnapshotBuilder.Build(pkg, 0, 1, counters, 0, sensors, true, null, 5000, 3000);
        Assert.Null(stale.UsagePercent);
        Assert.False(stale.SensorsAvailable);
    }
}

public class SensorMappingTests
{
    [Fact]
    public void GpuSensorsMapWithUnits()
    {
        var m = SensorMapping.MapGpu([
            new("Load", "GPU Core", 55),
            new("SmallData", "GPU Memory Used", 2048),
            new("SmallData", "GPU Memory Total", 8192),
            new("Temperature", "GPU Core", 61),
            new("Temperature", "GPU Hot Spot", 75),
            new("Temperature", "GPU Memory Junction", 80),
            new("Power", "GPU Package", 120),
            new("Clock", "GPU Core", 2500),
            new("Clock", "GPU Memory", 10000),
            new("Fan", "GPU Fan 1", 1500),
            new("Control", "GPU Fan 1", 40),
        ]);
        Assert.Equal(55, m.UtilizationPercent);
        Assert.Equal(2048L * 1024 * 1024, m.VramUsedBytes);
        Assert.Equal(8L * 1024 * 1024 * 1024, m.VramTotalBytes);
        Assert.Equal(61, m.TemperatureC);
        Assert.Equal(80, m.MemoryTemperatureC);
        Assert.Equal(10000, m.MemoryClockMhz);
        Assert.Equal(1500, m.FanRpm);
        Assert.Equal(40, m.FanPercent);
    }

    [Fact]
    public void GpuFallbacksAndNulls()
    {
        var m = SensorMapping.MapGpu([new("Load", "D3D 3D", 12), new("Temperature", "GPU", 50), new("Power", "GPU Power", null)]);
        Assert.Equal(12, m.UtilizationPercent);
        Assert.Equal(50, m.TemperatureC); // the single temperature sensor
        Assert.Null(m.PowerWatts);
    }

    [Fact]
    public void CpuAmdAndIntelNames()
    {
        Assert.Equal((65f, 110f), SensorMapping.MapCpu([new("Temperature", "Core (Tctl/Tdie)", 65), new("Power", "Package", 110)]));
        Assert.Equal((71f, 45f), SensorMapping.MapCpu([new("Temperature", "CPU Package", 71), new("Power", "CPU Package", 45)]));
        Assert.Equal(72f, SensorMapping.MapCpu([new("Temperature", "CPU Core #1", 60), new("Temperature", "CPU Core #2", 72)]).TemperatureC);
    }
}

public class ParameterMapperTests
{
    private static HardwareSnapshot Snapshot()
    {
        var d = G.Dgpu();
        var i = G.Igpu();
        GpuSnapshot Snap(GpuIdentity id, float util, float temp, long used, long total) =>
            G.Snap(id, util) with
            {
                TemperatureC = new(temp, MetricSource.Nvml, 0),
                PowerWatts = new(300, MetricSource.Nvml, 0),
                VramUsedBytes = new(used, MetricSource.Nvml, 0),
                VramTotalBytes = new(total, MetricSource.Nvml, 0),
            };
        var gpu = new GpuSnapshotSet(1, DateTime.UnixEpoch, [Snap(d, 50, 70, 2 * G.GiB, 8 * G.GiB), Snap(i, 5, 40, G.GiB, 16 * G.GiB)],
            "igpu", ["dgpu", "igpu", null, null], []);
        var ram = new RamSnapshot(new(32 * G.GiB, MetricSource.SystemApi, 0), new(8 * G.GiB, MetricSource.SystemApi, 0));
        return HardwareSnapshot.Empty with { Gpu = gpu, Ram = ram };
    }

    private static Dictionary<HardwareParameter, object> Run(HardwareSnapshot s, OutputSettings? o = null)
    {
        var sent = new Dictionary<HardwareParameter, object>();
        var sender = new ParameterSender<HardwareParameter>((k, v) => sent[k] = v, new SenderOptions { RefreshSeconds = 0 });
        sender.BeginTick(0);
        ParameterMapper.Apply(s, o ?? new OutputSettings(), sender);
        return sent;
    }

    [Fact]
    public void SelectedAndSlotGroups()
    {
        var p = Run(Snapshot());
        Assert.Equal(0.05f, (float)p[HardwareParameter.GpuUsage], 3);
        Assert.Equal(1, p[HardwareParameter.GpuIndex]);
        Assert.Equal(true, p[HardwareParameter.GpuIsIntegrated]);
        Assert.Equal(255, p[HardwareParameter.GpuPower]); // 300 W clamps
        Assert.Equal(1, p[HardwareParameter.VramUsed]);
        Assert.Equal(15, p[HardwareParameter.VramFree]);
        Assert.Equal(0.5f, (float)p[HardwareParameter.Gpu0Usage], 3);
        Assert.Equal(70, p[HardwareParameter.Gpu0Temp]);
        Assert.Equal(40, p[HardwareParameter.Gpu1Temp]);
        Assert.Equal(false, p[HardwareParameter.Gpu2Present]);
        Assert.Equal(24, p[HardwareParameter.RamUsed]);
        Assert.Equal(0.75f, (float)p[HardwareParameter.RamUsage], 3);
    }

    [Fact]
    public void DisabledDomainSendsNothing()
    {
        var p = Run(Snapshot(), new OutputSettings { EnableGpu = false });
        Assert.DoesNotContain(p.Keys, k => HardwareParameterTable.Get(k).Domain == ParameterDomain.Gpu);
        Assert.Contains(HardwareParameter.RamUsed, p.Keys);
    }

    [Fact]
    public void EveryParameterIsWrittenWithItsDeclaredType()
    {
        var p = Run(Snapshot());
        Assert.Equal(HardwareParameterTable.Count, p.Count);
        foreach (var (k, v) in p)
        {
            var expected = HardwareParameterTable.Get(k).Type switch
            {
                ParameterType.Bool => typeof(bool),
                ParameterType.Int => typeof(int),
                _ => typeof(float),
            };
            Assert.Equal(expected, v.GetType());
        }
    }

    [Fact]
    public void SlotArithmeticMatchesGeneratedTable()
    {
        // Check the slot offset calculation for every slot parameter.
        foreach (var p in HardwareParameterTable.All.Where(p => p.Slot is > 0))
        {
            var block = p.Domain == ParameterDomain.Gpu ? 8 : 5;
            var slot0 = (HardwareParameter)((int)p.Lookup - p.Slot!.Value * block);
            Assert.Equal(p.DefaultName.Replace($"/{p.Slot}/", "/0/"), HardwareParameterTable.Get(slot0).DefaultName);
        }
    }
}

public class ChatBoxTests
{
    [Fact]
    public void ParityVariablesExistWithOfficialNamesAndTypes()
    {
        var parity = ChatBoxModel.Variables.Where(v => v.Parity).ToList();
        Assert.Equal(16, parity.Count);
        string[] names = ["CPUName", "CPUUsage", "CPUPower", "CPUTemp", "GPUName", "GPUUsage", "GPUPower", "GPUTemp",
                          "RAMUsage", "RAMTotal", "RAMUsed", "RAMFree", "VRAMUsage", "VRAMTotal", "VRAMUsed", "VRAMFree"];
        Assert.Equal(names, parity.Select(v => v.Lookup.ToString()));
        Assert.All(parity.Where(v => v.Lookup.ToString().StartsWith("RAM") || v.Lookup.ToString().StartsWith("VRAM")),
            v => Assert.Equal(ChatBoxValueType.Float, v.Type));
        Assert.Equal(Enum.GetValues<ChatBoxVariable>().Length, ChatBoxModel.Variables.Count);
        Assert.Equal("CPU: {0}% | GPU: {1}%\nRAM: {2}GB/{3}GB", ChatBoxModel.DefaultStateFormat);
    }

    [Fact]
    public void MissingValuesRenderAsDashesNotZero()
    {
        var values = ChatBoxModel.Build(HardwareSnapshot.Empty, new(), new OutputSettings());
        var byKey = values.ToDictionary(v => v.Lookup, v => v.Value);
        Assert.Equal("--", ChatBoxModel.Render(byKey[ChatBoxVariable.GPUUsage]));
        Assert.Equal("--", ChatBoxModel.Render(byKey[ChatBoxVariable.RAMTotal]));
        Assert.Equal("--", byKey[ChatBoxVariable.GPUName]);
        foreach (var (k, v) in values)
        {
            var t = ChatBoxModel.Variables.First(x => x.Lookup == k).Type;
            Assert.Equal(t switch { ChatBoxValueType.Int => typeof(int), ChatBoxValueType.Float => typeof(float), _ => typeof(string) }, v.GetType());
        }
    }

    [Fact]
    public void UnitsApplyToChatBoxOnly()
    {
        var cpu = CpuSnapshot.Empty with { TemperatureC = new(100, MetricSource.LibreHardwareMonitor, 0), SensorsAvailable = true };
        var ram = new RamSnapshot(new(16L * 1024 * 1024 * 1024, MetricSource.SystemApi, 0), new(8L * 1024 * 1024 * 1024, MetricSource.SystemApi, 0));
        var s = HardwareSnapshot.Empty with { Cpu = cpu, Ram = ram };
        var v = ChatBoxModel.Build(s, new(TemperatureUnit.Fahrenheit, MemoryUnit.MB), new OutputSettings()).ToDictionary(x => x.Lookup, x => x.Value);
        Assert.Equal(212, v[ChatBoxVariable.CPUTemp]);
        Assert.Equal(16384f, v[ChatBoxVariable.RAMTotal]);
    }

    [Fact]
    public void EventsFireOnChangeAndUpwardCrossingWithDebounce()
    {
        var o = new OutputSettings();
        var tr = new ChatBoxEventTracker(60_000);
        HardwareSnapshot Gpu(string key, float temp) => HardwareSnapshot.Empty with
        {
            Gpu = new GpuSnapshotSet(0, DateTime.UnixEpoch, [G.Snap(G.Id(key, GpuKind.Discrete, G.GiB), 1) with { TemperatureC = new(temp, MetricSource.Nvml, 0) }],
                key, [key, null, null, null], []),
        };

        Assert.Empty(tr.Update(Gpu("a", 60), 85, o, 0)); // first selection is not a change
        Assert.Equal([ChatBoxEvent.GPUOverheat], tr.Update(Gpu("a", 90), 85, o, 1000));
        Assert.Empty(tr.Update(Gpu("a", 80), 85, o, 2000));
        Assert.Empty(tr.Update(Gpu("a", 90), 85, o, 3000)); // within 60 s
        Assert.Empty(tr.Update(Gpu("a", 80), 85, o, 70_000));
        Assert.Equal([ChatBoxEvent.GPUOverheat], tr.Update(Gpu("a", 90), 85, o, 71_000));
        Assert.Equal([ChatBoxEvent.GPUChanged], tr.Update(Gpu("b", 50), 85, o, 72_000));
    }
}

public class PollingWorkerTests
{
    private static void WaitFor(Func<bool> cond, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(10);
    }

    [Fact]
    public void PublishesLatestAndRunsInitAndCleanupOnWorkerThread()
    {
        int initThread = -1, cleanupThread = -2, n = 0;
        var w = new PollingWorker<string>("t", () => (++n).ToString(), new WorkerOptions { Interval = TimeSpan.FromMilliseconds(20) },
            init: () => { initThread = Environment.CurrentManagedThreadId; return null; },
            cleanup: () => cleanupThread = Environment.CurrentManagedThreadId);
        w.Start();
        WaitFor(() => n >= 3);
        Assert.Equal(BackendState.Running, w.State);
        Assert.NotNull(w.Latest);
        w.Dispose();
        Assert.Equal(initThread, cleanupThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, initThread);
    }

    [Fact]
    public void FailedInitDisables()
    {
        using var w = new PollingWorker<string>("t", () => "x", init: () => "nvml.dll not found");
        w.Start();
        WaitFor(() => w.State == BackendState.Disabled);
        Assert.Equal("nvml.dll not found", w.Status.Detail);
    }

    [Fact]
    public void ConsecutiveFailuresDisable()
    {
        var logs = new List<string>();
        using var w = new PollingWorker<string>("t", () => throw new InvalidOperationException("boom"),
            new WorkerOptions { Interval = TimeSpan.FromMilliseconds(5), MaxConsecutiveFailures = 3 }, log: logs.Add);
        w.Start();
        WaitFor(() => w.State == BackendState.Disabled);
        Assert.Equal(BackendState.Disabled, w.State);
        Assert.Contains(logs, l => l.Contains("3 consecutive failures"));
    }

    [Fact]
    public void HungCallIsDegradedThenRecovers()
    {
        using var gate = new ManualResetEventSlim(false);
        var calls = 0;
        using var w = new PollingWorker<string>("t", () => { if (Interlocked.Increment(ref calls) == 2) gate.Wait(); return "ok"; },
            new WorkerOptions { Interval = TimeSpan.FromMilliseconds(10), CallTimeout = TimeSpan.FromMilliseconds(50) });
        w.Start();
        WaitFor(() => Volatile.Read(ref calls) >= 2);
        Thread.Sleep(120);
        w.Check(Stopwatch.GetTimestamp());
        Assert.Equal(BackendState.Degraded, w.State);
        gate.Set();
        WaitFor(() => w.State == BackendState.Running);
        Assert.Equal(BackendState.Running, w.State);
    }

    [Fact]
    public void DisposeBeforeStartIsIdempotentAndPreventsStart()
    {
        var worker = new PollingWorker<string>("t", () => "ok");
        worker.Dispose();
        worker.Dispose();
        Assert.Throws<ObjectDisposedException>(worker.Start);
    }

    [Fact]
    public void DisposeAfterFailedInitializationRunsCleanupOnce()
    {
        var cleaned = 0;
        var worker = new PollingWorker<string>("t", () => "ok", init: () => "unavailable",
            cleanup: () => Interlocked.Increment(ref cleaned));
        worker.Start();
        WaitFor(() => Volatile.Read(ref cleaned) == 1);
        worker.Dispose();
        worker.Dispose();
        Assert.Equal(1, cleaned);
    }

    [Fact]
    public void TimedOutShutdownKeepsSignalAliveUntilWorkerFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        var worker = new PollingWorker<string>("t", () =>
        {
            entered.Set();
            release.Wait();
            return "ok";
        }, cleanup: cleaned.Set);
        worker.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        try
        {
            worker.Dispose();
            Assert.False(cleaned.IsSet);
        }
        finally { release.Set(); }
        Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("worker loop failed", worker.Status.Detail ?? "");
        worker.Dispose();
    }

    [Fact]
    public void WorkerCanDisposeItselfWithoutJoiningItsOwnThread()
    {
        using var cleaned = new ManualResetEventSlim();
        PollingWorker<string>? worker = null;
        worker = new PollingWorker<string>("t", () => { worker!.Dispose(); return "ok"; }, cleanup: cleaned.Set);
        worker.Start();
        Assert.True(cleaned.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.NotNull(worker.Latest);
        Assert.DoesNotContain("worker loop failed", worker.Status.Detail ?? "");
        worker.Dispose();
    }

    [Fact]
    public void LogLimiterDropsRepeatsWithinInterval()
    {
        var sink = new List<string>();
        var l = new LogLimiter(sink.Add, TimeSpan.FromSeconds(60), 1000);
        Assert.True(l.Log("x", 0));
        Assert.False(l.Log("x", 59_999));
        Assert.True(l.Log("y", 1));
        Assert.True(l.Log("x", 60_000));
        Assert.Equal(["x", "y", "x"], sink);
    }
}
