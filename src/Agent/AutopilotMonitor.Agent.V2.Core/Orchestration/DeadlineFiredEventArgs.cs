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
    /// whose <c>OccurredAtUtc</c> equals the deadline's <see cref="ActiveDeadline.DueAtUtc"/>
    /// (the policy instant the wait expired), with the due time repeated in the payload. The
    /// firing clock is deliberately NOT the stamp: a timer due during Modern Standby or a
    /// reboot fires only afterwards, and the outage would otherwise become session duration
    /// (D-238).
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

        /// <summary>Wall-clock time the event was raised. For observability; does NOT influence the signal's OccurredAtUtc.</summary>
        public DateTime FiredAtUtc { get; }
    }
}
