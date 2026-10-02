using System;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Thread-safe counters for update activity that is counted instead of emitted (D-310): Store,
    /// Defender and other non-OS update events, repeated scan results, servicing steps of non-OS
    /// packages. Shared by the update watchers of one host, which reports the totals once as
    /// <c>windows_update_activity_summary</c> when it stops.
    /// </summary>
    internal sealed class UpdateActivityTally
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public void Increment(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                _counts.TryGetValue(key, out var current);
                _counts[key] = current + 1;
            }
        }

        public bool IsEmpty
        {
            get { lock (_lock) return _counts.Count == 0; }
        }

        /// <summary>Counts ordered by key, for a stable event payload.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> Snapshot()
        {
            lock (_lock)
            {
                return _counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            }
        }
    }
}
