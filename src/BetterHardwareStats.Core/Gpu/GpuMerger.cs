using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public sealed record GpuMergeOptions(ResolverOptions Resolver, SelectionOptions Selection, bool AssignNewSlots = true);

public sealed record GpuMergeResult(
    GpuSnapshotSet Set,
    bool SelectionChanged,
    bool SlotPersistenceChanged,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CorrelationResult> NewCorrelations);
public sealed class GpuMerger
{
    private readonly SlotAssigner _slots;
    private readonly GpuSelectionEngine _selection;
    private readonly GpuMetricResolver _resolver;
    private readonly Dictionary<string, CorrelationCache> _correlations = new(StringComparer.Ordinal);
    private readonly List<string> _pendingWarnings = [];

    private sealed record CorrelationCache(
        IReadOnlyList<GpuIdentity> Catalog,
        IReadOnlyList<BackendDevice> Devices,
        IReadOnlyDictionary<string, string> BackendKeyToStableKey,
        IReadOnlyList<CorrelationResult> Results);

    public GpuMerger(SlotAssigner slots, GpuSelectionEngine? selection = null, GpuMetricResolver? resolver = null)
    {
        _slots = slots;
        _selection = selection ?? new GpuSelectionEngine();
        _resolver = resolver ?? new GpuMetricResolver();
    }

    public SlotAssigner Slots => _slots;
    public GpuSelectionEngine Selection => _selection;

    public GpuMergeResult Merge(
        IReadOnlyList<GpuIdentity> catalog,
        IReadOnlyList<GpuSourceInput> sources,
        GpuMergeOptions options,
        long nowTicks,
        DateTime nowUtc,
        long sequence)
    {
        _resolver.Options = options.Resolver;
        var warnings = new List<string>();
        var newCorrelations = new List<CorrelationResult>();

        // Correlate (cached until the catalog or the device list changes) and gather per-adapter metrics.
        var perAdapter = catalog.ToDictionary(c => c.StableKey, _ => new List<SourcedMetrics>(), StringComparer.Ordinal);
        var reportedKinds = new Dictionary<string, GpuKind>(StringComparer.Ordinal);
        foreach (var src in sources)
        {
            if (src.Status.State is not (BackendState.Running or BackendState.Degraded)) continue;

            var cache = GetCorrelation(src.Status.Name, catalog, src.Devices, newCorrelations);
            foreach (var d in src.Devices)
                if (d.ReportedKind is { } k && cache.BackendKeyToStableKey.TryGetValue(d.BackendKey, out var sk)) reportedKinds[sk] = k;

            if (src.Latest is not { } sample) continue;
            foreach (var (backendKey, metrics) in sample.ByBackendKey)
                if (cache.BackendKeyToStableKey.TryGetValue(backendKey, out var stableKey) && perAdapter.TryGetValue(stableKey, out var list))
                    list.Add(new SourcedMetrics(src.Source, sample.TimestampTicks, metrics));
        }

        warnings.AddRange(_pendingWarnings);
        _pendingWarnings.Clear();

        // Vendor-reported kind refines classification when D3DKMT hybrid flags did not decide.
        var effective = catalog.Select(c => reportedKinds.TryGetValue(c.StableKey, out var k) ? GpuCatalogBuilder.WithBackendKind(c, k) : c).ToList();

        var snapshots = effective.Select(id => _resolver.Resolve(id, perAdapter[id.StableKey], nowTicks)).ToList();
        var keys = effective.Select(e => e.StableKey).ToList();
        _resolver.Retain(keys);

        var slotResult = _slots.Assign(effective, nowUtc, options.AssignNewSlots);
        if (slotResult.Warning is not null) warnings.Add(slotResult.Warning);
        warnings.AddRange(slotResult.Messages);

        _selection.Observe(snapshots, nowTicks);
        var sel = _selection.Select(effective, options.Selection);
        if (sel.Warning is not null) warnings.Add(sel.Warning);

        var set = new GpuSnapshotSet(
            sequence,
            nowUtc,
            snapshots,
            sel.StableKey,
            slotResult.Slots,
            sources.Select(s => s.Status).ToList());

        return new GpuMergeResult(set, sel.Changed, slotResult.PersistenceChanged, warnings, newCorrelations);
    }

    private CorrelationCache GetCorrelation(
        string sourceName, IReadOnlyList<GpuIdentity> catalog, IReadOnlyList<BackendDevice> devices, List<CorrelationResult> newResults)
    {
        if (_correlations.TryGetValue(sourceName, out var c) && ReferenceEquals(c.Catalog, catalog) && ReferenceEquals(c.Devices, devices))
            return c;

        var results = BackendCorrelator.Correlate(catalog, devices);
        foreach (var r in results.Where(r => r.StableKey is null))
            _pendingWarnings.Add($"{sourceName}: device '{r.Device.Name ?? r.Device.BackendKey}' ({r.Device.BackendKey}) matches no GPU and is ignored. Tried: {string.Join("; ", r.Trace)}");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in results)
            if (r.StableKey is not null) map[r.Device.BackendKey] = r.StableKey;

        newResults.AddRange(results);
        c = new CorrelationCache(catalog, devices, map, results);
        _correlations[sourceName] = c;
        return c;
    }
}
