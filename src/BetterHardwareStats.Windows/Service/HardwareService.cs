using System.Diagnostics;
using BetterHardwareStats.Core.Cpu;
using BetterHardwareStats.Core.Disk;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Network;
using BetterHardwareStats.Core.Service;
using BetterHardwareStats.Windows.Adlx;
using BetterHardwareStats.Windows.Igcl;
using BetterHardwareStats.Windows.Lhm;
using BetterHardwareStats.Windows.HwInfo;
using BetterHardwareStats.Core.Sensors;
using BetterHardwareStats.Windows.Native;
using BetterHardwareStats.Windows.Nvml;

namespace BetterHardwareStats.Windows.Service;
public sealed class HardwareService : IDisposable
{
    private volatile HardwareServiceOptions _o;
    private readonly LogLimiter _log;
    private readonly GpuMerger _merger;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly List<IDisposable> _disposables = [];

    private readonly GpuCounterConsumer _gpuCounters = new();
    private readonly CpuCounterConsumer _cpuCounters = new();
    private readonly DiskCounterConsumer _diskCounters = new();
    private PdhEngine? _pdh;
    private PollingWorker<object>? _pdhWorker;
    private PollingWorker<SystemSample>? _systemWorker;
    private PollingWorker<HardwareSnapshot>? _mergeWorker;
    private PollingWorker<BackendSample>? _nvmlWorker, _adlxWorker, _lhmGpuWorker;
    private PollingWorker<CpuSensorReading>? _lhmCpuWorker;
    private NvmlBackend? _nvml;
    private AdlxBackend? _adlx;
    private IgclBackend? _igcl;
    private PollingWorker<BackendSample>? _igclWorker;
    private readonly BundledLhm _lhmBundle = new();
    private object? _lhmGpu; // LhmGpuBackend, typed as object so a missing LHM assembly cannot break this class
    private object? _lhmCpu;

    private GpuCatalog _catalog = new([], [], []);
    private IReadOnlyList<GpuIdentity> _catalogList = [];
    private IReadOnlyList<BackendDevice> _windowsDevices = [];
    private IReadOnlyList<CpuPackage> _packages = [];
    private int _cpuIndex;
    private string? _cpuSensorReason;
    private readonly DriveScanner _drives = new();
    private readonly NetworkRateTracker _rates = new();
    private readonly NetworkSelector _netSelector;
    private readonly Dictionary<string, string> _backendToStable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastActive = new(StringComparer.Ordinal);
    private readonly List<CorrelationResult> _correlations = [];
    private long _sequence;
    private HardwareSnapshot _latest = HardwareSnapshot.Empty;
    private int _slotsDirty;
    private int _pdhDumpRequested, _lhmDumpRequested;
    private IReadOnlyList<(string Path, string Instance, double Value, bool Valid)>? _pdhDump;
    private IReadOnlyList<(string Hardware, string Type, string Name, string Identifier, float? Value)>? _lhmDump;
    private IReadOnlyDictionary<string, int>? _pdhCounts;

    private sealed record SystemSample(
        (long Total, long Available)? Memory, NetworkSnapshot Network, IReadOnlyList<VolumeInfo> Volumes, IReadOnlyList<string> InterfaceAliases);

    public HardwareService(HardwareServiceOptions options, IEnumerable<SlotReservation>? slots, Action<string> log, HardwareSnapshot? initial = null)
    {
        _o = options;
        _latest = initial is null ? HardwareSnapshot.Empty : initial with
        {
            Gpu = options.EnableGpu ? initial.Gpu : GpuSnapshotSet.Empty,
            Cpu = options.EnableCpu ? initial.Cpu : CpuSnapshot.Empty,
            Ram = options.EnableRam ? initial.Ram : RamSnapshot.Empty,
            Disk = options.EnableDisk ? initial.Disk : DiskSnapshotSet.Empty,
            Network = options.EnableNetwork ? initial.Network : NetworkSnapshot.Empty with
            { SessionDownloadBytes = initial.Network.SessionDownloadBytes, SessionUploadBytes = initial.Network.SessionUploadBytes },
        };
        _sequence = initial?.Sequence ?? 0;
        _netSelector = new NetworkSelector(sessionDown: initial?.Network.SessionDownloadBytes ?? 0,
            sessionUp: initial?.Network.SessionUploadBytes ?? 0);
        _log = new LogLimiter(log);
        _merger = new GpuMerger(new SlotAssigner(slots));
        IsElevated = Elevation.IsAdministrator();
    }

