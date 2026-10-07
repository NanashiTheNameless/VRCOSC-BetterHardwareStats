using System.Diagnostics;
using BetterHardwareStats.Core.Gpu;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Network;
public sealed record InterfaceRow(
    string Key,
    string Alias,
    string Description,
    bool IsUp,
    bool IsLoopback,
    bool IsHardware,
    bool IsFilter,
    ulong ReceiveLinkBps,
    ulong TransmitLinkBps,
    ulong InOctets,
    ulong OutOctets);

public readonly record struct InterfaceRate(double DownBytesPerSec, double UpBytesPerSec);
public sealed class NetworkRateTracker
{
    private readonly long _ticksPerSecond;
    private readonly Dictionary<string, (ulong In, ulong Out, long Ticks)> _prev = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InterfaceRate> _rates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Down, long Up)> _session = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Down, long Up)> _deltas = new(StringComparer.Ordinal);

    public NetworkRateTracker(long? ticksPerSecond = null) => _ticksPerSecond = ticksPerSecond ?? Stopwatch.Frequency;
    public IReadOnlyDictionary<string, InterfaceRate> Rates => _rates;

    public (long Down, long Up) Session(string key) => _session.TryGetValue(key, out var s) ? s : default;
    public (long Down, long Up) LastDelta(string key) => _deltas.TryGetValue(key, out var d) ? d : default;

    public void Update(IReadOnlyList<InterfaceRow> rows, long nowTicks)
    {
        _rates.Clear();
        _deltas.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            seen.Add(r.Key);
            if (_prev.TryGetValue(r.Key, out var p))
            {
                var elapsed = (double)(nowTicks - p.Ticks) / _ticksPerSecond;
                if (r.InOctets >= p.In && r.OutOctets >= p.Out && elapsed > 0)
                {
                    var din = r.InOctets - p.In;
                    var dout = r.OutOctets - p.Out;
                    _rates[r.Key] = new InterfaceRate(din / elapsed, dout / elapsed);
                    _session.TryGetValue(r.Key, out var s);
                    _session[r.Key] = (s.Down + (long)din, s.Up + (long)dout);
                    _deltas[r.Key] = ((long)din, (long)dout);
                }
            }
            _prev[r.Key] = (r.InOctets, r.OutOctets, nowTicks);
        }
        foreach (var k in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(k);
    }
}

public sealed record NetworkOptions(NetworkSelectionMode Mode = NetworkSelectionMode.AutoDefaultRoute, string? Name = null, int MaxNetworkMbps = 0);
public sealed class NetworkSelector
{
    public const double DefaultCapacityBps = 1e9;
    private readonly LeaderTracker _route;
    private readonly LeaderTracker _busiest;

    // Session totals of whatever was selected at each tick, so switching adapters (or modes) does not make
    // the ChatBox totals jump to another adapter's history.
    private long _sessionDown;
    private long _sessionUp;

    public NetworkSelector(long? windowTicks = null, long sessionDown = 0, long sessionUp = 0)
    {
        _sessionDown = sessionDown;
        _sessionUp = sessionUp;
        var w = windowTicks ?? 5 * Stopwatch.Frequency;
        _route = new LeaderTracker(w, 3, 0);
        _busiest = new LeaderTracker(w, 3, 0);
    }

    public (long Down, long Up) SessionTotals => (Interlocked.Read(ref _sessionDown), Interlocked.Read(ref _sessionUp));

    public static bool IsCandidate(InterfaceRow r) => r.IsUp && !r.IsLoopback && !r.IsFilter;

