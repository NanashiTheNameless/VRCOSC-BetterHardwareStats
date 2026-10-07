using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Output;
using VRCOSC.App.Nodes;
using VRCOSC.App.SDK.Nodes;

namespace BetterHardwareStats.Module;
public abstract class HardwareNode : ModuleNode<BetterHardwareStatsModule>, IActiveUpdateNode
{
    private readonly GlobalStore<long> _sequence = new();

    public int UpdateOffset => 0;

    protected HardwareSnapshot Snapshot => Module.Latest;

    public Task<bool> OnUpdate(PulseContext c)
    {
        var seq = Snapshot.Sequence;
        if (_sequence.Read(c) == seq) return Task.FromResult(false);
        _sequence.Write(seq, c);
        return Task.FromResult(true);
    }

    protected static float Pct(float? percent) => Normalization.PercentToFraction(percent) ?? 0f;
    protected static int Byte(double? v) => Normalization.ToByteInt(v) ?? 0;
    protected static float GiB(double? bytes) => (float)(Normalization.GiB(bytes) ?? 0);
}

[Node("CPU Info Source", "Nanashi's Better Hardware Stats Module")]
public sealed class CpuInfoSourceNode : HardwareNode
{
    public ValueOutput<float> Usage = new("Usage");
    public ValueOutput<int> Power = new("Power");
    public ValueOutput<int> Temperature = new("Temperature");

    protected override Task Process(PulseContext c)
    {
        var cpu = Snapshot.Cpu;
        Usage.Write(Pct(cpu.UsagePercent?.Value), c);
        Power.Write(Byte(cpu.PowerWatts?.Value), c);
        Temperature.Write(Byte(cpu.TemperatureC?.Value), c);
        return Task.CompletedTask;
    }
}

[Node("GPU Info Source", "Nanashi's Better Hardware Stats Module")]
public sealed class GpuInfoSourceNode : HardwareNode
{
    public ValueOutput<float> Usage = new("Usage");
    public ValueOutput<int> Power = new("Power");
    public ValueOutput<int> Temperature = new("Temperature");

    protected override Task Process(PulseContext c)
    {
        var g = Snapshot.Gpu.Selected;
        Usage.Write(Pct(g?.UtilizationPercent?.Value), c);
        Power.Write(Byte(g?.PowerWatts?.Value), c);
        Temperature.Write(Byte(g?.TemperatureC?.Value), c);
        return Task.CompletedTask;
    }
}

[Node("RAM Info Source", "Nanashi's Better Hardware Stats Module")]
public sealed class RamInfoSourceNode : HardwareNode
{
    public ValueOutput<float> Usage = new("Usage");
    public ValueOutput<float> Total = new("Total");
    public ValueOutput<float> Used = new("Used");
    public ValueOutput<float> Free = new("Free");

    protected override Task Process(PulseContext c)
    {
        var r = Snapshot.Ram;
        Usage.Write(r.UsageFraction ?? 0f, c);
        Total.Write(GiB(r.TotalBytes?.Value), c);
        Used.Write(GiB(r.UsedBytes), c);
        Free.Write(GiB(r.FreeBytes), c);
        return Task.CompletedTask;
    }
}

[Node("VRAM Info Source", "Nanashi's Better Hardware Stats Module")]
public sealed class VramInfoSourceNode : HardwareNode
{
    public ValueOutput<float> Usage = new("Usage");
    public ValueOutput<float> Total = new("Total");
    public ValueOutput<float> Used = new("Used");
    public ValueOutput<float> Free = new("Free");

    protected override Task Process(PulseContext c)
    {
        var g = Snapshot.Gpu.Selected;
        double? used = g?.VramUsedBytes?.Value, total = g?.VramTotalBytes?.Value;
        Usage.Write(g?.VramFraction ?? 0f, c);
        Total.Write(GiB(total), c);
        Used.Write(GiB(used), c);
        Free.Write(used is { } u && total is { } t ? GiB(Math.Max(0, t - u)) : 0f, c);
        return Task.CompletedTask;
    }
}

[Node("GPU Slot Info Source", "Nanashi's Better Hardware Stats Module")]
public sealed class GpuSlotInfoSourceNode : HardwareNode
{
    public ValueInput<int> Slot = new("Slot");
    public ValueOutput<string> Name = new("Name");
    public ValueOutput<float> Usage = new("Usage");
    public ValueOutput<int> Temperature = new("Temperature");
    public ValueOutput<int> Power = new("Power");
    public ValueOutput<float> VramUsage = new("VRAM Usage");
    public ValueOutput<bool> Present = new("Present");

    protected override Task Process(PulseContext c)
    {
        var g = Snapshot.Gpu.Slot(Slot.Read(c));
        Name.Write(g?.Identity.Name ?? "", c);
        Usage.Write(Pct(g?.UtilizationPercent?.Value), c);
        Temperature.Write(Byte(g?.TemperatureC?.Value), c);
        Power.Write(Byte(g?.PowerWatts?.Value), c);
        VramUsage.Write(g?.VramFraction ?? 0f, c);
        Present.Write(g is { Present: true }, c);
        return Task.CompletedTask;
    }
}

[Node("Disk Info Source", "Nanashi's Better Hardware Stats Module")]
public sealed class DiskInfoSourceNode : HardwareNode
{
    public ValueInput<int> VolumeSlot = new("Volume Slot", -1);
    public ValueOutput<float> Activity = new("Activity");
    public ValueOutput<float> ReadMBps = new("Read MB/s");
    public ValueOutput<float> WriteMBps = new("Write MB/s");
    public ValueOutput<float> UsedFraction = new("Used Fraction");
    public ValueOutput<float> FreeGB = new("Free GB");

    protected override Task Process(PulseContext c)
    {
        var d = Snapshot.Disk;
        var slot = VolumeSlot.Read(c);
        var v = slot >= 0 && slot < d.Slots.Count ? d.Slots[slot] : null;
        Activity.Write(Pct(slot < 0 ? d.ActivityPercent : v?.ActivityPercent), c);
        ReadMBps.Write((float)(Normalization.MBps(d.ReadBytesPerSec) ?? 0), c);
        WriteMBps.Write((float)(Normalization.MBps(d.WriteBytesPerSec) ?? 0), c);
        UsedFraction.Write(v?.UsedFraction ?? 0f, c);
        FreeGB.Write(GiB(v?.FreeBytes), c);
        return Task.CompletedTask;
    }
}

[Node("Network Info Source", "Nanashi's Better Hardware Stats Module")]
public sealed class NetworkInfoSourceNode : HardwareNode
{
    public ValueOutput<float> DownloadMbps = new("Download Mbps");
    public ValueOutput<float> UploadMbps = new("Upload Mbps");
    public ValueOutput<bool> Present = new("Present");

    protected override Task Process(PulseContext c)
    {
        var n = Snapshot.Network;
        DownloadMbps.Write((float)(Normalization.Mbps(n.DownloadBytesPerSec) ?? 0), c);
        UploadMbps.Write((float)(Normalization.Mbps(n.UploadBytesPerSec) ?? 0), c);
        Present.Write(n.Present, c);
        return Task.CompletedTask;
    }
}
