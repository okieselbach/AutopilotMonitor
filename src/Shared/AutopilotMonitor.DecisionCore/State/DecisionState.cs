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
            DateTime? agentBootUtc = null,
            SignalFact<string>? lastFailureTrigger = null,
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
            AgentBootUtc = agentBootUtc;
            LastFailureTrigger = lastFailureTrigger;
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

        /// <summary>
        /// UTC wall-clock instant at which the current agent process took ownership of this
        /// session. Set by <see cref="CreateInitial"/> on first construction and re-stamped
        /// by the orchestrator on rehydration so deadline-arming sites in the reducer can
        /// floor their effective base at this anchor — preventing replayed signals from
        /// historical log entries from collapsing window/timeout deadlines into immediate
        /// fires when the agent first reads accumulated content.
        /// <para>
        /// Null means "agent never declared a boot anchor" — only possible on legacy
        /// snapshots from before this field existed; the deadline helper falls back to
        /// <c>signal.OccurredAtUtc</c> in that case (i.e. previous behavior).
        /// </para>
        /// </summary>
        public DateTime? AgentBootUtc { get; }

        /// <summary>
        /// Name of the DecisionSignalKind that drove the most recent transition into a
        /// terminal failure stage. Set by <c>HandleEspTerminalFailureV1</c>,
        /// <c>HandleEffectInfrastructureFailureV1</c> and <c>HandleSessionAbortedV1</c>.
        /// Null while the session is non-terminal or terminated via success.
        /// <para>
        /// Consumed by the V2 EnrollmentTerminationHandler to discriminate which terminal-
        /// failure pathways trigger downstream side-effects (e.g. promoting in-flight app
        /// installs to "likely stuck" on ESP-Apps timeout). Stored as a string rather than
        /// the enum so additions to <see cref="Signals.DecisionSignalKind"/> don't force a
        /// snapshot-schema bump.
        /// </para>
        /// </summary>
        public SignalFact<string>? LastFailureTrigger { get; }

        public string SchemaVersion { get; }

        /// <summary>
        /// Produce a mutable builder pre-populated with this state's values.
        /// Reducer handlers call <c>state.ToBuilder().WithStage(...).Build()</c> to
        /// express immutable "copy with changes" ergonomically (plan §2.3 / L.3).
        /// </summary>
        public DecisionStateBuilder ToBuilder() => new DecisionStateBuilder(this);

        /// <summary>
        /// Convenience overload — defaults <paramref name="sessionId"/>'s boot anchor to
        /// <see cref="DateTime.UtcNow"/>. Used by tests and code paths that don't care about
        /// replay-safety. Production callers (orchestrator, recovery) should use the explicit
        /// <see cref="CreateInitial(string, string, DateTime)"/> overload so the boot anchor
        /// is sourced from the agent's <c>IClock</c>.
        /// </summary>
        public static DecisionState CreateInitial(string sessionId, string tenantId) =>
            CreateInitial(sessionId, tenantId, DateTime.UtcNow);

        /// <summary>
        /// Construct the initial non-terminal state for a new session.
        /// Used by the reducer's <c>SessionStarted</c> handler in M3.
        /// </summary>
        /// <param name="agentBootUtc">
        /// Wall-clock instant at which the current agent process started owning this session.
        /// Stamped onto <see cref="AgentBootUtc"/> so the reducer can floor deadline-arming
        /// timestamps at this anchor (replay-safety). Production callers must source this
        /// from the same <c>IClock</c> the orchestrator uses.
        /// </param>
        public static DecisionState CreateInitial(string sessionId, string tenantId, DateTime agentBootUtc) =>
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
                helloPolicyEnabled: null,
                agentBootUtc: agentBootUtc);
    }
}
