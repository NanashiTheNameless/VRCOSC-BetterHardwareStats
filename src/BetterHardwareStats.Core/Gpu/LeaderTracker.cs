namespace BetterHardwareStats.Core.Gpu;
public sealed class LeaderTracker
{
    private readonly long _windowTicks;
    private readonly int _confirmWindows;
    private readonly double _threshold;
    private readonly Dictionary<string, (double Sum, int Count)> _acc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastMeans = new(StringComparer.Ordinal);
    private long _windowStart = long.MinValue;
    private string? _pending;
    private bool _pendingIsNone;
    private int _pendingCount;
    public LeaderTracker(long windowTicks, int confirmWindows = 3, double threshold = 0)
    {
        if (windowTicks <= 0) throw new ArgumentOutOfRangeException(nameof(windowTicks));
        if (confirmWindows < 1) throw new ArgumentOutOfRangeException(nameof(confirmWindows));
        _windowTicks = windowTicks;
        _confirmWindows = confirmWindows;
        _threshold = threshold;
    }

    public string? Leader { get; private set; }
    public IReadOnlyDictionary<string, double> LastWindowMeans => _lastMeans;
    public bool Add(string key, double value, long nowTicks)
    {
        var changed = Advance(nowTicks);
        if (double.IsFinite(value))
        {
            _acc.TryGetValue(key, out var a);
            _acc[key] = (a.Sum + value, a.Count + 1);
        }
        return changed;
    }
    public bool Advance(long nowTicks)
    {
        if (_windowStart == long.MinValue)
        {
            _windowStart = nowTicks;
            return false;
        }
        if (nowTicks - _windowStart < _windowTicks) return false;

        var changed = CloseWindow();
        // Skip whole empty windows after a long pause rather than evaluating each one.
        _windowStart = nowTicks - (nowTicks - _windowStart) % _windowTicks;
        return changed;
    }
    public bool Retain(IReadOnlyCollection<string> liveKeys)
    {
        foreach (var k in _acc.Keys.Where(k => !liveKeys.Contains(k)).ToList()) _acc.Remove(k);
        if (_pending is not null && !liveKeys.Contains(_pending)) ResetPending();
        if (Leader is not null && !liveKeys.Contains(Leader))
        {
            Leader = null;
            return true;
        }
        return false;
    }

    public void Reset()
    {
        _acc.Clear();
        _lastMeans.Clear();
        _windowStart = long.MinValue;
        Leader = null;
        ResetPending();
    }

    private bool CloseWindow()
    {
        _lastMeans.Clear();
        foreach (var (k, a) in _acc)
            if (a.Count > 0) _lastMeans[k] = a.Sum / a.Count;
        _acc.Clear();

        var candidate = Candidate();
        if (candidate == Leader)
        {
            ResetPending();
            return false;
        }
        if (Leader is null)
        {
            Leader = candidate;
            ResetPending();
            return true;
        }

        var candidateIsNone = candidate is null;
        if (_pendingCount > 0 && _pendingIsNone == candidateIsNone && _pending == candidate) _pendingCount++;
        else
        {
            _pending = candidate;
            _pendingIsNone = candidateIsNone;
            _pendingCount = 1;
        }

        if (_pendingCount < _confirmWindows) return false;
        Leader = candidate;
        ResetPending();
        return true;
    }

    private string? Candidate()
    {
        string? best = null;
        var bestMean = double.NegativeInfinity;
        foreach (var (k, mean) in _lastMeans)
        {
            if (!(mean > _threshold)) continue;
            var better = mean > bestMean
                || (mean == bestMean && (k == Leader || (best != Leader && string.CompareOrdinal(k, best) < 0)));
            if (better)
            {
                best = k;
                bestMean = mean;
            }
        }
        return best;
    }

    private void ResetPending()
    {
        _pending = null;
        _pendingIsNone = false;
        _pendingCount = 0;
    }
}
