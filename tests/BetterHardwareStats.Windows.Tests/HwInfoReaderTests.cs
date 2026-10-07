using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BetterHardwareStats.Windows.HwInfo;

namespace BetterHardwareStats.Windows.Tests;

public class HwInfoReaderTests
{
    [Fact]
    public void ReadsExistingLocalMappingAndReleasesMutexAfterBadData()
    {
        var name = "BetterHardwareStatsTest_" + Guid.NewGuid().ToString("N");
        using var mutex = new Mutex(false, name + "_mutex");
        using var memory = MemoryMappedFile.CreateNew(name, 4096);
        using var view = memory.CreateViewAccessor();
        var header = new byte[44];
        "HWiS"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12, 8), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 264);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32, 4), 44);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), 316);
        view.WriteArray(0, header, 0, header.Length);
        Assert.Empty(HwInfoReader.Read(name, name + "_mutex").Readings);
        view.Write(0, (byte)0);
        Assert.Throws<InvalidDataException>(() => HwInfoReader.Read(name, name + "_mutex"));
        view.Write(0, (byte)'H');
        Assert.Empty(HwInfoReader.Read(name, name + "_mutex").Readings);
        var after = new byte[44];
        view.ReadArray(0, after, 0, after.Length);
        Assert.Equal(header, after); // Consumer did not modify the export.
    }
}
