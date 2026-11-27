using System.Collections.Concurrent;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Lets exactly one caller per key through per window, also under concurrency — for notifications and
/// ops events that must not repeat. <c>AddOrUpdate</c> claims the slot atomically, and the caller whose
/// claim object ends up stored wins: compared by reference, never by time, so two callers that read the
/// same clock value cannot both win (a timestamp comparison let them, most often where the clock has
/// microsecond resolution). Keys are case-insensitive. Per process; state resets on restart.
/// </summary>
internal sealed class OncePerWindowGate
{
    private readonly ConcurrentDictionary<string, Claim> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNow;

    public OncePerWindowGate(TimeSpan window, Func<DateTime>? utcNow = null)
    {
        _window = window;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>True for the one caller that claims the slot of <paramref name="key"/> now; false while an earlier claim is inside the window.</summary>
    public bool TryClaim(string key)
    {
        var claim = new Claim(_utcNow());
        var stored = _claims.AddOrUpdate(
            key,
            addValueFactory: _ => claim,
            updateValueFactory: (_, existing) => claim.At - existing.At >= _window ? claim : existing);
        return ReferenceEquals(stored, claim);
    }

    /// <summary>One caller's claim on a slot; compared by reference, never by time.</summary>
    private sealed class Claim
    {
        public Claim(DateTime at) => At = at;

        public DateTime At { get; }
    }
}