    public bool TryUpdateOptions(HardwareServiceOptions options)
    {
        var old = _o;
        if (old.EnableGpu != options.EnableGpu || old.EnableCpu != options.EnableCpu || old.EnableRam != options.EnableRam
            || old.EnableDisk != options.EnableDisk || old.EnableNetwork != options.EnableNetwork
            || old.EnableNvml != options.EnableNvml || old.EnableAdlx != options.EnableAdlx || old.EnableIgcl != options.EnableIgcl
            || old.EnableLibreHardwareMonitor != options.EnableLibreHardwareMonitor || old.CpuSensorMode != options.CpuSensorMode
            || old.SelectedCpu != options.SelectedCpu || old.IncludeVirtualAdapters != options.IncludeVirtualAdapters
            || old.Selection.IncludeIntegrated != options.Selection.IncludeIntegrated || old.PollInactiveGpus != options.PollInactiveGpus)
            return false;
        _o = options;
        var timing = Every(Poll);
        foreach (var worker in _disposables)
        {
            switch (worker)
            {
                case PollingWorker<object> w: w.Options = timing; break;
                case PollingWorker<SystemSample> w: w.Options = timing; break;
                case PollingWorker<HardwareSnapshot> w: w.Options = timing; break;
                case PollingWorker<BackendSample> w: w.Options = timing; break;
                case PollingWorker<CpuSensorReading> w: w.Options = timing; break;
            }
        }
        return true;
    }

    public HardwareSnapshot Latest => Volatile.Read(ref _latest);
    public bool IsElevated { get; }
    public event Action<HardwareSnapshot>? Published;
    public bool TakeSlotsDirty() => Interlocked.Exchange(ref _slotsDirty, 0) == 1;

    public IReadOnlyList<SlotReservation> SlotReservations => _merger.Slots.Export();

    private WorkerOptions Every(TimeSpan interval) => new() { Interval = interval };
    private TimeSpan Poll => TimeSpan.FromMilliseconds(Math.Clamp(_o.PollIntervalMs, 250, 5000));
    public void Start()
    {
        if (_o.EnableGpu) RefreshCatalog();
        if (_o.EnableCpu)
        {
            _packages = CpuTopology.ReadPackages();
            var (idx, warn) = CpuUsageCalculator.ResolveIndex(_o.SelectedCpu, _packages);
            _cpuIndex = idx;
            if (warn is not null) _log.Log(warn);
            _cpuCounters.Select(_packages, idx);
        }

        var consumers = new List<Core.Counters.IPdhConsumer>();
        if (_o.EnableGpu) consumers.Add(_gpuCounters);
        if (_o.EnableCpu) consumers.Add(_cpuCounters);
        if (_o.EnableDisk) consumers.Add(_diskCounters);
        if (consumers.Count > 0)
        {
            _pdhWorker = new PollingWorker<object>("pdh", () =>
                {
                    _pdh!.Collect(Stopwatch.GetTimestamp());
                    // Dumps are captured here, on the thread that owns the native buffers.
                    Volatile.Write(ref _pdhCounts, _pdh.LastInstanceCounts);
                    if (Interlocked.Exchange(ref _pdhDumpRequested, 0) == 1) Volatile.Write(ref _pdhDump, _pdh.Dump().ToList());
                    return this;
                }, Every(Poll),
                init: () =>
                {
                    _pdh = new PdhEngine(consumers);
                    foreach (var f in _pdh.AddFailures) _log.Log("Counters: " + f + (f.Contains("PhysicalDisk") ? " (hint: diskperf -y or lodctr /R; not run automatically)" : f.Contains("GPU") ? " (hint: lodctr /R rebuilds counters; not run automatically)" : ""));
                    return null;
                },
                cleanup: () => _pdh?.Dispose(), log: m => _log.Log(m));
            Start(_pdhWorker);
        }

        if (!_o.EnableGpu && !_o.EnableCpu && !_o.EnableRam && !_o.EnableDisk && !_o.EnableNetwork) return;
        if (_o.EnableGpu || _o.EnableRam || _o.EnableDisk || _o.EnableNetwork)
        {
            _systemWorker = new PollingWorker<SystemSample>("system", SystemPass, Every(Poll), log: m => _log.Log(m));
            Start(_systemWorker);
        }

        if (_o.EnableGpu) StartVendorBackends();
        if (_o.EnableCpu) StartCpuSensors();

        _mergeWorker = new PollingWorker<HardwareSnapshot>("merge", Merge, Every(Poll), log: m => _log.Log(m));
        _mergeWorker.Sampled += s =>
        {
            Volatile.Write(ref _latest, s.Value);
            Published?.Invoke(s.Value);
        };
        Start(_mergeWorker);

        var enabled = new List<string>();
        if (_o.EnableGpu) enabled.Add($"GPUs [{string.Join("; ", _catalogList.Select(g => $"{g.Name} ({g.Kind}, {g.StableKey})"))}]");
        if (_o.EnableCpu) enabled.Add($"CPU packages {_packages.Count}, CPU sensors: {_cpuSensorReason ?? "off"}");
        if (_o.EnableRam) enabled.Add("RAM");
        if (_o.EnableDisk) enabled.Add("disks");
        if (_o.EnableNetwork) enabled.Add("network");
        _log.Log("Started: " + string.Join(", ", enabled));
    }

