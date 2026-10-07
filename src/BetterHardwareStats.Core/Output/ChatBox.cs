using System.Globalization;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Output;
public enum ChatBoxVariable
{
    // Standard hardware variables
    CPUName, CPUUsage, CPUPower, CPUTemp,
    GPUName, GPUUsage, GPUPower, GPUTemp,
    RAMUsage, RAMTotal, RAMUsed, RAMFree,
    VRAMUsage, VRAMTotal, VRAMUsed, VRAMFree,

    // GPU sensors and slots
    GPUKind, GPUCount, GPUClock, GPUFan,
    GPU0Name, GPU0Usage, GPU0VRAMUsed, GPU0Temp,
    GPU1Name, GPU1Usage, GPU1VRAMUsed, GPU1Temp,
    GPU2Name, GPU2Usage, GPU2VRAMUsed, GPU2Temp,
    GPU3Name, GPU3Usage, GPU3VRAMUsed, GPU3Temp,

    // CPU sensors
    CPUFrequency,

    // Disk
    DiskActivity, DiskRead, DiskWrite,
    Disk0Letter, Disk0UsedPercent, Disk0FreeGB, Disk0TotalGB,
    Disk1Letter, Disk1UsedPercent, Disk1FreeGB, Disk1TotalGB,
    Disk2Letter, Disk2UsedPercent, Disk2FreeGB, Disk2TotalGB,
    Disk3Letter, Disk3UsedPercent, Disk3FreeGB, Disk3TotalGB,

    // Network
    NetAdapterName, NetDownload, NetUpload, NetDownloadSessionGB, NetUploadSessionGB,
}

public enum ChatBoxState { Default, Extended }

public enum ChatBoxEvent { GPUChanged, GPUOverheat, CPUOverheat }

public enum ChatBoxValueType { String, Int, Float }

public sealed record ChatBoxVariableInfo(ChatBoxVariable Lookup, ChatBoxValueType Type, string DisplayName, ParameterDomain Domain, bool Parity);

public sealed record ChatBoxSettings(TemperatureUnit TemperatureUnit = TemperatureUnit.Celsius, MemoryUnit MemoryUnit = MemoryUnit.GB);
public static class ChatBoxModel
{
    public const string MissingText = "--";
    public const int MissingInt = int.MinValue;
    public const float MissingFloat = float.NaN;

    public static IReadOnlyList<ChatBoxVariableInfo> Variables { get; } = BuildTable();
    public const string DefaultStateFormat = "CPU: {0}% | GPU: {1}%\nRAM: {2}GB/{3}GB";

    public static readonly ChatBoxVariable[] DefaultStateVariables =
        [ChatBoxVariable.CPUUsage, ChatBoxVariable.GPUUsage, ChatBoxVariable.RAMUsed, ChatBoxVariable.RAMTotal];

    public const string ExtendedStateFormat =
        "CPU: {0}% {1}C | GPU: {2}% {3}C\nRAM: {4}/{5}GB | VRAM: {6}/{7}GB\nDisk: {8}% | Net: {9}/{10} Mbps";

    public static readonly ChatBoxVariable[] ExtendedStateVariables =
    [
        ChatBoxVariable.CPUUsage, ChatBoxVariable.CPUTemp, ChatBoxVariable.GPUUsage, ChatBoxVariable.GPUTemp,
        ChatBoxVariable.RAMUsed, ChatBoxVariable.RAMTotal, ChatBoxVariable.VRAMUsed, ChatBoxVariable.VRAMTotal,
        ChatBoxVariable.DiskActivity, ChatBoxVariable.NetDownload, ChatBoxVariable.NetUpload,
    ];

