using System.Text.RegularExpressions;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;

public sealed record CorrelationResult(
    BackendDevice Device,
    string? StableKey,
    int? Rule,
    IReadOnlyList<string> Trace);
public static partial class BackendCorrelator
{
    public static IReadOnlyList<CorrelationResult> Correlate(IReadOnlyList<GpuIdentity> catalog, IReadOnlyList<BackendDevice> devices)
    {
        var traces = devices.Select(_ => new List<string>()).ToArray();
        var keys = new string?[devices.Count];
        var rules = new int?[devices.Count];
        var claimed = new HashSet<string>();

        for (var rule = 1; rule <= RuleCount; rule++)
        {
            var hits = new List<GpuIdentity>?[devices.Count];
            for (var i = 0; i < devices.Count; i++)
            {
                if (keys[i] is not null) continue;
                var match = Rule(rule, catalog, devices, devices[i]);
                if (match is null) { traces[i].Add($"rule {rule} ({RuleNames[rule]}): not applicable"); continue; }
                hits[i] = catalog.Where(c => !claimed.Contains(c.StableKey) && match(c)).ToList();
                traces[i].Add($"rule {rule} ({RuleNames[rule]}): {hits[i]!.Count} match(es)");
            }
            for (var i = 0; i < devices.Count; i++)
            {
                if (hits[i] is not { Count: 1 } h) continue;
                var key = h[0].StableKey;
                var rivals = Enumerable.Range(0, devices.Count).Count(j => j != i && hits[j] is { Count: 1 } hj && hj[0].StableKey == key);
                if (rivals > 0) { traces[i].Add($"rule {rule}: {rivals} other device(s) match the same adapter, skipped"); continue; }
                keys[i] = key;
                rules[i] = rule;
                claimed.Add(key);
            }
        }

        var results = new List<CorrelationResult>(devices.Count);
        for (var i = 0; i < devices.Count; i++)
        {
            if (keys[i] is null) traces[i].Add("unmapped");
            results.Add(new CorrelationResult(devices[i], keys[i], rules[i], traces[i]));
        }
        return results;
    }
    public static CorrelationResult CorrelateOne(IReadOnlyList<GpuIdentity> catalog, BackendDevice d) => Correlate(catalog, [d])[0];

    private const int RuleCount = 5;
    private static readonly string[] RuleNames = ["", "LUID", "PCI bus/device/function", "vendor+device+subsystem+VRAM", "vendor+name+VRAM", "unique vendor ordinal"];

    private static Func<GpuIdentity, bool>? Rule(int rule, IReadOnlyList<GpuIdentity> catalog, IReadOnlyList<BackendDevice> devices, BackendDevice d) => rule switch
    {
        1 => d.Luid is { } l ? c => c.Luid == l : null,
        2 => d.Pci is { } p ? c => c.Pci is { } cp && cp.Bus == p.Bus && cp.Device == p.Device && cp.Function == p.Function : null,
        3 => d.DeviceId is { } dev && d.SubSystemId is { } sub && d.DedicatedVramBytes is { } v3
            ? c => c.Vendor == d.Vendor && c.DeviceId == dev && c.SubSystemId == sub && VramClose(c.DedicatedVramBytes, v3)
            : null,
        4 => d.Name is { } n && d.DedicatedVramBytes is { } v4
            ? c => c.Vendor == d.Vendor && NormalizeName(c.Name) == NormalizeName(n) && VramClose(c.DedicatedVramBytes, v4)
            : null,
        // Only when the vendor is unique on BOTH sides: one adapter in the catalog and one device in this backend.
        // A backend can see GPUs Windows does not list (an iGPU with no WDDM adapter), so a unique
        // catalog adapter alone proves nothing. Reported VRAM sizes that disagree also veto the match.
        5 => d.VendorOrdinal is not null
             && catalog.Count(c => c.Vendor == d.Vendor) == 1
             && devices.Count(x => x.Vendor == d.Vendor) == 1
            ? c => c.Vendor == d.Vendor && !VramContradicts(c.DedicatedVramBytes, d.DedicatedVramBytes)
            : null,
        _ => null,
    };

    private static bool VramContradicts(long catalogBytes, long? deviceBytes) =>
        catalogBytes > 0 && deviceBytes is > 0 && !VramClose(catalogBytes, deviceBytes.Value);

    public static bool VramClose(long a, long b)
    {
        if (a <= 0 || b <= 0) return false;
        var max = Math.Max(a, b);
        return Math.Abs(a - b) <= max * 0.05;
    }

    public static string NormalizeName(string name)
    {
        var s = name.ToLowerInvariant();
        foreach (var token in StripTokens) s = s.Replace(token, " ");
        return Whitespace().Replace(s, " ").Trim();
    }

    private static readonly string[] StripTokens = ["(tm)", "(r)", "intel(r)", "nvidia", "amd", "intel", "geforce"];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