    private void Start<T>(PollingWorker<T> w) where T : class
    {
        _disposables.Add(w);
        w.Start();
    }

    private void StartVendorBackends()
    {
        var poll = Every(Poll);
        if (_o.EnableNvml && _catalogList.Any(g => g.Vendor == GpuVendor.Nvidia))
        {
            _nvml = new NvmlBackend();
            if (!_o.PollInactiveGpus) _nvml.IsActive = IsActive;
            _nvmlWorker = new PollingWorker<BackendSample>("NVML", _nvml.Sample, poll, init: _nvml.Initialize, cleanup: _nvml.Shutdown, log: m => _log.Log(m));
            Start(_nvmlWorker);
        }
        if (_o.EnableAdlx && _catalogList.Any(g => g.Vendor == GpuVendor.Amd))
        {
            _adlx = new AdlxBackend();
            _adlxWorker = new PollingWorker<BackendSample>("ADLX", _adlx.Sample, poll, init: _adlx.Initialize, cleanup: _adlx.Shutdown, log: m => _log.Log(m));
            Start(_adlxWorker);
        }
        if (_o.EnableIgcl && _catalogList.Any(g => g.Vendor == GpuVendor.Intel))
        {
            _igcl = new IgclBackend();
            _igclWorker = new PollingWorker<BackendSample>("Intel IGCL", _igcl.Sample, poll, init: _igcl.Initialize, cleanup: _igcl.Shutdown, log: m => _log.Log(m));
            Start(_igclWorker);
        }
        if (_o.EnableLibreHardwareMonitor && _catalogList.Any(g => g.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel))
        {
            _lhmGpuWorker = new PollingWorker<BackendSample>("LibreHardwareMonitor", () =>
                {
                    var b = (ILhmGpuBackend)_lhmGpu!;
                    var sample = b.Sample();
                    if (Interlocked.Exchange(ref _lhmDumpRequested, 0) == 1) Volatile.Write(ref _lhmDump, b.Dump().ToList());
                    return sample;
                }, poll,
                init: () => InitLhm(() => _lhmGpu = _lhmBundle.CreateGpu()),
                cleanup: () => (_lhmGpu as IDisposable)?.Dispose(), log: m => _log.Log(m));
            Start(_lhmGpuWorker);
        }
    }

