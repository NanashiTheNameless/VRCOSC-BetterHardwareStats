using System.Diagnostics;
using System.IO;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Network;
using BetterHardwareStats.Core.Sensors;
using BetterHardwareStats.Windows.HwInfo;
using BetterHardwareStats.Core.Output;
using BetterHardwareStats.Windows.Native;
using BetterHardwareStats.Windows.Service;
using VRCOSC.App.SDK.Modules;
using VRCOSC.App.SDK.Modules.Attributes.Settings;
using VRCOSC.App.SDK.Parameters;
using VRCOSC.App.SDK.VRChat;
using ParameterType = BetterHardwareStats.Core.Output.ParameterType;

namespace BetterHardwareStats.Module;
[ModuleTitle("Nanashi's Better Hardware Stats Module")]
[ModuleDescription(
    "An opinionated replacement for VRCOSC's built-in Hardware Stats module, with added support for multiple GPUs, disk and network monitoring, and more sensor options.",
    "Disable the built-in Hardware Stats module before enabling this one.")]
[ModuleType(ModuleType.Generic)]
[ModuleInfo("https://github.com/NanashiTheNameless/VRCOSC-BetterHardwareStats")]
[ModuleSettingsWindow(typeof(HardwareSettingsWindow))]
public sealed class BetterHardwareStatsModule : VRCOSC.App.SDK.Modules.Module
{
    private readonly Dictionary<Setting, RefreshableDropdownSetting> _refreshable = new();
    private System.Windows.Threading.DispatcherTimer? _choiceRefreshTimer;
    private bool _refreshingChoices;
    private bool _refreshAgain;
    private const string SameAsMain = "Same";
    private HardwareService? _service;
    private CancellationTokenSource? _diagnosticsCancellation;
    private Task? _diagnosticsTask;
    private HardwareSnapshot? _restartSnapshot;
    private ParameterSender<HardwareParameter>? _sender;
    private ChatBoxEventTracker? _events;
    private OutputSettings _output = new();
    private ChatBoxSettings _chatBox = new();
    private float _overheatC = 85;
    private DetectedSources _detected = new([], [], new Dictionary<MetricSource, string>());
    private IReadOnlyList<HwInfoReading> _hwInfoReadings = [];
    private IReadOnlyList<GpuChoice> _gpuChoices = GpuChoices.Automatic;
    private List<SourceOption> _diskChoices = [new("Auto (next available drive)", "Auto"), new("None", "None")];
    private static readonly Setting[] DiskSlotSettings = [Setting.DiskSlot0, Setting.DiskSlot1, Setting.DiskSlot2, Setting.DiskSlot3];
    private List<SourceOption> _cpuChoices = [new("CPU package 0", "0")];
    private List<SourceOption> _networkChoices = [new("Auto (default route)", "AutoDefaultRoute"), new("Auto (busiest adapter)", "AutoBusiest"), new("Aggregate (physical adapters)", "Aggregate")];
    private readonly Dictionary<ChatBoxVariable, ChatBoxValueType> _variableTypes = new();
    [ModulePersistent("gpu_slots")]
    public string SlotState { get; set; } = "";
    internal HardwareSnapshot Latest => _service?.Latest ?? HardwareSnapshot.Empty;

    internal bool ShowCpuSensors => _output.EnableCpu;

    internal event Action? SettingsVisibilityChanged;

    internal bool IsSettingVisible(ModuleSetting setting)
    {
        bool Is(Setting lookup) => ReferenceEquals(setting, GetSetting(lookup));
        if (Is(Setting.EnableNvml) || Is(Setting.PollInactiveGpus)) return _detected.Available.Contains(MetricSource.Nvml);
        if (Is(Setting.EnableAdlx)) return _detected.Available.Contains(MetricSource.Adlx);
        if (Is(Setting.EnableIgcl)) return _detected.Available.Contains(MetricSource.Igcl);
        if (Is(Setting.EnableLibreHardwareMonitor)) return _detected.Available.Contains(MetricSource.LibreHardwareMonitor);
        if (Is(Setting.HwInfoCpuTemperature) || Is(Setting.HwInfoCpuPower))
            return GetSettingValue<CpuSensorMode>(Setting.CpuSensorMode) == CpuSensorMode.HwInfo;
        return true;
    }

    internal bool IsGroupVisible(string title) => title switch
    {
        "GPU" or "GPU data sources" => GetSettingValue<bool>(Setting.EnableGpu),
        "CPU" => GetSettingValue<bool>(Setting.EnableCpu),
        "Disk" => GetSettingValue<bool>(Setting.EnableDisk),
        "Network" => GetSettingValue<bool>(Setting.EnableNetwork),
        _ => true,
    };

