namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Per-instance cap on concurrent work of one kind; each kind is its own singleton subclass. A
/// caller that finds no free slot gets null at once, without queueing, so the guarded work never
/// holds more than <see cref="Limit"/> slots of an instance, whatever the request rate. A gate
/// stays local to the endpoints that take its slots: D-203 refuses a process-wide semaphore in
/// front of storage I/O because it would queue the ingest behind portal scans.
/// </summary>
public abstract class ConcurrencyGate
{
    private readonly SemaphoreSlim _slots;
    private long _nextAlertTicks;

    protected ConcurrencyGate(int limit)
    {
        Limit = limit;
        _slots = new SemaphoreSlim(limit, limit);
    }

    public int Limit { get; }

    /// <summary>A slot to dispose when the work ends, or null when every slot is taken.</summary>
    public IDisposable? TryEnter() => _slots.Wait(0) ? new Slot(_slots) : null;

    /// <summary>
    /// True for the first refusal of an alert window, the one that raises the gate's ops event;
    /// later refusals in that window get false, so a full gate can never flood the alarm.
    /// </summary>
    internal bool TakeAlert(DateTime utcNow, TimeSpan window)
    {
        var next = Interlocked.Read(ref _nextAlertTicks);
        return utcNow.Ticks >= next
            && Interlocked.CompareExchange(ref _nextAlertTicks, utcNow.Add(window).Ticks, next) == next;
    }

    private sealed class Slot : IDisposable
    {
        private SemaphoreSlim? _slots;

        public Slot(SemaphoreSlim slots)
        {
            _slots = slots;
        }

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
