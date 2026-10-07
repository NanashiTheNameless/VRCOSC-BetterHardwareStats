using System.Globalization;
using BetterHardwareStats.Core.Counters;

namespace BetterHardwareStats.Core.Cpu;
public readonly record struct GroupAffinity(ushort Group, ulong Mask)
{
    public bool Contains(int group, int index) => group == Group && index is >= 0 and < 64 && (Mask & (1UL << index)) != 0;
}
public sealed record CpuPackage(int Index, string? Name, IReadOnlyList<GroupAffinity> Affinity)
{
    public bool Contains(int group, int index)
    {
        foreach (var a in Affinity)
            if (a.Contains(group, index)) return true;
        return false;
    }
}

public enum ProcessorInstanceKind
{
    Total,
    GroupTotal,
    Logical,
}

public readonly record struct ProcessorInstance(ProcessorInstanceKind Kind, int Group, int Index);

public sealed record CpuCounterResult(float? UsagePercent, float? FrequencyMhz);
public static class CpuUsageCalculator
{
    public static bool TryParseInstance(ReadOnlySpan<char> name, out ProcessorInstance instance)
    {
        instance = default;
        name = name.Trim();
        if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
        {
            instance = new ProcessorInstance(ProcessorInstanceKind.Total, -1, -1);
            return true;
        }
        var comma = name.IndexOf(',');
        if (comma <= 0 || !int.TryParse(name[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out var group)) return false;
        var rest = name[(comma + 1)..];
        if (rest.Equals("_Total", StringComparison.OrdinalIgnoreCase))
        {
            instance = new ProcessorInstance(ProcessorInstanceKind.GroupTotal, group, -1);
            return true;
        }
        if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false;
        instance = new ProcessorInstance(ProcessorInstanceKind.Logical, group, index);
        return true;
    }
    public static (int Index, string? Warning) ResolveIndex(int requested, IReadOnlyList<CpuPackage> packages)
    {
        if (packages.Count == 0) return (0, null);
        if (requested >= 0 && requested < packages.Count) return (requested, null);
        var list = string.Join("; ", packages.Select(p => $"{p.Index}: {p.Name ?? "unknown CPU"}"));
        return (0, $"SelectedCPU {requested} does not match a CPU. Valid CPU indexes: {list}. Using 0.");
    }
    public static CpuCounterResult Compute(
        CpuPackage package, int packageCount, ICounterReader usage, ICounterReader? frequency, ICounterReader? performance)
    {
        var single = packageCount <= 1;
        float? u = usage.Available ? Value(usage, package, single) : null;
        if (u is { } uv) u = Math.Clamp(uv, 0f, 100f);

        float? mhz = null;
        if (frequency is { Available: true } && performance is { Available: true })
            mhz = Frequency(package, single, frequency, performance);

        return new CpuCounterResult(u, mhz);
    }

    private static float? Value(ICounterReader r, CpuPackage package, bool single)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < r.Count; i++)
        {
            if (!r.IsValid(i) || !TryParseInstance(r.GetName(i), out var inst)) continue;
            var v = r.GetValue(i);
            if (!double.IsFinite(v)) continue;
            if (single && inst.Kind == ProcessorInstanceKind.Total) return (float)v;
            if (inst.Kind == ProcessorInstanceKind.Logical && (single || package.Contains(inst.Group, inst.Index)))
            {
                sum += v;
                n++;
            }
        }
        return n > 0 ? (float)(sum / n) : null;
    }

    private static float? Frequency(CpuPackage package, bool single, ICounterReader nominal, ICounterReader perf)
    {
        // Join by instance; LP counts are small, so a short-lived dictionary is fine.
        var perfByInstance = new Dictionary<ProcessorInstance, double>();
        for (var i = 0; i < perf.Count; i++)
            if (perf.IsValid(i) && TryParseInstance(perf.GetName(i), out var inst) && double.IsFinite(perf.GetValue(i)))
                perfByInstance[inst] = perf.GetValue(i);

        double sum = 0;
        var n = 0;
        for (var i = 0; i < nominal.Count; i++)
        {
            if (!nominal.IsValid(i) || !TryParseInstance(nominal.GetName(i), out var inst)) continue;
            if (!perfByInstance.TryGetValue(inst, out var p)) continue;
            var mhz = nominal.GetValue(i) * p / 100.0;
            if (!double.IsFinite(mhz) || mhz <= 0) continue;
            if (single && inst.Kind == ProcessorInstanceKind.Total) return (float)mhz;
            if (inst.Kind == ProcessorInstanceKind.Logical && (single || package.Contains(inst.Group, inst.Index)))
            {
                sum += mhz;
                n++;
            }
        }
        return n > 0 ? (float)(sum / n) : null;
    }
}
