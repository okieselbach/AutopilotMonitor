#nullable enable
using System;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Payload for <see cref="IDeadlineScheduler.Fired"/>. Plan §2.6.
    /// <para>
    /// The orchestrator consumes this event and translates it into a synthetic
    /// <c>DeadlineFired</c>-<see cref="AutopilotMonitor.DecisionCore.Signals.DecisionSignal"/>
    /// whose <c>OccurredAtUtc</c> is <see cref="FiredAtUtc"/> — the moment the agent actually
    /// decided — and whose payload carries the deadline's <see cref="ActiveDeadline.DueAtUtc"/>.
    /// A timer that was due during Modern Standby or a reboot fires only afterwards; stamping
    /// the due time instead would date the verdict into the outage.
    /// </para>
    /// </summary>
    public sealed class DeadlineFiredEventArgs : EventArgs
    {
        public DeadlineFiredEventArgs(ActiveDeadline deadline, DateTime firedAtUtc)
        {
            Deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));
            FiredAtUtc = firedAtUtc;
        }

        public ActiveDeadline Deadline { get; }

        /// <summary>Wall-clock time the timer fired; becomes the signal's OccurredAtUtc.</summary>
        public DateTime FiredAtUtc { get; }
    }
}
