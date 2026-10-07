using System.Diagnostics;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Output;

public sealed record SenderOptions
{
    public SendMode Mode { get; init; } = SendMode.OnChange;
    public float ChangeEpsilon { get; init; } = 0.005f;
    public int RefreshSeconds { get; init; } = 10;

    public MissingMetricMode OnMissing { get; init; } = MissingMetricMode.SendZeroOnce;

    public long TicksPerSecond { get; init; } = Stopwatch.Frequency;
}
public sealed class ParameterSender<TKey> where TKey : struct, Enum
{
    private enum Kind : byte { Float, Int, Bool }

    private struct Entry
    {
        public Kind Kind;
        public bool HasSent;
        public double Sent;
        public bool HasKnown;
        public double Known;
        public bool Missing;
    }

    private readonly Action<TKey, object> _send;
    private readonly Dictionary<TKey, Entry> _entries = new();
    private bool _forceRequested = true; // module start sends everything
    private bool _forceThisTick;
    private long _lastRefresh = long.MinValue;

    public ParameterSender(Action<TKey, object> send, SenderOptions? options = null)
    {
        _send = send;
        Options = options ?? new SenderOptions();
    }

    public SenderOptions Options { get; set; }
    public long SentCount { get; private set; }
    public void RequestResendAll() => _forceRequested = true;

    public void BeginTick(long nowTicks)
    {
        var refresh = Options.RefreshSeconds > 0
            && (_lastRefresh == long.MinValue || nowTicks - _lastRefresh >= Options.RefreshSeconds * Options.TicksPerSecond);
        _forceThisTick = _forceRequested || refresh;
        if (_forceThisTick) _lastRefresh = nowTicks;
        _forceRequested = false;
    }

    public void Set(TKey key, float? value) => SetCore(key, Kind.Float, value is { } v && float.IsFinite(v) ? v : null);

    public void Set(TKey key, int? value) => SetCore(key, Kind.Int, value);

    public void Set(TKey key, bool? value) => SetCore(key, Kind.Bool, value is { } b ? (b ? 1 : 0) : null);
    public void Forget(TKey key) => _entries.Remove(key);

    private void SetCore(TKey key, Kind kind, double? value)
    {
        _entries.TryGetValue(key, out var e);
        e.Kind = kind;

        if (value is { } v)
        {
            e.HasKnown = true;
            e.Known = v;
            e.Missing = false;
            if (_forceThisTick || Options.Mode == SendMode.Always || !e.HasSent || Changed(kind, e.Sent, v)) Emit(key, ref e, v);
        }
        else if (Options.OnMissing == MissingMetricMode.HoldLast)
        {
            e.Missing = true;
            if (_forceThisTick && e.HasKnown) Emit(key, ref e, e.Known);
        }
        else
        {
            if (_forceThisTick || (!e.Missing && e.HasSent)) Emit(key, ref e, 0);
            e.Missing = true;
        }

        _entries[key] = e;
    }

    private bool Changed(Kind kind, double sent, double now) =>
        kind == Kind.Float ? Math.Abs(now - sent) > Options.ChangeEpsilon : now != sent;

    private void Emit(TKey key, ref Entry e, double v)
    {
        e.HasSent = true;
        e.Sent = v;
        SentCount++;
        object boxed = e.Kind switch
        {
            Kind.Float => (float)v,
            Kind.Int => (int)v,
            _ => v != 0,
        };
        _send(key, boxed);
    }
}
