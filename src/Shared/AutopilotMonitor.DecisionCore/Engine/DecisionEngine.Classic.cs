using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

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
                // handoff, not the true final. Only promote to AwaitingHello when the strong
                // post-AccountSetup gate (ShouldTransitionToAwaitingHello) holds: either
                // AccountSetupProvisioningSucceededUtc is set OR SkipUserEsp is observed as true.
                // Otherwise keep the current stage — the signal is still recorded as a taken
                // transition with the FinalizingEnteredUtc fact + CurrentEnrollmentPhase updated
                // below, but the HelloSafety deadline is NOT armed.
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

            // Device-Only ESP detection (plan §2.7 + 88a53223 defang): the DeviceOnlyEspDetection
            // deadline is now armed by HandleDeviceSetupProvisioningCompleteV1 (DeviceSetup-END),
            // not here at DeviceSetup-START. Rationale: DeviceSetup with apps takes 10-20min, so
            // a 5-min deadline armed at start fired mid-enrollment while DeviceSetup was still in
            // progress — semantically meaningless. The defang moves the arm to "5min after
            // DeviceSetup-resolved" so the "no AccountSetup after DeviceSetup-done" check actually
            // decides something. The AccountSetup-cancel below is unchanged — it's a no-op when
            // the deadline isn't armed (fast DeviceSetup, AccountSetup arrives before signal) and
            // a genuine cancel when it is (the normal Classic flow during the 5min window).
            var effectsList = new List<DecisionEffect>();
            if (enrollmentPhase == EnrollmentPhase.AccountSetup)
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
        /// the fact is still recorded and the stage is unchanged — but when the blocked exit
        /// happened AFTER AccountSetup entry, the shared
        /// <see cref="DeadlineNames.AdvisoryCompletion"/> resolution window is armed (session
        /// 1ec8f4c6 dead-end variant; see the inline comment in the no-transition branch).
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
                //
                // Session 1ec8f4c6 (2026-06-12) — third completion-dead-end variant: Windows can
                // close the ESP page NORMALLY (Shell-Core 62407, errorCode=0) while the
                // AccountSetup Apps subcategory is still in_progress (a user-targeted app never
                // started). No EspTerminalFailure ever fires, so the advisory-defang arming site
                // never runs; the registry gate can never open (Apps never succeeds, the
                // all-subcategories fallback needs every subcategory succeeded) and the session
                // would idle to the max-lifetime watchdog with no verdict. A guard-blocked exit
                // AFTER AccountSetup entry is exactly that shape — arm the shared
                // AdvisoryCompletion resolution window so HandleAdvisoryCompletionDeadlineFired
                // resolves the session either way. Fire-once: repeated post-AccountSetup exits
                // do not re-base an already-armed window (unlike the advisory site, which
                // deliberately replaces it — the failure is the fresher dead-end signal).
                var exitEffects = Array.Empty<DecisionEffect>();
                if (state.AccountSetupEnteredUtc != null && !HasAdvisoryCompletionDeadline(state))
                {
                    var resolutionDeadline = BuildAdvisoryCompletionDeadline(state, signal);
                    builder.AddDeadline(resolutionDeadline);
                    exitEffects = new[]
                    {
                        new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: resolutionDeadline),
                    };
                }

                // Liveness plan PR2: a post-AccountSetup guard-blocked exit is a blocked
                // completion attempt — say what the engine is still waiting on (state-change-
                // only via the fingerprint fact). The resolution window's due-time rides along
                // so consumers see when the AdvisoryCompletion backstop will resolve the
                // session either way. Pre-AccountSetup handoff exits stay silent — they are
                // the normal Device→Account transition, not a completion attempt.
                if (state.AccountSetupEnteredUtc != null)
                {
                    Dictionary<string, string>? waitingExtra = null;
                    foreach (var d in builder.Deadlines)
                    {
                        if (d.Name == DeadlineNames.AdvisoryCompletion)
                        {
                            waitingExtra = new Dictionary<string, string>(1)
                            {
                                ["resolutionDeadlineDueAtUtc"] = d.DueAtUtc.ToString("o"),
                            };
                            break;
                        }
                    }

                    var waitingEffect = BuildCompletionWaitingEffect(
                        state, builder, signal,
                        trigger: nameof(DecisionSignalKind.EspExiting) + ":GuardBlocked",
                        extraData: waitingExtra);
                    exitEffects = AppendEffect(exitEffects, waitingEffect);
                }

                var noopTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.EspExiting));
                return new DecisionStep(builder.Build(), noopTransition, exitEffects);
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
        /// <para>
        /// Session 8b8d611d fix (2026-05-20): when <see cref="DeadlineNames.HelloSafety"/> is
        /// armed in the live <see cref="DecisionState.Deadlines"/> set we also emit a
        /// <see cref="DecisionEffectKind.CancelDeadline"/> effect so the wall-clock
        /// <see cref="DeadlineScheduler"/> timer is disposed. Without that effect the builder
        /// cancel only cleared the reducer-state view; the OS timer kept ticking and fired
        /// later (~300 s after arm), re-entering <see cref="HandleHelloSafetyDeadlineFired"/>
        /// from a Completed stage and emitting a duplicate <c>enrollment_complete</c>.
        /// Mirrors the pattern already used by the Hello-disabled fast-path in
        /// <see cref="HandleDesktopArrivedV1"/>.
        /// </para>
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
            var helloSafetyCancelEffect = BuildHelloSafetyCancelEffectIfArmed(state);

            // Plan §5 Fix 6: when both prerequisites have resolved, go through the
            // non-terminal Finalizing stage with a short grace period before Completed.
            // This lets the backend see phase_transition(FinalizingSetup) + enrollment_complete
            // before IsTerminal() fires and EnrollmentTerminationHandler tears down the spool.
            //
            // Completion gates (ARCH-F1): while a gate (e.g. an active RealmJoin deployment) is
            // closed the Finalizing transition is deferred. Hello+Desktop facts are still
            // recorded; the gate's release handler re-attempts completion via
            // CompleteIfDeferredOrBookkeep().
            if (desktopAlreadyArrived)
            {
                return CompleteThroughFinalizingOrDefer(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.HelloResolved),
                    leadingEffects: helloSafetyCancelEffect != null
                        ? new[] { helloSafetyCancelEffect }
                        : null);
            }

            builder.WithStage(SessionStage.AwaitingDesktop);

            // Liveness plan PR2: Hello is in but Desktop is not — a blocked completion attempt.
            // Computed on the builder so the just-recorded Hello facts are not listed as missing.
            var waitingEffect = BuildCompletionWaitingEffect(
                state, builder, signal, trigger: nameof(DecisionSignalKind.HelloResolved));

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.AwaitingDesktop,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.HelloResolved));

            var effects = helloSafetyCancelEffect != null
                ? new[] { helloSafetyCancelEffect }
                : Array.Empty<DecisionEffect>();
            return new DecisionStep(newState, transition, AppendEffect(effects, waitingEffect));
        }

        /// <summary>
        /// Session 8b8d611d fix (2026-05-20): emit a <see cref="DecisionEffectKind.CancelDeadline"/>
        /// effect for <see cref="DeadlineNames.HelloSafety"/> when it is actually armed in
        /// <paramref name="state"/>. Returns <c>null</c> when the deadline is not present —
        /// avoids spurious cancel-noise on the scheduler. Callers chain this in front of any
        /// reducer-side <c>CancelDeadline(HelloSafety)</c> so the live timer is disposed
        /// before it can fire after a Completed transition.
        /// </summary>
        private static DecisionEffect? BuildHelloSafetyCancelEffectIfArmed(DecisionState state)
        {
            foreach (var d in state.Deadlines)
            {
                if (d.Name == DeadlineNames.HelloSafety)
                {
                    return new DecisionEffect(
                        DecisionEffectKind.CancelDeadline,
                        cancelDeadlineName: DeadlineNames.HelloSafety);
                }
            }
            return null;
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DesktopArrived"/>. Mirror of the Hello handler:
        /// records <see cref="DecisionState.DesktopArrivedUtc"/>, and if Hello has already
        /// resolved the session completes here.
        /// <para>
        /// Hello-disabled fast-path: when <see cref="DecisionState.HelloPolicyEnabled"/> is
        /// explicitly <c>false</c> AND the post-AccountSetup completion gate
        /// (<see cref="ShouldTransitionToAwaitingHello"/>) holds — i.e. either
        /// <see cref="DecisionState.AccountSetupProvisioningSucceededUtc"/> is set (ESP genuinely
        /// finished AccountSetup) OR <see cref="EnrollmentScenarioObservations.SkipUserEsp"/>
        /// is <c>true</c> (no User-ESP page on this flow) — synthesise
        /// <c>HelloOutcome="Skipped"</c> here and route directly through Finalizing.
        /// This avoids waiting out the full 5-min HelloSafety window when the policy reader
        /// already told us no Hello wizard is expected. Belt-and-suspenders alongside the
        /// EspExiting → HelloSafety reducer path: the path that fires first wins.
        /// </para>
        /// <para>
        /// Session 08c99638 fix (2026-05-21): the gate was previously
        /// <c>AccountSetupEnteredUtc != null</c>, which is too weak. Shell-Core event 62407
        /// (<c>CommercialOOBE_ESPProgress_Page_Exiting</c>) can fire with <c>errorCode=0</c>
        /// while AccountSetup is still <c>categorySucceeded=in_progress</c> (apps never finished,
        /// 0/N subcategories complete). The old gate let a late <c>desktop_arrived</c> + Hello
        /// disabled drive <c>enrollment_complete</c> for a session that should have stalled.
        /// The strong gate keeps parity with <see cref="HandleEspExitingV1"/>'s
        /// <see cref="ShouldTransitionToAwaitingHello"/> check — both paths now require the same
        /// post-AccountSetup evidence before any forward transition.
        /// </para>
        /// </summary>
        private DecisionStep HandleDesktopArrivedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.DesktopArrivedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            var helloAlreadyResolved = state.HelloResolvedUtc != null;

            // Hello-disabled fast-path. Both guards required:
            //   1. HelloPolicyEnabled?.Value == false — policy reader confirmed no Hello wizard
            //   2. ShouldTransitionToAwaitingHello(state) — strong post-AccountSetup gate, identical
            //      to the one used by HandleEspExitingV1. Requires AccountSetupProvisioningSucceededUtc
            //      to be set OR SkipUserEsp observed as true. The prior weak guard
            //      (AccountSetupEnteredUtc != null) allowed completion when ESP "exited" via
            //      Shell-Core 62407 while AccountSetup categorySucceeded was still in_progress
            //      (session 08c99638).
            // Skipping unknown policy (HelloPolicyEnabled == null) preserves the prior pessimistic
            // behaviour: keep waiting for Hello / HelloSafety.
            if (!helloAlreadyResolved
                && state.HelloPolicyEnabled?.Value == false
                && ShouldTransitionToAwaitingHello(state))
            {
                builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
                builder.HelloOutcome = new SignalFact<string>("Skipped", signal.SessionSignalOrdinal);

                // Cancel HelloSafety if it was armed by an earlier EspExiting — both in the
                // reducer state AND as a scheduler-visible CancelDeadline effect, so the
                // external timer (DefaultComponentFactory's scheduler) doesn't fire the
                // deadline post-Completion. The effect is only emitted when the deadline was
                // actually present; otherwise it would be a no-op for the scheduler but adds
                // noise to the audit trail.
                List<DecisionEffect>? extraEffects = null;
                var helloSafetyWasArmed = false;
                foreach (var d in state.Deadlines)
                {
                    if (d.Name == DeadlineNames.HelloSafety) { helloSafetyWasArmed = true; break; }
                }
                if (helloSafetyWasArmed)
                {
                    builder.CancelDeadline(DeadlineNames.HelloSafety);
                    extraEffects = new List<DecisionEffect>
                    {
                        new DecisionEffect(
                            DecisionEffectKind.CancelDeadline,
                            cancelDeadlineName: DeadlineNames.HelloSafety),
                    };
                }

                // Completion gates (ARCH-F1): synthetic Hello + Desktop are recorded; complete
                // through Finalizing when all gates are open, else defer until a gate releases.
                return CompleteThroughFinalizingOrDefer(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.DesktopArrived) + ":HelloDisabledFastPath",
                    leadingEffects: extraEffects);
            }

            // Plan §5 Fix 6: mirror of HandleHelloResolvedV1. When both prerequisites are in,
            // go through Finalizing (non-terminal) instead of jumping straight to Completed.
            // Session 8b8d611d fix (2026-05-20): when HelloSafety is still armed in the live
            // state (deferred-promote path from HandleAccountSetupProvisioningCompleteV1 — the
            // HelloResolved branch did not clear it because Hello came in via the Hello-timeout
            // synthesis after this Desktop signal in some orderings), cancel the scheduler
            // timer too. Without the effect the timer fires after Completed and re-enters
            // HandleHelloSafetyDeadlineFired → TransitionToFinalizing → duplicate
            // enrollment_complete.
            if (helloAlreadyResolved)
            {
                var helloSafetyCancelEffect = BuildHelloSafetyCancelEffectIfArmed(state);
                if (helloSafetyCancelEffect != null)
                {
                    builder.CancelDeadline(DeadlineNames.HelloSafety);
                }

                // Completion gates (ARCH-F1): Desktop fact recorded; complete through Finalizing
                // when all gates are open, else defer.
                return CompleteThroughFinalizingOrDefer(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.DesktopArrived),
                    leadingEffects: helloSafetyCancelEffect != null
                        ? new[] { helloSafetyCancelEffect }
                        : null);
            }

            // Desktop came first: keep current stage (AwaitingHello or EspAccountSetup) until
            // HelloResolved arrives.
            builder.WithStage(state.Stage);

            // Liveness plan PR2: Desktop is in but completion is still blocked (Hello pending,
            // or the Hello-disabled fast-path's strong gate is not satisfied) — surface the
            // missing prerequisites. Covers both the gate-false fast-path and the plain
            // desktop-first ordering; the fingerprint dedupes against earlier emissions.
            var waitingEffect = BuildCompletionWaitingEffect(
                state, builder, signal, trigger: nameof(DecisionSignalKind.DesktopArrived));

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.DesktopArrived));

            return new DecisionStep(
                newState,
                transition,
                waitingEffect != null ? new[] { waitingEffect } : Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.ImeUserSessionCompleted"/>. Primarily a
        /// <c>EnrollmentType</c>-hypothesis strengthener (IME's user-session-complete pattern
        /// is a strong UserDriven-v1 indicator), and records the matched pattern id.
        /// <para>
        /// Additionally records <see cref="DecisionState.ImeUserSessionCompletedUtc"/>
        /// (set-once — the first observation wins, replays keep the original anchor). The fact
        /// is NOT a completion trigger here: the IME "user session" can run under
        /// <c>defaultuser0</c>, so the raw signal proves nothing about the real user. The
        /// <c>AdvisoryCompletion</c> deadline handler consumes it lazily inside a correlation
        /// conjunction (see <c>HandleAdvisoryCompletionDeadlineFired</c>).
        /// </para>
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

            if (state.ImeUserSessionCompletedUtc == null)
            {
                builder.ImeUserSessionCompletedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
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
            string trigger,
            IReadOnlyList<DecisionEffect>? extraLeadingEffects = null)
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

            // extraLeadingEffects (e.g. a Hello-safety CancelDeadline from the Hello-disabled
            // fast-path) lead the schedule + phase-transition emit so the scheduler observes the
            // cancel before any subsequent deadline arm. Most callers pass null.
            var effects = new List<DecisionEffect>(
                capacity: (extraLeadingEffects?.Count ?? 0) + 2);
            if (extraLeadingEffects != null && extraLeadingEffects.Count > 0)
            {
                effects.AddRange(extraLeadingEffects);
            }
            effects.Add(new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: deadline));
            effects.Add(BuildPhaseTransitionEffect(EnrollmentPhase.FinalizingSetup));

            return new DecisionStep(newState, transition, effects.ToArray());
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

            // Plan v9 Phase 4 — UI phase coverage: emit phase_transition(Complete) before
            // enrollment_complete so the Web timeline opens the Complete phase bar. The prior
            // phase_transition(FinalizingSetup) was already emitted by TransitionToFinalizing
            // (Classic.cs:609) when this deadline was armed.
            var effects = new[]
            {
                BuildPhaseTransitionEffect(EnrollmentPhase.Complete),
                BuildEnrollmentCompleteEffect(newState, $"DeadlineFired:{DeadlineNames.FinalizingGrace}"),
            };

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
                    ["eventType"] = SharedConstants.EventTypes.PhaseTransition,
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
                    ["eventType"] = SharedConstants.EventTypes.EnrollmentComplete,
                },
                typedPayload: DecisionAuditTrailBuilder.Build(
                    postState: state,
                    decidedStage: state.Stage,
                    trigger: completionTrigger));
        }
    }
}
