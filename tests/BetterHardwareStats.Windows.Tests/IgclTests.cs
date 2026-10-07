using System.Runtime.InteropServices;
using BetterHardwareStats.Windows.Igcl;

namespace BetterHardwareStats.Windows.Tests;

public class IgclTests
{
    [Fact]
    public void X64AbiMatchesIntelHeaderLayout()
    {
        Assert.Equal(36, Marshal.SizeOf<IgclBackend.InitArgs>());
        Assert.Equal(8, Marshal.OffsetOf<IgclBackend.InitArgs>(nameof(IgclBackend.InitArgs.AppVersion)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<IgclBackend.InitArgs>(nameof(IgclBackend.InitArgs.ApplicationId)).ToInt32());
        Assert.Equal(320, Marshal.SizeOf<IgclBackend.DeviceProperties>());
        Assert.Equal(8, Marshal.OffsetOf<IgclBackend.DeviceProperties>(nameof(IgclBackend.DeviceProperties.DeviceId)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<IgclBackend.DeviceProperties>(nameof(IgclBackend.DeviceProperties.VendorId)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<IgclBackend.DeviceProperties>(nameof(IgclBackend.DeviceProperties.Name)).ToInt32());
        Assert.Equal(24, Marshal.SizeOf<IgclBackend.TemperatureProperties>());
        Assert.Equal(32, Marshal.SizeOf<IgclBackend.FrequencyProperties>());
        Assert.Equal(56, Marshal.SizeOf<IgclBackend.FrequencyState>());
        Assert.Equal(40, Marshal.OffsetOf<IgclBackend.FrequencyState>(nameof(IgclBackend.FrequencyState.Actual)).ToInt32());
        Assert.Equal(24, Marshal.SizeOf<IgclBackend.EnergyCounter>());
    }

    [Fact]
    public void PowerUsesEnergyTimeDeltasAndRejectsResets()
    {
        Assert.Equal(50f, IgclBackend.PowerDelta(1000000, 1000000, 26000000, 1500000));
        Assert.Equal(0f, IgclBackend.PowerDelta(100, 100, 100, 200));
        Assert.Null(IgclBackend.PowerDelta(100, 100, 99, 200));
        Assert.Null(IgclBackend.PowerDelta(100, 100, 200, 100));
        Assert.Null(IgclBackend.PowerDelta(100, 100, 200, 99));
    }
}
