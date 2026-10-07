using System.Globalization;
using BetterHardwareStats.Core.Counters;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Disk;
public sealed record PhysicalDiskInstance(int Index, IReadOnlyList<char> Letters);

public sealed record DiskCounterResult(
    float ActivityPercent,
    double? ReadBytesPerSec,
    double? WriteBytesPerSec,
    IReadOnlyDictionary<char, float> ActivityByLetter);
public sealed record VolumeInfo(char Letter, bool IsFixed, bool IsRemovable, bool IsReady, long? TotalBytes = null, long? FreeBytes = null);

public static class DiskCalculators
{
    public const int SlotCount = 4;
    public static bool TryParseInstance(string name, out PhysicalDiskInstance instance)
    {
        instance = null!;
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false;
        var letters = new List<char>();
        foreach (var p in parts.Skip(1))
            if (p.Length == 2 && p[1] == ':' && char.IsAsciiLetter(p[0])) letters.Add(char.ToUpperInvariant(p[0]));
        instance = new PhysicalDiskInstance(index, letters);
        return true;
    }
    public static DiskCounterResult? Aggregate(ICounterReader idle, ICounterReader? read, ICounterReader? write)
    {
        if (!idle.Available) return null;

        var byLetter = new Dictionary<char, float>();
        float max = 0;
        for (var i = 0; i < idle.Count; i++)
        {
            if (!idle.IsValid(i) || !TryParseInstance(idle.GetName(i).ToString(), out var inst)) continue;
            var v = idle.GetValue(i);
            if (!double.IsFinite(v)) continue;
            var activity = (float)Math.Clamp(100 - v, 0, 100);
            max = Math.Max(max, activity);
            foreach (var l in inst.Letters) byLetter[l] = activity;
        }

        return new DiskCounterResult(max, Sum(read), Sum(write), byLetter);
    }

    private static double? Sum(ICounterReader? r)
    {
        if (r is not { Available: true }) return null;
        double sum = 0;
        for (var i = 0; i < r.Count; i++)
        {
            if (!r.IsValid(i) || !TryParseInstance(r.GetName(i).ToString(), out _)) continue;
            var v = r.GetValue(i);
            if (double.IsFinite(v) && v > 0) sum += v;
        }
        return sum;
    }
    public static IReadOnlyList<char> ParseLetters(string? setting)
    {
        var result = new List<char>();
        if (string.IsNullOrWhiteSpace(setting)) return result;
        foreach (var raw in setting.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim().TrimEnd('\\', '/').TrimEnd(':');
            if (t.Length != 1 || !char.IsAsciiLetter(t[0])) continue;
            var c = char.ToUpperInvariant(t[0]);
            if (!result.Contains(c)) result.Add(c);
            if (result.Count == SlotCount) break;
        }
        return result;
    }
    public static char?[] PlanSlots(string? diskVolumesSetting, char? systemDrive, IReadOnlyList<VolumeInfo> volumes, bool includeRemovable)
    {
        var slots = new char?[SlotCount];
        var configured = ParseLetters(diskVolumesSetting);
        if (configured.Count > 0)
        {
            for (var i = 0; i < configured.Count; i++) slots[i] = configured[i];
            return slots;
        }

        var eligible = volumes.Where(v => v.IsReady && (v.IsFixed || (includeRemovable && v.IsRemovable)))
                              .Select(v => char.ToUpperInvariant(v.Letter))
                              .Distinct()
                              .OrderBy(c => c)
                              .ToList();
        var sys = systemDrive is { } s ? char.ToUpperInvariant(s) : (char?)null;
        if (sys is { } sd && eligible.Remove(sd)) eligible.Insert(0, sd);
        for (var i = 0; i < Math.Min(SlotCount, eligible.Count); i++) slots[i] = eligible[i];
        return slots;
    }
    public static char?[] PlanSelectedSlots(IReadOnlyList<string?> choices, char? systemDrive, IReadOnlyList<VolumeInfo> volumes, bool includeRemovable)
    {
        var slots = new char?[SlotCount];
        var reserved = new HashSet<char>();
        for (var i = 0; i < SlotCount && i < choices.Count; i++)
        {
            var value = choices[i];
            if (value is { Length: 1 } && char.IsAsciiLetter(value[0]))
            {
                var letter = char.ToUpperInvariant(value[0]);
                if (reserved.Add(letter)) slots[i] = letter;
            }
        }
        var automatic = new Queue<char>(PlanSlots(null, systemDrive, volumes.Where(v => !reserved.Contains(char.ToUpperInvariant(v.Letter))).ToList(), includeRemovable)
            .Where(c => c.HasValue).Select(c => c!.Value));
        for (var i = 0; i < SlotCount; i++)
        {
            var value = i < choices.Count ? choices[i] : null;
            if (value is null or "Auto" && automatic.Count > 0) slots[i] = automatic.Dequeue();
        }
        return slots;
    }
    public static DiskSnapshotSet BuildSnapshot(
        DiskCounterResult? counters, IReadOnlyList<char?> slots, IReadOnlyList<VolumeInfo> volumes, long timestampTicks)
    {
        var entries = new DiskVolumeSnapshot?[SlotCount];
        for (var i = 0; i < SlotCount && i < slots.Count; i++)
        {
            if (slots[i] is not { } letter) continue;
            var v = volumes.FirstOrDefault(x => char.ToUpperInvariant(x.Letter) == letter);
            var present = v is { IsReady: true, TotalBytes: > 0 };
            float? activity = counters is not null && counters.ActivityByLetter.TryGetValue(letter, out var a) ? a : null;
            entries[i] = new DiskVolumeSnapshot(
                $"{letter}:", present, present ? v!.TotalBytes : null, present ? v!.FreeBytes : null, present ? activity : null);
        }
        return new DiskSnapshotSet(
            counters is not null,
            counters?.ActivityPercent,
            counters?.ReadBytesPerSec,
            counters?.WriteBytesPerSec,
            entries,
            timestampTicks);
    }
}
