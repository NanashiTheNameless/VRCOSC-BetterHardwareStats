using System.Diagnostics;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;

public sealed record SelectionOptions(
    GpuSelectionMode Mode = GpuSelectionMode.AutoVRChat,
    string? SpecificGpu = null,
    bool IncludeIntegrated = true);

public sealed record SelectionResult(string? StableKey, string Reason, bool Changed, string? Warning);
public sealed class GpuSelectionEngine
{
    public const float VRChatThresholdPercent = 1f;

    private readonly LeaderTracker _vrchat;
    private readonly LeaderTracker _load;
    private string? _lastSelected;
    private string? _lastWarning;

    public GpuSelectionEngine(long? windowTicks = null)
    {
        var w = windowTicks ?? 5 * Stopwatch.Frequency;
        _vrchat = new LeaderTracker(w, 3, VRChatThresholdPercent);
        _load = new LeaderTracker(w, 3, 0);
    }
    public string? VRChatGpu => _vrchat.Leader;

    public string? HighestLoadGpu => _load.Leader;
    public void Observe(IReadOnlyList<GpuSnapshot> adapters, long nowTicks)
    {
        var keys = adapters.Select(a => a.Identity.StableKey).ToList();
        _vrchat.Retain(keys);
        _load.Retain(keys);
        _vrchat.Advance(nowTicks);
        _load.Advance(nowTicks);
        foreach (var a in adapters)
        {
            if (a.VRChatUtilizationPercent is { } v) _vrchat.Add(a.Identity.StableKey, v.Value, nowTicks);
            if (a.UtilizationPercent is { } u) _load.Add(a.Identity.StableKey, u.Value, nowTicks);
        }
    }

    public SelectionResult Select(IReadOnlyList<GpuIdentity> adapters, SelectionOptions options)
    {
        var pool = options.IncludeIntegrated ? adapters : adapters.Where(a => a.Kind != GpuKind.Integrated).ToList();
        if (pool.Count == 0 && adapters.Count > 0) pool = adapters; // never select nothing when something exists
        string? warning = null;

        (string? Key, string Reason) pick = options.Mode switch
        {
            GpuSelectionMode.AutoVRChat => FromLeader(pool, _vrchat.Leader, "VRChat GPU"),
            GpuSelectionMode.AutoHighestLoad => FromLeader(pool, _load.Leader, "highest 5 s load"),
            GpuSelectionMode.DiscreteFirst => (null, ""),
            GpuSelectionMode.IntegratedFirst => FirstIntegrated(pool),
            GpuSelectionMode.Specific => Specific(adapters, pool, options.SpecificGpu, out warning),
            _ => (null, ""),
        };

        if (pick.Key is null)
        {
            var (key, reason) = DiscreteFirst(pool);
            pick = (key, options.Mode == GpuSelectionMode.DiscreteFirst ? reason : $"fallback: {reason}");
        }

        if (warning is not null)
        {
            if (warning == _lastWarning) warning = null;
            else _lastWarning = warning;
        }

        var changed = pick.Key != _lastSelected;
        _lastSelected = pick.Key;
        return new SelectionResult(pick.Key, pick.Reason, changed, warning);
    }
    public static (string? Key, string Reason) DiscreteFirst(IReadOnlyList<GpuIdentity> pool)
    {
        var d = SlotAssigner.SortOrder(pool.Where(a => a.Kind == GpuKind.Discrete)).FirstOrDefault();
        if (d is not null) return (d.StableKey, "discrete with most VRAM");
        var any = pool.OrderByDescending(a => a.DedicatedVramBytes).ThenBy(a => a.StableKey, StringComparer.Ordinal).FirstOrDefault();
        return (any?.StableKey, any is null ? "no adapters" : "highest VRAM adapter");
    }

    private static (string?, string) FromLeader(IReadOnlyList<GpuIdentity> pool, string? leader, string why) =>
        leader is not null && pool.Any(a => a.StableKey == leader) ? (leader, why) : (null, "");

    private static (string?, string) FirstIntegrated(IReadOnlyList<GpuIdentity> pool)
    {
        var i = SlotAssigner.SortOrder(pool.Where(a => a.Kind == GpuKind.Integrated)).FirstOrDefault();
        return (i?.StableKey, "first integrated");
    }

    private (string?, string) Specific(IReadOnlyList<GpuIdentity> all, IReadOnlyList<GpuIdentity> pool, string? key, out string? warning)
    {
        warning = null;
        if (key is null) return FromLeader(pool, _vrchat.Leader, "VRChat GPU");
        if (all.FirstOrDefault(a => a.StableKey == key) is { } exact) return (exact.StableKey, $"chosen GPU {exact.Name}");

        var model = GpuChoices.ModelKey(key);
        var sameModel = all.Where(a => GpuChoices.ModelKey(a.StableKey) == model).ToList();
        if (sameModel.Count == 1)
        {
            warning = $"The chosen GPU ({key}) was not found; using {sameModel[0].Name}, the same model at a different location. Choose it again in the settings to save its new location.";
            return (sameModel[0].StableKey, "chosen GPU (same model)");
        }

        var present = SlotAssigner.SortOrder(all).Select(a => a.Name).ToList();
        warning = $"The chosen GPU ({key}) was not found; using Auto (VRChat's GPU) until it is back. GPUs found: " +
                  (present.Count == 0 ? "none" : string.Join("; ", present));
        var (leader, why) = FromLeader(pool, _vrchat.Leader, "VRChat GPU");
        return (leader, leader is null ? "" : $"chosen GPU missing, {why}");
    }
}
