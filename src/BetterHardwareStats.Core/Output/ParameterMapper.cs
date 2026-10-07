using BetterHardwareStats.Core.Model;
using P = BetterHardwareStats.Core.Output.HardwareParameter;

namespace BetterHardwareStats.Core.Output;
public sealed record OutputSettings
{
    public bool EnableGpu { get; init; } = true;
    public bool EnableCpu { get; init; } = true;
    public bool EnableRam { get; init; } = true;
    public bool EnableDisk { get; init; } = true;
    public bool EnableNetwork { get; init; } = true;

    public float MaxGpuTempC { get; init; } = 100;
    public float MaxGpuPowerW { get; init; } = 450;
    public float MaxGpuClockMhz { get; init; } = 3000;
    public float MaxCpuTempC { get; init; } = 100;
    public float MaxCpuPowerW { get; init; } = 250;
    public float MaxCpuClockMhz { get; init; } = 5500;
    public float MaxDiskMBps { get; init; } = 3500;
}
public static class ParameterMapper
{
    public const int NoSlot = 255;

    public static void Apply(HardwareSnapshot s, OutputSettings o, ParameterSender<P> sender)
    {
        if (o.EnableGpu) ApplyGpu(s.Gpu, o, sender);
        if (o.EnableCpu) ApplyCpu(s.Cpu, o, sender);
        if (o.EnableRam) ApplyRam(s.Ram, sender);
        if (o.EnableDisk) ApplyDisk(s.Disk, o, sender);
        if (o.EnableNetwork) ApplyNetwork(s.Network, sender);
    }

    private static void ApplyGpu(GpuSnapshotSet set, OutputSettings o, ParameterSender<P> x)
    {
        var g = set.Selected;
        var util = g?.UtilizationPercent?.Value;
        x.Set(P.GpuPresent, g is { Present: true });
        x.Set(P.GpuUsage, Normalization.PercentToFraction(util));
        x.Set(P.GpuUsagePercent, Normalization.Percent(util));
        x.Set(P.GpuPower, Normalization.ToByteInt(g?.PowerWatts?.Value));
        x.Set(P.GpuTemp, Normalization.ToByteInt(g?.TemperatureC?.Value));
        x.Set(P.GpuTempNormalized, Normalization.Scaled(g?.TemperatureC?.Value, o.MaxGpuTempC));
        x.Set(P.GpuPowerNormalized, Normalization.Scaled(g?.PowerWatts?.Value, o.MaxGpuPowerW));
        x.Set(P.GpuCoreClock, Normalization.Scaled(g?.CoreClockMhz?.Value, o.MaxGpuClockMhz));
        x.Set(P.GpuMemoryClock, Normalization.Scaled(g?.MemoryClockMhz?.Value, o.MaxGpuClockMhz));
        x.Set(P.GpuFan, Normalization.PercentToFraction(g?.FanPercent?.Value));
        x.Set(P.GpuIsIntegrated, g?.Identity.Kind == GpuKind.Integrated);
        x.Set(P.GpuIsDiscrete, g?.Identity.Kind == GpuKind.Discrete);
        x.Set(P.GpuIndex, g is null ? null : set.SlotOf(g.Identity.StableKey) is var i and >= 0 ? i : NoSlot);

        double? used = g?.VramUsedBytes?.Value, total = g?.VramTotalBytes?.Value;
        var frac = g?.VramFraction;
        x.Set(P.VramUsage, frac);
        x.Set(P.VramTotal, Normalization.GiBInt(total));
        x.Set(P.VramUsed, Normalization.GiBInt(used));
        x.Set(P.VramFree, used is { } u && total is { } t ? Normalization.GiBInt(Math.Max(0, t - u)) : null);
        x.Set(P.VramUsagePercent, Normalization.PercentFromFraction(frac));

        for (var n = 0; n < 4; n++)
        {
            var sg = set.Slot(n);
            var su = sg?.UtilizationPercent?.Value;
            x.Set(Slot(P.Gpu0Present, n, 8), sg is { Present: true });
            x.Set(Slot(P.Gpu0Usage, n, 8), Normalization.PercentToFraction(su));
            x.Set(Slot(P.Gpu0UsagePercent, n, 8), Normalization.Percent(su));
            x.Set(Slot(P.Gpu0Temp, n, 8), Normalization.ToByteInt(sg?.TemperatureC?.Value));
            x.Set(Slot(P.Gpu0Power, n, 8), Normalization.ToByteInt(sg?.PowerWatts?.Value));
            x.Set(Slot(P.Gpu0IsIntegrated, n, 8), sg?.Identity.Kind == GpuKind.Integrated);
            x.Set(Slot(P.Gpu0VramUsage, n, 8), sg?.VramFraction);
            x.Set(Slot(P.Gpu0VramPercent, n, 8), Normalization.PercentFromFraction(sg?.VramFraction));
        }
    }

