namespace BetterHardwareStats.Core.Model;
public enum MetricSource
{
    WindowsCounters,
    SystemApi,
    Nvml,
    Adlx,
    LibreHardwareMonitor,
    HostProvider,
    Dxgi,
    HwInfo,
    Igcl,
}
public sealed record MetricValue<T>(T Value, MetricSource Source, long TimestampTicks) where T : struct;

public enum BackendState
{
    NotStarted,
    Running,
    Degraded,
    Disabled,
}

public sealed record BackendStatus(
    string Name,
    BackendState State,
    string? Detail,
    int ConsecutiveFailures = 0,
    double LastPassMilliseconds = 0);
public enum NativeResultClass
{
    Success,
    NotSupported,
    Transient,
    Fatal,
}

public static class MetricValueExtensions
{
    public static MetricValue<T>? Fresh<T>(this MetricValue<T>? value, long nowTicks, long maxAgeTicks) where T : struct
        => value is not null && nowTicks - value.TimestampTicks <= maxAgeTicks ? value : null;
}
