using System.Diagnostics;
using BetterHardwareStats.Core.Model;

namespace BetterHardwareStats.Core.Service;

public sealed record WorkerOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMilliseconds(2000);
    public int MaxConsecutiveFailures { get; init; } = 5;
}
public sealed record Stamped<T>(T Value, long TimestampTicks) where T : class;
public sealed class PollingWorker<T> : IDisposable where T : class
{
    private readonly Func<string?>? _init;
    private readonly Func<T> _sample;
    private readonly Action? _cleanup;
    private readonly Action<string>? _log;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private readonly object _lifecycle = new();
    private bool _disposed;
    private bool _finished;
    private Stamped<T>? _latest;
    private long _callStart; // 0 when not inside a call
    private volatile BackendState _state = BackendState.NotStarted;
    private volatile string? _detail;
    private int _failures;
    private double _lastPassMs;
    public PollingWorker(string name, Func<T> sample, WorkerOptions? options = null, Func<string?>? init = null, Action? cleanup = null, Action<string>? log = null)
    {
        Name = name;
        _sample = sample;
        _init = init;
        _cleanup = cleanup;
        _log = log;
        Options = options ?? new WorkerOptions();
    }

    public string Name { get; }
    private WorkerOptions _options = new();
    public WorkerOptions Options { get => Volatile.Read(ref _options); set => Volatile.Write(ref _options, value); }
    public BackendState State => _state;
    public Stamped<T>? Latest => Volatile.Read(ref _latest);
    public event Action<Stamped<T>>? Sampled;

    public BackendStatus Status => new(Name, _state, _detail, Volatile.Read(ref _failures), Volatile.Read(ref _lastPassMs));

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "betterhardwarestats-" + Name };
            _thread.Start();
        }
    }
    public void Check(long nowTicks)
    {
        var start = Interlocked.Read(ref _callStart);
        if (start != 0 && _state == BackendState.Running
            && nowTicks - start > Options.CallTimeout.TotalSeconds * Stopwatch.Frequency)
        {
            _state = BackendState.Degraded;
            _detail = $"call running longer than {Options.CallTimeout.TotalMilliseconds:F0} ms";
            _log?.Invoke($"{Name}: {_detail}; marking Degraded");
        }
    }

    private void Run()
    {
        try
        {
            if (_init is not null)
            {
                string? reason;
                try { reason = _init(); }
                catch (Exception e) { reason = $"{e.GetType().Name}: {e.Message}"; }
                if (reason is not null)
                {
                    _detail = reason;
                    _state = BackendState.Disabled;
                    return;
                }
            }
            _state = BackendState.Running;

            while (!_stop.IsSet)
            {
                var t0 = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _callStart, t0);
                try
                {
                    var value = _sample();
                    var t1 = Stopwatch.GetTimestamp();
                    var stamped = new Stamped<T>(value, t1);
                    Volatile.Write(ref _latest, stamped);
                    Volatile.Write(ref _failures, 0);
                    if (_state == BackendState.Degraded) { _state = BackendState.Running; _detail = null; }
                    Sampled?.Invoke(stamped);
                }
                catch (Exception e)
                {
                    var n = Interlocked.Increment(ref _failures);
                    _detail = $"{e.GetType().Name}: {e.Message}";
                    if (n >= Options.MaxConsecutiveFailures)
                    {
                        _state = BackendState.Disabled;
                        _log?.Invoke($"{Name}: disabled after {n} consecutive failures; last: {_detail}");
                        break;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _callStart, 0);
                    Volatile.Write(ref _lastPassMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                }

                var elapsed = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - t0) / Stopwatch.Frequency);
                var wait = Options.Interval - elapsed;
                _stop.Wait(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(10));
            }
        }
        catch (Exception e)
        {
            _state = BackendState.Disabled;
            _detail = $"worker loop failed: {e.Message}";
        }
        finally
        {
            try { _cleanup?.Invoke(); }
            catch (Exception e) { _log?.Invoke($"{Name}: cleanup failed: {e.Message}"); }
            finally
            {
                lock (_lifecycle)
                {
                    _finished = true;
                    if (_disposed) _stop.Dispose();
                }
            }
        }
    }
    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            thread = _thread;
            if (thread is null || _finished) _stop.Dispose();
        }
        if (thread is { IsAlive: true } && thread != Thread.CurrentThread && !thread.Join(TimeSpan.FromSeconds(3)))
            _log?.Invoke($"{Name}: worker did not stop within 3 s; cleanup will run when the native call returns");
    }
}
public sealed class LogLimiter
{
    private readonly Action<string> _sink;
    private readonly long _intervalTicks;
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public LogLimiter(Action<string> sink, TimeSpan? interval = null, long? ticksPerSecond = null)
    {
        _sink = sink;
        _intervalTicks = (long)((interval ?? TimeSpan.FromMinutes(1)).TotalSeconds * (ticksPerSecond ?? Stopwatch.Frequency));
    }
    public bool Log(string message, long? nowTicks = null)
    {
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_last.TryGetValue(message, out var t) && now - t < _intervalTicks) return false;
            _last[message] = now;
            if (_last.Count > 1000)
                foreach (var k in _last.Where(kv => now - kv.Value >= _intervalTicks).Select(kv => kv.Key).ToList()) _last.Remove(k);
        }
        _sink(message);
        return true;
    }
}
