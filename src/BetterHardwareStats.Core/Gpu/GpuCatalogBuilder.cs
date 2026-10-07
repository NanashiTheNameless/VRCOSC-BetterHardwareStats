using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;

public sealed record CatalogOptions(bool IncludeIntegrated = true, bool IncludeVirtualAdapters = false);
public sealed record AdapterDecision(RawAdapter Adapter, bool Included, string Reason);

public sealed record GpuCatalog(IReadOnlyList<GpuIdentity> Adapters, IReadOnlyList<AdapterDecision> Decisions, IReadOnlyList<string> Warnings);
public static class GpuCatalogBuilder
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    public static GpuVendor MapVendor(ushort vendorId) => vendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };

    public static GpuCatalog Build(IReadOnlyList<RawAdapter> raw, CatalogOptions options)
    {
        var decisions = new List<AdapterDecision>();
        var warnings = new List<string>();
        var kept = new List<RawAdapter>();

        foreach (var a in raw)
        {
            var reason = ExclusionReason(a, options);
            decisions.Add(new AdapterDecision(a, reason is null, reason ?? "included"));
            if (reason is null) kept.Add(a);
        }

        if (kept.Count == 0 && raw.Count > 0)
        {
            // Keep a fallback adapter when filtering would otherwise remove every device.
            var best = raw.Where(a => !a.IsSoftwareFlag && a.VendorId != 0x1414)
                          .DefaultIfEmpty(raw[0])
                          .OrderByDescending(a => a.DedicatedVideoMemory)
                          .First();
            kept.Add(best);
            warnings.Add($"All adapters were filtered out; keeping '{best.Description}' as the best remaining candidate.");
        }

        // Integrated filter applies after classification.
        var identities = kept.Select(Identify).ToList();
        if (!options.IncludeIntegrated)
        {
            var nonIntegrated = identities.Where(i => i.Kind != GpuKind.Integrated).ToList();
            if (nonIntegrated.Count > 0) identities = nonIntegrated;
            else warnings.Add("IncludeIntegrated is off but only integrated adapters exist; keeping them.");
        }

        return new GpuCatalog(DisambiguateKeys(identities, warnings), decisions, warnings);
    }

    public static string? ExclusionReason(RawAdapter a, CatalogOptions options)
    {
        if (a.IsSoftwareFlag) return "DXGI software adapter flag";
        if (a.IsRemoteFlag) return "DXGI remote adapter flag";
        if (a.VendorId == 0x1414) return "Microsoft vendor id (Basic Render/Display Driver)";
        if (a.TypeFlags is { } t)
        {
            if (t.SoftwareDevice) return "D3DKMT SoftwareDevice";
            if (!options.IncludeVirtualAdapters && t.IndirectDisplayDevice) return "D3DKMT IndirectDisplayDevice (virtual display)";
            if (!options.IncludeVirtualAdapters && t.Paravirtualized) return "D3DKMT Paravirtualized (VM adapter)";
            if (!t.RenderSupported) return "D3DKMT lacks RenderSupported (display-only)";
        }
        return null;
    }

    public static (GpuKind Kind, string Reason) Classify(RawAdapter a, GpuKind? backendReportedKind = null)
    {
        if (a.TypeFlags is { HybridIntegrated: true }) return (GpuKind.Integrated, "D3DKMT HybridIntegrated");
        if (a.TypeFlags is { HybridDiscrete: true }) return (GpuKind.Discrete, "D3DKMT HybridDiscrete");
        if (backendReportedKind is GpuKind.Integrated or GpuKind.Discrete) return (backendReportedKind.Value, "vendor backend type");
        var vendor = MapVendor(a.VendorId);
        if (a.DedicatedVideoMemory < 512 * MiB && a.SharedSystemMemory >= 2 * GiB && vendor is GpuVendor.Intel or GpuVendor.Amd)
            return (GpuKind.Integrated, "unified memory heuristic");
        if (a.DedicatedVideoMemory >= GiB) return (GpuKind.Discrete, "dedicated VRAM >= 1 GiB");
        return (GpuKind.Unknown, "no rule matched");
    }

    public static string BaseStableKey(RawAdapter a) =>
        $"{a.VendorId:X4}:{a.DeviceId:X4}:{a.SubSystemId:X8}:{a.Revision:X2}:" + (a.Pci is null ? "nopci" : a.Pci.ToString());

    public static GpuIdentity Identify(RawAdapter a)
    {
        var (kind, reason) = Classify(a);
        return new GpuIdentity(
            BaseStableKey(a), a.Luid, MapVendor(a.VendorId), a.VendorId, a.DeviceId, a.SubSystemId, a.Revision,
            a.Pci, a.Description.Trim(), kind, kind == GpuKind.Integrated, a.DedicatedVideoMemory, a.SharedSystemMemory)
        {
            ClassificationReason = reason,
        };
    }
    public static GpuIdentity WithBackendKind(GpuIdentity id, GpuKind reported)
    {
        if (id.ClassificationReason.StartsWith("D3DKMT", StringComparison.Ordinal) || reported == GpuKind.Unknown || reported == id.Kind)
            return id;
        return id with { Kind = reported, IsUnifiedMemory = reported == GpuKind.Integrated, ClassificationReason = "vendor backend type" };
    }

    private static List<GpuIdentity> DisambiguateKeys(List<GpuIdentity> ids, List<string> warnings)
    {
        foreach (var group in ids.GroupBy(i => i.StableKey).Where(g => g.Count() > 1).ToList())
        {
            warnings.Add($"Adapters share identity '{group.Key}' and have no PCI address; identity may not be stable across reboots.");
            var n = 1;
            foreach (var id in group.OrderBy(i => i.Luid.Value).ToList())
            {
                var idx = ids.IndexOf(id);
                ids[idx] = id with { StableKey = $"{id.StableKey}#{n++}" };
            }
        }
        return ids;
    }
}
