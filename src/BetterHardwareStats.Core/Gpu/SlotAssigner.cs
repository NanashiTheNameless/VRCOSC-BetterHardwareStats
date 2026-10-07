using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public sealed record SlotReservation(string StableKey, int Slot, DateTime LastSeenUtc, GpuKind Kind = GpuKind.Unknown);
public sealed record SlotAssignment(
    IReadOnlyList<string?> Slots,
    IReadOnlyList<string> Overflow,
    bool PersistenceChanged,
    string? Warning,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Pending);
public sealed class SlotAssigner
{
    public const int SlotCount = 4;

    private readonly TimeSpan _reservation;
    private readonly Dictionary<string, SlotReservation> _byKey = new(StringComparer.Ordinal);
    private bool _overflowWarned;

    public SlotAssigner(IEnumerable<SlotReservation>? persisted = null, TimeSpan? reservation = null)
    {
        _reservation = reservation ?? TimeSpan.FromDays(7);
        if (persisted is null) return;

        // Defensive load: drop out-of-range slots and duplicates (first entry by most recent sighting wins).
        var taken = new HashSet<int>();
        foreach (var r in persisted.Where(r => r is not null && !string.IsNullOrEmpty(r.StableKey))
                                   .OrderByDescending(r => r.LastSeenUtc))
        {
            if (r.Slot is < 0 or >= SlotCount || _byKey.ContainsKey(r.StableKey) || !taken.Add(r.Slot)) continue;
            _byKey[r.StableKey] = r;
        }
    }

    public IReadOnlyList<SlotReservation> Export() => _byKey.Values.OrderBy(r => r.Slot).ToList();
    public static IOrderedEnumerable<GpuIdentity> SortOrder(IEnumerable<GpuIdentity> adapters) =>
        adapters.OrderBy(a => a.Kind switch { GpuKind.Discrete => 0, GpuKind.Integrated => 1, _ => 2 })
                .ThenByDescending(a => a.DedicatedVramBytes)
                .ThenBy(a => a.StableKey, StringComparer.Ordinal);
    public SlotAssignment Assign(IReadOnlyList<GpuIdentity> adapters, DateTime nowUtc, bool assignNew = true)
    {
        var changed = false;
        var messages = new List<string>();
        var byKey = adapters.GroupBy(a => a.StableKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var r in _byKey.Values.ToList())
        {
            if (byKey.TryGetValue(r.StableKey, out var a))
            {
                // Persist sightings at day granularity so the module is not rewriting persistence every tick.
                // Kind can change after vendor-backend classification; keep it current for replacement matching.
                if (nowUtc - r.LastSeenUtc >= TimeSpan.FromDays(1) || r.Kind != a.Kind)
                {
                    _byKey[r.StableKey] = r with { LastSeenUtc = nowUtc, Kind = a.Kind };
                    changed = true;
                }
            }
            else if (nowUtc - r.LastSeenUtc > _reservation)
            {
                _byKey.Remove(r.StableKey);
                changed = true;
            }
        }

        var slots = new string?[SlotCount];
        foreach (var r in _byKey.Values)
            if (byKey.ContainsKey(r.StableKey)) slots[r.Slot] = r.StableKey;

        var overflow = new List<string>();
        var pending = new List<string>();
        foreach (var a in SortOrder(byKey.Values.Where(a => !_byKey.ContainsKey(a.StableKey))))
        {
            if (!assignNew)
            {
                pending.Add(a.StableKey);
                continue;
            }

            var held = _byKey.Values.Where(r => !byKey.ContainsKey(r.StableKey)).ToList();
            var taken = new HashSet<int>(_byKey.Values.Select(r => r.Slot));

            var replaced = held.Where(r => r.Kind == a.Kind).OrderBy(r => r.Slot).FirstOrDefault();
            int slot;
            if (replaced is not null) slot = replaced.Slot;
            else if (Enumerable.Range(0, SlotCount).FirstOrDefault(i => !taken.Contains(i), -1) is var free and >= 0) slot = free;
            else if ((replaced = held.OrderBy(r => r.LastSeenUtc).ThenBy(r => r.Slot).FirstOrDefault()) is not null) slot = replaced.Slot;
            else
            {
                overflow.Add(a.StableKey);
                continue;
            }

            if (replaced is not null)
            {
                _byKey.Remove(replaced.StableKey);
                messages.Add($"GPU '{a.Name}' takes slot {slot} from missing GPU {replaced.StableKey} (last seen {replaced.LastSeenUtc:yyyy-MM-dd}).");
            }
            slots[slot] = a.StableKey;
            _byKey[a.StableKey] = new SlotReservation(a.StableKey, slot, nowUtc, a.Kind);
            changed = true;
        }

        string? warning = null;
        if (overflow.Count > 0 && !_overflowWarned)
        {
            _overflowWarned = true;
            warning = $"More GPUs than slots ({SlotCount}); without a slot (Selected group and diagnostics only): {string.Join(", ", overflow)}";
        }

        return new SlotAssignment(slots, overflow, changed, warning, messages, pending);
    }
}
