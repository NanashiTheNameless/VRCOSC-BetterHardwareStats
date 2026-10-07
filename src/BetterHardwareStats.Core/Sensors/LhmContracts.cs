using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Sensors;

// Shared contracts keep sensor-library types inside their isolated loading context.
public interface ILhmGpuBackend : IDisposable
{
    IReadOnlyList<BackendDevice> Devices { get; }
    string? Version { get; }
    string? Location { get; }
    BackendSample Sample();
    IEnumerable<(string Hardware, string Type, string Name, string Identifier, float? Value)> Dump();
}

public interface ILhmCpuSensors : IDisposable
{
    int CpuCount { get; }
    (float? TemperatureC, float? PowerWatts) Read(int packageIndex);
    IEnumerable<(string Type, string Name, float? Value)> Dump();
}
