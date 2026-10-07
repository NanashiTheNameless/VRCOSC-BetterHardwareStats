using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public sealed record GpuChoice(string Title, string Value);
public static class GpuChoices
{
    public const string AutoVRChat = "auto:vrchat";
    public const string AutoHighestLoad = "auto:load";
    public const string DiscreteFirst = "auto:discrete";
    public const string IntegratedFirst = "auto:integrated";
    private const string GpuPrefix = "gpu:";

    public static readonly IReadOnlyList<GpuChoice> Automatic =
    [
        new("Auto (VRChat's GPU)", AutoVRChat),
        new("Auto (busiest GPU)", AutoHighestLoad),
        new("Auto (discrete first)", DiscreteFirst),
        new("Auto (integrated first)", IntegratedFirst),
    ];

    public static IReadOnlyList<GpuChoice> Build(IReadOnlyList<GpuIdentity> adapters)
    {
        var list = new List<GpuChoice>(Automatic);
        var sorted = SlotAssigner.SortOrder(adapters).ToList();
        var counts = sorted.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in sorted)
        {
            var title = a.Name;
            if (counts[a.Name] > 1)
            {
                seen[a.Name] = seen.GetValueOrDefault(a.Name) + 1;
                title = $"{a.Name} #{seen[a.Name]}";
            }
            list.Add(new GpuChoice($"{title} [{a.StableKey}]", GpuPrefix + a.StableKey));
        }
        return list;
    }
    public static (GpuSelectionMode Mode, string? StableKey) Parse(string? value) => value switch
    {
        AutoHighestLoad => (GpuSelectionMode.AutoHighestLoad, null),
        DiscreteFirst => (GpuSelectionMode.DiscreteFirst, null),
        IntegratedFirst => (GpuSelectionMode.IntegratedFirst, null),
        { } v when v.StartsWith(GpuPrefix, StringComparison.Ordinal) && v.Length > GpuPrefix.Length => (GpuSelectionMode.Specific, v[GpuPrefix.Length..]),
        _ => (GpuSelectionMode.AutoVRChat, null),
    };
    public static string ModelKey(string stableKey)
    {
        var parts = stableKey.Split(':');
        return parts.Length >= 4 ? string.Join(':', parts.Take(4)) : stableKey;
    }
}