    public static (ChatBoxEvent Event, string DisplayName, string Format, ChatBoxVariable[] Variables)[] Events { get; } =
    [
        (ChatBoxEvent.GPUChanged, "GPU Changed", "GPU: {0}", [ChatBoxVariable.GPUName]),
        (ChatBoxEvent.GPUOverheat, "GPU Overheat", "GPU hot: {0}C", [ChatBoxVariable.GPUTemp]),
        (ChatBoxEvent.CPUOverheat, "CPU Overheat", "CPU hot: {0}C", [ChatBoxVariable.CPUTemp]),
    ];
    public static IReadOnlyList<(ChatBoxVariable Lookup, object Value)> Build(HardwareSnapshot s, ChatBoxSettings cs, OutputSettings o)
    {
        var list = new List<(ChatBoxVariable, object)>(Variables.Count);
        void Str(ChatBoxVariable v, string? x) => list.Add((v, string.IsNullOrWhiteSpace(x) ? MissingText : x));
        void Int(ChatBoxVariable v, double? x) => list.Add((v, x is { } d && double.IsFinite(d) ? (int)Math.Round(d, MidpointRounding.ToEven) : MissingInt));
        void Flt(ChatBoxVariable v, double? x) => list.Add((v, x is { } d && double.IsFinite(d) ? (float)d : MissingFloat));
        double? Temp(double? c) => cs.TemperatureUnit == TemperatureUnit.Fahrenheit ? Normalization.CelsiusToFahrenheit(c) : c;
        double? Mem(double? bytes) => cs.MemoryUnit == MemoryUnit.MB ? Normalization.MiB(bytes) : Normalization.GiB(bytes);

        if (o.EnableCpu)
        {
            var c = s.Cpu;
            Str(ChatBoxVariable.CPUName, c.Name);
            Int(ChatBoxVariable.CPUUsage, c.UsagePercent?.Value);
            Int(ChatBoxVariable.CPUPower, c.PowerWatts?.Value);
            Int(ChatBoxVariable.CPUTemp, Temp(c.TemperatureC?.Value));
            Int(ChatBoxVariable.CPUFrequency, c.FrequencyMhz?.Value);
        }

        if (o.EnableGpu)
        {
            var g = s.Gpu.Selected;
            Str(ChatBoxVariable.GPUName, g?.Identity.Name);
            Int(ChatBoxVariable.GPUUsage, g?.UtilizationPercent?.Value);
            Int(ChatBoxVariable.GPUPower, g?.PowerWatts?.Value);
            Int(ChatBoxVariable.GPUTemp, Temp(g?.TemperatureC?.Value));
            Flt(ChatBoxVariable.VRAMUsage, g?.VramFraction * 100);
            double? used = g?.VramUsedBytes?.Value, total = g?.VramTotalBytes?.Value;
            Flt(ChatBoxVariable.VRAMTotal, Mem(total));
            Flt(ChatBoxVariable.VRAMUsed, Mem(used));
            Flt(ChatBoxVariable.VRAMFree, used is { } u && total is { } t ? Mem(Math.Max(0, t - u)) : null);
            Str(ChatBoxVariable.GPUKind, g?.Identity.Kind.ToString());
            Int(ChatBoxVariable.GPUCount, s.Gpu.Adapters.Count);
            Int(ChatBoxVariable.GPUClock, g?.CoreClockMhz?.Value);
            Int(ChatBoxVariable.GPUFan, g?.FanPercent?.Value);
            for (var n = 0; n < 4; n++)
            {
                var sg = s.Gpu.Slot(n);
                Str(ChatBoxVariable.GPU0Name + n * 4, sg?.Identity.Name);
                Int(ChatBoxVariable.GPU0Usage + n * 4, sg?.UtilizationPercent?.Value);
                Flt(ChatBoxVariable.GPU0VRAMUsed + n * 4, Mem(sg?.VramUsedBytes?.Value));
                Int(ChatBoxVariable.GPU0Temp + n * 4, Temp(sg?.TemperatureC?.Value));
            }
        }

        if (o.EnableRam)
        {
            var r = s.Ram;
            Flt(ChatBoxVariable.RAMUsage, r.UsageFraction * 100);
            Flt(ChatBoxVariable.RAMTotal, Mem(r.TotalBytes?.Value));
            Flt(ChatBoxVariable.RAMUsed, Mem(r.UsedBytes));
            Flt(ChatBoxVariable.RAMFree, Mem(r.FreeBytes));
        }

        if (o.EnableDisk)
        {
            var d = s.Disk;
            Int(ChatBoxVariable.DiskActivity, d.ActivityPercent);
            Flt(ChatBoxVariable.DiskRead, Normalization.MBps(d.ReadBytesPerSec));
            Flt(ChatBoxVariable.DiskWrite, Normalization.MBps(d.WriteBytesPerSec));
            for (var n = 0; n < 4; n++)
            {
                var v = n < d.Slots.Count ? d.Slots[n] : null;
                Str(ChatBoxVariable.Disk0Letter + n * 4, v?.Letter);
                Int(ChatBoxVariable.Disk0UsedPercent + n * 4, v?.UsedFraction * 100);
                Flt(ChatBoxVariable.Disk0FreeGB + n * 4, Normalization.GiB(v?.FreeBytes));
                Flt(ChatBoxVariable.Disk0TotalGB + n * 4, Normalization.GiB(v?.TotalBytes));
            }
        }

        if (o.EnableNetwork)
        {
            var n = s.Network;
            Str(ChatBoxVariable.NetAdapterName, n.AdapterName);
            Flt(ChatBoxVariable.NetDownload, Normalization.Mbps(n.DownloadBytesPerSec));
            Flt(ChatBoxVariable.NetUpload, Normalization.Mbps(n.UploadBytesPerSec));
            Flt(ChatBoxVariable.NetDownloadSessionGB, Normalization.GiB(n.SessionDownloadBytes));
            Flt(ChatBoxVariable.NetUploadSessionGB, Normalization.GiB(n.SessionUploadBytes));
        }

        return list;
    }
    public static string Render(object value) => value switch
    {
        int i when i == MissingInt => MissingText,
        float f when float.IsNaN(f) => MissingText,
        int i => i.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString("F1", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? MissingText,
    };

    private static List<ChatBoxVariableInfo> BuildTable()
    {
        var t = new List<ChatBoxVariableInfo>();
        void Add(ChatBoxVariable v, ChatBoxValueType type, string name, ParameterDomain d, bool parity = false) => t.Add(new(v, type, name, d, parity));
        const ChatBoxValueType S = ChatBoxValueType.String, I = ChatBoxValueType.Int, F = ChatBoxValueType.Float;
        const ParameterDomain G = ParameterDomain.Gpu, C = ParameterDomain.Cpu, R = ParameterDomain.Ram, D = ParameterDomain.Disk, N = ParameterDomain.Network;

        Add(ChatBoxVariable.CPUName, S, "CPU Name", C, true);
        Add(ChatBoxVariable.CPUUsage, I, "CPU Usage (%)", C, true);
        Add(ChatBoxVariable.CPUPower, I, "CPU Power (W)", C, true);
        Add(ChatBoxVariable.CPUTemp, I, "CPU Temp", C, true);
        Add(ChatBoxVariable.GPUName, S, "GPU Name", G, true);
        Add(ChatBoxVariable.GPUUsage, I, "GPU Usage (%)", G, true);
        Add(ChatBoxVariable.GPUPower, I, "GPU Power (W)", G, true);
        Add(ChatBoxVariable.GPUTemp, I, "GPU Temp", G, true);
        Add(ChatBoxVariable.RAMUsage, F, "RAM Usage (%)", R, true);
        Add(ChatBoxVariable.RAMTotal, F, "RAM Total", R, true);
        Add(ChatBoxVariable.RAMUsed, F, "RAM Used", R, true);
        Add(ChatBoxVariable.RAMFree, F, "RAM Free", R, true);
        Add(ChatBoxVariable.VRAMUsage, F, "VRAM Usage (%)", G, true);
        Add(ChatBoxVariable.VRAMTotal, F, "VRAM Total", G, true);
        Add(ChatBoxVariable.VRAMUsed, F, "VRAM Used", G, true);
        Add(ChatBoxVariable.VRAMFree, F, "VRAM Free", G, true);

        Add(ChatBoxVariable.GPUKind, S, "GPU Kind", G);
        Add(ChatBoxVariable.GPUCount, I, "GPU Count", G);
        Add(ChatBoxVariable.GPUClock, I, "GPU Clock (MHz)", G);
        Add(ChatBoxVariable.GPUFan, I, "GPU Fan (%)", G);
        for (var n = 0; n < 4; n++)
        {
            Add(ChatBoxVariable.GPU0Name + n * 4, S, $"GPU {n} Name", G);
            Add(ChatBoxVariable.GPU0Usage + n * 4, I, $"GPU {n} Usage (%)", G);
            Add(ChatBoxVariable.GPU0VRAMUsed + n * 4, F, $"GPU {n} VRAM Used", G);
            Add(ChatBoxVariable.GPU0Temp + n * 4, I, $"GPU {n} Temp", G);
        }
        Add(ChatBoxVariable.CPUFrequency, I, "CPU Frequency (MHz)", C);
        Add(ChatBoxVariable.DiskActivity, I, "Disk Activity (%)", D);
        Add(ChatBoxVariable.DiskRead, F, "Disk Read (MB/s)", D);
        Add(ChatBoxVariable.DiskWrite, F, "Disk Write (MB/s)", D);
        for (var n = 0; n < 4; n++)
        {
            Add(ChatBoxVariable.Disk0Letter + n * 4, S, $"Disk {n} Letter", D);
            Add(ChatBoxVariable.Disk0UsedPercent + n * 4, I, $"Disk {n} Used (%)", D);
            Add(ChatBoxVariable.Disk0FreeGB + n * 4, F, $"Disk {n} Free (GB)", D);
            Add(ChatBoxVariable.Disk0TotalGB + n * 4, F, $"Disk {n} Total (GB)", D);
        }
        Add(ChatBoxVariable.NetAdapterName, S, "Network Adapter", N);
        Add(ChatBoxVariable.NetDownload, F, "Network Download (Mbps)", N);
        Add(ChatBoxVariable.NetUpload, F, "Network Upload (Mbps)", N);
        Add(ChatBoxVariable.NetDownloadSessionGB, F, "Network Downloaded This Session (GB)", N);
        Add(ChatBoxVariable.NetUploadSessionGB, F, "Network Uploaded This Session (GB)", N);
        return t;
    }
}
public sealed class ChatBoxEventTracker
{
    private readonly long _debounceTicks;
    private string? _lastGpu;
    private bool _gpuSeen;
    private float? _lastGpuTemp, _lastCpuTemp;
    private long _lastGpuFire = long.MinValue, _lastCpuFire = long.MinValue;

