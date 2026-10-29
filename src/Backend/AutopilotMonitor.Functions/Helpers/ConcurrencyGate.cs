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

    protected ConcurrencyGate(int limit)
    {
        Limit = limit;
        _slots = new SemaphoreSlim(limit, limit);
    }

    public int Limit { get; }

    /// <summary>A slot to dispose when the work ends, or null when every slot is taken.</summary>
    public IDisposable? TryEnter() => _slots.Wait(0) ? new Slot(_slots) : null;

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
