using BetterHardwareStats.Core.Model;
using BetterHardwareStats.Core.Output;

namespace BetterHardwareStats.Core.Tests;

public class NormalizationTests
{
    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 2)]
    [InlineData(254.6, 255)]
    [InlineData(400, 255)]
    [InlineData(-3, 0)]
    public void ByteIntRoundsHalfToEvenAndClamps(double v, int expected) => Assert.Equal(expected, Normalization.ToByteInt(v));

    [Fact]
    public void NullStaysNull()
    {
        Assert.Null(Normalization.ToByteInt(null));
        Assert.Null(Normalization.PercentToFraction(null));
        Assert.Null(Normalization.Ratio(1, null));
        Assert.Null(Normalization.Ratio(1, 0));
        Assert.Null(Normalization.ToByteInt(double.NaN));
        Assert.Null(Normalization.GiBInt(null));
    }

    [Fact]
    public void Fractions()
    {
        Assert.Equal(0.42f, Normalization.PercentToFraction(42));
        Assert.Equal(1f, Normalization.PercentToFraction(130));
        Assert.Equal(0.5f, Normalization.Scaled(50, 100));
        Assert.Equal(1f, Normalization.Scaled(600, 450));
        Assert.Equal(43, Normalization.PercentFromFraction(0.43f));
        Assert.Equal(42, Normalization.Percent(42.5f)); // half to even
        Assert.Equal(44, Normalization.Percent(43.5f));
    }

    [Fact]
    public void Units()
    {
        Assert.Equal(16, Normalization.GiBInt(16L * 1024 * 1024 * 1024));
        Assert.Equal(16, Normalization.GiBInt(15.6 * Normalization.BytesPerGiB));
        Assert.Equal(255, Normalization.GiBInt(512 * Normalization.BytesPerGiB));
        Assert.Equal(100, Normalization.Mbps(12_500_000));
        Assert.Equal(212, Normalization.CelsiusToFahrenheit(100));
    }
}

public class ParameterSenderTests
{
    private enum P { F, I, B }

    private sealed class Sink
    {
        public readonly List<(P Key, object Value)> Sent = [];
        public void Send(P k, object v) => Sent.Add((k, v));
        public object? Last(P k) => Sent.LastOrDefault(s => s.Key == k).Value;
        public int Count(P k) => Sent.Count(s => s.Key == k);
    }

    private static (ParameterSender<P> S, Sink Sink) Make(SenderOptions? o = null)
    {
        var sink = new Sink();
        return (new ParameterSender<P>(sink.Send, o ?? new SenderOptions { TicksPerSecond = 1000, RefreshSeconds = 0 }), sink);
    }

    private static void Tick(ParameterSender<P> s, long t, float? f, int? i = null, bool? b = null)
    {
        s.BeginTick(t);
        s.Set(P.F, f);
        if (i is not null || b is not null)
        {
            s.Set(P.I, i);
            s.Set(P.B, b);
        }
    }

    [Fact]
    public void SendsEverythingOnFirstTickThenOnlyChanges()
    {
        var (s, sink) = Make();
        Tick(s, 0, 0.5f, 3, true);
        Assert.Equal(3, sink.Sent.Count);
        Assert.IsType<float>(sink.Last(P.F));
        Assert.IsType<int>(sink.Last(P.I));
        Assert.IsType<bool>(sink.Last(P.B));

        Tick(s, 1, 0.503f, 3, true); // within epsilon, same int and bool
        Assert.Equal(3, sink.Sent.Count);
        Tick(s, 2, 0.51f, 4, false);
        Assert.Equal(6, sink.Sent.Count);
    }

    [Fact]
    public void AlwaysModeSendsEveryTick()
    {
        var (s, sink) = Make(new SenderOptions { Mode = SendMode.Always, RefreshSeconds = 0, TicksPerSecond = 1000 });
        for (var t = 0; t < 5; t++) Tick(s, t, 0.5f);
        Assert.Equal(5, sink.Count(P.F));
    }

    [Fact]
    public void SendZeroOnceOnMissing()
    {
        var (s, sink) = Make();
        Tick(s, 0, 0.7f);
        Tick(s, 1, null);
        Tick(s, 2, null);
        Tick(s, 3, null);
        Assert.Equal([0.7f, 0f], sink.Sent.Select(x => (float)x.Value));
        Tick(s, 4, 0.7f);
        Assert.Equal(0.7f, sink.Last(P.F));
    }

    [Fact]
    public void HoldLastSendsNothingOnMissing()
    {
        var (s, sink) = Make(new SenderOptions { OnMissing = MissingMetricMode.HoldLast, RefreshSeconds = 0, TicksPerSecond = 1000 });
        Tick(s, 0, 0.7f);
        Tick(s, 1, null);
        Assert.Single(sink.Sent);
        s.RequestResendAll();
        Tick(s, 2, null);
        Assert.Equal(2, sink.Count(P.F));
        Assert.Equal(0.7f, sink.Last(P.F));
    }

