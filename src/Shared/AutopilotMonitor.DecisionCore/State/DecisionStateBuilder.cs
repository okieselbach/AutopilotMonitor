using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Mutable construction helper for producing a new immutable <see cref="DecisionState"/>.
    /// <para>
    /// Plan §2.3 / L.3 — <c>DecisionState</c> itself is immutable; reducer handlers use the
    /// builder to express "copy with changes" without typing all 20+ constructor arguments.
    /// Call <see cref="Build"/> to materialize a new <see cref="DecisionState"/>. The original
    /// state is never mutated.
    /// </para>
    /// </summary>
    public sealed class DecisionStateBuilder
    {
        public DecisionStateBuilder(DecisionState source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            SessionId = source.SessionId;
            TenantId = source.TenantId;
            Stage = source.Stage;
            Outcome = source.Outcome;
            CurrentEnrollmentPhase = source.CurrentEnrollmentPhase;
            DeviceSetupEnteredUtc = source.DeviceSetupEnteredUtc;
            AccountSetupEnteredUtc = source.AccountSetupEnteredUtc;
            FinalizingEnteredUtc = source.FinalizingEnteredUtc;
            AccountSetupProvisioningSucceededUtc = source.AccountSetupProvisioningSucceededUtc;
            EspFinalExitUtc = source.EspFinalExitUtc;
            DesktopArrivedUtc = source.DesktopArrivedUtc;
            HelloResolvedUtc = source.HelloResolvedUtc;
            SystemRebootUtc = source.SystemRebootUtc;
            HelloOutcome = source.HelloOutcome;
            ImeMatchedPatternId = source.ImeMatchedPatternId;
            Deadlines = new List<ActiveDeadline>(source.Deadlines);
            LastAppliedSignalOrdinal = source.LastAppliedSignalOrdinal;
            StepIndex = source.StepIndex;
            AppInstallFacts = source.AppInstallFacts;
            ScenarioProfile = source.ScenarioProfile;
            ScenarioObservations = source.ScenarioObservations;
            ClassifierOutcomes = source.ClassifierOutcomes;
            HelloPolicyEnabled = source.HelloPolicyEnabled;
            AgentBootUtc = source.AgentBootUtc;
            LastFailureTrigger = source.LastFailureTrigger;
            RealmJoinFacts = source.RealmJoinFacts;
            DeviceSetupResolvedUtc = source.DeviceSetupResolvedUtc;
            SchemaVersion = source.SchemaVersion;
            EspAdvisoryFailureRecordedUtc = source.EspAdvisoryFailureRecordedUtc;
            ImeUserSessionCompletedUtc = source.ImeUserSessionCompletedUtc;
            CompletionWaitingFingerprint = source.CompletionWaitingFingerprint;
            HelloWizardStartedUtc = source.HelloWizardStartedUtc;
        }

        public string SessionId { get; set; }
        public string TenantId { get; set; }
        public SessionStage Stage { get; set; }
        public SessionOutcome? Outcome { get; set; }
        public SignalFact<EnrollmentPhase>? CurrentEnrollmentPhase { get; set; }
        public SignalFact<DateTime>? DeviceSetupEnteredUtc { get; set; }
        public SignalFact<DateTime>? AccountSetupEnteredUtc { get; set; }
        public SignalFact<DateTime>? FinalizingEnteredUtc { get; set; }
        public SignalFact<DateTime>? AccountSetupProvisioningSucceededUtc { get; set; }
        public SignalFact<DateTime>? EspFinalExitUtc { get; set; }
        public SignalFact<DateTime>? DesktopArrivedUtc { get; set; }
        public SignalFact<DateTime>? HelloResolvedUtc { get; set; }
        public SignalFact<DateTime>? SystemRebootUtc { get; set; }
        public SignalFact<string>? HelloOutcome { get; set; }
        public SignalFact<string>? ImeMatchedPatternId { get; set; }
        public List<ActiveDeadline> Deadlines { get; set; }
        public long LastAppliedSignalOrdinal { get; set; }
        public int StepIndex { get; set; }
        public AppInstallFacts AppInstallFacts { get; set; } = AppInstallFacts.Empty;
        public EnrollmentScenarioProfile ScenarioProfile { get; set; } = EnrollmentScenarioProfile.Empty;
        public EnrollmentScenarioObservations ScenarioObservations { get; set; } = EnrollmentScenarioObservations.Empty;
        public ClassifierOutcomes ClassifierOutcomes { get; set; } = ClassifierOutcomes.Empty;
        public SignalFact<bool>? HelloPolicyEnabled { get; set; }
        public DateTime? AgentBootUtc { get; set; }
        public SignalFact<string>? LastFailureTrigger { get; set; }
        public RealmJoinFacts RealmJoinFacts { get; set; } = RealmJoinFacts.Empty;
        public SignalFact<DateTime>? DeviceSetupResolvedUtc { get; set; }
        public SignalFact<DateTime>? EspAdvisoryFailureRecordedUtc { get; set; }
        public SignalFact<DateTime>? ImeUserSessionCompletedUtc { get; set; }
        public SignalFact<string>? CompletionWaitingFingerprint { get; set; }
        public SignalFact<DateTime>? HelloWizardStartedUtc { get; set; }
        public string SchemaVersion { get; set; }

        // ---------- fluent helpers for the most common reducer operations ----------

        public DecisionStateBuilder WithStage(SessionStage stage) { Stage = stage; return this; }

        public DecisionStateBuilder WithOutcome(SessionOutcome? outcome) { Outcome = outcome; return this; }

        public DecisionStateBuilder WithStepIndex(int stepIndex) { StepIndex = stepIndex; return this; }

        public DecisionStateBuilder WithLastAppliedSignalOrdinal(long ordinal) { LastAppliedSignalOrdinal = ordinal; return this; }

        public DecisionStateBuilder WithCurrentEnrollmentPhase(EnrollmentPhase phase, long sourceSignalOrdinal)
        {
            CurrentEnrollmentPhase = new SignalFact<EnrollmentPhase>(phase, sourceSignalOrdinal);
            return this;
        }

        public DecisionStateBuilder AddDeadline(ActiveDeadline deadline)
        {
            if (deadline == null) throw new ArgumentNullException(nameof(deadline));
            // Replace-if-same-name semantic: deadlines identified by Name. Plan §2.6.
            for (int i = 0; i < Deadlines.Count; i++)
            {
                if (string.Equals(Deadlines[i].Name, deadline.Name, StringComparison.Ordinal))
                {
                    Deadlines[i] = deadline;
                    return this;
                }
            }
            Deadlines.Add(deadline);
            return this;
        }

        public DecisionStateBuilder CancelDeadline(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            Deadlines.RemoveAll(d => string.Equals(d.Name, name, StringComparison.Ordinal));
            return this;
        }

        public DecisionStateBuilder ClearDeadlines() { Deadlines.Clear(); return this; }

        public DecisionStateBuilder WithAppInstallFacts(AppInstallFacts facts)
        {
            AppInstallFacts = facts ?? throw new ArgumentNullException(nameof(facts));
            return this;
        }

        public DecisionStateBuilder WithRealmJoinFacts(RealmJoinFacts facts)
        {
            RealmJoinFacts = facts ?? throw new ArgumentNullException(nameof(facts));
            return this;
        }

        public DecisionStateBuilder WithScenarioProfile(EnrollmentScenarioProfile profile)
        {
            ScenarioProfile = profile ?? throw new ArgumentNullException(nameof(profile));
            return this;
        }

        public DecisionStateBuilder WithScenarioObservations(EnrollmentScenarioObservations observations)
        {
            ScenarioObservations = observations ?? throw new ArgumentNullException(nameof(observations));
            return this;
        }

        public DecisionStateBuilder WithClassifierOutcomes(ClassifierOutcomes outcomes)
        {
            ClassifierOutcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
            return this;
        }

        // Set-once on first detection. Re-detection by the agent (e.g. after a re-poll of
        // the CSP store) is allowed to update the value, but the source ordinal must
        // monotonically advance — that's enforced by the caller passing the current
        // signal ordinal. PR4 (882fef64 debrief).
        public DecisionStateBuilder WithHelloPolicyEnabled(bool value, long sourceSignalOrdinal)
        {
            HelloPolicyEnabled = new SignalFact<bool>(value, sourceSignalOrdinal);
            return this;
        }

        /// <summary>
        /// Re-stamp the agent-boot anchor used for deadline arming. Called by the orchestrator
        /// on rehydration so deadlines armed by replayed signals get floored at "now" (the
        /// current run's boot time) rather than the prior session's boot time.
        /// </summary>
        public DecisionStateBuilder WithAgentBootUtc(DateTime agentBootUtc)
        {
            AgentBootUtc = agentBootUtc;
            return this;
        }

        /// <summary>
        /// Record the name of the DecisionSignalKind that drove the most recent transition
        /// into a terminal failure stage. Called by <c>HandleEspTerminalFailureV1</c>,
        /// <c>HandleEffectInfrastructureFailureV1</c> and <c>HandleSessionAbortedV1</c> so the
        /// V2 EnrollmentTerminationHandler can discriminate failure pathways without parsing
        /// the signal log.
        /// </summary>
        public DecisionStateBuilder WithLastFailureTrigger(string triggerName, long sourceSignalOrdinal)
        {
            LastFailureTrigger = new SignalFact<string>(triggerName, sourceSignalOrdinal);
            return this;
        }

        /// <summary>
        /// Record the UTC instant at which an incoming <c>EspTerminalFailure</c> was downgraded
        /// to an advisory by <c>HandleEspTerminalFailureV1</c> (ContinueAnyway-aware defang).
        /// Fire-once gate: subsequent <c>EspTerminalFailure</c> signals see this fact set and
        /// dead-end without further effect.
        /// </summary>
        public DecisionStateBuilder WithEspAdvisoryFailureRecorded(DateTime utc, long sourceSignalOrdinal)
        {
            EspAdvisoryFailureRecordedUtc = new SignalFact<DateTime>(utc, sourceSignalOrdinal);
            return this;
        }

        public DecisionState Build() =>
            new DecisionState(
                sessionId: SessionId,
                tenantId: TenantId,
                stage: Stage,
                outcome: Outcome,
                currentEnrollmentPhase: CurrentEnrollmentPhase,
                deviceSetupEnteredUtc: DeviceSetupEnteredUtc,
                accountSetupEnteredUtc: AccountSetupEnteredUtc,
                finalizingEnteredUtc: FinalizingEnteredUtc,
                accountSetupProvisioningSucceededUtc: AccountSetupProvisioningSucceededUtc,
                espFinalExitUtc: EspFinalExitUtc,
                desktopArrivedUtc: DesktopArrivedUtc,
                helloResolvedUtc: HelloResolvedUtc,
                systemRebootUtc: SystemRebootUtc,
                helloOutcome: HelloOutcome,
                imeMatchedPatternId: ImeMatchedPatternId,
                deadlines: Deadlines.ToArray(),
                lastAppliedSignalOrdinal: LastAppliedSignalOrdinal,
                stepIndex: StepIndex,
                appInstallFacts: AppInstallFacts,
                scenarioProfile: ScenarioProfile,
                scenarioObservations: ScenarioObservations,
                classifierOutcomes: ClassifierOutcomes,
                helloPolicyEnabled: HelloPolicyEnabled,
                agentBootUtc: AgentBootUtc,
                lastFailureTrigger: LastFailureTrigger,
                realmJoinFacts: RealmJoinFacts,
                deviceSetupResolvedUtc: DeviceSetupResolvedUtc,
                schemaVersion: SchemaVersion,
                espAdvisoryFailureRecordedUtc: EspAdvisoryFailureRecordedUtc,
                imeUserSessionCompletedUtc: ImeUserSessionCompletedUtc,
                completionWaitingFingerprint: CompletionWaitingFingerprint,
                helloWizardStartedUtc: HelloWizardStartedUtc);
    }
}
