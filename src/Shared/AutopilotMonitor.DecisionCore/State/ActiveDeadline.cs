using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Immutable persisted deadline record. Plan §2.6.
    /// Timer-Monopol (L.7): only the Deadline-Scheduler (EffectRunner-role) holds
    /// decision-relevant timers. On restart, deadlines are re-hydrated from the
    /// persisted <see cref="DecisionState.Deadlines"/>; <c>remaining = DueAtUtc - clock.UtcNow</c>.
    /// </summary>
    public sealed class ActiveDeadline
    {
        public ActiveDeadline(
            string name,
            DateTime dueAtUtc,
            DecisionSignalKind firesSignalKind,
            IReadOnlyDictionary<string, string>? firesPayload = null)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Deadline Name is mandatory.", nameof(name));
            }

            Name = name;
            DueAtUtc = dueAtUtc;
            FiresSignalKind = firesSignalKind;
            FiresPayload = firesPayload;
        }

        /// <summary>Unique deadline name within a session (e.g. "hello_safety", "esp_failure_grace").</summary>
        public string Name { get; }

        public DateTime DueAtUtc { get; }

        public DecisionSignalKind FiresSignalKind { get; }

        public IReadOnlyDictionary<string, string>? FiresPayload { get; }
    }
}
