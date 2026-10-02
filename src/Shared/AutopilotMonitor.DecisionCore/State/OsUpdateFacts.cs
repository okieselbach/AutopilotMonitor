using System;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Windows/.NET update activity observed during the session (D-310), fed by
    /// <see cref="Signals.DecisionSignalKind.OsUpdateActivity"/> and the reboot signals. Drives the
    /// bounded OOBE update phase: Windows installs the OOBE quality update after the Device-ESP exit
    /// and before the user signs in, which can take longer than the <c>AdvisoryCompletion</c> window
    /// and ends with a restart to the lock screen.
    /// </summary>
    /// <remarks>
    /// Times follow the event time, not the arrival order: the watchers backfill their logs after an
    /// agent restart, so a record written before the update restart can arrive after it. The
    /// earliest-time facts (<see cref="FirstActivityUtc"/>, <see cref="PostExitActivityUtc"/>,
    /// <see cref="RebootRequiredUtc"/>) keep the earliest time, the latest-time facts
    /// (<see cref="LastActivityUtc"/>, <see cref="LastStep"/>, <see cref="LastUpdate"/>,
    /// <see cref="FailedUtc"/>, <see cref="RestartUtc"/>) change only for a newer one — otherwise a
    /// backfilled older step would read as the latest and the update would count as running again.
    /// Immutable; additive on <see cref="DecisionState"/> (Empty default), so older snapshots
    /// deserialize without a schema bump.
    /// </remarks>
    public sealed class OsUpdateFacts
    {
        public static readonly OsUpdateFacts Empty = new OsUpdateFacts(
            firstActivityUtc: null,
            postExitActivityUtc: null,
            lastActivityUtc: null,
            lastStep: null,
            lastUpdate: null,
            activityCount: 0,
            rebootRequiredUtc: null,
            failedUtc: null,
            restartUtc: null);

        public OsUpdateFacts(
            SignalFact<DateTime>? firstActivityUtc,
            SignalFact<DateTime>? postExitActivityUtc,
            SignalFact<DateTime>? lastActivityUtc,
            SignalFact<string>? lastStep,
            SignalFact<string>? lastUpdate,
            int activityCount,
            SignalFact<DateTime>? rebootRequiredUtc,
            SignalFact<DateTime>? failedUtc,
            SignalFact<DateTime>? restartUtc = null)
        {
            FirstActivityUtc = firstActivityUtc;
            PostExitActivityUtc = postExitActivityUtc;
            LastActivityUtc = lastActivityUtc;
            LastStep = lastStep;
            LastUpdate = lastUpdate;
            ActivityCount = activityCount;
            RebootRequiredUtc = rebootRequiredUtc;
            FailedUtc = failedUtc;
            RestartUtc = restartUtc;
        }

        /// <summary>Earliest OS update activity of the session, wherever it happened.</summary>
        public SignalFact<DateTime>? FirstActivityUtc { get; }

        /// <summary>
        /// Earliest OS update activity after the ESP exit while no real user had signed in — the
        /// anchor of the OOBE update phase and the start of its cap.
        /// </summary>
        public SignalFact<DateTime>? PostExitActivityUtc { get; }

        /// <summary>Latest OS update activity.</summary>
        public SignalFact<DateTime>? LastActivityUtc { get; }

        /// <summary>Step of the latest activity (e.g. <c>install_started</c>).</summary>
        public SignalFact<string>? LastStep { get; }

        /// <summary>Update title or KB of the latest activity that named one, bounded.</summary>
        public SignalFact<string>? LastUpdate { get; }

        public int ActivityCount { get; }

        /// <summary>Earliest <c>reboot_required</c> step (CBS staged the update for the restart).</summary>
        public SignalFact<DateTime>? RebootRequiredUtc { get; }

        /// <summary>Latest <c>failed</c> step.</summary>
        public SignalFact<DateTime>? FailedUtc { get; }

        /// <summary>
        /// Latest system restart observed after the phase anchor while no real user had signed in —
        /// the update's own restart (the OOBE update page ends with one).
        /// </summary>
        public SignalFact<DateTime>? RestartUtc { get; }

        /// <summary>
        /// Record one activity. <paramref name="isPostExit"/> marks it as part of the OOBE update
        /// phase (after the ESP exit, no real user yet).
        /// </summary>
        public OsUpdateFacts WithActivity(
            DateTime utc, long sourceSignalOrdinal, string step, string? update, bool isPostExit)
        {
            var at = new SignalFact<DateTime>(utc, sourceSignalOrdinal);
            var isNewest = LastActivityUtc == null || utc >= LastActivityUtc.Value;
            var boundedUpdate = FactStringBounds.Bound(update);

            return new OsUpdateFacts(
                firstActivityUtc: Earliest(FirstActivityUtc, at),
                postExitActivityUtc: isPostExit ? Earliest(PostExitActivityUtc, at) : PostExitActivityUtc,
                lastActivityUtc: isNewest ? at : LastActivityUtc,
                lastStep: isNewest
                    ? new SignalFact<string>(FactStringBounds.Bound(step) ?? string.Empty, sourceSignalOrdinal)
                    : LastStep,
                lastUpdate: isNewest && boundedUpdate != null
                    ? new SignalFact<string>(boundedUpdate, sourceSignalOrdinal)
                    : LastUpdate,
                activityCount: ActivityCount + 1,
                rebootRequiredUtc: string.Equals(step, OsUpdateSteps.RebootRequired, StringComparison.Ordinal)
                    ? Earliest(RebootRequiredUtc, at)
                    : RebootRequiredUtc,
                failedUtc: string.Equals(step, OsUpdateSteps.Failed, StringComparison.Ordinal)
                    ? Latest(FailedUtc, at)
                    : FailedUtc,
                restartUtc: RestartUtc);
        }

        /// <summary>Record a system restart that happened during the OOBE update phase.</summary>
        public OsUpdateFacts WithRestart(DateTime utc, long sourceSignalOrdinal) =>
            new OsUpdateFacts(
                firstActivityUtc: FirstActivityUtc,
                postExitActivityUtc: PostExitActivityUtc,
                lastActivityUtc: LastActivityUtc,
                lastStep: LastStep,
                lastUpdate: LastUpdate,
                activityCount: ActivityCount,
                rebootRequiredUtc: RebootRequiredUtc,
                failedUtc: FailedUtc,
                restartUtc: Latest(RestartUtc, new SignalFact<DateTime>(utc, sourceSignalOrdinal)));

        private static SignalFact<DateTime> Earliest(SignalFact<DateTime>? current, SignalFact<DateTime> candidate) =>
            current == null || candidate.Value < current.Value ? candidate : current;

        private static SignalFact<DateTime> Latest(SignalFact<DateTime>? current, SignalFact<DateTime> candidate) =>
            current == null || candidate.Value > current.Value ? candidate : current;
    }

    /// <summary>Step names carried in the <c>step</c> payload of <c>OsUpdateActivity</c>.</summary>
    public static class OsUpdateSteps
    {
        public const string DownloadStarted = "download_started";
        public const string Downloaded = "downloaded";
        public const string InstallStarted = "install_started";
        public const string StagingStarted = "staging_started";
        public const string Staged = "staged";
        public const string RebootRequired = "reboot_required";
        public const string Installed = "installed";
        public const string Failed = "failed";

        /// <summary>
        /// True for a step after which the update work is done: installed, failed, or installed
        /// pending the restart. Any other step means the update is still running.
        /// </summary>
        public static bool IsTerminal(string? step) =>
            string.Equals(step, Installed, StringComparison.Ordinal)
            || string.Equals(step, Failed, StringComparison.Ordinal)
            || string.Equals(step, RebootRequired, StringComparison.Ordinal);
    }

    /// <summary>Payload keys of <c>OsUpdateActivity</c>.</summary>
    public static class OsUpdatePayloadKeys
    {
        public const string Step = "step";
        /// <summary><c>wu</c> (Windows Update client) or <c>servicing</c> (CBS, Setup log).</summary>
        public const string Source = "source";
        /// <summary>Update title (WU) or package identifier (CBS).</summary>
        public const string Update = "update";
    }

    /// <summary>
    /// Modes of the OOBE update phase (D-310), from the remote config field
    /// <c>OobeUpdatePhaseMode</c>. <see cref="Off"/>: the update watchers feed nothing into the
    /// engine. <see cref="Shadow"/>: they do, the engine records the phase and reports at the
    /// completion window what the phase would decide — today's rule still decides.
    /// <see cref="Active"/>: the phase holds, stops or fails.
    /// </summary>
    public static class OobeUpdatePhaseModes
    {
        public const string Off = "Off";
        public const string Shadow = "Shadow";
        public const string Active = "Active";

        /// <summary>
        /// One of the three modes, case-insensitive. Anything else — empty, unknown, a value from
        /// a newer backend — is <see cref="Shadow"/>: it changes no decision and still measures.
        /// </summary>
        public static string Normalize(string? mode)
        {
            if (string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase)) return Off;
            if (string.Equals(mode, Active, StringComparison.OrdinalIgnoreCase)) return Active;
            return Shadow;
        }

        /// <summary>True when the update watchers post <c>OsUpdateActivity</c> to the engine.</summary>
        public static bool PostsSignals(string? mode) => Normalize(mode) != Off;
    }

    /// <summary>Bounds of the OOBE update phase (D-310, user decision 2026-10-02).</summary>
    public static class OobeUpdatePhaseBounds
    {
        /// <summary>Longest the update may run (from its first activity) before the session fails as not finished.</summary>
        public const int UpdateCapMinutes = 180;

        /// <summary>Sign-in window after the update ended (its restart or terminal step).</summary>
        public const int SignInWindowMinutes = 60;
    }

    /// <summary>Reason codes of <see cref="DecisionState.StoppedWaitingReason"/>.</summary>
    public static class StoppedWaitingReasons
    {
        /// <summary>Nobody signed in within the sign-in window after the OOBE update (D-310).</summary>
        public const string OobeUpdateNoSignIn = "oobe_update_no_sign_in";

        /// <summary>One sentence for the session's end reason (final status, shutdown event).</summary>
        public static string Describe(string? reason) =>
            string.Equals(reason, OobeUpdateNoSignIn, StringComparison.Ordinal)
                ? $"Nobody signed in within {OobeUpdatePhaseBounds.SignInWindowMinutes} minutes after the Windows update during setup; the agent stopped waiting."
                : "The agent stopped waiting for the user without a verdict.";
    }
}
