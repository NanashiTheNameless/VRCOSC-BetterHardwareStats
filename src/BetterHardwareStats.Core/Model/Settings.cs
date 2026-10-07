namespace BetterHardwareStats.Core.Model;

// Setting enums shared by Core and the module. Member names are what VRCOSC persists,
// so never rename them after release.
public enum GpuMetricGroup
{
    Utilization,
    Memory,
    Temperature,
    Power,
    Clocks,
    Fan,
}

public enum GpuSelectionMode
{
    AutoVRChat,
    AutoHighestLoad,
    DiscreteFirst,
    IntegratedFirst,
    Specific,
}

public enum SendMode
{
    OnChange,
    Always,
}

public enum MissingMetricMode
{
    SendZeroOnce,
    HoldLast,
}

public enum NetworkSelectionMode
{
    AutoDefaultRoute,
    AutoBusiest,
    ByName,
    Aggregate,
    ByKey,
}

public enum CpuSensorMode
{
    Off,
    AutoIfElevated,
    On,
    HwInfo,
}

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

public enum MemoryUnit
{
    GB,
    MB,
}
