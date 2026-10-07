using System.Globalization;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public static class SlotStateCodec
{
    public static string Encode(IEnumerable<SlotReservation> reservations) =>
        string.Join(';', reservations.Select(r =>
            string.Join('|', r.StableKey, r.Slot.ToString(CultureInfo.InvariantCulture), r.LastSeenUtc.Ticks.ToString(CultureInfo.InvariantCulture), r.Kind.ToString())));

    public static IReadOnlyList<SlotReservation> Decode(string? state)
    {
        var list = new List<SlotReservation>();
        if (string.IsNullOrWhiteSpace(state)) return list;
        foreach (var entry in state.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split('|');
            if (p.Length != 4 || p[0].Length == 0) continue;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)) continue;
            if (!long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
            var kind = Enum.TryParse<GpuKind>(p[3], out var k) ? k : GpuKind.Unknown;
            list.Add(new SlotReservation(p[0], slot, new DateTime(ticks, DateTimeKind.Utc), kind));
        }
        return list;
    }
}
