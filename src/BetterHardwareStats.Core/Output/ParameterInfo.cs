namespace BetterHardwareStats.Core.Output;

public enum ParameterType
{
    Bool,
    Int,
    Float,
}

public enum ParameterDomain
{
    Gpu,
    Cpu,
    Ram,
    Disk,
    Network,
}
public sealed record ParameterInfo(
    HardwareParameter Lookup,
    ParameterDomain Domain,
    int? Slot,
    ParameterType Type,
    string DefaultName,
    string DisplayName,
    string Description,
    bool Parity);

public static partial class HardwareParameterTable
{
    private static Dictionary<HardwareParameter, ParameterInfo>? _byLookup;

    public static ParameterInfo Get(HardwareParameter lookup) =>
        (_byLookup ??= All.ToDictionary(p => p.Lookup))[lookup];
}
