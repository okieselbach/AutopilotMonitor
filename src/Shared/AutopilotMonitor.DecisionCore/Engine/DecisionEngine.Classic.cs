using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Classic UserDriven-v1 enrollment handlers. Plan §2.5 partial-class layout.
    public sealed partial class DecisionEngine
    {
        // Post-ESP Hello-safety grace period per plan §2.7.
        private static readonly TimeSpan s_helloSafetyWindow = TimeSpan.FromSeconds(300);

        // Grace window between both-prerequisites-resolved and Completed. Plan §5 Fix 6.
        // Short enough that the user doesn't notice; long enough for the reducer's
        // phase_transition(FinalizingSetup) + enrollment_complete effects to reach the
        // backend before IsTerminal() fires and EnrollmentTerminationHandler drains the spool.
        private static readonly TimeSpan s_finalizingGraceWindow = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspPhaseChanged"/>. Drives the
        /// <see cref="SessionStage.EspDeviceSetup"/> → <see cref="SessionStage.EspAccountSetup"/>
        /// progression and updates the user-visible <see cref="DecisionState.CurrentEnrollmentPhase"/>
        /// fact.
        /// </summary>
        private DecisionStep HandleEspPhaseChangedV1(DecisionState state, DecisionSignal signal)
        {
            var rawPhase = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.EspPhase, out var p)
                ? p
                : string.Empty;
            var enrollmentPhase = MapEspPhaseToEnrollmentPhase(rawPhase);

            var newStage = enrollmentPhase switch
            {
                EnrollmentPhase.DeviceSetup => SessionStage.EspDeviceSetup,
                EnrollmentPhase.AccountSetup => SessionStage.EspAccountSetup,
                // Plan §6 Fix 8 — Finalizing is a synthetic phase derived from the Shell-Core
                // esp_exiting event; on Classic V1 enrollments the first exit is the Device-ESP
                // handoff, not the true final. Only promote to AwaitingHello when reaching it is
                // legitimate (AccountSetup already observed, OR SkipUser flow). Otherwise keep
                // the current stage — the signal is still recorded as a taken transition with
                // the FinalizingEnteredUtc fact + CurrentEnrollmentPhase updated below, but the
                // HelloSafety deadline is NOT armed.
                EnrollmentPhase.FinalizingSetup => ShouldTransitionToAwaitingHello(state)
                    ? SessionStage.AwaitingHello
                    : state.Stage,
                _ => state.Stage,
            };

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStage(newStage)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            if (enrollmentPhase != EnrollmentPhase.Unknown)
            {
                builder.WithCurrentEnrollmentPhase(enrollmentPhase, signal.SessionSignalOrdinal);
            }

            if (enrollmentPhase == EnrollmentPhase.DeviceSetup && state.DeviceSetupEnteredUtc == null)
            {
                builder.DeviceSetupEnteredUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }
            if (enrollmentPhase == EnrollmentPhase.AccountSetup && state.AccountSetupEnteredUtc == null)
            {
                builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }
            if (enrollmentPhase == EnrollmentPhase.FinalizingSetup && state.FinalizingEnteredUtc == null)
            {
                builder.FinalizingEnteredUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }

            // Classic UserDriven-v1 tell — seeing AccountSetup promotes the scenario profile.
            // Codex follow-up #5: Mode promotion is delegated to EnrollmentScenarioProfileUpdater
            // so the monotonic-confidence rule stays in one place.
            if (enrollmentPhase == EnrollmentPhase.AccountSetup)
            {
                builder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplyAccountSetupObserved(
                    builder.ScenarioProfile, signal);
            }

            // Device-Only ESP detection (plan §2.7): arm on first DeviceSetup, cancel on AccountSetup.
            // See DecisionEngine.SelfDeploying.cs for the deadline-fired handler.
            // EffectiveDeadlineBase floors the 5-min window at AgentBootUtc so a replayed
            // DeviceSetup signal from an older CMTrace log entry doesn't collapse the deadline
            // to immediate-fire — that would mark a perfectly normal Classic enrollment as
            // device-only at boot (premature classifier promotion to DeviceOnly@Strong).
            var effectsList = new List<DecisionEffect>();
            if (enrollmentPhase == EnrollmentPhase.DeviceSetup &&
                state.DeviceSetupEnteredUtc == null)
            {
                var devOnlyDl = BuildDeviceOnlyEspDetectionDeadline(EffectiveDeadlineBase(state, signal));
                builder.AddDeadline(devOnlyDl);
                effectsList.Add(new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: devOnlyDl));
            }
            else if (enrollmentPhase == EnrollmentPhase.AccountSetup)
            {
                builder.CancelDeadline(DeadlineNames.DeviceOnlyEspDetection);
                effectsList.Add(new DecisionEffect(
                    DecisionEffectKind.CancelDeadline,
                    cancelDeadlineName: DeadlineNames.DeviceOnlyEspDetection));

                // Plan §6 Fix 10 — defensive belt-and-suspenders for the premature-AwaitingHello
                // bounce-back case. If Fix 7's tracker guard or Fix 8's reducer guards ever fail
                // and the stage reached AwaitingHello before AccountSetup (i.e. an early Finalizing
                // synthesis armed HelloSafety from the wrong baseline), cancel the deadline here
                // so it cannot fire from the wrong window after we resume EspAccountSetup.
                if (state.Stage == SessionStage.AwaitingHello)
                {
                    builder.CancelDeadline(DeadlineNames.HelloSafety);
                    effectsList.Add(new DecisionEffect(
                        DecisionEffectKind.CancelDeadline,
                        cancelDeadlineName: DeadlineNames.HelloSafety));
                }
            }

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: newStage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspPhaseChanged));

            return new DecisionStep(newState, transition, effectsList.ToArray());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspExiting"/>. Records
        /// <see cref="DecisionState.EspFinalExitUtc"/> for observability; transitions to
        /// <see cref="SessionStage.AwaitingHello"/> and arms the Hello-safety deadline only
        /// when <see cref="ShouldTransitionToAwaitingHello"/> holds (plan §6 Fix 8). On an
        /// early / intermediate exit (e.g. the Device-ESP handoff on a Classic V1 enrollment)
        /// the fact is still recorded but the stage is unchanged and no deadline is armed.
        /// </summary>
        private DecisionStep HandleEspExitingV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var shouldTransition = ShouldTransitionToAwaitingHello(state);

            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.EspFinalExitUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            if (!shouldTransition)
            {
                // Early / intermediate esp_exiting: keep stage, no HelloSafety arm. Observability
                // still records EspFinalExitUtc so downstream classifiers see the full picture.
                var noopTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.EspExiting));
                return new DecisionStep(builder.Build(), noopTransition, Array.Empty<DecisionEffect>());
            }

            // Replay-safety: floor the 300-s Hello-safety window at AgentBootUtc so a replayed
            // EspExiting signal from CMTrace doesn't fire HelloSafety immediately at boot
            // (which would mark Hello as Timeout the moment the agent reads the log tail).
            var dueAtUtc = EffectiveDeadlineBase(state, signal).Add(s_helloSafetyWindow);
            var helloSafety = new ActiveDeadline(
                name: DeadlineNames.HelloSafety,
                dueAtUtc: dueAtUtc,
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety,
                });

            builder = builder
                .WithStage(SessionStage.AwaitingHello)
                .AddDeadline(helloSafety);

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.AwaitingHello,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspExiting));

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: helloSafety),
            };

            return new DecisionStep(builder.Build(), transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.HelloResolved"/>. Records the resolution fact
        /// and cancels the Hello-safety deadline. If Desktop has already arrived the session
        /// completes here; otherwise stage transitions to <see cref="SessionStage.AwaitingDesktop"/>.
        /// </summary>
        private DecisionStep HandleHelloResolvedV1(DecisionState state, DecisionSignal signal)
        {
            var outcome = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.HelloOutcome, out var o)
                ? o
                : "Success";

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.HelloSafety);
            builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            builder.HelloOutcome = new SignalFact<string>(outcome, signal.SessionSignalOrdinal);

            var desktopAlreadyArrived = state.DesktopArrivedUtc != null;

            // Plan §5 Fix 6: when both prerequisites have resolved, go through the
            // non-terminal Finalizing stage with a short grace period before Completed.
            // This lets the backend see phase_transition(FinalizingSetup) + enrollment_complete
            // before IsTerminal() fires and EnrollmentTerminationHandler tears down the spool.
            if (desktopAlreadyArrived)
            {
                return TransitionToFinalizing(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.HelloResolved));
            }

            builder.WithStage(SessionStage.AwaitingDesktop);
            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.AwaitingDesktop,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.HelloResolved));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DesktopArrived"/>. Mirror of the Hello handler:
        /// records <see cref="DecisionState.DesktopArrivedUtc"/>, and if Hello has already
        /// resolved the session completes here.
        /// </summary>
        private DecisionStep HandleDesktopArrivedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.DesktopArrivedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            var helloAlreadyResolved = state.HelloResolvedUtc != null;

            // Plan §5 Fix 6: mirror of HandleHelloResolvedV1. When both prerequisites are in,
            // go through Finalizing (non-terminal) instead of jumping straight to Completed.
            if (helloAlreadyResolved)
            {
                return TransitionToFinalizing(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.DesktopArrived));
            }

            // Desktop came first: keep current stage (AwaitingHello or EspAccountSetup) until
            // HelloResolved arrives.
            builder.WithStage(state.Stage);
            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.DesktopArrived));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.ImeUserSessionCompleted"/>. Primarily a
        /// <c>EnrollmentType</c>-hypothesis strengthener (IME's user-session-complete pattern
        /// is a strong UserDriven-v1 indicator), and records the matched pattern id.
        /// </summary>
        private DecisionStep HandleImeUserSessionCompletedV1(DecisionState state, DecisionSignal signal)
        {
            var patternId = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.ImePatternId, out var pid)
                ? pid
                : null;

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            if (!string.IsNullOrEmpty(patternId))
            {
                builder.ImeMatchedPatternId = new SignalFact<string>(patternId!, signal.SessionSignalOrdinal);
            }

            // Classic High-confidence promotion on user-session-complete. Codex follow-up #5:
            // delegate to the updater for monotonicity.
            builder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplyImeUserSessionCompleted(
                builder.ScenarioProfile, signal);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.ImeUserSessionCompleted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AadUserJoinedLate"/> — observation-only update.
        /// <para>
        /// Per project memory <c>feedback_aad_joined_late_not_completion</c>: this signal is a
        /// classifier-state update ONLY — never a completion trigger. Stage is unchanged, no
        /// terminal event is emitted. Codex follow-up #5: the user-presence flag lives in
        /// <see cref="EnrollmentScenarioObservations.AadUserJoinWithUserObserved"/> (NOT
        /// <see cref="EnrollmentScenarioProfile.JoinMode"/> — that is strictly the
        /// <see cref="DecisionSignalKind.EnrollmentFactsObserved"/> <c>isHybridJoin</c>
        /// payload), and the profile's <see cref="EnrollmentScenarioProfile.Reason"/> is
        /// annotated without touching Mode.
        /// </para>
        /// </summary>
        private DecisionStep HandleAadUserJoinedLateV1(DecisionState state, DecisionSignal signal)
        {
            var withUser = signal.Payload != null &&
                           signal.Payload.TryGetValue(SignalPayloadKeys.AadJoinedWithUser, out var raw) &&
                           bool.TryParse(raw, out var parsed) && parsed;

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            builder.ScenarioObservations = builder.ScenarioObservations.WithAadUserJoinWithUserObserved(
                value: withUser, sourceSignalOrdinal: signal.SessionSignalOrdinal);
            builder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplyAadUserJoinedLate(
                builder.ScenarioProfile, signal, withUser);

            var newState = builder.Build();

            // Explicit "taken but stage unchanged" transition — the Inspector should see the
            // hypothesis annotation but nobody should mistake it for a completion step.
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.AadUserJoinedLate));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        // ============================================================== internal helpers

        /// <summary>
        /// Plan §5 Fix 6 helper: transition to <see cref="SessionStage.Finalizing"/>, arm the
        /// <see cref="DeadlineNames.FinalizingGrace"/> deadline (~5 s), emit a
        /// <c>phase_transition(FinalizingSetup)</c> declaration so the UI sees Phase 6, and
        /// return a <see cref="DecisionStep"/>. The deadline fire eventually drives the actual
        /// Finalizing → Completed transition with the terminal <c>enrollment_complete</c> effect
        /// (see <c>HandleFinalizingGraceDeadlineFired</c> below).
        /// <para>
        /// The caller has already populated <paramref name="preparedBuilder"/> with its
        /// signal-specific state mutations (e.g. <c>HelloResolvedUtc</c> /
        /// <c>DesktopArrivedUtc</c> / Hello-safety cancel); this helper only adds the stage +
        /// deadline bookkeeping.
        /// </para>
        /// </summary>
        private DecisionStep TransitionToFinalizing(
            DecisionState state,
            DecisionSignal signal,
            DecisionStateBuilder preparedBuilder,
            int nextStepIndex,
            string trigger)
        {
            // Replay-safety: floor the 5-s grace window at AgentBootUtc so a replayed Hello/
            // Desktop signal pair doesn't drive an immediate enrollment_complete the moment
            // the agent boots and reads accumulated logs.
            var dueAtUtc = EffectiveDeadlineBase(state, signal).Add(s_finalizingGraceWindow);
            var deadline = new ActiveDeadline(
                name: DeadlineNames.FinalizingGrace,
                dueAtUtc: dueAtUtc,
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace,
                });

            var builder = preparedBuilder
                .WithStage(SessionStage.Finalizing)
                .AddDeadline(deadline);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Finalizing,
                nextStepIndex: nextStepIndex,
                trigger: trigger);

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: deadline),
                BuildPhaseTransitionEffect(EnrollmentPhase.FinalizingSetup),
            };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Plan §5 Fix 6: FinalizingGrace deadline fired → transition Finalizing → Completed,
        /// set <see cref="SessionOutcome.EnrollmentComplete"/>, emit the terminal
        /// <c>enrollment_complete</c> event. <see cref="State.SessionStageExtensions.IsTerminal"/>
        /// returns true for <see cref="SessionStage.Completed"/>, so
        /// <c>DecisionStepProcessor.OnDecisionTerminalStage</c> now fires and
        /// <c>EnrollmentTerminationHandler</c> takes over — 5 s later than before, with the
        /// prior <c>phase_transition(FinalizingSetup)</c> already on the wire.
        /// </summary>
        private DecisionStep HandleFinalizingGraceDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithStage(SessionStage.Completed)
                .WithOutcome(SessionOutcome.EnrollmentComplete)
                .ClearDeadlines();

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Completed,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.FinalizingGrace}");

            var effects = new[] { BuildEnrollmentCompleteEffect(newState, $"DeadlineFired:{DeadlineNames.FinalizingGrace}") };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Phase-declaration effect used by <see cref="TransitionToFinalizing"/>. Emits a
        /// <c>phase_transition</c> <see cref="EnrollmentEvent"/> with the requested
        /// <see cref="EnrollmentPhase"/> so the Web UI's PhaseTimeline renders the phase bar.
        /// <para>
        /// Per <c>feedback_phase_strategy</c>: <c>phase_transition</c> is one of the small set
        /// of event types allowed to carry <see cref="EnrollmentPhase"/> != Unknown. Flushes
        /// immediately (<c>enrollment_complete</c> reducer-default) so the UI sees the phase
        /// change within seconds, not at the next batch boundary.
        /// </para>
        /// </summary>
        private static DecisionEffect BuildPhaseTransitionEffect(EnrollmentPhase phase) =>
            new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: new Dictionary<string, string>
                {
                    ["eventType"] = "phase_transition",
                    ["phase"] = phase.ToString(),
                    ["source"] = "DecisionEngine",
                    ["message"] = $"Phase: {phase}",
                });

        /// <summary>
        /// Terminal <c>enrollment_complete</c> effect emitted by the Classic FinalizingGrace
        /// deadline path and the SelfDeploying DeviceSetupProvisioningComplete path. Restores
        /// V1 backend-side audit-trail parity (882fef64 debrief follow-up): Parameters carry
        /// just <c>eventType</c> (legacy contract), and a structured <see cref="DecisionEffect.TypedPayload"/>
        /// dictionary built by <see cref="DecisionAuditTrailBuilder"/> feeds
        /// <c>EnrollmentEvent.Data</c> with <c>signalsSeen</c>, <c>signalEvidence</c>,
        /// <c>signalTimestamps</c>, <c>decisionSource</c>, <c>trigger</c>, plus the resolved
        /// Hello / scenario context. Lets backend consumers reconstruct "which signals drove
        /// completion" without re-reading the agent's <c>final-status.json</c>.
        /// </summary>
        /// <param name="state">Post-transition state (Stage=Completed, Outcome=EnrollmentComplete).</param>
        /// <param name="completionTrigger">
        /// The trigger label that was passed to <see cref="DecisionEngine.BuildTakenTransition"/>
        /// for this step (e.g. <c>"DeadlineFired:FinalizingGrace"</c> or
        /// <c>"DeviceSetupProvisioningComplete"</c>) — keeps the audit trail self-describing
        /// without requiring the consumer to re-derive it from the StepIndex.
        /// </param>
        internal static DecisionEffect BuildEnrollmentCompleteEffect(DecisionState state, string completionTrigger)
        {
            return new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: new Dictionary<string, string>
                {
                    ["eventType"] = "enrollment_complete",
                },
                typedPayload: DecisionAuditTrailBuilder.Build(
                    postState: state,
                    decidedStage: state.Stage,
                    trigger: completionTrigger));
        }
    }
}
