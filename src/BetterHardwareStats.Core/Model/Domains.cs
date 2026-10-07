namespace BetterHardwareStats.Core.Model;

public sealed record CpuSnapshot(
    string? Name,
    int SelectedIndex,
    int PackageCount,
    MetricValue<float>? UsagePercent,
    MetricValue<float>? FrequencyMhz,
    MetricValue<float>? TemperatureC,
    MetricValue<float>? PowerWatts,
    bool SensorsAvailable,
    string? SensorDetail)
{
    public static CpuSnapshot Empty { get; } = new(null, 0, 0, null, null, null, null, false, null);
}

public sealed record RamSnapshot(MetricValue<long>? TotalBytes, MetricValue<long>? AvailableBytes)
{
    public static RamSnapshot Empty { get; } = new(null, null);

    public long? UsedBytes => TotalBytes is { } t && AvailableBytes is { } a ? Math.Max(0, t.Value - a.Value) : null;
    public long? FreeBytes => AvailableBytes?.Value;

    public float? UsageFraction =>
        TotalBytes is { Value: > 0 } t && UsedBytes is { } u ? Math.Clamp((float)u / t.Value, 0f, 1f) : null;
}

public sealed record DiskVolumeSnapshot(
    string Letter,
    bool Present,
    long? TotalBytes,
    long? FreeBytes,
    float? ActivityPercent)
{
    public float? UsedFraction =>
        TotalBytes is > 0 && FreeBytes is { } f ? Math.Clamp((float)(TotalBytes.Value - f) / TotalBytes.Value, 0f, 1f) : null;
}

public sealed record DiskSnapshotSet(
    bool Present,
    float? ActivityPercent,
    double? ReadBytesPerSec,
    double? WriteBytesPerSec,
    IReadOnlyList<DiskVolumeSnapshot?> Slots,
    long TimestampTicks)
{
    public static DiskSnapshotSet Empty { get; } = new(false, null, null, null, [null, null, null, null], 0);
}

public sealed record NetworkSnapshot(
    bool Present,
    string? AdapterName,
    double? DownloadBytesPerSec,
    double? UploadBytesPerSec,
    double DownloadCapacityBitsPerSec,
    double UploadCapacityBitsPerSec,
    long SessionDownloadBytes,
    long SessionUploadBytes,
    long TimestampTicks)
{
    public static NetworkSnapshot Empty { get; } = new(false, null, null, null, 1e9, 1e9, 0, 0, 0);
}
public sealed record HardwareSnapshot(
    long Sequence,
    long TimestampTicks,
    GpuSnapshotSet Gpu,
    CpuSnapshot Cpu,
    RamSnapshot Ram,
    DiskSnapshotSet Disk,
    NetworkSnapshot Network)
{
    public static HardwareSnapshot Empty { get; } =
        new(0, 0, GpuSnapshotSet.Empty, CpuSnapshot.Empty, RamSnapshot.Empty, DiskSnapshotSet.Empty, NetworkSnapshot.Empty);
}
