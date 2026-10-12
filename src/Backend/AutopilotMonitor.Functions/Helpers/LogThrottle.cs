using System.Collections.Concurrent;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Lets one log line per key through per interval, for request paths that would otherwise repeat it on every
/// call (worker traces reach App Insights at Warning+ only, so a polled or hot path must not flood them).
/// Process-local and bounded: past <c>maxKeys</c> the memory is dropped, which at worst repeats a line early.
/// </summary>
public sealed class LogThrottle
{
    private readonly ConcurrentDictionary<string, DateTime> _lastLogged = new();
    private readonly TimeSpan _interval;
    private readonly int _maxKeys;

    public LogThrottle(TimeSpan interval, int maxKeys = 1000)
    {
        _interval = interval;
        _maxKeys = maxKeys;
    }

    /// <summary>True when the line for <paramref name="key"/> may be written now; records that it was.</summary>
    public bool ShouldLog(string key) => ShouldLog(key, DateTime.UtcNow);

    internal bool ShouldLog(string key, DateTime nowUtc)
    {
        if (_lastLogged.TryGetValue(key, out var last) && nowUtc - last < _interval)
            return false;
        if (_lastLogged.Count >= _maxKeys)
            _lastLogged.Clear();
        _lastLogged[key] = nowUtc;
        return true;
    }
}
