using System.IO.MemoryMappedFiles;
using BetterHardwareStats.Core.Sensors;

namespace BetterHardwareStats.Windows.HwInfo;
public static class HwInfoReader
{
    public static HwInfoSnapshot Read() => Read(@"Global\HWiNFO_SENS_SM2", @"Global\HWiNFO_SM2_MUTEX");

    internal static HwInfoSnapshot Read(string memoryName, string mutexName)
    {
        using var mutex = Mutex.OpenExisting(mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(100); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("HWiNFO export is busy.");
            using var memory = MemoryMappedFile.OpenExisting(memoryName, MemoryMappedFileRights.Read);
            using var view = memory.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.Capacity < 44 || view.Capacity > HwInfoParser.MaxBytes) throw new InvalidDataException("Invalid HWiNFO export size.");
            var bytes = new byte[(int)view.Capacity];
            view.ReadArray(0, bytes, 0, bytes.Length);
            return HwInfoParser.Parse(bytes);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
