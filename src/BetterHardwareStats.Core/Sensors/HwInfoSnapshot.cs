using System.Buffers.Binary;
using System.Text;

namespace BetterHardwareStats.Core.Sensors;

public sealed record HwInfoReading(string Key, string Device, string Name, string Unit, int Type, double Value);
public sealed record HwInfoSnapshot(long PollUnixSeconds, IReadOnlyList<HwInfoReading> Readings);
public static class HwInfoParser
{
    public const int MaxBytes = 16 * 1024 * 1024;

    public static HwInfoSnapshot Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 || !bytes[..4].SequenceEqual("HWiS"u8)) throw new InvalidDataException("HWiNFO export is inactive or truncated.");
        var version = U(bytes, 4);
        if (version is not (1 or 2)) throw new InvalidDataException("Unsupported HWiNFO shared-memory version.");
        var sensorOffset = U(bytes, 20);
        var sensorSize = U(bytes, 24);
        var sensorCount = U(bytes, 28);
        var readingOffset = U(bytes, 32);
        var readingSize = U(bytes, 36);
        var readingCount = U(bytes, 40);
        CheckSection(bytes.Length, sensorOffset, sensorSize, sensorCount, version == 2 ? 392u : 264u);
        CheckSection(bytes.Length, readingOffset, readingSize, readingCount, version == 2 ? 460u : 316u);
        var result = new List<HwInfoReading>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (uint i = 0; i < readingCount; i++)
        {
            var reading = bytes.Slice(checked((int)(readingOffset + i * readingSize)), (int)readingSize);
            var sensorIndex = U(reading, 4);
            if (sensorIndex >= sensorCount) throw new InvalidDataException("Invalid HWiNFO sensor reference.");
            var sensor = bytes.Slice(checked((int)(sensorOffset + sensorIndex * sensorSize)), (int)sensorSize);
            var key = $"{U(sensor, 0):X8}:{U(sensor, 4):X8}:{U(reading, 8):X8}";
            if (!keys.Add(key)) throw new InvalidDataException("Duplicate HWiNFO sensor identity.");
            var device = Text(sensor, version == 2 ? 264 : 136, 128);
            if (device.Length == 0) device = Text(sensor, 8, 128);
            var name = Text(reading, version == 2 ? 316 : 140, 128);
            if (name.Length == 0) name = Text(reading, 12, 128);
            var unit = Text(reading, version == 2 ? 444 : 268, 16);
            var value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(reading.Slice(284, 8)));
            if (double.IsFinite(value)) result.Add(new(key, device, name, unit, (int)U(reading, 0), value));
        }
        return new(BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(12, 8)), result);
    }

    private static uint U(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

    private static void CheckSection(int length, uint offset, uint size, uint count, uint minimum)
    {
        if (offset < 44 || size < minimum || count > 100000 || (ulong)offset + (ulong)size * count > (ulong)length)
            throw new InvalidDataException("Invalid HWiNFO section bounds.");
    }

    private static string Text(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        var text = bytes.Slice(offset, count);
        var end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]).Trim();
    }
    public static float? Value(HwInfoSnapshot snapshot, string? key, int type, DateTimeOffset now)
    {
        var age = now.ToUnixTimeSeconds() - snapshot.PollUnixSeconds;
        if (age < -5 || age > 15 || string.IsNullOrEmpty(key) || key == "None") return null;
        var reading = snapshot.Readings.FirstOrDefault(r => r.Key == key && r.Type == type);
        if (reading is null || reading.Value > float.MaxValue) return null;
        if (type == 1)
        {
            if (reading.Value < -273.15) return null;
            if (reading.Unit is "C" or "\u00b0C") return (float)reading.Value;
            if (reading.Unit is "F" or "\u00b0F") return (float)((reading.Value - 32) * 5 / 9);
            return null;
        }
        return type == 5 && reading.Unit == "W" && reading.Value >= 0 ? (float)reading.Value : null;
    }
}
