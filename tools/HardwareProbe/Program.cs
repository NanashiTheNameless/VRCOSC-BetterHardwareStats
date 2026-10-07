// HardwareProbe: runs the full hardware service outside VRCOSC and prints what it sees.
//
//   HardwareProbe [--duration <s>] [--domain gpu|cpu|ram|disk|network|all] [--backend windows|nvml|adlx|lhm|all]
//                 [--json] [--dump-counters] [--dump-lhm-sensors] [--diagnostics] [--cpu-sensors]
//                 [--source auto|nvml|adlx|lhm] [--fixtures <dir>]
//
// Prints resolved metrics and raw backend values for each GPU every second.
// Exit code 1 if no GPU produced a utilization value within 10 s.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Output;
using BetterHardwareStats.Windows.Service;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

var duration = 15;
var domain = "all";
var backend = "all";
string source = "auto";
string? fixtures = null;
bool json = false, dumpCounters = false, dumpLhm = false, diagnostics = false, cpuSensors = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--duration": duration = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--domain": domain = Next().ToLowerInvariant(); break;
        case "--backend": backend = Next().ToLowerInvariant(); break;
        case "--source": source = Next().ToLowerInvariant(); break;
        case "--fixtures": fixtures = Next(); break;
        case "--json": json = true; break;
        case "--dump-counters": dumpCounters = true; break;
        case "--dump-lhm-sensors": dumpLhm = true; break;
        case "--diagnostics": diagnostics = true; break;
        case "--cpu-sensors": cpuSensors = true; break;
        case "-h" or "--help":
            Console.WriteLine("See the usage examples at the top of tools/HardwareProbe/Program.cs.");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}");
            return 2;
    }
}

bool D(string d) => domain == "all" || domain == d;
bool B(string b) => backend == "all" || backend == b;

var detected = SourceDetection.Detect();
Console.WriteLine($"# Sources available: {string.Join(", ", detected.Available)}");
foreach (var (k, v) in detected.Reasons) Console.WriteLine($"# {k} unavailable: {v}");

MetricSource? chosen = source switch { "nvml" => MetricSource.Nvml, "adlx" => MetricSource.Adlx, "lhm" => MetricSource.LibreHardwareMonitor, _ => null };
var options = new HardwareServiceOptions
{
    EnableGpu = D("gpu"), EnableCpu = D("cpu"), EnableRam = D("ram"), EnableDisk = D("disk"), EnableNetwork = D("network"),
    EnableNvml = B("nvml"), EnableAdlx = B("adlx"), EnableLibreHardwareMonitor = B("lhm"),
    Sources = SourceCatalog.Build(chosen, new Dictionary<GpuMetricGroup, MetricSource?>(), strict: false),
    CpuSensorMode = cpuSensors ? CpuSensorMode.On : CpuSensorMode.Off,
    PollInactiveGpus = true,
};

using var service = new HardwareService(options, slots: null, log: m => Console.WriteLine($"# log: {m}"));
var sw = Stopwatch.StartNew();
service.Start();
Console.WriteLine($"# Started in {sw.ElapsedMilliseconds} ms");

