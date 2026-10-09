#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Reasons for which <see cref="EnrollmentOrchestrator.Terminated"/> fires. Plan §4.x M4.6.α.
    /// </summary>
    public enum EnrollmentTerminationReason
    {
        /// <summary>DecisionEngine reached a terminal stage via real signals (success or failure path).</summary>
        DecisionTerminalStage,

        /// <summary>
        /// <c>AgentMaxLifetimeMinutes</c> safety watchdog fired — orchestrator has been running
        /// longer than the configured cap without reaching a terminal stage.
        /// </summary>
        MaxLifetimeExceeded,

        /// <summary>
        /// The DecisionEngine stopped waiting for the user without a verdict — a bounded wait
        /// expired (<c>DecisionState.StoppedWaitingReason</c>, D-310). Ends the session like
        /// <see cref="MaxLifetimeExceeded"/> (outcome TimedOut), with the engine's reason.
        /// </summary>
        StoppedWaiting,
    }

    /// <summary>
    /// Outcome descriptor emitted alongside <see cref="EnrollmentOrchestrator.Terminated"/>.
    /// <para>
    /// M4.6.β will consume this to drive <c>CleanupService.ExecuteSelfDestruct</c>,
    /// <c>SummaryDialog</c> launch and <c>DiagnosticsPackageService</c> upload — the orchestrator
    /// stays kernel-pure and only declares "we are done, here is the outcome".
    /// </para>
    /// </summary>
    public enum EnrollmentTerminationOutcome
    {
        /// <summary>
        /// Reached a success stage (<c>Completed</c> / <c>WhiteGloveSealed</c> for Part 1 exit).
        /// </summary>
        Succeeded,

        /// <summary>Reached an explicit failure stage.</summary>
        Failed,

        /// <summary>
        /// Not yet terminal (used when the watchdog fires mid-flight — no classifier verdict,
        /// stage is still an in-progress state).
        /// </summary>
        TimedOut,
    }

    /// <summary>
    /// Payload for <see cref="EnrollmentOrchestrator.Terminated"/>. Plan §4.x M4.6.α.
    /// <para>
    /// Carries the decision-relevant fields needed by downstream peripheral capabilities
    /// (M4.6.β: CleanupService + SummaryDialog + DiagnosticsPackageService) without forcing
    /// them to touch the DecisionEngine state directly — that coupling would leak kernel
    /// invariants into peripheral code.
    /// </para>
    /// </summary>
    public sealed class EnrollmentTerminatedEventArgs : EventArgs
    {
        public EnrollmentTerminationReason Reason { get; }
        public EnrollmentTerminationOutcome Outcome { get; }
        public string? StageName { get; }
        public DateTime TerminatedAtUtc { get; }
        public string? Details { get; }

        /// <summary>
        /// The engine's reason code for <see cref="EnrollmentTerminationReason.StoppedWaiting"/>
        /// (e.g. <c>oobe_update_no_sign_in</c>); null for every other reason.
        /// </summary>
        public string? StopReason { get; }

        /// <summary>
        /// The <c>origin</c> param of a server-requested <c>terminate_session</c> action
        /// (<see cref="TerminationOrigins"/>); null for every termination the agent decided
        /// itself. <see cref="TerminationOrigins.SessionGone"/> makes the termination handler
        /// skip the spool drain and the diagnostics upload — the backend no longer knows the
        /// session, so both could only fail.
        /// </summary>
        public string? Origin { get; }

        public EnrollmentTerminatedEventArgs(
            EnrollmentTerminationReason reason,
            EnrollmentTerminationOutcome outcome,
            string? stageName,
            DateTime terminatedAtUtc,
            string? details = null,
            string? stopReason = null,
            string? origin = null)
        {
            Reason = reason;
            Outcome = outcome;
            StageName = stageName;
            TerminatedAtUtc = terminatedAtUtc;
            Details = details;
            StopReason = stopReason;
            Origin = origin;
        }
    }

    /// <summary>
    /// Values of the <c>origin</c> param on a synthesised <c>terminate_session</c>
    /// <c>ServerAction</c>, carried into <see cref="EnrollmentTerminatedEventArgs.Origin"/>.
    /// </summary>
    public static class TerminationOrigins
    {
        /// <summary>Administrator kill signal (<c>DeviceKillSignal</c> on a 2xx telemetry response).</summary>
        public const string KillSignal = "kill_signal";

        /// <summary>The backend answered HTTP 410 for the current session's telemetry.</summary>
        public const string SessionGone = "session_gone";
    }
}