    [Fact]
    public void NeverKnownValueIsNotSentExceptOnForce()
    {
        var (s, sink) = Make();
        s.BeginTick(0); // the initial forced tick
        s.Set(P.I, (int?)null);
        Assert.Equal(0, sink.Last(P.I));
        s.BeginTick(1);
        s.Set(P.I, (int?)null);
        Assert.Equal(1, sink.Count(P.I));
    }

    [Fact]
    public void ResendAllAndRefresh()
    {
        var (s, sink) = Make(new SenderOptions { RefreshSeconds = 10, TicksPerSecond = 1000 });
        Tick(s, 0, 0.5f);
        Tick(s, 1000, 0.5f);
        Assert.Equal(1, sink.Count(P.F));

        s.RequestResendAll(); // avatar change
        Tick(s, 2000, 0.5f);
        Assert.Equal(2, sink.Count(P.F));

        Tick(s, 11000, 0.5f); // 9 s after the last forced tick: no refresh yet
        Assert.Equal(2, sink.Count(P.F));
        Tick(s, 12000, 0.5f);
        Assert.Equal(3, sink.Count(P.F));
    }

    [Fact]
    public void NanFloatIsMissing()
    {
        var (s, sink) = Make();
        Tick(s, 0, 0.5f);
        Tick(s, 1, float.NaN);
        Assert.Equal(0f, sink.Last(P.F));
    }
}

public class ParameterTableTests
{
    // names, types and units must match the official module exactly.
    public static TheoryData<string, ParameterType> Official => new()
    {
        { "VRCOSC/Hardware/CPU/Usage", ParameterType.Float },
        { "VRCOSC/Hardware/CPU/Power", ParameterType.Int },
        { "VRCOSC/Hardware/CPU/Temp", ParameterType.Int },
        { "VRCOSC/Hardware/GPU/Usage", ParameterType.Float },
        { "VRCOSC/Hardware/GPU/Power", ParameterType.Int },
        { "VRCOSC/Hardware/GPU/Temp", ParameterType.Int },
        { "VRCOSC/Hardware/RAM/Usage", ParameterType.Float },
        { "VRCOSC/Hardware/RAM/Total", ParameterType.Int },
        { "VRCOSC/Hardware/RAM/Used", ParameterType.Int },
        { "VRCOSC/Hardware/RAM/Free", ParameterType.Int },
        { "VRCOSC/Hardware/VRAM/Usage", ParameterType.Float },
        { "VRCOSC/Hardware/VRAM/Total", ParameterType.Int },
        { "VRCOSC/Hardware/VRAM/Used", ParameterType.Int },
        { "VRCOSC/Hardware/VRAM/Free", ParameterType.Int },
    };

    [Theory]
    [MemberData(nameof(Official))]
    public void OfficialParameterExistsWithSameType(string name, ParameterType type)
    {
        var p = Assert.Single(HardwareParameterTable.All, x => x.DefaultName == name);
        Assert.Equal(type, p.Type);
        Assert.True(p.Parity);
    }

    [Fact]
    public void CountsMatchSpec()
    {
        var all = HardwareParameterTable.All;
        Assert.Equal(96, all.Count);
        Assert.Equal(HardwareParameterTable.Count, all.Count);
        Assert.Equal(Enum.GetValues<HardwareParameter>().Length, all.Count);
        Assert.Equal(14, all.Count(p => p.Parity));
        Assert.Equal(50, all.Count(p => p.Domain == ParameterDomain.Gpu));
        Assert.Equal(9, all.Count(p => p.Domain == ParameterDomain.Cpu));
        Assert.Equal(5, all.Count(p => p.Domain == ParameterDomain.Ram));
        Assert.Equal(27, all.Count(p => p.Domain == ParameterDomain.Disk));
        Assert.Equal(5, all.Count(p => p.Domain == ParameterDomain.Network));
    }

    [Fact]
    public void NamesAndLookupsAreUniqueAndAscii()
    {
        var all = HardwareParameterTable.All;
        Assert.Equal(all.Count, all.Select(p => p.DefaultName).Distinct().Count());
        Assert.Equal(all.Count, all.Select(p => p.Lookup).Distinct().Count());
        Assert.All(all, p =>
        {
            Assert.StartsWith("VRCOSC/Hardware/", p.DefaultName);
            Assert.True((p.DefaultName + p.DisplayName + p.Description).All(c => c < 128));
        });
        Assert.Equal("VRCOSC/Hardware/GPU/2/VRAMPercent", HardwareParameterTable.Get(HardwareParameter.Gpu2VramPercent).DefaultName);
        Assert.Equal(2, HardwareParameterTable.Get(HardwareParameter.Gpu2VramPercent).Slot);
    }
}