    private void StartCpuSensors()
    {
        if (_o.CpuSensorMode == CpuSensorMode.HwInfo)
        {
            _cpuSensorReason = "HWiNFO shared memory (external sensors; no module driver)";
            _lhmCpuWorker = new PollingWorker<CpuSensorReading>("HWiNFO CPU", () =>
            {
                float? temperature = null, power = null;
                try
                {
                    var snapshot = HwInfoReader.Read();
                    temperature = HwInfoParser.Value(snapshot, _o.HwInfoCpuTemperature, 1, DateTimeOffset.UtcNow);
                    power = HwInfoParser.Value(snapshot, _o.HwInfoCpuPower, 5, DateTimeOffset.UtcNow);
                }
                catch (Exception e) { _log.Log($"HWiNFO unavailable: {e.Message}. Start HWiNFO 7+ sensors and enable Shared Memory Support."); }
                return new CpuSensorReading(temperature, power, Stopwatch.GetTimestamp(), MetricSource.HwInfo);
            }, Every(Poll), log: m => _log.Log(m));
            Start(_lhmCpuWorker);
            return;
        }
        var (start, reason) = CpuSensorPolicy.Decide(_o.CpuSensorMode, IsElevated);
        _cpuSensorReason = reason;
        if (!start) return;
        if (!IsElevated) _log.Log("CPU sensors: CpuSensorMode is On but VRCOSC is not running as administrator; temperature and power may be unavailable.");
        _lhmCpuWorker = new PollingWorker<CpuSensorReading>("LibreHardwareMonitor CPU", () =>
            {
                var (t, p) = ((ILhmCpuSensors)_lhmCpu!).Read(_cpuIndex);
                return new CpuSensorReading(t, p, Stopwatch.GetTimestamp(), MetricSource.LibreHardwareMonitor);
            }, Every(Poll),
            init: () => InitLhm(() => _lhmCpu = _lhmBundle.CreateCpu()),
            cleanup: () => (_lhmCpu as IDisposable)?.Dispose(), log: m => _log.Log(m));
        Start(_lhmCpuWorker);
    }
    private static string? InitLhm(Action create)
    {
        try
        {
            create();
            return null;
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or MissingMethodException or TypeLoadException or MissingFieldException)
        {
            return $"LibreHardwareMonitorLib could not be loaded ({e.GetType().Name}); the local 0.9.6 bundle or a dependency is missing or incompatible; host fallback is disabled";
        }
    }
    private bool IsActive(BackendDevice d)
    {
        lock (_backendToStable)
        {
            if (!_backendToStable.TryGetValue(d.BackendKey, out var key)) return true;
            if (Latest.Gpu.SelectedStableKey == key) return true;
            return _lastActive.TryGetValue(key, out var t) && Stopwatch.GetTimestamp() - t < 10 * Stopwatch.Frequency;
        }
    }

    private long _lastCatalog, _lastRoute, _lastPids;
    private string? _routeKey;

    private SystemSample SystemPass()
    {
        var now = Stopwatch.GetTimestamp();
        var f = Stopwatch.Frequency;
        if (_o.EnableGpu && now - _lastCatalog >= 30 * f) RefreshCatalog();
        if (_o.EnableGpu && now - _lastPids >= 5 * f)
        {
            _gpuCounters.SetVRChatPids(VRChatProcesses.Find());
            _lastPids = now;
        }

        (long, long)? mem = _o.EnableRam ? MemoryStatus.Read() : null;

        var net = NetworkSnapshot.Empty;
        IReadOnlyList<string> aliases = [];
        if (_o.EnableNetwork)
        {
            var rows = NetworkTable.Read();
            aliases = rows.Where(NetworkSelector.IsCandidate).Select(r => r.Alias).ToList();
            if (now - _lastRoute >= 5 * f)
            {
                _routeKey = NetworkTable.DefaultRouteKey();
                _lastRoute = now;
            }
            _rates.Update(rows, now);
            net = _netSelector.Select(rows, _rates, _routeKey, _o.Network, now);
        }

        IReadOnlyList<VolumeInfo> vols = _o.EnableDisk ? _drives.Scan(_o.IncludeRemovableDrives, now) : [];
        return new SystemSample(mem, net, vols, aliases);
    }

