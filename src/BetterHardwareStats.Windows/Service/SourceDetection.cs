using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Windows.Adlx;
using BetterHardwareStats.Windows.Igcl;
using BetterHardwareStats.Windows.Native;
using BetterHardwareStats.Windows.Nvml;

namespace BetterHardwareStats.Windows.Service;
public sealed record DetectedSources(
    IReadOnlyCollection<MetricSource> Available,
    IReadOnlyList<RawAdapter> Adapters,
    IReadOnlyDictionary<MetricSource, string> Reasons);
public static class SourceDetection
{
    public static DetectedSources Detect()
    {
        var available = new HashSet<MetricSource>();
        var reasons = new Dictionary<MetricSource, string>();
        IReadOnlyList<RawAdapter> adapters = [];
        try { adapters = DxgiEnumerator.Enumerate(); }
        catch { /* treated as no adapters */ }

        var vendors = adapters.Select(a => GpuCatalogBuilder.MapVendor(a.VendorId)).ToHashSet();

        if (PdhEngine.CanAdd(@"\GPU Engine(*)\Utilization Percentage")) available.Add(MetricSource.WindowsCounters);
        else reasons[MetricSource.WindowsCounters] = "GPU Engine counters unavailable (requires WDDM 2.0+)";

        if (!vendors.Contains(GpuVendor.Nvidia)) reasons[MetricSource.Nvml] = "no NVIDIA GPU";
        else if (NvmlBackend.FindLibrary() is null) reasons[MetricSource.Nvml] = "nvml.dll not found";
        else available.Add(MetricSource.Nvml);

        if (!vendors.Contains(GpuVendor.Amd)) reasons[MetricSource.Adlx] = "no AMD GPU";
        else if (!AdlxBackend.LibraryPresent()) reasons[MetricSource.Adlx] = "amdadlx64.dll not found";
        else available.Add(MetricSource.Adlx);

        if (!vendors.Contains(GpuVendor.Intel)) reasons[MetricSource.Igcl] = "no Intel GPU";
        else if (!IgclBackend.LibraryPresent()) reasons[MetricSource.Igcl] = "Intel ControlLib.dll not found (requires supported Intel graphics driver and 64-bit process)";
        else available.Add(MetricSource.Igcl);

        if (!(vendors.Contains(GpuVendor.Nvidia) || vendors.Contains(GpuVendor.Amd)))
            reasons[MetricSource.LibreHardwareMonitor] = "no NVIDIA or AMD GPU (LHM reads Intel GPUs only with the CPU driver)";
        else if (!LhmAssemblyPresent()) reasons[MetricSource.LibreHardwareMonitor] = "LibreHardwareMonitorLib not loadable";
        else available.Add(MetricSource.LibreHardwareMonitor);

        return new DetectedSources(available, adapters, reasons);
    }

    private static bool LhmAssemblyPresent() => Lhm.BundledLhm.IsPresent();

}
public sealed record HardwareServiceOptions
{
    public int PollIntervalMs { get; init; } = 500;
    public bool EnableGpu { get; init; } = true;
    public bool EnableCpu { get; init; } = true;
    public bool EnableRam { get; init; } = true;
    public bool EnableDisk { get; init; } = true;
    public bool EnableNetwork { get; init; } = true;

    public SelectionOptions Selection { get; init; } = new();
    public bool IncludeVirtualAdapters { get; init; }
    public SourcePreferences Sources { get; init; } = SourcePreferences.Auto;
    public float UtilizationSmoothing { get; init; }
    public bool EnableNvml { get; init; } = true;
    public bool EnableAdlx { get; init; } = true;
    public bool EnableIgcl { get; init; } = true;
    public bool EnableLibreHardwareMonitor { get; init; } = true;
    public bool PollInactiveGpus { get; init; }

    public int SelectedCpu { get; init; }
    public CpuSensorMode CpuSensorMode { get; init; } = CpuSensorMode.Off;

    public string? HwInfoCpuTemperature { get; init; }
    public string? HwInfoCpuPower { get; init; }

    public string? DiskVolumes { get; init; }
    public IReadOnlyList<string?>? DiskSlotChoices { get; init; }
    public bool IncludeRemovableDrives { get; init; }

    public Core.Network.NetworkOptions Network { get; init; } = new();
}
