using System;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Event args for <see cref="ShellCoreTracker.EspExited"/>. Carries the source-event
    /// timestamp (Shell-Core 62407 log time on live, <c>record.TimeCreated</c> on backfill)
    /// so subscribers can stamp downstream Decision-Signals with the historical UTC instead
    /// of collapsing to wall-clock-now.
    /// </summary>
    public sealed class EspExitedEventArgs : EventArgs
    {
        public EspExitedEventArgs(DateTime occurredAtUtc, bool isBackfill = false)
        {
            OccurredAtUtc = occurredAtUtc;
            IsBackfill = isBackfill;
        }

        public DateTime OccurredAtUtc { get; }

        /// <summary>
        /// True when this exit was replayed from the Shell-Core log rather than observed live.
        /// <para>
        /// Codex review P1 (2026-08-19): the distinction is load-bearing for the deferred
        /// user-apps-settled re-check. Shell-Core writes the SAME description
        /// (<c>CommercialOOBE_ESPProgress_Page_Exiting</c>) for the intermediate
        /// DeviceSetup→AccountSetup transition and for the final post-AccountSetup exit, so a
        /// replayed record carries no evidence of its own position. Ordering it against
        /// "does the registry show AccountSetup activity?" is invalid for a replay, because that
        /// state is read NOW and not at the event's time — an agent that was down across the
        /// Device→AccountSetup transition would confirm the stale intermediate exit. Only a LIVE
        /// exit lets that read double as an ordering fact, because the agent was continuously
        /// observing up to that instant.
        /// </para>
        /// </summary>
        public bool IsBackfill { get; }
    }
}