    private void UpdateSettingsVisibility()
    {
        var hwInfo = GetSettingValue<bool>(Setting.EnableCpu) && GetSettingValue<CpuSensorMode>(Setting.CpuSensorMode) == CpuSensorMode.HwInfo;
        GetSetting(Setting.HwInfoCpuTemperature).IsEnabled.Value = hwInfo;
        GetSetting(Setting.HwInfoCpuPower).IsEnabled.Value = hwInfo;
        SettingsVisibilityChanged?.Invoke();
    }

    private void ReconfigureFeatures()
    {
        UpdateSettingsVisibility();
        if (_service is not { } service) return;
        var options = ReadOptions();
        if (service.TryUpdateOptions(options))
        {
            if (_sender is { } sender)
            {
                sender.Options = ReadSenderOptions();
                sender.RequestResendAll();
            }
            if (!GetSettingValue<bool>(Setting.WriteDiagnosticsOnStart)) StopDiagnostics();
            return;
        }
        StopDiagnostics();
        _service = null;
        service.Dispose();
        SlotState = SlotStateCodec.Encode(service.SlotReservations);
        _restartSnapshot = service.Latest;
        try { OnModuleStart().GetAwaiter().GetResult(); }
        finally { _restartSnapshot = null; }
    }

    // Settings

    protected override void OnPreLoad()
    {
        _refreshable.Clear();
        CreateSettings();

        foreach (var p in HardwareParameterTable.All)
        {
            switch (p.Type)
            {
                case ParameterType.Bool: RegisterParameter<bool>(p.Lookup, p.DefaultName, ParameterMode.Write, p.DisplayName, p.Description); break;
                case ParameterType.Int: RegisterParameter<int>(p.Lookup, p.DefaultName, ParameterMode.Write, p.DisplayName, p.Description); break;
                default: RegisterParameter<float>(p.Lookup, p.DefaultName, ParameterMode.Write, p.DisplayName, p.Description); break;
            }
        }
    }

    private void DetectChoices(bool includeVirtual, bool includeRemovable, bool gpu, bool cpu, bool disk, bool network, bool hwInfo)
    {
        if (cpu && hwInfo) try
        {
            var snapshot = HwInfoReader.Read();
            _hwInfoReadings = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - snapshot.PollUnixSeconds) <= 15 ? snapshot.Readings : [];
        }
        catch { _hwInfoReadings = []; }
        _diskChoices = [new("Auto (next available drive)", "Auto"), new("None", "None")];
        _networkChoices = [new("Auto (default route)", "AutoDefaultRoute"), new("Auto (busiest adapter)", "AutoBusiest"), new("Aggregate (physical adapters)", "Aggregate")];
        if (gpu) try { _detected = SourceDetection.Detect(); }
        catch (Exception e) { Log($"Source detection failed: {e.Message}"); }
        if (gpu) try { _gpuChoices = GpuChoices.Build(GpuCatalogBuilder.Build(DxgiEnumerator.Enumerate(), new CatalogOptions(IncludeVirtualAdapters: includeVirtual)).Adapters); }
        catch (Exception e) { Log($"GPU detection for the settings list failed: {e.Message}"); }

