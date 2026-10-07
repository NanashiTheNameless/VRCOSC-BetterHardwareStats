using System.Runtime.InteropServices;
using BetterHardwareStats.Core.Counters;

namespace BetterHardwareStats.Windows.Native;
internal static unsafe partial class PdhNative
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint ERROR_SUCCESS = 0;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_NO_DATA = 0x800007D5;
    public const uint PDH_CSTATUS_NO_INSTANCE = 0x800007D1;
    public const uint PDH_CSTATUS_VALID_DATA = 0;
    public const uint PDH_CSTATUS_NEW_DATA = 1;
    public const int ItemSize = 24;
    public const int ItemStatusOffset = 8;
    public const int ItemValueOffset = 16;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* itemBuffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}
internal sealed unsafe class PdhCounterReader : ICounterReader, IDisposable
{
    private byte* _buffer;
    private uint _capacity;
    private int _count;

    public PdhCounterReader(nint counter, bool available, string path)
    {
        Counter = counter;
        Path = path;
        CounterAdded = available;
        Available = available;
    }

    public nint Counter { get; }
    public string Path { get; }
    public bool CounterAdded { get; }
    public bool Available { get; private set; }

    public uint LastStatus { get; private set; }
    public int Count => Available ? _count : 0;

    public ReadOnlySpan<char> GetName(int index)
    {
        var namePtr = *(char**)(_buffer + index * PdhNative.ItemSize);
        return namePtr is null ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(namePtr);
    }

    public double GetValue(int index) => *(double*)(_buffer + index * PdhNative.ItemSize + PdhNative.ItemValueOffset);

    public bool IsValid(int index)
    {
        var status = *(uint*)(_buffer + index * PdhNative.ItemSize + PdhNative.ItemStatusOffset);
        return status is PdhNative.PDH_CSTATUS_VALID_DATA or PdhNative.PDH_CSTATUS_NEW_DATA;
    }
    public bool Fill()
    {
        if (!CounterAdded) return false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var size = _capacity;
            var status = PdhNative.PdhGetFormattedCounterArray(Counter, PdhNative.PDH_FMT_DOUBLE | PdhNative.PDH_FMT_NOCAP100, ref size, out var count, _buffer);
            LastStatus = status;
            if (status == PdhNative.PDH_MORE_DATA)
            {
                // Instance count can change between calls; leave headroom.
                Grow(Math.Max(size, _capacity) + 4096);
                continue;
            }
            if (status == PdhNative.ERROR_SUCCESS)
            {
                _count = (int)count;
                Available = true;
                return true;
            }
            if (status is PdhNative.PDH_NO_DATA or PdhNative.PDH_CSTATUS_NO_INSTANCE)
            {
                _count = 0; // healthy counter set with zero instances
                Available = true;
                return true;
            }
            break;
        }
        _count = 0;
        Available = false;
        return false;
    }

    private void Grow(uint size)
    {
        if (_buffer is not null) NativeMemory.Free(_buffer);
        _buffer = (byte*)NativeMemory.Alloc(size);
        _capacity = size;
    }

    public void Dispose()
    {
        if (_buffer is not null) NativeMemory.Free(_buffer);
        _buffer = null;
        _capacity = 0;
    }
}
public sealed class PdhEngine : IDisposable
{
    private readonly nint _query;
    private readonly List<(IPdhConsumer Consumer, PdhCounterReader[] Readers)> _consumers = [];
    private bool _warm;

    public PdhEngine(IEnumerable<IPdhConsumer> consumers)
    {
        var status = PdhNative.PdhOpenQuery(null, 0, out _query);
        if (status != 0) throw new InvalidOperationException($"PdhOpenQuery failed: 0x{status:X8}");

        foreach (var c in consumers)
        {
            var readers = new PdhCounterReader[c.CounterPaths.Count];
            for (var i = 0; i < readers.Length; i++)
            {
                var path = c.CounterPaths[i];
                var s = PdhNative.PdhAddEnglishCounter(_query, path, 0, out var counter);
                readers[i] = new PdhCounterReader(counter, s == 0, path);
                if (s != 0) AddFailures.Add($"{c.Name}: {path} unavailable (0x{s:X8})");
            }
            _consumers.Add((c, readers));
        }
    }
    public List<string> AddFailures { get; } = [];
    public IReadOnlyDictionary<string, int> LastInstanceCounts =>
        _consumers.SelectMany(c => c.Readers).GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.First().Count);
    public IEnumerable<(string Path, string Instance, double Value, bool Valid)> Dump(int max = int.MaxValue)
    {
        foreach (var (_, readers) in _consumers)
            foreach (var r in readers)
                for (var i = 0; i < Math.Min(r.Count, max); i++)
                    yield return (r.Path, r.GetName(i).ToString(), r.GetValue(i), r.IsValid(i));
    }
    public bool Collect(long timestampTicks)
    {
        var status = PdhNative.PdhCollectQueryData(_query);
        if (!_warm)
        {
            _warm = true;
            return false;
        }
        if (status != 0) throw new InvalidOperationException($"PdhCollectQueryData failed: 0x{status:X8}");

        foreach (var (consumer, readers) in _consumers)
        {
            foreach (var r in readers) r.Fill();
            consumer.OnCollected(readers, timestampTicks);
        }
        return true;
    }

    public void Dispose()
    {
        PdhNative.PdhCloseQuery(_query);
        foreach (var (_, readers) in _consumers)
            foreach (var r in readers) r.Dispose();
    }
    public static bool CanAdd(string path)
    {
        if (PdhNative.PdhOpenQuery(null, 0, out var q) != 0) return false;
        try { return PdhNative.PdhAddEnglishCounter(q, path, 0, out _) == 0; }
        finally { PdhNative.PdhCloseQuery(q); }
    }
}
