using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Windows.Adlx;
using BetterHardwareStats.Windows.Lhm;

namespace BetterHardwareStats.Windows.Service;
internal static class Diagnostics
{
    public static string Build(HardwareService svc)
    {
        var st = svc.State();
        var snap = svc.Latest;
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');

        L("Nanashi's Better Hardware Stats Module diagnostics");
        L($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        L($"OS: {RuntimeInformation.OSDescription}; .NET {Environment.Version}; module {typeof(HardwareService).Assembly.GetName().Version}");
        L($"Elevated: {svc.IsElevated}; CPU sensor mode: {st.Options.CpuSensorMode} ({st.CpuSensorReason ?? "n/a"})");
        L($"Poll interval: {st.Options.PollIntervalMs} ms; selection: {st.Options.Selection}");
        L($"Data sources: {Describe(st.Options.Sources)}");
        L();

        L("== Workers and backends");
        foreach (var w in st.Workers) L($"  {w.Name}: {w.State}{(w.Detail is null ? "" : " - " + w.Detail)}; failures {w.ConsecutiveFailures}; last pass {w.LastPassMilliseconds:F1} ms");
        if (st.Nvml?.DriverVersion is { } dv) L($"  NVML driver version: {dv}");
        if (st.Adlx is { } adlx)
        {
            L($"  ADLX driver version: {AdlxBackend.FormatVersion(adlx.DriverAdlxVersion)}");
            foreach (var (k, v) in adlx.PowerSource) L($"  ADLX power source {k}: {v}");
        }
        if (st.LhmGpu is BetterHardwareStats.Core.Sensors.ILhmGpuBackend lg) L($"  LibreHardwareMonitorLib {lg.Version} from local isolated bundle ({lg.Location})");
        L();

        L("== GPU catalog (filter decisions)");
        foreach (var d in st.Catalog.Decisions)
            L($"  {(d.Included ? "+" : "-")} {d.Adapter.Description} [{d.Adapter.VendorId:X4}:{d.Adapter.DeviceId:X4}] LUID {d.Adapter.Luid} PCI {d.Adapter.Pci?.ToString() ?? "n/a"}: {d.Reason}");
        foreach (var w in st.Catalog.Warnings) L($"  warning: {w}");
        foreach (var a in st.Catalog.Adapters)
            L($"  {a.StableKey}: {a.Name}, {a.Vendor}, {a.Kind} ({a.ClassificationReason}), dedicated {a.DedicatedVramBytes / 1048576} MiB, shared {a.SharedSystemMemoryBytes / 1048576} MiB");
        foreach (var l in st.AbsentAdapters) L($"  adapter {l} never seen in GPU counters (utilization reported as missing)");
        L();

        L("== Correlation");
        foreach (var c in st.Correlations)
            L($"  {c.Device.BackendKey} ({c.Device.Name}) -> {c.StableKey ?? "UNMAPPED"} {(c.Rule is { } r ? $"by rule {r}" : "")}; {string.Join("; ", c.Trace)}");
        L();

        L("== Windows counters");
        if (st.Pdh is { } pdh)
        {
            foreach (var f in pdh.AddFailures.ToList()) L($"  unavailable: {f}");
            foreach (var (path, n) in svc.CounterInstanceCounts) L($"  {path}: {n} instances");
            foreach (var x in svc.CounterDump().Where(x => x.Path.Contains("GPU Engine")).Take(20)) L($"  sample: {x.Instance} = {x.Value:F2}{(x.Valid ? "" : " (invalid)")}");
        }
        L($"  CPU usage counter: {(st.CpuFallback ? "% Processor Time (fallback)" : "% Processor Utility")}");
        L();

        L("== Resolved GPUs (source and age of each metric)");
        foreach (var g in snap.Gpu.Adapters)
        {
            L($"  {g.Identity.Name} [{g.Identity.StableKey}] present {g.Present}, slot {snap.Gpu.SlotOf(g.Identity.StableKey)}");
            void M<T>(string n, MetricValue<T>? v) where T : struct =>
                L($"    {n}: {(v is null ? "--" : $"{v.Value} ({v.Source}, {(Stopwatch.GetTimestamp() - v.TimestampTicks) * 1000 / Stopwatch.Frequency} ms old)")}");
            M("utilization %", g.UtilizationPercent);
            M("VRChat utilization %", g.VRChatUtilizationPercent);
            M("VRAM used", g.VramUsedBytes);
            M("VRAM total", g.VramTotalBytes);
            M("temperature C", g.TemperatureC);
            M("power W", g.PowerWatts);
            M("core clock MHz", g.CoreClockMhz);
            M("fan %", g.FanPercent);
            M("fan RPM", g.FanRpm);
        }
        L($"  Selected: {snap.Gpu.SelectedStableKey ?? "none"}; VRChat GPU: {st.VRChatGpu ?? "none"}; slots: {string.Join(", ", snap.Gpu.SlotStableKeys.Select(k => k ?? "-"))}");
        L();

        L("== CPU");
        foreach (var p in st.Packages) L($"  package {p.Index}: {p.Name}; groups {string.Join(", ", p.Affinity.Select(a => $"{a.Group}:{a.Mask:X}"))}");
        L($"  selected {st.CpuIndex}; usage {snap.Cpu.UsagePercent?.Value.ToString("F1") ?? "--"}%; freq {snap.Cpu.FrequencyMhz?.Value.ToString("F0") ?? "--"} MHz; temp {snap.Cpu.TemperatureC?.Value.ToString("F1") ?? "--"}; power {snap.Cpu.PowerWatts?.Value.ToString("F1") ?? "--"}; sensors {snap.Cpu.SensorsAvailable} {snap.Cpu.SensorDetail}");
        L();

        L("== RAM, disk, network");
        L($"  RAM total {snap.Ram.TotalBytes?.Value / 1048576} MiB, available {snap.Ram.AvailableBytes?.Value / 1048576} MiB");
        L($"  Disk counters present {snap.Disk.Present}; activity {snap.Disk.ActivityPercent?.ToString("F1") ?? "--"}%");
        for (var i = 0; i < snap.Disk.Slots.Count; i++)
            if (snap.Disk.Slots[i] is { } v) L($"  disk slot {i}: {v.Letter} present {v.Present} used {v.UsedFraction?.ToString("P0") ?? "--"} activity {v.ActivityPercent?.ToString("F0") ?? "--"}");
        L($"  Network mode {st.Options.Network.Mode}; selected '{snap.Network.AdapterName ?? "none"}' present {snap.Network.Present}");
        L($"  Up interfaces: {string.Join(", ", st.Interfaces)}");
        L();

        var lhm = svc.LhmSensorDump();
        if (lhm.Count > 0)
        {
            L("== LibreHardwareMonitor GPU sensors");
            foreach (var (hw, type, name, id, value) in lhm) L($"  {hw} | {type} | {name} | {id} = {value?.ToString("F2") ?? "null"}");
        }
        return sb.ToString();
    }

    private static string Describe(SourcePreferences p) =>
        p.Preferred.Count == 0 ? "Auto" : string.Join(", ", p.Preferred.Select(kv => $"{kv.Key}={kv.Value}")) + (p.Strict ? " (strict)" : "");
}
