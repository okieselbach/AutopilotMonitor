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
        public EspExitedEventArgs(DateTime occurredAtUtc)
        {
            OccurredAtUtc = occurredAtUtc;
        }

        public DateTime OccurredAtUtc { get; }
    }
}
