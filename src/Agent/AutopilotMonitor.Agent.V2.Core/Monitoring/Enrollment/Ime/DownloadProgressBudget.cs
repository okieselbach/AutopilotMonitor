using System.Threading;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Session-wide count of emitted <c>download_progress</c> events and the throttle stage reached.
    /// Owned by the tracker so it is saved with the tracker state: the event limit counts the whole
    /// session, and one enrollment spans agent restarts and both WhiteGlove parts. Written by the
    /// download-progress publisher from the DO poll thread and the tracker thread, read by the
    /// tracker's state save — hence the lock.
    /// </summary>
    internal sealed class DownloadProgressBudget
    {
        private readonly object _lock = new object();
        private int _eventsSent;
        private int _stage;
        private int _dirty;

        public int EventsSent { get { lock (_lock) return _eventsSent; } }

        public int Stage { get { lock (_lock) return _stage; } }

        /// <summary>Counts one emitted event and returns the new session total.</summary>
        public int RecordEvent()
        {
            int total;
            lock (_lock) total = ++_eventsSent;
            Interlocked.Exchange(ref _dirty, 1);
            return total;
        }

        /// <summary>Raises the stage. False when <paramref name="stage"/> is not above the current one.</summary>
        public bool TryAdvanceStage(int stage)
        {
            lock (_lock)
            {
                if (stage <= _stage) return false;
                _stage = stage;
            }
            Interlocked.Exchange(ref _dirty, 1);
            return true;
        }

        /// <summary>True once per change since the last call; the tracker then saves its state.</summary>
        public bool ConsumeDirty() => Interlocked.Exchange(ref _dirty, 0) == 1;

        public void Restore(DownloadProgressBudgetState state)
        {
            if (state == null) return;
            lock (_lock)
            {
                _eventsSent = state.EventsSent > 0 ? state.EventsSent : 0;
                _stage = state.Stage > 0 ? state.Stage : 0;
            }
        }

        public DownloadProgressBudgetState ToPersisted()
        {
            lock (_lock) return new DownloadProgressBudgetState { EventsSent = _eventsSent, Stage = _stage };
        }
    }
}