    public ChatBoxEventTracker(long debounceTicks) => _debounceTicks = debounceTicks;

    public IReadOnlyList<ChatBoxEvent> Update(HardwareSnapshot s, float thresholdC, OutputSettings o, long nowTicks)
    {
        var events = new List<ChatBoxEvent>();
        if (o.EnableGpu)
        {
            var key = s.Gpu.SelectedStableKey;
            if (_gpuSeen && key is not null && key != _lastGpu) events.Add(ChatBoxEvent.GPUChanged);
            if (key is not null)
            {
                _gpuSeen = true;
                _lastGpu = key;
            }

            var t = s.Gpu.Selected?.TemperatureC?.Value;
            if (Crossed(_lastGpuTemp, t, thresholdC) && (_lastGpuFire == long.MinValue || nowTicks - _lastGpuFire >= _debounceTicks))
            {
                events.Add(ChatBoxEvent.GPUOverheat);
                _lastGpuFire = nowTicks;
            }
            _lastGpuTemp = t;
        }

        if (o.EnableCpu)
        {
            var t = s.Cpu.SensorsAvailable ? s.Cpu.TemperatureC?.Value : null;
            if (Crossed(_lastCpuTemp, t, thresholdC) && (_lastCpuFire == long.MinValue || nowTicks - _lastCpuFire >= _debounceTicks))
            {
                events.Add(ChatBoxEvent.CPUOverheat);
                _lastCpuFire = nowTicks;
            }
            _lastCpuTemp = t;
        }
        return events;
    }

    private static bool Crossed(float? prev, float? now, float threshold) => prev is { } p && now is { } n && p < threshold && n >= threshold;
}
