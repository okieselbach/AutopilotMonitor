using System;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Event args for <see cref="ShellCoreTracker.HelloWizardStarted"/>. Carries the
    /// source-event timestamp (Shell-Core 62404 log time on live, <c>record.TimeCreated</c>
    /// on backfill) so subscribers can stamp the downstream
    /// <c>DecisionSignalKind.HelloWizardStarted</c> signal with the historical UTC instead
    /// of collapsing to wall-clock-now. Session 772fe502.
    /// </summary>
    public sealed class HelloWizardStartedEventArgs : EventArgs
    {
        public HelloWizardStartedEventArgs(DateTime occurredAtUtc)
        {
            OccurredAtUtc = occurredAtUtc;
        }

        public DateTime OccurredAtUtc { get; }
    }
}