    private void RefreshCatalog()
    {
        _lastCatalog = Stopwatch.GetTimestamp();
        var errors = new List<string>();
        var raw = DxgiEnumerator.Enumerate(errors);
        foreach (var e in errors) _log.Log("GPU discovery: " + e);
        var cat = GpuCatalogBuilder.Build(raw, new CatalogOptions(_o.Selection.IncludeIntegrated, _o.IncludeVirtualAdapters));
        foreach (var w in cat.Warnings) _log.Log("GPU discovery: " + w);

        // Keep the same list instance while nothing changed, so backend correlation stays cached.
        var changed = cat.Adapters.Count != _catalogList.Count
                      || cat.Adapters.Zip(_catalogList).Any(p => p.First.StableKey != p.Second.StableKey || p.First.Luid != p.Second.Luid);
        _catalog = cat;
        if (!changed) return;
        if (_catalogList.Count > 0) _log.Log($"GPU list changed: {string.Join("; ", cat.Adapters.Select(a => a.Name))}");
        Volatile.Write(ref _catalogList, cat.Adapters);
        Volatile.Write(ref _windowsDevices, WindowsCounterGpuSource.DevicesFor(cat.Adapters));
        _gpuCounters.SetKnownAdapters(cat.Adapters.Select(a => a.Luid).ToList());
    }

