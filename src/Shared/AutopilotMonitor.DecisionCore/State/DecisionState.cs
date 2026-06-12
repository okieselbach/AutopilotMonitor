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
        public const string CurrentSchemaVersion = "v4";

        public DecisionState(
            string sessionId,
            string tenantId,
            SessionStage stage,
            SessionOutcome? outcome,
            SignalFact<EnrollmentPhase>? currentEnrollmentPhase,
            SignalFact<DateTime>? deviceSetupEnteredUtc,
            SignalFact<DateTime>? accountSetupEnteredUtc,
            SignalFact<DateTime>? finalizingEnteredUtc,
            SignalFact<DateTime>? accountSetupProvisioningSucceededUtc,
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
            RealmJoinFacts? realmJoinFacts = null,
            SignalFact<DateTime>? deviceSetupResolvedUtc = null,
            string? schemaVersion = null,
            SignalFact<DateTime>? espAdvisoryFailureRecordedUtc = null,
            SignalFact<DateTime>? imeUserSessionCompletedUtc = null,
            SignalFact<string>? completionWaitingFingerprint = null)
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
            AccountSetupProvisioningSucceededUtc = accountSetupProvisioningSucceededUtc;
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
            RealmJoinFacts = realmJoinFacts ?? RealmJoinFacts.Empty;
            DeviceSetupResolvedUtc = deviceSetupResolvedUtc;
            SchemaVersion = schemaVersion ?? CurrentSchemaVersion;
            EspAdvisoryFailureRecordedUtc = espAdvisoryFailureRecordedUtc;
            ImeUserSessionCompletedUtc = imeUserSessionCompletedUtc;
            CompletionWaitingFingerprint = completionWaitingFingerprint;
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

        /// <summary>
        /// Set when the ESP <c>AccountSetupCategory.Status</c> registry resolves to
        /// <c>categorySucceeded=true</c>, or when the fallback fires (all subcategories
        /// succeeded/notRequired but Windows never set the boolean — analog to the existing
        /// DeviceSetup fallback). This is the strong "User-ESP is genuinely finished" fact;
        /// <see cref="AccountSetupEnteredUtc"/> only signals page-entry and is not sufficient.
        /// Posted via <see cref="Signals.DecisionSignalKind.AccountSetupProvisioningComplete"/>.
        /// Null while AccountSetup is still in_progress, missing entirely on flows that skip
        /// AccountSetup (handled by the <c>SkipUserEsp</c> observation in
        /// <see cref="ScenarioObservations"/>).
        /// </summary>
        public SignalFact<DateTime>? AccountSetupProvisioningSucceededUtc { get; }

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

        /// <summary>
        /// Aggregated facts about a RealmJoin (RJ) deployment observed during this session.
        /// Never null; defaults to <see cref="State.RealmJoinFacts.Empty"/>. The V2 AND-gate
        /// (Classic and SelfDeploying) extends the session lifetime while
        /// <see cref="State.RealmJoinFacts.DetectedUtc"/> is set and <see cref="State.RealmJoinFacts.ResolvedUtc"/>
        /// is not — driven by the new <c>HKLM\SYSTEM\CurrentControlSet\Services\realmjoin\Parameters</c>
        /// + <c>HKLM\SOFTWARE\RealmJoin\Packages\*</c> registry collectors.
        /// </summary>
        public RealmJoinFacts RealmJoinFacts { get; }

        /// <summary>
        /// Set when <see cref="Signals.DecisionSignalKind.DeviceSetupProvisioningComplete"/> is
        /// observed — the "DeviceSetup phase is done" anchor. Drives the new SelfDeploying
        /// classification path: arming the <see cref="Engine.DeadlineNames.DeviceOnlyEspDetection"/>
        /// deadline from this anchor (5-min wait) replaces the v1 "terminate-on-signal" behavior
        /// that mis-classified Classic UserDriven enrollments as SelfDeploying when Windows
        /// transitioned slowly DeviceSetup→AccountSetup.
        /// <para>
        /// Null until <c>DeviceSetupProvisioningComplete</c> arrives; the signal handler sets
        /// this fire-once. The new <c>HandleDeviceOnlyEspDetectionDeadlineFired</c> stale-fire
        /// guard treats <c>null</c> here as "deadline armed by old code path before this anchor
        /// existed" and dead-ends, ensuring rollout safety.
        /// </para>
        /// </summary>
        public SignalFact<DateTime>? DeviceSetupResolvedUtc { get; }

        /// <summary>
        /// Set when <c>HandleEspTerminalFailureV1</c> downgrades an incoming
        /// <see cref="Signals.DecisionSignalKind.EspTerminalFailure"/> signal to an advisory
        /// instead of transitioning to <see cref="SessionStage.Failed"/>. The downgrade applies
        /// when the ESP profile permits "Continue anyway" (<c>ScenarioObservations.EspAllowContinueAnyway</c>
        /// is <c>true</c>) AND <see cref="AccountSetupEnteredUtc"/> is already set — both facts
        /// together prove that the device has already progressed past DeviceSetup despite the
        /// reported subcategory failure, so the agent stays in monitoring instead of declaring
        /// the session failed.
        /// <para>
        /// Acts as a fire-once idempotency gate: subsequent <c>EspTerminalFailure</c> signals
        /// (e.g. duplicates from <c>ShellCoreTracker</c>) are dropped as dead-ends so the
        /// timeline does not accumulate redundant <c>esp_failure_advisory</c> events.
        /// </para>
        /// </summary>
        public SignalFact<DateTime>? EspAdvisoryFailureRecordedUtc { get; }

        /// <summary>
        /// Set when <c>HandleImeUserSessionCompletedV1</c> observes the IME
        /// <c>IME-USER-SESSION-COMPLETED</c> pattern (set-once; the first observation wins).
        /// <para>
        /// On its own this fact is deliberately weak evidence: the IME "user session" can run
        /// under <c>defaultuser0</c> (OOBE auto-logon, WhiteGlove technician flow), where its
        /// completion says nothing about the real user's enrollment. It becomes meaningful only
        /// in conjunction with independent facts — a DAD-validated real-user desktop
        /// (<see cref="DesktopArrivedUtc"/>, which excludes defaultuser0/SYSTEM by construction)
        /// AND a timestamp at-or-after <see cref="AccountSetupEnteredUtc"/> (defaultuser0 IME
        /// sessions live in the pre-AccountSetup OOBE frame). The
        /// <c>AdvisoryCompletion</c> deadline handler evaluates exactly that conjunction.
        /// </para>
        /// </summary>
        public SignalFact<DateTime>? ImeUserSessionCompletedUtc { get; }

        /// <summary>
        /// Dedupe anchor for the <c>completion_waiting</c> observability event (liveness plan
        /// PR2). Holds the comma-joined list of missing completion prerequisites that was last
        /// surfaced on the timeline (e.g. <c>"hello_resolution,desktop_arrival"</c>). The
        /// engine's blocked/deferred completion sites only emit a new <c>completion_waiting</c>
        /// event when the freshly computed set differs from this fingerprint — making the event
        /// state-change-only by construction (no heartbeats). Null until the first blocked
        /// completion attempt; additive-nullable, no snapshot-schema bump.
        /// </summary>
        public SignalFact<string>? CompletionWaitingFingerprint { get; }

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
                accountSetupProvisioningSucceededUtc: null,
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