var catalog = service.Latest; // empty until the first merge
var sawUtilization = false;
long lastSeq = -1;
var end = TimeSpan.FromSeconds(duration);
while (sw.Elapsed < end)
{
    Thread.Sleep(1000);
    var s = service.Latest;
    if (s.Sequence == lastSeq) continue;
    lastSeq = s.Sequence;
    var t = sw.Elapsed.TotalSeconds;

    if (D("gpu"))
    {
        var raw = service.RawGpuValues();
        foreach (var g in s.Gpu.Adapters)
        {
            if (g.UtilizationPercent is not null) sawUtilization = true;
            var rawFor = raw.Where(r => r.StableKey == g.Identity.StableKey).ToList();
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    t = Math.Round(t, 1),
                    gpu = g.Identity.Name,
                    key = g.Identity.StableKey,
                    kind = g.Identity.Kind.ToString(),
                    selected = s.Gpu.SelectedStableKey == g.Identity.StableKey,
                    slot = s.Gpu.SlotOf(g.Identity.StableKey),
                    utilization = V(g.UtilizationPercent),
                    vrchat = V(g.VRChatUtilizationPercent),
                    vramUsed = V(g.VramUsedBytes),
                    vramTotal = V(g.VramTotalBytes),
                    temperature = V(g.TemperatureC),
                    power = V(g.PowerWatts),
                    coreClock = V(g.CoreClockMhz),
                    fanPercent = V(g.FanPercent),
                    fanRpm = V(g.FanRpm),
                    raw = rawFor.Select(r => new { r.Source, r.Metrics.UtilizationPercent, r.Metrics.TemperatureC, r.Metrics.PowerWatts, r.Metrics.VramUsedBytes, ageMs = Math.Round(r.AgeMs) }),
                }));
            }
            else
            {
                var sel = s.Gpu.SelectedStableKey == g.Identity.StableKey ? "*" : " ";
                Console.WriteLine($"{t,6:F1}s {sel} slot {s.Gpu.SlotOf(g.Identity.StableKey),2} {g.Identity.Name} ({g.Identity.Kind}): " +
                                  $"util {F(g.UtilizationPercent)}  vrchat {F(g.VRChatUtilizationPercent)}  temp {F(g.TemperatureC)}  power {F(g.PowerWatts)}  " +
                                  $"vram {Mb(g.VramUsedBytes?.Value)}/{Mb(g.VramTotalBytes?.Value)} MiB  clock {F(g.CoreClockMhz)}");
                foreach (var r in rawFor)
                    Console.WriteLine($"          raw {r.Source,-20} util {N(r.Metrics.UtilizationPercent)}  temp {N(r.Metrics.TemperatureC)}  power {N(r.Metrics.PowerWatts)}  vram {(r.Metrics.VramUsedBytes is { } u ? (u / 1048576).ToString() : "--")} MiB  ({r.AgeMs:F0} ms old)");
            }
        }
        // Counter LUIDs of filtered adapters (Basic Render Driver, virtual displays) are expected and not listed.
        foreach (var r in service.RawGpuValues().Where(r => r.StableKey is null && r.Source != "WindowsCounters"))
            Console.WriteLine($"          unmapped {r.Source} device {r.BackendKey}");
    }
    if (D("cpu")) Console.WriteLine($"{t,6:F1}s CPU {s.Cpu.Name}: usage {F(s.Cpu.UsagePercent)}  freq {F(s.Cpu.FrequencyMhz)}  temp {F(s.Cpu.TemperatureC)}  power {F(s.Cpu.PowerWatts)}  sensors {s.Cpu.SensorsAvailable} {s.Cpu.SensorDetail}");
    if (D("ram")) Console.WriteLine($"{t,6:F1}s RAM: {Mb(s.Ram.UsedBytes)}/{Mb(s.Ram.TotalBytes?.Value)} MiB used");
    if (D("disk"))
        Console.WriteLine($"{t,6:F1}s Disk: activity {s.Disk.ActivityPercent?.ToString("F1") ?? "--"}%  read {(s.Disk.ReadBytesPerSec / 1e6)?.ToString("F2") ?? "--"} MB/s  write {(s.Disk.WriteBytesPerSec / 1e6)?.ToString("F2") ?? "--"} MB/s  slots " +
                          string.Join(" ", s.Disk.Slots.Select(v => v is null ? "-" : $"{v.Letter}{(v.Present ? "" : "(absent)")} {v.UsedFraction?.ToString("P0") ?? "--"} act {v.ActivityPercent?.ToString("F0") ?? "--"}")));
    if (D("network"))
        Console.WriteLine($"{t,6:F1}s Network '{s.Network.AdapterName ?? "none"}': down {(s.Network.DownloadBytesPerSec * 8 / 1e6)?.ToString("F2") ?? "--"} Mbps  up {(s.Network.UploadBytesPerSec * 8 / 1e6)?.ToString("F2") ?? "--"} Mbps  present {s.Network.Present}");
}

if (D("gpu"))
{
    var final = service.Latest;
    Console.WriteLine($"# Selected GPU: {final.Gpu.Selected?.Identity.Name ?? "none"}; slots: {string.Join(", ", final.Gpu.SlotStableKeys.Select(k => k ?? "-"))}");
    Console.WriteLine("# ChatBox default state: " + string.Format(CultureInfo.InvariantCulture, ChatBoxModel.DefaultStateFormat.Replace("\n", " / "),
        ChatBoxModel.Build(final, new(), new OutputSettings()).Where(v => ChatBoxModel.DefaultStateVariables.Contains(v.Lookup))
            .OrderBy(v => Array.IndexOf(ChatBoxModel.DefaultStateVariables, v.Lookup)).Select(v => ChatBoxModel.Render(v.Value)).ToArray()));
}

if (dumpCounters)
{
    var dump = service.CounterDump();
    Console.WriteLine($"# Counter dump: {dump.Count} instances");
    foreach (var (path, inst, value, valid) in dump) Console.WriteLine($"counter\t{path}\t{inst}\t{value:R}\t{valid}");
    if (fixtures is not null)
    {
        Directory.CreateDirectory(fixtures);
        foreach (var g in dump.GroupBy(d => d.Path))
        {
            var name = new string(g.Key.Where(char.IsLetterOrDigit).ToArray()) + ".txt";
            File.WriteAllLines(Path.Combine(fixtures, name), g.Select(d => $"{d.Instance}\t{d.Value.ToString("R", CultureInfo.InvariantCulture)}\t{d.Valid}"));
        }
        Console.WriteLine($"# Fixtures written to {fixtures}");
    }
}

if (dumpLhm)
{
    var sensors = service.LhmSensorDump();
    Console.WriteLine($"# LibreHardwareMonitor GPU sensors: {sensors.Count}");
    foreach (var (hw, type, name, id, value) in sensors) Console.WriteLine($"lhm\t{hw}\t{type}\t{name}\t{id}\t{value?.ToString("F2") ?? "null"}");
}

if (diagnostics)
{
    Console.WriteLine();
    Console.WriteLine(service.DiagnosticsReport());
}

if (D("gpu") && !sawUtilization)
{
    Console.Error.WriteLine("# FAIL: no GPU produced a utilization value");
    return 1;
}
return 0;

static object? V<T>(MetricValue<T>? v) where T : struct => v is null ? null : new { value = v.Value, source = v.Source.ToString() };
static string F(MetricValue<float>? v) => v is null ? "--" : $"{v.Value:F1} [{Short(v.Source)}]";
static string N(float? v) => v?.ToString("F1") ?? "--";
static string Mb(long? b) => b is { } x ? (x / 1048576).ToString() : "--";
static string Short(MetricSource s) => s switch
{
    MetricSource.WindowsCounters => "win",
    MetricSource.Nvml => "nvml",
    MetricSource.Adlx => "adlx",
    MetricSource.LibreHardwareMonitor => "lhm",
    MetricSource.Dxgi => "dxgi",
    _ => s.ToString(),
};