    private HardwareSnapshot Merge()
    {
        var now = Stopwatch.GetTimestamp();
        var nowUtc = DateTime.UtcNow;
        foreach (var w in new IDisposable?[] { _pdhWorker, _systemWorker, _nvmlWorker, _adlxWorker, _igclWorker, _lhmGpuWorker, _lhmCpuWorker })
            switch (w)
            {
                case PollingWorker<object> x: x.Check(now); break;
                case PollingWorker<SystemSample> x: x.Check(now); break;
                case PollingWorker<BackendSample> x: x.Check(now); break;
                case PollingWorker<CpuSensorReading> x: x.Check(now); break;
            }

        var maxAge = Math.Max(3 * Poll.TotalSeconds, 3) * Stopwatch.Frequency;
        var seq = Interlocked.Increment(ref _sequence);

        var gpu = GpuSnapshotSet.Empty;
        if (_o.EnableGpu)
        {
            var catalog = Volatile.Read(ref _catalogList);
            var sources = new List<GpuSourceInput>
            {
                new(WindowsStatus(), MetricSource.WindowsCounters, Volatile.Read(ref _windowsDevices), _gpuCounters.Latest?.Value),
            };
            void Add(PollingWorker<BackendSample>? w, MetricSource src, Func<IReadOnlyList<BackendDevice>> devices)
            {
                if (w is null) return;
                var st = w.Status;
                sources.Add(new(st, src, st.State is BackendState.Running or BackendState.Degraded ? devices() : [], w.Latest?.Value));
            }
            Add(_nvmlWorker, MetricSource.Nvml, () => _nvml!.Devices);
            Add(_adlxWorker, MetricSource.Adlx, () => _adlx!.Devices);
            Add(_igclWorker, MetricSource.Igcl, () => _igcl!.Devices);
            Add(_lhmGpuWorker, MetricSource.LibreHardwareMonitor, () => _lhmGpu is ILhmGpuBackend b ? b.Devices : []);

            // Slots for new GPUs wait until vendor backends finished starting (their kind can refine classification) or 10 s passed.
            var vendorsSettled = new[] { _nvmlWorker, _adlxWorker, _igclWorker, _lhmGpuWorker }.All(w => w is null || w.State != BackendState.NotStarted);
            var assignNew = vendorsSettled || now - _start > 10 * Stopwatch.Frequency;

            var resolver = new ResolverOptions
            {
                PollIntervalTicks = (long)(Poll.TotalSeconds * Stopwatch.Frequency),
                Sources = _o.Sources,
                Smoothing = _o.UtilizationSmoothing,
            };
            var r = _merger.Merge(catalog, sources, new GpuMergeOptions(resolver, _o.Selection, assignNew), now, nowUtc, seq);
            foreach (var w in r.Warnings) _log.Log(w);
            if (r.SlotPersistenceChanged) Interlocked.Exchange(ref _slotsDirty, 1);
            lock (_backendToStable)
            {
                foreach (var c in r.NewCorrelations)
                {
                    _correlations.RemoveAll(x => x.Device.BackendKey == c.Device.BackendKey);
                    _correlations.Add(c);
                    if (c.StableKey is not null) _backendToStable[c.Device.BackendKey] = c.StableKey;
                }
                foreach (var a in r.Set.Adapters)
                    if (a.UtilizationPercent is { Value: > 0 }) _lastActive[a.Identity.StableKey] = now;
            }
            gpu = r.Set;
        }

        var cpu = CpuSnapshot.Empty;
        if (_o.EnableCpu)
        {
            var c = _cpuCounters.Latest;
            var s = _lhmCpuWorker?.Latest?.Value;
            var detail = _lhmCpuWorker is null ? _cpuSensorReason : _lhmCpuWorker.Status.State == BackendState.Disabled ? _lhmCpuWorker.Status.Detail : null;
            cpu = CpuSnapshotBuilder.Build(_cpuIndex < _packages.Count ? _packages[_cpuIndex] : null, _cpuIndex, _packages.Count,
                c?.Value, c?.TimestampTicks ?? 0, s, _lhmCpuWorker is not null, detail, now, (long)maxAge);
        }

        var sys = _systemWorker?.Latest;
        var sysFresh = sys is not null && now - sys.TimestampTicks <= maxAge;
        var ram = RamSnapshot.Empty;
        if (_o.EnableRam && sysFresh && sys!.Value.Memory is { } m)
            ram = new RamSnapshot(new(m.Total, MetricSource.SystemApi, sys.TimestampTicks), new(m.Available, MetricSource.SystemApi, sys.TimestampTicks));

        var disk = DiskSnapshotSet.Empty;
        if (_o.EnableDisk)
        {
            var dc = _diskCounters.Latest;
            var counters = dc is not null && now - dc.TimestampTicks <= maxAge ? dc.Value : null;
            var vols = sys?.Value.Volumes ?? [];
            var plan = _o.DiskSlotChoices is { } choices
                ? DiskCalculators.PlanSelectedSlots(choices, _drives.SystemDrive, vols, _o.IncludeRemovableDrives)
                : DiskCalculators.PlanSlots(_o.DiskVolumes, _drives.SystemDrive, vols, _o.IncludeRemovableDrives);
            disk = DiskCalculators.BuildSnapshot(counters, plan, vols, now);
        }

        var totals = _netSelector.SessionTotals;
        var net = _o.EnableNetwork && sysFresh ? sys!.Value.Network : NetworkSnapshot.Empty with
        { SessionDownloadBytes = totals.Down, SessionUploadBytes = totals.Up };

        return new HardwareSnapshot(seq, now, gpu, cpu, ram, disk, net);
    }

