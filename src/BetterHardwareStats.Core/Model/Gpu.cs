namespace BetterHardwareStats.Core.Model;
public readonly record struct Luid(long Value)
{
    public static Luid FromParts(int highPart, uint lowPart) => new(((long)highPart << 32) | lowPart);

    public int HighPart => (int)(Value >> 32);
    public uint LowPart => (uint)(Value & 0xFFFFFFFF);

    public override string ToString() => $"0x{HighPart:X8}_0x{LowPart:X8}";
}

public sealed record PciAddress(uint Bus, uint Device, uint Function)
{
    public override string ToString() => $"{Bus}.{Device}.{Function}";
}

public enum GpuKind
{
    Unknown,
    Integrated,
    Discrete,
}

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Other,
}

public enum EngineKind
{
    Graphics3D,
    Compute,
    Copy,
    VideoDecode,
    VideoEncode,
    VideoProcessing,
    Other,
    VideoCodec,
}
public sealed record AdapterTypeFlags(
    bool RenderSupported,
    bool DisplaySupported,
    bool SoftwareDevice,
    bool HybridDiscrete,
    bool HybridIntegrated,
    bool IndirectDisplayDevice,
    bool Paravirtualized);
public sealed record RawAdapter(
    string Description,
    ushort VendorId,
    ushort DeviceId,
    uint SubSystemId,
    byte Revision,
    long DedicatedVideoMemory,
    long DedicatedSystemMemory,
    long SharedSystemMemory,
    Luid Luid,
    bool IsSoftwareFlag,
    bool IsRemoteFlag,
    PciAddress? Pci,
    AdapterTypeFlags? TypeFlags);

public sealed record GpuIdentity(
    string StableKey,
    Luid Luid,
    GpuVendor Vendor,
    ushort VendorId,
    ushort DeviceId,
    uint SubSystemId,
    byte Revision,
    PciAddress? Pci,
    string Name,
    GpuKind Kind,
    bool IsUnifiedMemory,
    long DedicatedVramBytes,
    long SharedSystemMemoryBytes)
{
    public string ClassificationReason { get; init; } = "";
}
public sealed record PartialGpuMetrics
{
    public float? UtilizationPercent { get; init; }
    public IReadOnlyDictionary<EngineKind, float>? EnginePercent { get; init; }
    public float? VideoEncodePercent { get; init; }
    public float? VideoDecodePercent { get; init; }
    public long? VramUsedBytes { get; init; }
    public long? VramTotalBytes { get; init; }
    public long? SharedUsedBytes { get; init; }
    public float? TemperatureC { get; init; }
    public float? HotspotC { get; init; }
    public float? MemoryTemperatureC { get; init; }
    public float? PowerWatts { get; init; }
    public float? PowerLimitWatts { get; init; }
    public float? CoreClockMhz { get; init; }
    public float? MemoryClockMhz { get; init; }
    public float? FanPercent { get; init; }
    public float? FanRpm { get; init; }
    public float? VRChatUtilizationPercent { get; init; }
}

public sealed record BackendDevice(
    string BackendKey,
    GpuVendor Vendor,
    string? Name,
    PciAddress? Pci,
    ushort? DeviceId,
    uint? SubSystemId,
    long? DedicatedVramBytes,
    Luid? Luid,
    int? VendorOrdinal,
    GpuKind? ReportedKind = null);

public sealed record BackendSample(long TimestampTicks, IReadOnlyDictionary<string, PartialGpuMetrics> ByBackendKey)
{
    public static BackendSample Empty(long ticks) => new(ticks, new Dictionary<string, PartialGpuMetrics>());
}

public sealed record GpuSnapshot(
    GpuIdentity Identity,
    MetricValue<float>? UtilizationPercent,
    IReadOnlyDictionary<EngineKind, float> EnginePercent,
    MetricValue<float>? VideoEncodePercent,
    MetricValue<float>? VideoDecodePercent,
    MetricValue<long>? VramUsedBytes,
    MetricValue<long>? VramTotalBytes,
    MetricValue<long>? SharedUsedBytes,
    MetricValue<float>? TemperatureC,
    MetricValue<float>? HotspotC,
    MetricValue<float>? MemoryTemperatureC,
    MetricValue<float>? PowerWatts,
    MetricValue<float>? PowerLimitWatts,
    MetricValue<float>? CoreClockMhz,
    MetricValue<float>? MemoryClockMhz,
    MetricValue<float>? FanPercent,
    MetricValue<float>? FanRpm,
    MetricValue<float>? VRChatUtilizationPercent)
{
    public bool Present { get; init; } = true;

    public float? VramFraction =>
        VramUsedBytes is { } u && VramTotalBytes is { } t && t.Value > 0 ? Math.Clamp((float)u.Value / t.Value, 0f, 1f) : null;
}

public sealed record GpuSnapshotSet(
    long Sequence,
    DateTime TimestampUtc,
    IReadOnlyList<GpuSnapshot> Adapters,
    string? SelectedStableKey,
    IReadOnlyList<string?> SlotStableKeys,
    IReadOnlyList<BackendStatus> Backends)
{
    public static GpuSnapshotSet Empty { get; } = new(0, DateTime.MinValue, [], null, [null, null, null, null], []);

    public GpuSnapshot? Selected => SelectedStableKey is null ? null : Find(SelectedStableKey);

    public GpuSnapshot? Slot(int index) =>
        index >= 0 && index < SlotStableKeys.Count && SlotStableKeys[index] is { } key ? Find(key) : null;

    public int SlotOf(string? stableKey)
    {
        if (stableKey is null) return -1;
        for (var i = 0; i < SlotStableKeys.Count; i++)
            if (SlotStableKeys[i] == stableKey) return i;
        return -1;
    }

    public GpuSnapshot? Find(string stableKey)
    {
        foreach (var a in Adapters)
            if (a.Identity.StableKey == stableKey) return a;
        return null;
    }
}
