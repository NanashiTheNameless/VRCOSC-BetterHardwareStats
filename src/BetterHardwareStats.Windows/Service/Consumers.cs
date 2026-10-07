using BetterHardwareStats.Core.Counters;
using BetterHardwareStats.Core.Cpu;
using BetterHardwareStats.Core.Disk;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Service;

namespace BetterHardwareStats.Windows.Service;
public sealed class GpuCounterConsumer : IPdhConsumer
{
    private readonly GpuCounterAggregator _aggregator = new();
    private readonly WindowsCounterGpuSource _source = new();
    private Stamped<BackendSample>? _latest;
    private int[] _vrchatPids = [];
    private IReadOnlyList<Luid> _known = [];

    public string Name => "GPU";

    public IReadOnlyList<string> CounterPaths { get; } =
    [
        @"\GPU Engine(*)\Utilization Percentage",
        @"\GPU Adapter Memory(*)\Dedicated Usage",
        @"\GPU Adapter Memory(*)\Shared Usage",
    ];

    public Stamped<BackendSample>? Latest => Volatile.Read(ref _latest);
    public bool CounterSetAvailable { get; private set; } = true;

    public IReadOnlyCollection<Luid> AbsentAdapters => _source.AbsentAdapters;

    public void SetVRChatPids(int[] pids) => Volatile.Write(ref _vrchatPids, pids);

    public void SetKnownAdapters(IReadOnlyList<Luid> luids) => Volatile.Write(ref _known, luids);

    public void OnCollected(IReadOnlyList<ICounterReader> counters, long timestampTicks)
    {
        CounterSetAvailable = counters[0].Available;
        var pids = Volatile.Read(ref _vrchatPids);
        var results = _aggregator.Aggregate(counters[0], counters[1], counters[2], pids, Volatile.Read(ref _known));
        if (_source.Convert(results, timestampTicks) is { } sample)
            Volatile.Write(ref _latest, new Stamped<BackendSample>(sample, timestampTicks));
    }
}
public sealed class CpuCounterConsumer : IPdhConsumer
{
    private Stamped<CpuCounterResult>? _latest;
    private (IReadOnlyList<CpuPackage> Packages, int Index) _selection = ([], 0);

    public string Name => "CPU";

    public IReadOnlyList<string> CounterPaths { get; } =
    [
        @"\Processor Information(*)\% Processor Utility",
        @"\Processor Information(*)\% Processor Time",
        @"\Processor Information(*)\Processor Frequency",
        @"\Processor Information(*)\% Processor Performance",
    ];

    public Stamped<CpuCounterResult>? Latest => Volatile.Read(ref _latest);
    public bool UsingFallback { get; private set; }

    public void Select(IReadOnlyList<CpuPackage> packages, int index) => _selection = (packages, index);

    public void OnCollected(IReadOnlyList<ICounterReader> counters, long timestampTicks)
    {
        var (packages, index) = _selection;
        var package = index < packages.Count ? packages[index] : new CpuPackage(0, null, []);
        UsingFallback = !counters[0].Available;
        var usage = counters[0].Available ? counters[0] : counters[1];
        var r = CpuUsageCalculator.Compute(package, Math.Max(1, packages.Count), usage, counters[2], counters[3]);
        Volatile.Write(ref _latest, new Stamped<CpuCounterResult>(r, timestampTicks));
    }
}
public sealed class DiskCounterConsumer : IPdhConsumer
{
    private Stamped<DiskCounterResult>? _latest;

    public string Name => "Disk";

    public IReadOnlyList<string> CounterPaths { get; } =
    [
        @"\PhysicalDisk(*)\% Idle Time",
        @"\PhysicalDisk(*)\Disk Read Bytes/sec",
        @"\PhysicalDisk(*)\Disk Write Bytes/sec",
    ];

    public Stamped<DiskCounterResult>? Latest => Volatile.Read(ref _latest);

    public bool CounterSetAvailable { get; private set; } = true;

    public void OnCollected(IReadOnlyList<ICounterReader> counters, long timestampTicks)
    {
        CounterSetAvailable = counters[0].Available;
        var r = DiskCalculators.Aggregate(counters[0], counters[1], counters[2]);
        Volatile.Write(ref _latest, r is null ? null : new Stamped<DiskCounterResult>(r, timestampTicks));
    }
}