        if (cpu) try
        {
            var packages = CpuTopology.ReadPackages();
            if (packages.Count > 0) _cpuChoices = packages.Select(p => new SourceOption($"{p.Name ?? "CPU"} (package {p.Index})", p.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        }
        catch (Exception e) { Log($"CPU settings detection failed: {e.Message}"); }
        if (network) try
        {
            _networkChoices.AddRange(NetworkTable.Read().Where(r => !r.IsLoopback && !r.IsFilter)
                .OrderBy(r => r.Alias, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => new SourceOption($"{r.Alias} ({r.Description}) [{r.Key}]", "adapter:" + r.Key)));
        }
        catch (Exception e) { Log($"Network settings detection failed: {e.Message}"); }
        if (disk) try
        {
            foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed && !(includeRemovable && drive.DriveType == DriveType.Removable)) continue;
                    var title = drive.Name;
                    try { if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)) title += " " + drive.VolumeLabel; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    if (drive.DriveType == DriveType.Removable) title += " (removable)";
                    _diskChoices.Add(new SourceOption(title, char.ToUpperInvariant(drive.Name[0]).ToString()));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) { Log($"Disk settings detection failed: {e.Message}"); }
    }

    private void CreateChoiceDropdown(Setting lookup, string title, string description, List<SourceOption> items,
        SourceOption defaultItem, string titlePath, string valuePath)
    {
        var setting = new RefreshableDropdownSetting(title, description, items, defaultItem.Value);
        _refreshable[lookup] = setting;
        CreateCustomSetting(lookup, setting);
    }

    private List<SourceOption> HwInfoChoices(int type) =>
        [new("None", "None"), .. _hwInfoReadings.Where(r => r.Type == type)
            .Select(r => new SourceOption($"{r.Device}: {r.Name} ({r.Unit}) [{r.Key}]", r.Key))];

    private void ScheduleChoiceRefresh()
    {
        _choiceRefreshTimer?.Stop();
        _choiceRefreshTimer?.Start();
    }

    private async void RefreshChoices(object? sender, EventArgs args)
    {
        _choiceRefreshTimer?.Stop();
        if (_refreshingChoices) { _refreshAgain = true; return; }
        _refreshingChoices = true;
        try
        {
            var includeVirtual = GetSettingValue<bool>(Setting.IncludeVirtualAdapters);
            var includeRemovable = GetSettingValue<bool>(Setting.IncludeRemovableDrives);
            var gpu = GetSettingValue<bool>(Setting.EnableGpu);
            var cpu = GetSettingValue<bool>(Setting.EnableCpu);
            var disk = GetSettingValue<bool>(Setting.EnableDisk);
            var network = GetSettingValue<bool>(Setting.EnableNetwork);
            var hwInfo = GetSettingValue<CpuSensorMode>(Setting.CpuSensorMode) == CpuSensorMode.HwInfo;
            await Task.Run(() => DetectChoices(includeVirtual, includeRemovable, gpu, cpu, disk, network, hwInfo));
            _refreshable[Setting.GpuSelection].ReplaceChoices(_gpuChoices.Select(c => new SourceOption(c.Title, c.Value)));
            _refreshable[Setting.SelectedCPU].ReplaceChoices(_cpuChoices);
            _refreshable[Setting.HwInfoCpuTemperature].ReplaceChoices(HwInfoChoices(1));
            _refreshable[Setting.HwInfoCpuPower].ReplaceChoices(HwInfoChoices(5));
            _refreshable[Setting.NetworkSelection].ReplaceChoices(_networkChoices);
            foreach (var slot in DiskSlotSettings) _refreshable[slot].ReplaceChoices(_diskChoices);
            _refreshable[Setting.GpuDataSource].ReplaceChoices(SourceCatalog.Options(GpuMetricGroup.Temperature, _detected.Available)
                .Select(c => new SourceOption(SourceCatalog.DisplayName(c), c?.ToString() ?? "Auto")));
            foreach (var (lookup, group, _) in GroupSettings)
                _refreshable[lookup].ReplaceChoices(new[] { new SourceOption("Same as GPU data source", SameAsMain) }.Concat(
                    SourceCatalog.Options(group, _detected.Available).Select(c => new SourceOption(SourceCatalog.DisplayName(c), c?.ToString() ?? "Auto"))));
            ReconfigureFeatures();
        }
        catch (Exception error) { Log($"Refreshing settings choices failed: {error.Message}"); }
        finally
        {
            _refreshingChoices = false;
            if (_refreshAgain) { _refreshAgain = false; ScheduleChoiceRefresh(); }
        }
    }

    private void CreateSettings()
    {
        CreateCustomSetting(Setting.Support, new StringModuleSetting("Keep my projects working", "If my projects help you, please consider supporting their upkeep. Support is optional. Thank you!", typeof(SupportSettingView), ""));
        CreateCustomSetting(Setting.PollIntervalMs, new SliderModuleSetting("Sampling interval (ms)", "250-5000 ms. Arrow steps: 250 ms. Default: 500 ms, matching the official module.", typeof(SamplingIntervalSettingView), 500, 250, 5000, 250));
        CreateToggle(Setting.EnableGpu, "Enable GPU", "Monitor GPU values.", true);
        CreateToggle(Setting.EnableCpu, "Enable CPU", "Monitor CPU values.", true);
        CreateToggle(Setting.EnableRam, "Enable RAM", "Monitor memory usage.", true);
        CreateToggle(Setting.EnableDisk, "Enable disks", "Monitor drive activity and space.", true);
        CreateToggle(Setting.EnableNetwork, "Enable network", "Monitor local adapter speeds.", true);
        CreateDropdown(Setting.SendMode, "Send mode", "OnChange sends a value only when it changes. Always sends every value every tick (not recommended).", SendMode.OnChange);
        CreateSlider(Setting.ChangeEpsilon, "Float change threshold", "Smallest change of a float parameter that is sent.", 0.005f, 0.001f, 0.05f, 0.001f);
        CreateSlider(Setting.RefreshSeconds, "Resend every (s)", "Resend all values periodically; 0 disables.", 10, 0, 60);
        CreateDropdown(Setting.OnMissingMetric, "When a value is unavailable", "SendZeroOnce sends 0 once so avatars do not freeze on a stale number. HoldLast keeps the last value.", MissingMetricMode.SendZeroOnce);
        CreateDropdown(Setting.TemperatureUnit, "ChatBox temperature unit", "Parameters always use Celsius.", TemperatureUnit.Celsius);
        CreateDropdown(Setting.MemoryUnit, "ChatBox memory unit", "GB means GiB (1024^3 bytes).", MemoryUnit.GB);
        CreateSlider(Setting.OverheatThresholdC, "Overheat threshold (C)", "Temperature that triggers the GPU and CPU overheat ChatBox events.", 85, 50, 120);
        CreateToggle(Setting.VerboseLogging, "Verbose logging", "Log every published snapshot (for troubleshooting).", false);
        CreateToggle(Setting.WriteDiagnosticsOnStart, "Save local diagnostics on start", "Saves local diagnostics to %APPDATA%\\VRCOSC\\betterhardwarestats\\diagnostics.txt when the module starts.", false);

        CreateChoiceDropdown(Setting.GpuSelection, "Selected GPU",
            "Choose a GPU. Auto follows VRChat. Missing choices temporarily use Auto.",
            _gpuChoices.Select(c => new SourceOption(c.Title, c.Value)).ToList(), new SourceOption(_gpuChoices[0].Title, _gpuChoices[0].Value), nameof(SourceOption.Title), nameof(SourceOption.Value));
        CreateToggle(Setting.IncludeIntegrated, "Include integrated GPUs", "Allow Auto to select integrated GPUs.", true);
        CreateToggle(Setting.IncludeVirtualAdapters, "Include virtual GPUs", "Virtual display drivers and VM adapters.", false);
        CreateSlider(Setting.UtilizationSmoothing, "GPU usage smoothing", "Smooth GPU usage. 0 disables smoothing.", 0f, 0f, 0.9f, 0.05f);
        CreateToggle(Setting.PollInactiveGpus, "Poll idle GPUs at full rate", "Off saves power by polling idle NVIDIA GPUs every 10 seconds.", false);
        CreateSlider(Setting.MaxGpuTempC, "GPU temperature scale (C)", "Temperature that maps to 1.0 in the normalized GPU float parameter.", 100, 50, 150);
        CreateSlider(Setting.MaxGpuPowerW, "GPU power scale (W)", "Power that maps to 1.0 in the normalized GPU float parameter.", 450, 25, 1000, 5);
        CreateSlider(Setting.MaxGpuClockMhz, "GPU clock scale (MHz)", "Clock that maps to 1.0 in the GPU clock float parameters.", 3000, 500, 4000, 50);

        // List supported data sources for the enabled features.
        var mainItems = SourceCatalog.Options(GpuMetricGroup.Temperature, _detected.Available)
            .Select(s => new SourceOption(SourceCatalog.DisplayName(s), s?.ToString() ?? "Auto")).ToList();
        CreateChoiceDropdown(Setting.GpuDataSource, "GPU data source",
            "Auto chooses the best available source.",
            mainItems, mainItems[0], nameof(SourceOption.Title), nameof(SourceOption.Value));
        foreach (var (lookup, group, title) in GroupSettings)
        {
            var items = new List<SourceOption> { new("Same as GPU data source", SameAsMain) };
            items.AddRange(SourceCatalog.Options(group, _detected.Available).Select(s => new SourceOption(SourceCatalog.DisplayName(s), s?.ToString() ?? "Auto")));
            CreateChoiceDropdown(lookup, title, $"Override the GPU data source for {title.ToLowerInvariant()}.", items, items[0], nameof(SourceOption.Title), nameof(SourceOption.Value));
        }
        CreateToggle(Setting.StrictDataSource, "Only use the chosen tool",
            "Disable fallback when the chosen source has no value.", false);
        CreateToggle(Setting.EnableNvml, "Enable NVML", "NVIDIA management library (installed with the NVIDIA driver).", true);
        CreateToggle(Setting.EnableIgcl, "Enable Intel IGCL", "Read sensors through the installed Intel graphics driver.", true);
        CreateToggle(Setting.EnableAdlx, "Enable ADLX", "AMD ADLX (installed with the AMD Adrenalin driver).", true);
        CreateToggle(Setting.EnableLibreHardwareMonitor, "Enable LibreHardwareMonitor (GPU)", "GPU sensors through LibreHardwareMonitor. The GPU-only mode loads no kernel driver.", true);

        CreateChoiceDropdown(Setting.SelectedCPU, "Selected CPU", "Choose a CPU package. Missing choices temporarily use package 0.", _cpuChoices, _cpuChoices[0], nameof(SourceOption.Title), nameof(SourceOption.Value));
        CreateDropdown(Setting.CpuSensorMode, "CPU temperature and power",
            "Off disables sensors. HWiNFO reads shared memory. Other modes use a kernel driver and need admin.",
            CpuSensorMode.Off);
        var hwTemperature = HwInfoChoices(1);
        var hwPower = HwInfoChoices(5);
        CreateChoiceDropdown(Setting.HwInfoCpuTemperature, "HWiNFO CPU temperature sensor",
            "Choose the CPU package temperature. HWiNFO shared memory must be enabled.",
            hwTemperature, hwTemperature[0], nameof(SourceOption.Title), nameof(SourceOption.Value));
        CreateChoiceDropdown(Setting.HwInfoCpuPower, "HWiNFO CPU power sensor",
            "Choose the CPU package power in watts.",
            hwPower, hwPower[0], nameof(SourceOption.Title), nameof(SourceOption.Value));
        CreateSlider(Setting.MaxCpuTempC, "CPU temperature scale (C)", "Temperature that maps to 1.0 in the normalized CPU float parameter.", 100, 50, 150);
        CreateSlider(Setting.MaxCpuPowerW, "CPU power scale (W)", "Power that maps to 1.0 in the normalized CPU float parameter.", 250, 15, 600, 5);
        CreateSlider(Setting.MaxCpuClockMhz, "CPU clock scale (MHz)", "Clock that maps to 1.0 in the CPU frequency parameter.", 5500, 1000, 7000, 100);

        for (var i = 0; i < DiskSlotSettings.Length; i++)
            CreateChoiceDropdown(DiskSlotSettings[i], $"Disk slot {i}",
                "Choose a drive. Auto fills unused drives; None disables this slot.",
                _diskChoices, _diskChoices[0], nameof(SourceOption.Title), nameof(SourceOption.Value));
        CreateToggle(Setting.IncludeRemovableDrives, "Include removable drives", "Allow removable drive monitoring, including explicit slot choices.", false);
        CreateSlider(Setting.MaxDiskMBps, "Disk throughput scale (MB/s)", "Throughput that maps to 1.0 in the disk read and write float parameters.", 3500, 100, 15000, 100);

        CreateChoiceDropdown(Setting.NetworkSelection, "Selected network adapter",
            "Choose an adapter. Missing choices temporarily use the default route.",
            _networkChoices, _networkChoices[0], nameof(SourceOption.Title), nameof(SourceOption.Value));
        CreateSlider(Setting.MaxNetworkMbps, "Network speed scale (Mbps)", "0 uses the adapter's link speed.", 0, 0, 10000, 50);

        CreateGroup("Support", "", Setting.Support);
        CreateGroup("General", "", Setting.PollIntervalMs, Setting.EnableGpu, Setting.EnableCpu, Setting.EnableRam, Setting.EnableDisk, Setting.EnableNetwork);
        CreateGroup("GPU", "", Setting.GpuSelection, Setting.IncludeIntegrated, Setting.IncludeVirtualAdapters,
            Setting.UtilizationSmoothing, Setting.PollInactiveGpus, Setting.MaxGpuTempC, Setting.MaxGpuPowerW, Setting.MaxGpuClockMhz);
        CreateGroup("GPU data sources", "", [Setting.GpuDataSource, .. GroupSettings.Select(g => (Enum)g.Lookup), Setting.StrictDataSource,
            Setting.EnableNvml, Setting.EnableAdlx, Setting.EnableIgcl, Setting.EnableLibreHardwareMonitor]);
        CreateGroup("CPU", "", Setting.SelectedCPU, Setting.CpuSensorMode, Setting.HwInfoCpuTemperature, Setting.HwInfoCpuPower, Setting.MaxCpuTempC, Setting.MaxCpuPowerW, Setting.MaxCpuClockMhz);
        CreateGroup("Disk", "", Setting.DiskSlot0, Setting.DiskSlot1, Setting.DiskSlot2, Setting.DiskSlot3, Setting.IncludeRemovableDrives, Setting.MaxDiskMBps);
        CreateGroup("Network", "", Setting.NetworkSelection, Setting.MaxNetworkMbps);
        CreateGroup("Output", "", Setting.SendMode, Setting.ChangeEpsilon, Setting.RefreshSeconds, Setting.OnMissingMetric, Setting.TemperatureUnit, Setting.MemoryUnit, Setting.OverheatThresholdC);
        CreateGroup("Diagnostics", "", Setting.VerboseLogging, Setting.WriteDiagnosticsOnStart);
    }

    private static readonly (Setting Lookup, GpuMetricGroup Group, string Title)[] GroupSettings =
    [
        (Setting.GpuUtilizationSource, GpuMetricGroup.Utilization, "GPU usage source"),
        (Setting.GpuMemorySource, GpuMetricGroup.Memory, "GPU memory source"),
        (Setting.GpuTemperatureSource, GpuMetricGroup.Temperature, "GPU temperature source"),
        (Setting.GpuPowerSource, GpuMetricGroup.Power, "GPU power source"),
        (Setting.GpuClockSource, GpuMetricGroup.Clocks, "GPU clock source"),
        (Setting.GpuFanSource, GpuMetricGroup.Fan, "GPU fan source"),
    ];

    protected override void OnPostLoad()
    {
        _choiceRefreshTimer?.Stop();
        _choiceRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _choiceRefreshTimer.Tick += RefreshChoices;
        foreach (var lookup in Enum.GetValues<Setting>())
        {
            if (lookup is Setting.DiskVolumes or Setting.NetworkSelectionMode or Setting.NetworkName) continue;
            GetSetting(lookup).OnSettingChange += ScheduleChoiceRefresh;
        }
        UpdateSettingsVisibility();
        ScheduleChoiceRefresh();
        var refs = new Dictionary<ChatBoxVariable, VRCOSC.App.ChatBox.Clips.Variables.ClipVariableReference>();
        foreach (var v in ChatBoxModel.Variables)
        {
            _variableTypes[v.Lookup] = v.Type;
            var r = v.Type switch
            {
                ChatBoxValueType.Int => CreateVariable<int>(v.Lookup, v.DisplayName, typeof(MissingAwareIntClipVariable)),
                ChatBoxValueType.Float => CreateVariable<float>(v.Lookup, v.DisplayName, typeof(MissingAwareFloatClipVariable)),
                _ => CreateVariable<string>(v.Lookup, v.DisplayName),
            };
            if (r is not null) refs[v.Lookup] = r;
        }

        IEnumerable<VRCOSC.App.ChatBox.Clips.Variables.ClipVariableReference> Refs(ChatBoxVariable[] vars) => vars.Where(refs.ContainsKey).Select(v => refs[v]);
        CreateState(ChatBoxState.Default, "Default", ChatBoxModel.DefaultStateFormat, Refs(ChatBoxModel.DefaultStateVariables));
        CreateState(ChatBoxState.Extended, "Extended", ChatBoxModel.ExtendedStateFormat, Refs(ChatBoxModel.ExtendedStateVariables));
        foreach (var (e, name, format, vars) in ChatBoxModel.Events) CreateEvent(e, name, format, Refs(vars));
    }

    // Start and stop

    protected override Task<bool> OnModuleStart()
    {
        DetectChoices(GetSettingValue<bool>(Setting.IncludeVirtualAdapters), GetSettingValue<bool>(Setting.IncludeRemovableDrives),
            GetSettingValue<bool>(Setting.EnableGpu), GetSettingValue<bool>(Setting.EnableCpu),
            GetSettingValue<bool>(Setting.EnableDisk), GetSettingValue<bool>(Setting.EnableNetwork),
            GetSettingValue<CpuSensorMode>(Setting.CpuSensorMode) == CpuSensorMode.HwInfo);
        var options = ReadOptions();
        _service = new HardwareService(options, SlotStateCodec.Decode(SlotState), m => Log(m), _restartSnapshot);
        _sender = new ParameterSender<HardwareParameter>((k, v) => SendParameter(k, v), ReadSenderOptions());
        _events = new ChatBoxEventTracker(60 * Stopwatch.Frequency);

        try
        {
            _service.Start();
        }
        catch (Exception e)
        {
            Log($"Failed to start: {e.Message}");
            _service.Dispose();
            _service = null;
            return Task.FromResult(false);
        }

        ChangeState(ChatBoxState.Default);

        if (GetSettingValue<bool>(Setting.WriteDiagnosticsOnStart))
        {
            var service = _service;
            _diagnosticsCancellation = new CancellationTokenSource();
            var token = _diagnosticsCancellation.Token;
            _diagnosticsTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    token.ThrowIfCancellationRequested();
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCOSC", "betterhardwarestats");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "diagnostics.txt");
                    await File.WriteAllTextAsync(path, service.DiagnosticsReport(), token);
                    Log($"Diagnostics written to {path}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception e)
                {
                    Log($"Writing diagnostics failed: {e.Message}");
                }
            });
        }
        return Task.FromResult(true);
    }

    private SenderOptions ReadSenderOptions() => new()
    {
        Mode = GetSettingValue<SendMode>(Setting.SendMode),
        ChangeEpsilon = GetSettingValue<float>(Setting.ChangeEpsilon),
        RefreshSeconds = GetSettingValue<int>(Setting.RefreshSeconds),
        OnMissing = GetSettingValue<MissingMetricMode>(Setting.OnMissingMetric),
    };

    private HardwareServiceOptions ReadOptions()
    {
        _output = new OutputSettings
        {
            EnableGpu = GetSettingValue<bool>(Setting.EnableGpu),
            EnableCpu = GetSettingValue<bool>(Setting.EnableCpu),
            EnableRam = GetSettingValue<bool>(Setting.EnableRam),
            EnableDisk = GetSettingValue<bool>(Setting.EnableDisk),
            EnableNetwork = GetSettingValue<bool>(Setting.EnableNetwork),
            MaxGpuTempC = GetSettingValue<int>(Setting.MaxGpuTempC),
            MaxGpuPowerW = GetSettingValue<int>(Setting.MaxGpuPowerW),
            MaxGpuClockMhz = GetSettingValue<int>(Setting.MaxGpuClockMhz),
            MaxCpuTempC = GetSettingValue<int>(Setting.MaxCpuTempC),
            MaxCpuPowerW = GetSettingValue<int>(Setting.MaxCpuPowerW),
            MaxCpuClockMhz = GetSettingValue<int>(Setting.MaxCpuClockMhz),
            MaxDiskMBps = GetSettingValue<int>(Setting.MaxDiskMBps),
        };
        _chatBox = new ChatBoxSettings(GetSettingValue<TemperatureUnit>(Setting.TemperatureUnit), GetSettingValue<MemoryUnit>(Setting.MemoryUnit));
        _overheatC = GetSettingValue<int>(Setting.OverheatThresholdC);

        var main = SourceCatalog.Parse(DropdownValue(Setting.GpuDataSource), _detected.Available);
        var overrides = new Dictionary<GpuMetricGroup, MetricSource?>();
        foreach (var (lookup, group, _) in GroupSettings)
        {
            var v = DropdownValue(lookup);
            overrides[group] = v is null or SameAsMain ? null : SourceCatalog.Parse(v, _detected.Available);
        }

        // Read saved text directly so an unavailable GPU keeps its selection.
        var (gpuMode, gpuKey) = GpuChoices.Parse((GetSetting(Setting.GpuSelection) as StringModuleSetting)?.Attribute.Value);

        var cpuValue = (GetSetting(Setting.SelectedCPU) as StringModuleSetting)?.Attribute.Value;
        var networkValue = (GetSetting(Setting.NetworkSelection) as StringModuleSetting)?.Attribute.Value;
        var networkMode = networkValue?.StartsWith("adapter:", StringComparison.Ordinal) == true
            ? NetworkSelectionMode.ByKey
            : Enum.TryParse<NetworkSelectionMode>(networkValue, out var mode) && mode is NetworkSelectionMode.AutoDefaultRoute or NetworkSelectionMode.AutoBusiest or NetworkSelectionMode.Aggregate
                ? mode : NetworkSelectionMode.AutoDefaultRoute;
        return new HardwareServiceOptions
        {
            PollIntervalMs = GetSettingValue<int>(Setting.PollIntervalMs),
            EnableGpu = _output.EnableGpu,
            EnableCpu = _output.EnableCpu,
            EnableRam = _output.EnableRam,
            EnableDisk = _output.EnableDisk,
            EnableNetwork = _output.EnableNetwork,
            Selection = new SelectionOptions(gpuMode, gpuKey, GetSettingValue<bool>(Setting.IncludeIntegrated)),
            IncludeVirtualAdapters = GetSettingValue<bool>(Setting.IncludeVirtualAdapters),
            Sources = SourceCatalog.Build(main, overrides, GetSettingValue<bool>(Setting.StrictDataSource)),
            UtilizationSmoothing = GetSettingValue<float>(Setting.UtilizationSmoothing),
            EnableNvml = GetSettingValue<bool>(Setting.EnableNvml),
            EnableAdlx = GetSettingValue<bool>(Setting.EnableAdlx),
            EnableIgcl = GetSettingValue<bool>(Setting.EnableIgcl),
            EnableLibreHardwareMonitor = GetSettingValue<bool>(Setting.EnableLibreHardwareMonitor),
            PollInactiveGpus = GetSettingValue<bool>(Setting.PollInactiveGpus),
            SelectedCpu = int.TryParse(cpuValue, out var cpuIndex) ? cpuIndex : 0,
            CpuSensorMode = GetSettingValue<CpuSensorMode>(Setting.CpuSensorMode),
            HwInfoCpuTemperature = DropdownValue(Setting.HwInfoCpuTemperature),
            HwInfoCpuPower = DropdownValue(Setting.HwInfoCpuPower),
            DiskSlotChoices = DiskSlotSettings.Select(s => (GetSetting(s) as StringModuleSetting)?.Attribute.Value).ToArray(),
            IncludeRemovableDrives = GetSettingValue<bool>(Setting.IncludeRemovableDrives),
            Network = new NetworkOptions(
                networkMode,
                networkMode == NetworkSelectionMode.ByKey ? networkValue!["adapter:".Length..] : null,
                GetSettingValue<int>(Setting.MaxNetworkMbps)),
        };
    }
    private string? DropdownValue(Setting lookup)
    {
        return (GetSetting(lookup) as StringModuleSetting)?.Attribute.Value;
    }

    private void StopDiagnostics()
    {
        _diagnosticsCancellation?.Cancel();
        if (_diagnosticsTask is { } diagnostics) diagnostics.GetAwaiter().GetResult();
        _diagnosticsTask = null;
        _diagnosticsCancellation?.Dispose();
        _diagnosticsCancellation = null;
    }

    protected override Task OnModuleStop()
    {
        var service = _service;
        _service = null;
        StopDiagnostics();
        if (_sender is { } sender)
        {
            // Final zeros and Present = false for every enabled group.
            sender.RequestResendAll();
            sender.BeginTick(Stopwatch.GetTimestamp());
            ParameterMapper.Apply(HardwareSnapshot.Empty, _output, sender);
        }
        if (service is not null)
        {
            SlotState = SlotStateCodec.Encode(service.SlotReservations);
            service.Dispose();
        }
        return Task.CompletedTask;
    }

    protected override void OnAvatarChange(Avatar? avatar) => _sender?.RequestResendAll();

    // Output updates

    [ModuleUpdate(ModuleUpdateMode.Custom, true, 250)]
    private void SendParameters()
    {
        if (_service is not { } service || _sender is not { } sender) return;
        var snapshot = service.Latest;
        var now = Stopwatch.GetTimestamp();

        if (_events?.Update(snapshot, _overheatC, _output, now) is { Count: > 0 } events)
        {
            if (events.Contains(ChatBoxEvent.GPUChanged)) sender.RequestResendAll(); // Refresh outputs after a GPU switch.
            UpdateChatBoxVariables(snapshot);
            foreach (var e in events) TriggerEvent(e);
        }

        sender.BeginTick(now);
        ParameterMapper.Apply(snapshot, _output, sender);

        if (service.TakeSlotsDirty()) SlotState = SlotStateCodec.Encode(service.SlotReservations);
        if (GetSettingValue<bool>(Setting.VerboseLogging) && snapshot.Sequence % 10 == 0)
        {
            var values = new List<string>();
            if (_output.EnableGpu) values.Add($"GPU {snapshot.Gpu.Selected?.Identity.Name} {snapshot.Gpu.Selected?.UtilizationPercent?.Value:F0}%");
            if (_output.EnableCpu) values.Add($"CPU {snapshot.Cpu.UsagePercent?.Value:F0}%");
            if (values.Count > 0) LogDebug($"snapshot {snapshot.Sequence}: {string.Join(", ", values)}");
        }
    }

    [ModuleUpdate(ModuleUpdateMode.ChatBox)]
    private void UpdateChatBox() => UpdateChatBoxVariables(Latest);

    private void UpdateChatBoxVariables(HardwareSnapshot snapshot)
    {
        foreach (var (lookup, value) in ChatBoxModel.Build(snapshot, _chatBox, _output))
        {
            switch (value)
            {
                case int i: SetVariableValue(lookup, i); break;
                case float f: SetVariableValue(lookup, f); break;
                case string s: SetVariableValue(lookup, s); break;
            }
        }
    }
    public sealed record SourceOption(string Title, string Value);
    internal enum Setting
    {
        PollIntervalMs, EnableGpu, EnableCpu, EnableRam, EnableDisk, EnableNetwork,
        SendMode, ChangeEpsilon, RefreshSeconds, OnMissingMetric, TemperatureUnit, MemoryUnit, OverheatThresholdC,
        VerboseLogging, WriteDiagnosticsOnStart,
        GpuSelection, IncludeIntegrated, IncludeVirtualAdapters, UtilizationSmoothing,
        PollInactiveGpus, MaxGpuTempC, MaxGpuPowerW, MaxGpuClockMhz,
        GpuDataSource, GpuUtilizationSource, GpuMemorySource, GpuTemperatureSource, GpuPowerSource, GpuClockSource, GpuFanSource,
        StrictDataSource, EnableNvml, EnableAdlx, EnableLibreHardwareMonitor,
        SelectedCPU, CpuSensorMode, MaxCpuTempC, MaxCpuPowerW, MaxCpuClockMhz,
        DiskVolumes, IncludeRemovableDrives, MaxDiskMBps, DiskSlot0, DiskSlot1, DiskSlot2, DiskSlot3,
        NetworkSelectionMode, NetworkName, MaxNetworkMbps, NetworkSelection, Support, HwInfoCpuTemperature, HwInfoCpuPower, EnableIgcl,
    }
}
