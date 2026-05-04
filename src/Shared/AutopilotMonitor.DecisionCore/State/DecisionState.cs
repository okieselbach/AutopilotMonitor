using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Immutable snapshot of the engine-visible session state. Plan §2.3.
    /// <para>
    /// <b>Invariant</b>: all DTOs in DecisionCore are immutable value objects.
    /// "Change" is a new instance via <c>With…</c>-methods — no in-place mutation.
    /// Reducer contract: <c>(newState, transition, effects) = engine.Reduce(oldState, signal)</c>.
    /// </para>
    /// <para>
    /// Codex follow-up #5 — the legacy 9-field hypothesis/fact mosaic
    /// (<c>EnrollmentType</c>, <c>WhiteGloveSealing</c>,
    /// <c>DeviceOnlyDeployment</c>, <c>SkipUserEsp</c>, <c>SkipDeviceEsp</c>,
    /// <c>AadJoinedWithUser</c>, <c>ShellCoreWhiteGloveSuccessSeen</c>,
    /// <c>WhiteGloveSealingPatternSeen</c>) has been replaced by three structured aggregates:
    /// <list type="bullet">
    ///   <item><see cref="ScenarioProfile"/> — the derived semantic classification.</item>
    ///   <item><see cref="ScenarioObservations"/> — raw per-signal observations feeding the classifier.</item>
    ///   <item><see cref="ClassifierOutcomes"/> — classifier verdicts + anti-loop state.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Agent-process lifecycle flags (crash, admin actions, boot-time, heartbeat) live
    /// in <c>agent-lifecycle.json</c>, not here (L.11 separation).
    /// </para>
    /// </summary>
    public sealed class DecisionState
    {
        public const string CurrentSchemaVersion = "v2";

        public DecisionState(
            string sessionId,
            string tenantId,
            SessionStage stage,
            SessionOutcome? outcome,
            SignalFact<EnrollmentPhase>? currentEnrollmentPhase,
            SignalFact<DateTime>? deviceSetupEnteredUtc,
            SignalFact<DateTime>? accountSetupEnteredUtc,
            SignalFact<DateTime>? finalizingEnteredUtc,
            SignalFact<DateTime>? espFinalExitUtc,
            SignalFact<DateTime>? desktopArrivedUtc,
            SignalFact<DateTime>? helloResolvedUtc,
            SignalFact<DateTime>? systemRebootUtc,
            SignalFact<string>? helloOutcome,
            SignalFact<string>? imeMatchedPatternId,
            IReadOnlyList<ActiveDeadline> deadlines,
            long lastAppliedSignalOrdinal,
            int stepIndex,
            AppInstallFacts? appInstallFacts = null,
            EnrollmentScenarioProfile? scenarioProfile = null,
            EnrollmentScenarioObservations? scenarioObservations = null,
            ClassifierOutcomes? classifierOutcomes = null,
            SignalFact<bool>? helloPolicyEnabled = null,
            string? schemaVersion = null)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("SessionId is mandatory.", nameof(sessionId));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));
            }

            SessionId = sessionId;
            TenantId = tenantId;
            Stage = stage;
            Outcome = outcome;
            CurrentEnrollmentPhase = currentEnrollmentPhase;
            DeviceSetupEnteredUtc = deviceSetupEnteredUtc;
            AccountSetupEnteredUtc = accountSetupEnteredUtc;
            FinalizingEnteredUtc = finalizingEnteredUtc;
            EspFinalExitUtc = espFinalExitUtc;
            DesktopArrivedUtc = desktopArrivedUtc;
            HelloResolvedUtc = helloResolvedUtc;
            SystemRebootUtc = systemRebootUtc;
            HelloOutcome = helloOutcome;
            ImeMatchedPatternId = imeMatchedPatternId;
            Deadlines = deadlines ?? throw new ArgumentNullException(nameof(deadlines));
            LastAppliedSignalOrdinal = lastAppliedSignalOrdinal;
            StepIndex = stepIndex;
            AppInstallFacts = appInstallFacts ?? AppInstallFacts.Empty;
            ScenarioProfile = scenarioProfile ?? EnrollmentScenarioProfile.Empty;
            ScenarioObservations = scenarioObservations ?? EnrollmentScenarioObservations.Empty;
            ClassifierOutcomes = classifierOutcomes ?? ClassifierOutcomes.Empty;
            HelloPolicyEnabled = helloPolicyEnabled;
            SchemaVersion = schemaVersion ?? CurrentSchemaVersion;
        }

        public string SessionId { get; }

        public string TenantId { get; }

        /// <summary>Engine-stage — what the reducer is currently waiting on.</summary>
        public SessionStage Stage { get; }

        /// <summary>Null when the session is non-terminal.</summary>
        public SessionOutcome? Outcome { get; }

        // --- Enrollment-Phase (end-user reality, separate from Stage) ---
        public SignalFact<EnrollmentPhase>? CurrentEnrollmentPhase { get; }

        public SignalFact<DateTime>? DeviceSetupEnteredUtc { get; }

        public SignalFact<DateTime>? AccountSetupEnteredUtc { get; }

        public SignalFact<DateTime>? FinalizingEnteredUtc { get; }

        // --- Signal-induced facts (with source ordinal for evidence trace) ---
        public SignalFact<DateTime>? EspFinalExitUtc { get; }

        public SignalFact<DateTime>? DesktopArrivedUtc { get; }

        public SignalFact<DateTime>? HelloResolvedUtc { get; }

        public SignalFact<DateTime>? SystemRebootUtc { get; }

        public SignalFact<string>? HelloOutcome { get; }

        public SignalFact<string>? ImeMatchedPatternId { get; }

        public IReadOnlyList<ActiveDeadline> Deadlines { get; }

        public long LastAppliedSignalOrdinal { get; }

        public int StepIndex { get; }

        /// <summary>
        /// Rolled-up terminal outcomes from <see cref="Signals.DecisionSignalKind.AppInstallCompleted"/>
        /// and <see cref="Signals.DecisionSignalKind.AppInstallFailed"/> signals — Codex
        /// follow-up #4. Never null; defaults to <see cref="State.AppInstallFacts.Empty"/>.
        /// </summary>
        public AppInstallFacts AppInstallFacts { get; }

        /// <summary>
        /// Typed enrollment-scenario classification (Mode / JoinMode / EspConfig /
        /// PreProvisioningSide / Confidence). Codex follow-up #5. Never null; defaults to
        /// <see cref="EnrollmentScenarioProfile.Empty"/>.
        /// </summary>
        public EnrollmentScenarioProfile ScenarioProfile { get; }

        /// <summary>
        /// Raw per-signal observations feeding the WhiteGlove sealing classifier and downstream
        /// guards. Codex follow-up #5. Never null; defaults to
        /// <see cref="EnrollmentScenarioObservations.Empty"/>.
        /// </summary>
        public EnrollmentScenarioObservations ScenarioObservations { get; }

        /// <summary>
        /// Classifier verdict storage + anti-loop state (WhiteGlove sealing,
        /// device-only deployment). Codex follow-up #5. Never null; defaults to
        /// <see cref="ClassifierOutcomes.Empty"/>.
        /// </summary>
        public ClassifierOutcomes ClassifierOutcomes { get; }

        /// <summary>
        /// WHfB / Hello-for-Business policy fact, set once when the agent first observes the
        /// CSP/GPO state. Null when the policy hasn't been detected yet — the engine treats
        /// null as unknown and keeps the default wait cadence. PR4 (882fef64 debrief).
        /// </summary>
        /// <remarks>
        /// This fact drives the post-ESP-exit Hello wait cadence (30s default vs 10s when
        /// policy is explicitly disabled), NOT the enrollment-completion gate. Completion is
        /// orthogonal to Hello — see <c>feedback_hello_policy_wait_not_completion</c>.
        /// </remarks>
        public SignalFact<bool>? HelloPolicyEnabled { get; }

        public string SchemaVersion { get; }

        /// <summary>
        /// Produce a mutable builder pre-populated with this state's values.
        /// Reducer handlers call <c>state.ToBuilder().WithStage(...).Build()</c> to
        /// express immutable "copy with changes" ergonomically (plan §2.3 / L.3).
        /// </summary>
        public DecisionStateBuilder ToBuilder() => new DecisionStateBuilder(this);

        /// <summary>
        /// Construct the initial non-terminal state for a new session.
        /// Used by the reducer's <c>SessionStarted</c> handler in M3.
        /// </summary>
        public static DecisionState CreateInitial(string sessionId, string tenantId) =>
            new DecisionState(
                sessionId: sessionId,
                tenantId: tenantId,
                stage: SessionStage.SessionStarted,
                outcome: null,
                currentEnrollmentPhase: null,
                deviceSetupEnteredUtc: null,
                accountSetupEnteredUtc: null,
                finalizingEnteredUtc: null,
                espFinalExitUtc: null,
                desktopArrivedUtc: null,
                helloResolvedUtc: null,
                systemRebootUtc: null,
                helloOutcome: null,
                imeMatchedPatternId: null,
                deadlines: Array.Empty<ActiveDeadline>(),
                lastAppliedSignalOrdinal: -1,
                stepIndex: 0,
                appInstallFacts: AppInstallFacts.Empty,
                scenarioProfile: EnrollmentScenarioProfile.Empty,
                scenarioObservations: EnrollmentScenarioObservations.Empty,
                classifierOutcomes: ClassifierOutcomes.Empty,
                helloPolicyEnabled: null);
    }
}
