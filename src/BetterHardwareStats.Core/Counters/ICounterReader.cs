namespace BetterHardwareStats.Core.Counters;
public interface ICounterReader
{
    bool Available { get; }

    int Count { get; }
    ReadOnlySpan<char> GetName(int index);
    double GetValue(int index);
    bool IsValid(int index);
}
public interface IPdhConsumer
{
    string Name { get; }
    IReadOnlyList<string> CounterPaths { get; }
    void OnCollected(IReadOnlyList<ICounterReader> counters, long timestampTicks);
}
public sealed class ArrayCounterReader : ICounterReader
{
    private readonly (string Name, double Value, bool Valid)[] _items;

    public ArrayCounterReader(IEnumerable<(string Name, double Value)> items, bool available = true)
        : this(items.Select(i => (i.Name, i.Value, true)), available) { }

    public ArrayCounterReader(IEnumerable<(string Name, double Value, bool Valid)> items, bool available = true)
    {
        _items = items.ToArray();
        Available = available;
    }

    public static ArrayCounterReader Missing { get; } = new(Array.Empty<(string, double)>(), available: false);

    public bool Available { get; }
    public int Count => _items.Length;
    public ReadOnlySpan<char> GetName(int index) => _items[index].Name;
    public double GetValue(int index) => _items[index].Value;
    public bool IsValid(int index) => _items[index].Valid;
}