    private BackendStatus WindowsStatus()
    {
        var w = _pdhWorker;
        if (w is null) return new BackendStatus("WindowsCounters", BackendState.Disabled, "GPU domain disabled");
        var st = w.Status;
        return _gpuCounters.CounterSetAvailable
            ? st with { Name = "WindowsCounters" }
            : st with { Name = "WindowsCounters", State = BackendState.Disabled, Detail = "GPU Engine counters unavailable (requires WDDM 2.0+)" };
    }
    public IReadOnlyList<(string Source, string? StableKey, string BackendKey, PartialGpuMetrics Metrics, double AgeMs)> RawGpuValues()
    {
        var now = Stopwatch.GetTimestamp();
        var list = new List<(string, string?, string, PartialGpuMetrics, double)>();
        var byLuid = Volatile.Read(ref _catalogList).ToDictionary(c => WindowsCounterGpuSource.KeyFor(c.Luid), c => c.StableKey);
        void Add(string name, BackendSample? sample, Func<string, string?> map)
        {
            if (sample is null) return;
            foreach (var (k, m) in sample.ByBackendKey)
                list.Add((name, map(k), k, m, (now - sample.TimestampTicks) * 1000.0 / Stopwatch.Frequency));
        }
        string? Mapped(string k) { lock (_backendToStable) return _backendToStable.TryGetValue(k, out var v) ? v : null; }
        Add("WindowsCounters", _gpuCounters.Latest?.Value, k => byLuid.TryGetValue(k, out var v) ? v : null);
        Add("NVML", _nvmlWorker?.Latest?.Value, Mapped);
        Add("ADLX", _adlxWorker?.Latest?.Value, Mapped);
        Add("LibreHardwareMonitor", _lhmGpuWorker?.Latest?.Value, Mapped);
        return list;
    }
    public IReadOnlyList<(string Path, string Instance, double Value, bool Valid)> CounterDump(TimeSpan? timeout = null)
    {
        if (_pdhWorker is null) return [];
        Volatile.Write(ref _pdhDump, null);
        Interlocked.Exchange(ref _pdhDumpRequested, 1);
        return WaitFor(() => Volatile.Read(ref _pdhDump), timeout) ?? [];
    }
    public IReadOnlyList<(string Hardware, string Type, string Name, string Identifier, float? Value)> LhmSensorDump(TimeSpan? timeout = null)
    {
        if (_lhmGpuWorker is not { State: BackendState.Running or BackendState.Degraded }) return [];
        Volatile.Write(ref _lhmDump, null);
        Interlocked.Exchange(ref _lhmDumpRequested, 1);
        return WaitFor(() => Volatile.Read(ref _lhmDump), timeout) ?? [];
    }

    internal IReadOnlyDictionary<string, int> CounterInstanceCounts => Volatile.Read(ref _pdhCounts) ?? new Dictionary<string, int>();

    private static T? WaitFor<T>(Func<T?> read, TimeSpan? timeout) where T : class
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        while (sw.Elapsed < limit)
        {
            if (read() is { } v) return v;
            Thread.Sleep(50);
        }
        return null;
    }
    public string DiagnosticsReport() => Diagnostics.Build(this);

    internal (GpuCatalog Catalog, IReadOnlyList<CorrelationResult> Correlations, IReadOnlyList<BackendStatus> Workers,
        PdhEngine? Pdh, IReadOnlyList<CpuPackage> Packages, int CpuIndex, string? CpuSensorReason, bool CpuFallback,
        object? LhmGpu, object? LhmCpu, NvmlBackend? Nvml, AdlxBackend? Adlx, IReadOnlyCollection<Luid> AbsentAdapters, HardwareServiceOptions Options,
        IReadOnlyList<string> Interfaces, string? VRChatGpu) State()
    {
        List<CorrelationResult> corr;
        lock (_backendToStable) corr = [.. _correlations];
        var workers = new IDisposable?[] { _pdhWorker, _systemWorker, _nvmlWorker, _adlxWorker, _igclWorker, _lhmGpuWorker, _lhmCpuWorker, _mergeWorker }
            .Select(w => w switch
            {
                PollingWorker<object> x => x.Status,
                PollingWorker<SystemSample> x => x.Status,
                PollingWorker<BackendSample> x => x.Status,
                PollingWorker<CpuSensorReading> x => x.Status,
                PollingWorker<HardwareSnapshot> x => x.Status,
                _ => null,
            }).OfType<BackendStatus>().ToList();
        return (_catalog, corr, workers, _pdh, _packages, _cpuIndex, _cpuSensorReason, _cpuCounters.UsingFallback, _lhmGpu, _lhmCpu,
            _nvml, _adlx, _gpuCounters.AbsentAdapters, _o, _systemWorker?.Latest?.Value.InterfaceAliases ?? [], _merger.Selection.VRChatGpu);
    }

    public void Dispose()
    {
        // Merge first so nothing reads backends that are shutting down, then backends, then the rest.
        foreach (var d in Enumerable.Reverse(_disposables))
        {
            try { d.Dispose(); }
            catch (Exception e) { _log.Log($"Shutdown: {e.Message}"); }
        }
        _disposables.Clear();
        _lhmBundle.Dispose();
    }
}