    private static void ApplyCpu(CpuSnapshot c, OutputSettings o, ParameterSender<P> x)
    {
        var u = c.UsagePercent?.Value;
        x.Set(P.CpuPresent, u is not null);
        x.Set(P.CpuSensorsPresent, c.SensorsAvailable);
        x.Set(P.CpuUsage, Normalization.PercentToFraction(u));
        x.Set(P.CpuUsagePercent, Normalization.Percent(u));
        x.Set(P.CpuPower, Normalization.ToByteInt(c.PowerWatts?.Value));
        x.Set(P.CpuTemp, Normalization.ToByteInt(c.TemperatureC?.Value));
        x.Set(P.CpuTempNormalized, Normalization.Scaled(c.TemperatureC?.Value, o.MaxCpuTempC));
        x.Set(P.CpuPowerNormalized, Normalization.Scaled(c.PowerWatts?.Value, o.MaxCpuPowerW));
        x.Set(P.CpuFrequency, Normalization.Scaled(c.FrequencyMhz?.Value, o.MaxCpuClockMhz));
    }

    private static void ApplyRam(RamSnapshot r, ParameterSender<P> x)
    {
        x.Set(P.RamUsage, r.UsageFraction);
        x.Set(P.RamTotal, Normalization.GiBInt(r.TotalBytes?.Value));
        x.Set(P.RamUsed, Normalization.GiBInt(r.UsedBytes));
        x.Set(P.RamFree, Normalization.GiBInt(r.FreeBytes));
        x.Set(P.RamUsagePercent, Normalization.PercentFromFraction(r.UsageFraction));
    }

    private static void ApplyDisk(DiskSnapshotSet d, OutputSettings o, ParameterSender<P> x)
    {
        var maxBps = o.MaxDiskMBps * 1_000_000d;
        x.Set(P.DiskPresent, d.Present);
        x.Set(P.DiskActivity, Normalization.PercentToFraction(d.ActivityPercent));
        x.Set(P.DiskActivityPercent, Normalization.Percent(d.ActivityPercent));
        x.Set(P.DiskRead, Normalization.Scaled(d.ReadBytesPerSec, maxBps));
        x.Set(P.DiskWrite, Normalization.Scaled(d.WriteBytesPerSec, maxBps));
        x.Set(P.DiskReadMBps, Normalization.ToByteInt(Normalization.MBps(d.ReadBytesPerSec)));
        x.Set(P.DiskWriteMBps, Normalization.ToByteInt(Normalization.MBps(d.WriteBytesPerSec)));

        for (var n = 0; n < 4; n++)
        {
            var v = n < d.Slots.Count ? d.Slots[n] : null;
            x.Set(Slot(P.Disk0Present, n, 5), v is { Present: true });
            x.Set(Slot(P.Disk0UsedFraction, n, 5), v?.UsedFraction);
            x.Set(Slot(P.Disk0UsedPercent, n, 5), Normalization.PercentFromFraction(v?.UsedFraction));
            x.Set(Slot(P.Disk0Activity, n, 5), Normalization.PercentToFraction(v?.ActivityPercent));
            x.Set(Slot(P.Disk0ActivityPercent, n, 5), Normalization.Percent(v?.ActivityPercent));
        }
    }

    private static void ApplyNetwork(NetworkSnapshot n, ParameterSender<P> x)
    {
        double? down = n.DownloadBytesPerSec * 8, up = n.UploadBytesPerSec * 8;
        x.Set(P.NetworkPresent, n.Present);
        x.Set(P.NetworkDownload, Normalization.Scaled(down, n.DownloadCapacityBitsPerSec));
        x.Set(P.NetworkUpload, Normalization.Scaled(up, n.UploadCapacityBitsPerSec));
        x.Set(P.NetworkDownloadMbps, Normalization.ToByteInt(Normalization.Mbps(n.DownloadBytesPerSec)));
        x.Set(P.NetworkUploadMbps, Normalization.ToByteInt(Normalization.Mbps(n.UploadBytesPerSec)));
    }
    private static P Slot(P slot0, int n, int blockSize) => (P)((int)slot0 + n * blockSize);
}
