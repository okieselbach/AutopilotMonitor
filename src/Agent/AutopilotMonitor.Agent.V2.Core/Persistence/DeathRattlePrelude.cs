#nullable enable
using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// Death-Rattle prelude (Plan §B — Edge-Triggered State Snapshots, 2026-05-03).
    /// Captures the prior agent run's last persisted <see cref="DecisionState"/> BEFORE the
    /// new run's <c>EnrollmentOrchestrator.Start()</c> runs the recovery pipeline. Returns
    /// <c>null</c> when the previous exit was clean, when this is a planned WhiteGlove
    /// Part-2 resume, when the snapshot is missing or corrupt, or when any I/O error
    /// occurs — the caller emits the <c>prior_run_died_with_state</c> event only when
    /// this returns non-null.
    /// <para>
    /// Extracted from <c>AgentRuntimeHost</c> so the gate logic + ordering contract are
    /// directly testable. Three regress classes the host wiring is fragile against:
    /// </para>
    /// <list type="number">
    /// <item><c>TryReadRaw</c> moves to after <c>orchestrator.Start()</c> → snapshot
    /// either overwritten by the first reducer save or quarantined → we read the wrong
    /// state. Pin via the "no-mutation" contract test.</item>
    /// <item>WhiteGlove Part-2 resume no longer skipped → false-positive death-rattle
    /// for a planned exit. Pin via the WG-skip test.</item>
    /// <item><c>clean</c> / <c>first_run</c> exits start emitting → telemetry noise on
    /// every successful previous run. Pin via the clean-exit-table test.</item>
    /// </list>
    /// </summary>
    public static class DeathRattlePrelude
    {
        // The orchestrator hardcodes this exact filename inside _stateDirectory
        // (EnrollmentOrchestrator.cs:309 — `Path.Combine(_stateDirectory, "snapshot.json")`).
        // Any rename on either side without updating both is a bit-level path-layout drift
        // that DeathRattlePreludeTests.Reads_from_path_layout_matching_orchestrator catches.
        internal const string SnapshotFileName = "snapshot.json";

        /// <summary>
        /// Returns the prior run's persisted snapshot if all gates pass, else <c>null</c>.
        /// </summary>
        /// <param name="stateDirectory">
        /// Same directory the orchestrator's <c>SnapshotPersistence</c> reads from / writes
        /// to. Must be non-empty.
        /// </param>
        /// <param name="previousExitType">
        /// One of <c>first_run</c> / <c>clean</c> / <c>exception_crash</c> / <c>hard_kill</c>
        /// / <c>reboot_kill</c> as classified by <c>Program.DetectPreviousExit</c>.
        /// Case-insensitive on principle.
        /// </param>
        /// <param name="isWhiteGloveResume">
        /// <c>true</c> when this run is the Part-2 continuation of a sealed WhiteGlove flow.
        /// The "death" of the Part-1 run is a planned reboot, not an unclean exit, so no
        /// attestation is warranted.
        /// </param>
        /// <param name="logger">Mandatory — used for telemetry-friendly Info / Debug / Warning lines.</param>
        public static DecisionState? TryCapture(
            string stateDirectory,
            string? previousExitType,
            bool isWhiteGloveResume,
            AgentLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrEmpty(stateDirectory))
                throw new ArgumentException("stateDirectory is mandatory.", nameof(stateDirectory));

            if (isWhiteGloveResume) return null;
            if (!IsUncleanExit(previousExitType)) return null;

            try
            {
                var snapshotPath = Path.Combine(stateDirectory, SnapshotFileName);
                var prior = SnapshotPersistence.TryReadRaw(snapshotPath);
                if (prior != null)
                {
                    logger.Info(
                        $"Death-rattle: prior snapshot loaded (Stage={prior.Stage}, " +
                        $"StepIndex={prior.StepIndex}, exitType={previousExitType}).");
                }
                else
                {
                    logger.Debug(
                        $"Death-rattle: no prior snapshot to attest (path missing or unreadable, exitType={previousExitType}).");
                }
                return prior;
            }
            catch (Exception ex)
            {
                logger.Warning($"Death-rattle: prior snapshot read failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gate: which previous-exit classifications warrant a death-rattle. The set is
        /// intentionally small — <c>first_run</c> / <c>clean</c> are planned exits with no
        /// state worth re-attesting. Case-insensitive on principle to be robust against any
        /// future capitalization drift in the producer.
        /// </summary>
        public static bool IsUncleanExit(string? exitType)
        {
            return exitType != null
                && (StringComparer.OrdinalIgnoreCase.Equals(exitType, "reboot_kill")
                    || StringComparer.OrdinalIgnoreCase.Equals(exitType, "hard_kill")
                    || StringComparer.OrdinalIgnoreCase.Equals(exitType, "exception_crash"));
        }
    }
}
