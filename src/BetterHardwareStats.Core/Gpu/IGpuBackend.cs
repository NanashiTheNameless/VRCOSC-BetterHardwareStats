using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Gpu;
public interface IGpuBackend : IDisposable
{
    string Name { get; }
    MetricSource Source { get; }
    BackendState State { get; }
    string? StatusDetail { get; }
    bool TryInitialize(IReadOnlyList<GpuIdentity> catalog, out string? failureReason);

    IReadOnlyList<BackendDevice> Devices { get; }
    BackendSample Sample();
}
public sealed record GpuSourceInput(
    BackendStatus Status,
    MetricSource Source,
    IReadOnlyList<BackendDevice> Devices,
    BackendSample? Latest);