    public static double Capacity(ulong linkBps, int maxNetworkMbps) =>
        maxNetworkMbps > 0 ? maxNetworkMbps * 1e6 : linkBps is 0 or ulong.MaxValue ? DefaultCapacityBps : linkBps;
    public NetworkSnapshot Select(
        IReadOnlyList<InterfaceRow> rows, NetworkRateTracker rates, string? defaultRouteKey, NetworkOptions options, long nowTicks)
    {
        var candidates = rows.Where(IsCandidate).ToList();
        var keys = candidates.Select(c => c.Key).ToList();

        // Both trackers run in every mode so switching modes starts with warm state.
        _route.Retain(keys);
        _busiest.Retain(keys);
        _route.Advance(nowTicks);
        _busiest.Advance(nowTicks);
        if (defaultRouteKey is not null && keys.Contains(defaultRouteKey)) _route.Add(defaultRouteKey, 1, nowTicks);
        foreach (var c in candidates)
            if (rates.Rates.TryGetValue(c.Key, out var r)) _busiest.Add(c.Key, r.DownBytesPerSec + r.UpBytesPerSec, nowTicks);

        if (options.Mode == NetworkSelectionMode.Aggregate)
        {
            foreach (var c in candidates.Where(c => c.IsHardware)) Accumulate(rates.LastDelta(c.Key));
            return Aggregate(candidates, rates, options, nowTicks, _sessionDown, _sessionUp);
        }

        InterfaceRow? pick = options.Mode switch
        {
            NetworkSelectionMode.AutoDefaultRoute => candidates.FirstOrDefault(c => c.Key == _route.Leader),
            NetworkSelectionMode.ByKey => candidates.FirstOrDefault(c => c.Key == options.Name)
                ?? candidates.FirstOrDefault(c => c.Key == _route.Leader),
            NetworkSelectionMode.ByName when !string.IsNullOrWhiteSpace(options.Name) => candidates.FirstOrDefault(c =>
                c.Alias.Contains(options.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(options.Name.Trim(), StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        pick ??= Busiest(candidates);
        if (pick is null) return NetworkSnapshot.Empty with { SessionDownloadBytes = _sessionDown, SessionUploadBytes = _sessionUp, TimestampTicks = nowTicks };

        var has = rates.Rates.TryGetValue(pick.Key, out var rate);
        Accumulate(rates.LastDelta(pick.Key));
        return new NetworkSnapshot(
            has,
            pick.Alias,
            has ? rate.DownBytesPerSec : null,
            has ? rate.UpBytesPerSec : null,
            Capacity(pick.ReceiveLinkBps, options.MaxNetworkMbps),
            Capacity(pick.TransmitLinkBps, options.MaxNetworkMbps),
            _sessionDown,
            _sessionUp,
            nowTicks);
    }

    private void Accumulate((long Down, long Up) d)
    {
        _sessionDown += d.Down;
        _sessionUp += d.Up;
    }
    private InterfaceRow? Busiest(List<InterfaceRow> candidates) =>
        candidates.FirstOrDefault(c => c.Key == _busiest.Leader)
        ?? candidates.Where(c => c.IsHardware).OrderBy(c => c.Key, StringComparer.Ordinal).FirstOrDefault()
        ?? candidates.OrderBy(c => c.Key, StringComparer.Ordinal).FirstOrDefault();
    private static NetworkSnapshot Aggregate(
        List<InterfaceRow> candidates, NetworkRateTracker rates, NetworkOptions options, long nowTicks, long sd, long su)
    {
        var hw = candidates.Where(c => c.IsHardware).ToList();
        double down = 0, up = 0, capDown = 0, capUp = 0;
        var any = false;
        foreach (var c in hw)
        {
            if (rates.Rates.TryGetValue(c.Key, out var r))
            {
                down += r.DownBytesPerSec;
                up += r.UpBytesPerSec;
                any = true;
            }
            capDown += Capacity(c.ReceiveLinkBps, 0);
            capUp += Capacity(c.TransmitLinkBps, 0);
        }
        if (options.MaxNetworkMbps > 0) capDown = capUp = options.MaxNetworkMbps * 1e6;
        if (capDown <= 0) capDown = DefaultCapacityBps;
        if (capUp <= 0) capUp = DefaultCapacityBps;

        return new NetworkSnapshot(any, "All adapters", any ? down : null, any ? up : null, capDown, capUp, sd, su, nowTicks);
    }
}
