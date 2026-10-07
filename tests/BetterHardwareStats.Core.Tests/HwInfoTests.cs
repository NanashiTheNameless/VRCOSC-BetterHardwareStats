using System.Buffers.Binary;
using System.Text;
using BetterHardwareStats.Core.Sensors;

namespace BetterHardwareStats.Core.Tests;

public class HwInfoTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);

    private static byte[] Export(uint version = 2)
    {
        var sensorSize = version == 2 ? 392 : 264;
        var readingSize = version == 2 ? 460 : 316;
        var readingOffset = 48 + sensorSize;
        var bytes = new byte[readingOffset + readingSize];
        "HWiS"u8.CopyTo(bytes);
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
        U(4, version); U(8, 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12, 8), Now.ToUnixTimeSeconds());
        U(20, 48); U(24, (uint)sensorSize); U(28, 1);
        U(32, (uint)readingOffset); U(36, (uint)readingSize); U(40, 1);
        U(48, 0x1234); U(52, 2);
        Encoding.UTF8.GetBytes("CPU Package").CopyTo(bytes, 48 + (version == 2 ? 264 : 136));
        U(readingOffset, 1); U(readingOffset + 4, 0); U(readingOffset + 8, 0x5678);
        Encoding.UTF8.GetBytes("CPU Temperature").CopyTo(bytes, readingOffset + (version == 2 ? 316 : 140));
        Encoding.UTF8.GetBytes("C").CopyTo(bytes, readingOffset + (version == 2 ? 444 : 268));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(readingOffset + 284, 8), BitConverter.DoubleToInt64Bits(65.5));
        return bytes;
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void ReadsPackedLayoutsAndStableIdentity(uint version)
    {
        var snapshot = HwInfoParser.Parse(Export(version));
        var reading = Assert.Single(snapshot.Readings);
        Assert.Equal("00001234:00000002:00005678", reading.Key);
        Assert.Equal("CPU Package", reading.Device);
        Assert.Equal("CPU Temperature", reading.Name);
        Assert.Equal(65.5f, HwInfoParser.Value(snapshot, reading.Key, 1, Now));
        Assert.Null(HwInfoParser.Value(snapshot, "different", 1, Now));
        Assert.Null(HwInfoParser.Value(snapshot, reading.Key, 5, Now));
    }

    [Fact]
    public void RejectsInactiveTruncatedAndUnboundedExports()
    {
        Assert.Throws<InvalidDataException>(() => HwInfoParser.Parse([]));
        var bytes = Export();
        Assert.Throws<InvalidDataException>(() => HwInfoParser.Parse(bytes[..^1]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => HwInfoParser.Parse(bytes));
        bytes = Export(); bytes[0] = 0;
        Assert.Throws<InvalidDataException>(() => HwInfoParser.Parse(bytes));
        bytes = Export();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 3);
        Assert.Throws<InvalidDataException>(() => HwInfoParser.Parse(bytes));
        bytes = Export();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48 + 392 + 4, 4), 1);
        Assert.Throws<InvalidDataException>(() => HwInfoParser.Parse(bytes));
    }

    [Fact]
    public void StaleOrFutureDataAndMissingSelectionsNeverBecomeZero()
    {
        var snapshot = HwInfoParser.Parse(Export());
        var key = snapshot.Readings[0].Key;
        Assert.Null(HwInfoParser.Value(snapshot, key, 1, Now.AddSeconds(16)));
        Assert.Null(HwInfoParser.Value(snapshot, key, 1, Now.AddSeconds(-6)));
        Assert.Null(HwInfoParser.Value(snapshot, "None", 1, Now));
        Assert.Null(HwInfoParser.Value(snapshot, null, 1, Now));
        var bytes = Export();
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48 + 392 + 284, 8), BitConverter.DoubleToInt64Bits(double.NaN));
        Assert.Empty(HwInfoParser.Parse(bytes).Readings);
    }

    [Fact]
    public void ConvertsTemperatureUnitsAndRequiresWattsForPower()
    {
        HwInfoSnapshot Sample(string unit, int type, double value) => new(Now.ToUnixTimeSeconds(), [new("id", "CPU", "Sensor", unit, type, value)]);
        Assert.Equal(100f, HwInfoParser.Value(Sample("F", 1, 212), "id", 1, Now));
        Assert.Equal(125f, HwInfoParser.Value(Sample("W", 5, 125), "id", 5, Now));
        Assert.Null(HwInfoParser.Value(Sample("%", 5, 125), "id", 5, Now));
        Assert.Null(HwInfoParser.Value(Sample("W", 5, -1), "id", 5, Now));
    }
}
