using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Lifecycle + cross-scenario shared handlers. Plan §2.5 partial-class layout.
    public sealed partial class DecisionEngine
    {
        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionStarted"/>. Plan §2.7 / §4.x M4.4.4.
        /// <para>
        /// For a fresh session this is the first signal and the state already equals
        /// <see cref="DecisionState.CreateInitial(string, string)"/>. The handler still runs
        /// through the pipeline so the start is recorded as a journal transition (step 0) —
        /// this anchors the Inspector timeline.
        /// </para>
        /// <para>
        /// Also arms the <see cref="DeadlineNames.ClassifierTick"/> deadline up-front
        /// (Plan §4.x M4.4 re-trigger-lücke fix): the legacy reactive arming in
        /// <c>AttachWhiteGloveClassifierEffects</c> only fired on the first WG-relevant
        /// signal, which meant non-WG or late-WG sessions never re-evaluated the classifier.
        /// Arming from SessionStarted guarantees a periodic classifier pass; the existing
        /// <c>hasTick</c> dedup in <c>AttachWhiteGloveClassifierEffects</c> makes the reactive
        /// arm a no-op when a tick is already present.
        /// </para>
        /// <para>
        /// If the engine sees <c>SessionStarted</c> on a state whose stage is already
        /// something other than <see cref="SessionStage.SessionStarted"/>, we treat it as a
        /// defensive no-op (dead-end) rather than silently reinitializing — replay of a
        /// truncated log should fail visibly, not reset hard-won hypotheses.
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionStartedV1(DecisionState state, DecisionSignal signal)
        {
            if (state.Stage != SessionStage.SessionStarted && state.StepIndex != 0)
            {
                var bookkeptDead = BumpStepBookkeeping(state, signal);
                return new DecisionStep(
                    bookkeptDead,
                    BuildDeadEndTransition(
                        state: state,
                        signal: signal,
                        nextStepIndex: bookkeptDead.StepIndex,
                        trigger: nameof(DecisionSignalKind.SessionStarted),
                        deadEndReason: $"session_started_in_active_state:{state.Stage}"),
                    Array.Empty<DecisionEffect>());
            }

            // Arm ClassifierTick up-front so the White-Glove classifier re-evaluates
            // periodically from the very start of the session — Plan §4.x M4.4.4.
            // Floor the base at AgentBootUtc to keep the tick anchored to "now" even when
            // SessionStarted itself ever carries a replayed historical timestamp.
            var classifierTick = BuildClassifierTickDeadline(EffectiveDeadlineBase(state, signal));

            // V2 race-fix (10c8e0bf debrief, 2026-04-26): SessionStarted is now a pure
            // lifecycle anchor (stage / classifier-tick deadline / step bookkeeping).
            // The registry-derived profile facts (enrollmentType + isHybridJoin) flow
            // through the dedicated DecisionSignalKind.EnrollmentFactsObserved signal,
            // which is stage-agnostic and therefore immune to the V2-Tracker /
            // Backend-register-session race that previously dropped the JoinMode update.
            var builder = state.ToBuilder()
                .WithStage(SessionStage.SessionStarted)
                .WithStepIndex(state.StepIndex + 1)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .AddDeadline(classifierTick);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.SessionStarted,
                nextStepIndex: newState.StepIndex,
                trigger: nameof(DecisionSignalKind.SessionStarted));

            var effects = new DecisionEffect[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: classifierTick),
            };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EffectInfrastructureFailure"/>. Codex
        /// follow-up #2 — posted synchronously by the EffectRunner when a critical
        /// effect (ScheduleDeadline / CancelDeadline) cannot reach the scheduler.
        /// Without this reducer branch, <see cref="EffectRunResult.SessionMustAbort"/>
        /// would be observable only in the log and the session could hang on a
        /// phantom deadline that was never actually armed.
        /// <para>
        /// Transition shape mirrors <see cref="HandleSessionAbortedV1"/>: stage →
        /// <see cref="SessionStage.Failed"/>, outcome →
        /// <see cref="SessionOutcome.EnrollmentFailed"/> (distinguishes an
        /// infrastructure-induced failure from a user/operator-initiated
        /// <see cref="SessionOutcome.Aborted"/>), all <c>ActiveDeadlines</c> cleared.
        /// One <see cref="DecisionEffectKind.EmitEventTimelineEntry"/> effect
        /// publishes <c>enrollment_failed</c> so backend + UI see a proper terminal
        /// record with the failure reason from the signal payload.
        /// </para>
        /// </summary>
        private DecisionStep HandleEffectInfrastructureFailureV1(DecisionState state, DecisionSignal signal)
        {
            var reason = signal.Payload != null && signal.Payload.TryGetValue("reason", out var r) && !string.IsNullOrEmpty(r)
                ? r
                : "effect_infrastructure_failure";

            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithLastFailureTrigger(nameof(DecisionSignalKind.EffectInfrastructureFailure), signal.SessionSignalOrdinal)
                .ClearDeadlines()
                .Build();

            var failEffect = new DecisionEffect(
                DecisionEffectKind.EmitEventTimelineEntry,
                parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["eventType"] = "enrollment_failed",
                    ["reason"] = reason,
                },
                typedPayload: DecisionAuditTrailBuilder.Build(
                    postState: newState,
                    decidedStage: SessionStage.Failed,
                    trigger: nameof(DecisionSignalKind.EffectInfrastructureFailure),
                    failureReason: reason));

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Failed,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EffectInfrastructureFailure));

            return new DecisionStep(newState, transition, new[] { failEffect });
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AppInstallCompleted"/>. Codex follow-up #4 —
        /// observation-only: advance bookkeeping, roll the terminal outcome into
        /// <see cref="DecisionState.AppInstallFacts"/>, record a taken transition. Does NOT
        /// affect <see cref="SessionStage"/> or <see cref="SessionOutcome"/>; downstream
        /// consumers (Inspector UI, future classifiers) read the aggregate directly.
        /// <para>
        /// Payload contract (from <c>ImeLogTrackerAdapter</c>): <c>appId</c>, <c>newState</c>
        /// ∈ {<c>Installed</c>, <c>Skipped</c>, <c>Postponed</c>}. Unknown <c>newState</c>
        /// values still count toward <see cref="AppInstallFacts.CompletedCount"/> but do not
        /// update any breakdown counter.
        /// </para>
        /// </summary>
        private DecisionStep HandleAppInstallCompletedV1(DecisionState state, DecisionSignal signal)
        {
            var newStatePayload = signal.Payload != null && signal.Payload.TryGetValue("newState", out var v)
                ? v
                : null;
            var updatedFacts = state.AppInstallFacts.WithCompleted(newStatePayload);

            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithAppInstallFacts(updatedFacts)
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.AppInstallCompleted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AppInstallFailed"/>. Codex follow-up #4 —
        /// observation-only: advance bookkeeping, roll the failure into
        /// <see cref="AppInstallFacts"/> (increments counter, appends <c>appId</c> up to
        /// <see cref="AppInstallFacts.MaxFailedAppIds"/>), record a taken transition.
        /// </summary>
        private DecisionStep HandleAppInstallFailedV1(DecisionState state, DecisionSignal signal)
        {
            var appId = signal.Payload != null && signal.Payload.TryGetValue("appId", out var v)
                ? v
                : null;
            var updatedFacts = state.AppInstallFacts.WithFailed(appId);

            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithAppInstallFacts(updatedFacts)
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.AppInstallFailed));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionAborted"/>.
        /// <para>
        /// Emitted by the orchestrator, never by a collector. Stage transitions to
        /// <see cref="SessionStage.Failed"/> with <see cref="SessionOutcome.Aborted"/>.
        /// This is a terminal event; the orchestrator uses it to record admin-kill /
        /// override actions cleanly without going through the regular completion paths
        /// (plan §2.7 admin-action audit).
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionAbortedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.Aborted)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithLastFailureTrigger(nameof(DecisionSignalKind.SessionAborted), signal.SessionSignalOrdinal)
                .ClearDeadlines()
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Failed,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.SessionAborted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AdminPreemptionDetected"/>. Plan §2.7 admin-
        /// action audit; V2 parity PR-B3.
        /// <para>
        /// Emitted by <c>Program.RunAgent</c> when the register-session response carries an
        /// <c>AdminAction</c> value (operator marked the session terminal via the portal before
        /// the agent even started). The signal payload carries
        /// <c>adminOutcome=Succeeded|Failed</c>.
        /// </para>
        /// <para>
        /// Stage transitions to <see cref="SessionStage.Completed"/> (Succeeded) or
        /// <see cref="SessionStage.Failed"/> (anything else); <see cref="SessionOutcome.AdminPreempted"/>
        /// captures the non-enrollment nature of the transition so dashboards + KQL can tell
        /// an admin-override apart from a genuine enrollment_complete/_failed.
        /// </para>
        /// </summary>
        private DecisionStep HandleAdminPreemptionDetectedV1(DecisionState state, DecisionSignal signal)
        {
            var adminOutcome = signal.Payload != null && signal.Payload.TryGetValue("adminOutcome", out var v)
                ? v
                : "Failed"; // defensive default: preemption without outcome is treated as failure.

            var succeeded = string.Equals(adminOutcome, "Succeeded", StringComparison.OrdinalIgnoreCase);
            var toStage = succeeded ? SessionStage.Completed : SessionStage.Failed;
            var eventType = succeeded ? "enrollment_complete" : "enrollment_failed";

            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStage(toStage)
                .WithOutcome(SessionOutcome.AdminPreempted)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .ClearDeadlines()
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: toStage,
                nextStepIndex: nextStep,
                trigger: $"AdminPreemption:{adminOutcome}");

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: new Dictionary<string, string>
                    {
                        ["eventType"] = eventType,
                        ["adminAction"] = adminOutcome,
                        ["source"] = signal.SourceOrigin ?? "register_session_response",
                        ["reason"] = $"Session {adminOutcome.ToLowerInvariant()} by administrator (detected on register-session).",
                    }),
            };

            return new DecisionStep(newState, transition, effects);
        }

        // ============================================================== shared helpers
        // Partial-class shared helpers used by Classic / SelfDeploying / WhiteGlove handlers
        // as they come online in M3.1+ live below. M3.0 establishes the skeleton; the bodies
        // grow with each sub-milestone.

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DeadlineFired"/>. Plan §2.6.
        /// <para>
        /// The payload carries <see cref="SignalPayloadKeys.Deadline"/> = the deadline name
        /// (from <see cref="DeadlineNames"/>). The handler removes the corresponding
        /// <see cref="ActiveDeadline"/> from state and dispatches to a deadline-specific body.
        /// Deadlines for stages that don't yet exist in this sub-milestone (e.g. Part-2
        /// safety) land in the Unknown-Deadline path — they complete bookkeeping without
        /// changing state, which lets M3.0 replay logs that contain future deadline names.
        /// </para>
        /// </summary>
        private DecisionStep HandleDeadlineFiredV1(DecisionState state, DecisionSignal signal)
        {
            var deadlineName = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.Deadline, out var n)
                ? n
                : null;

            if (string.IsNullOrEmpty(deadlineName))
            {
                var bookkeptDead = BumpStepBookkeeping(state, signal);
                return new DecisionStep(
                    bookkeptDead,
                    BuildDeadEndTransition(
                        state: state,
                        signal: signal,
                        nextStepIndex: bookkeptDead.StepIndex,
                        trigger: nameof(DecisionSignalKind.DeadlineFired),
                        deadEndReason: "deadline_fired_without_name"),
                    Array.Empty<DecisionEffect>());
            }

            switch (deadlineName)
            {
                case DeadlineNames.HelloSafety:
                    return HandleHelloSafetyDeadlineFired(state, signal);
                case DeadlineNames.DeviceOnlyEspDetection:
                    return HandleDeviceOnlyEspDetectionDeadlineFired(state, signal);
                case DeadlineNames.ClassifierTick:
                    return HandleClassifierTickDeadlineFired(state, signal);
                case DeadlineNames.FinalizingGrace:
                    return HandleFinalizingGraceDeadlineFired(state, signal);
                default:
                    // Deadline name not recognized in this sub-milestone. Cancel it from state
                    // and record a neutral taken transition — M3.3+ adds handlers for
                    // ClassifierTick, etc.
                    var nextStepIgnored = state.StepIndex + 1;
                    var cancelled = state.ToBuilder()
                        .WithStepIndex(nextStepIgnored)
                        .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                        .CancelDeadline(deadlineName!)
                        .Build();
                    var transitionIgnored = BuildTakenTransition(
                        before: state,
                        signal: signal,
                        toStage: state.Stage,
                        nextStepIndex: nextStepIgnored,
                        trigger: $"DeadlineFired:{deadlineName}");
                    return new DecisionStep(cancelled, transitionIgnored, Array.Empty<DecisionEffect>());
            }
        }

        /// <summary>
        /// Hello-safety deadline fired: the post-ESP grace window expired without a
        /// <see cref="DecisionSignalKind.HelloResolved"/>. Treat as a Hello timeout — the
        /// session completes with <see cref="DecisionState.HelloOutcome"/>=<c>Timeout</c>
        /// if Desktop has also arrived; otherwise we stay in <see cref="SessionStage.AwaitingDesktop"/>
        /// and the downstream <c>DesktopArrived</c> handler completes the session.
        /// </summary>
        private DecisionStep HandleHelloSafetyDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.HelloSafety);

            // If Hello already resolved before the deadline fired (race), the fact is already
            // set; don't overwrite it. Otherwise record the synthetic timeout.
            if (state.HelloResolvedUtc == null)
            {
                builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
                builder.HelloOutcome = new SignalFact<string>("Timeout", signal.SessionSignalOrdinal);
            }

            var desktopAlreadyArrived = state.DesktopArrivedUtc != null;

            // Plan §5 Fix 6: route the both-prerequisites case through Finalizing so the
            // synthetic-timeout terminal event shares the same grace window + phase-declaration
            // pathway as the happy-path handlers. AwaitingDesktop path is unchanged.
            if (desktopAlreadyArrived)
            {
                return TransitionToFinalizing(
                    state: state,
                    signal: signal,
                    preparedBuilder: builder,
                    nextStepIndex: nextStep,
                    trigger: $"DeadlineFired:{DeadlineNames.HelloSafety}");
            }

            builder.WithStage(SessionStage.AwaitingDesktop);
            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.AwaitingDesktop,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.HelloSafety}");

            // AwaitingDesktop path: terminal effect is deferred to the later DesktopArrived
            // handler (which will itself route through Finalizing per Fix 6).
            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        // ============================================================== diagnostic signals
        // Plan §4.x M4.4.3 — close the reducer-handler gap for signals that carry useful
        // telemetry but do NOT influence the state machine. Previously these fell through to
        // HandleUnhandledSignal, which wrote a DeadEnd transition every time — noise in the
        // journal. Now they record as neutral taken transitions; full payload/evidence
        // remains in the SignalLog for Inspector analysis.

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DeviceInfoCollected"/>. Diagnostic-only —
        /// carries hardware inventory in payload, does not drive stage or hypothesis.
        /// </summary>
        private DecisionStep HandleDeviceInfoCollectedV1(DecisionState state, DecisionSignal signal) =>
            RecordDiagnosticObservation(state, signal, nameof(DecisionSignalKind.DeviceInfoCollected));

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AutopilotProfileRead"/>. Diagnostic-only —
        /// carries Autopilot profile registry contents, does not drive stage or hypothesis.
        /// </summary>
        private DecisionStep HandleAutopilotProfileReadV1(DecisionState state, DecisionSignal signal) =>
            RecordDiagnosticObservation(state, signal, nameof(DecisionSignalKind.AutopilotProfileRead));

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspConfigDetected"/>. Plan §6 Fix 9 +
        /// Codex follow-up #5.
        /// <para>
        /// Populates <see cref="EnrollmentScenarioObservations.SkipUserEsp"/> /
        /// <see cref="EnrollmentScenarioObservations.SkipDeviceEsp"/> observations and, when
        /// both halves are known, derives <see cref="EnrollmentScenarioProfile.EspConfig"/>.
        /// Set-once semantics — later signals with identical payload are no-ops, and keys
        /// missing from the payload never clear a fact that was already set (a re-read that
        /// failed to pick up one half must not invalidate the other). Stage is unchanged;
        /// this is a fact-only signal that Fix 8's reducer guards read via
        /// <see cref="EnrollmentScenarioProfile.EspConfig"/>.
        /// </para>
        /// </summary>
        private DecisionStep HandleEspConfigDetectedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            var (newProfile, newObservations) = EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected(
                builder.ScenarioProfile, builder.ScenarioObservations, signal);
            builder.ScenarioProfile = newProfile;
            builder.ScenarioObservations = newObservations;

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspConfigDetected));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// V2 race-fix (10c8e0bf debrief, 2026-04-26) — record the registry-derived
        /// enrollment facts (<c>enrollmentType</c> + <c>isHybridJoin</c>) on the
        /// scenario profile. Stage is unchanged; this is a fact-only signal whose
        /// reducer is intentionally stage-agnostic so it can land at any point in the
        /// session without being swallowed by a Stage-Wache (the bug that motivated
        /// this signal: the legacy <see cref="DecisionSignalKind.SessionStarted"/>
        /// path lost the same payload when other signals had already advanced Stage
        /// past <see cref="SessionStage.SessionStarted"/>).
        /// <para>
        /// Idempotency / monotonicity are owned by
        /// <see cref="EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved"/> —
        /// the handler delegates the merge logic and only carries out the standard
        /// step bookkeeping + transition.
        /// </para>
        /// </summary>
        private DecisionStep HandleEnrollmentFactsObservedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            builder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplyEnrollmentFactsObserved(
                builder.ScenarioProfile, signal);

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EnrollmentFactsObserved));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// PR4 (882fef64 debrief) — record the WHfB / Hello-for-Business policy fact on the
        /// engine state. Stage is NOT changed; this is a fact-only signal that downstream
        /// observers (HelloTracker wait cadence, mismatch detector) can read off
        /// <see cref="DecisionState.HelloPolicyEnabled"/>. Set-once: a prior fact with the
        /// same value is a no-op, a different value updates with the new ordinal so the
        /// Inspector evidence trace points at the most recent re-detection.
        /// </summary>
        /// <remarks>
        /// Completion-gating is intentionally not influenced here — the existing Hello+Desktop
        /// AND-gate accepts any terminal Hello outcome (success/skip/not_configured). See
        /// <c>feedback_hello_policy_wait_not_completion</c>. Missing or unparseable
        /// <see cref="SignalPayloadKeys.HelloEnabled"/> → DeadEnd transition so the malformed
        /// signal is visible in the transitions table.
        /// </remarks>
        private DecisionStep HandleHelloPolicyDetectedV1(DecisionState state, DecisionSignal signal)
        {
            var payload = signal.Payload;
            if (payload == null
                || !payload.TryGetValue(SignalPayloadKeys.HelloEnabled, out var rawEnabled)
                || !bool.TryParse(rawEnabled, out var helloEnabled))
            {
                var deadEnd = BumpStepBookkeeping(state, signal);
                var deadEndTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: deadEnd.StepIndex,
                    trigger: nameof(DecisionSignalKind.HelloPolicyDetected),
                    deadEndReason: "hello_policy_detected_missing_helloEnabled");
                return new DecisionStep(deadEnd, deadEndTransition, Array.Empty<DecisionEffect>());
            }

            // Set-once: same value, same ordinal-or-later → no-op (skip the update). Different
            // value → update so a fixed detector / corrected reading is reflected.
            if (state.HelloPolicyEnabled != null
                && state.HelloPolicyEnabled.Value == helloEnabled)
            {
                var unchanged = BumpStepBookkeeping(state, signal);
                var noOpTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: unchanged.StepIndex,
                    trigger: nameof(DecisionSignalKind.HelloPolicyDetected) + ":no-op");
                return new DecisionStep(unchanged, noOpTransition, Array.Empty<DecisionEffect>());
            }

            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .WithHelloPolicyEnabled(helloEnabled, signal.SessionSignalOrdinal)
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.HelloPolicyDetected));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        private DecisionStep RecordDiagnosticObservation(DecisionState state, DecisionSignal signal, string trigger)
        {
            var newState = BumpStepBookkeeping(state, signal);
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: newState.StepIndex,
                trigger: trigger);
            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.InformationalEvent"/>. Pure pass-through for
        /// the single-rail refactor (plan §1.3): the signal payload is copied 1:1 into an
        /// <see cref="DecisionEffectKind.EmitEventTimelineEntry"/> effect and the
        /// <see cref="Telemetry.Events.EventTimelineEmitter"/> reconstructs the
        /// <c>EnrollmentEvent</c> from the <see cref="SignalPayloadKeys"/>. DecisionState is
        /// unchanged apart from the standard bookkeeping (<c>StepIndex</c>,
        /// <c>LastAppliedSignalOrdinal</c>) — this handler is deliberately not a decision point.
        /// <para>
        /// <b>Validation</b>: <see cref="SignalPayloadKeys.EventType"/> and
        /// <see cref="SignalPayloadKeys.Source"/> are mandatory. A missing / empty key produces
        /// a <c>DeadEnd</c> transition with reason
        /// <c>informational_event_missing_{key}</c> so the malformed signal is visible in the
        /// transitions table instead of silently reaching the emitter with a throw (kernel
        /// fail-safe would also catch it, but with a less descriptive reason).
        /// </para>
        /// <para>
        /// <b>Promotion path</b>: if a sender later needs a specific pass-through to influence
        /// a decision, swap the signal kind for a dedicated one (e.g.
        /// <c>PlatformScriptCompleted</c>) and add a state-mutating reducer case. The emission
        /// contract and UI shape stay identical because the emitter still receives the same
        /// parameter keys.
        /// </para>
        /// </summary>
        private DecisionStep HandleInformationalEventV1(DecisionState state, DecisionSignal signal)
        {
            var payload = signal.Payload;

            if (payload == null
                || !payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)
                || string.IsNullOrEmpty(eventType))
            {
                return BuildInformationalEventDeadEnd(state, signal, SignalPayloadKeys.EventType);
            }
            if (!payload.TryGetValue(SignalPayloadKeys.Source, out var source)
                || string.IsNullOrEmpty(source))
            {
                return BuildInformationalEventDeadEnd(state, signal, SignalPayloadKeys.Source);
            }

            var newState = BumpStepBookkeeping(state, signal);
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: newState.StepIndex,
                trigger: nameof(DecisionSignalKind.InformationalEvent));

            // Effect parameters are the signal payload verbatim. EventTimelineEmitter extracts
            // the reserved top-level keys (eventType, source, severity, message, phase,
            // immediateUpload) and keeps the rest as Data entries. The TypedPayload sidecar —
            // when present — carries the original EnrollmentEvent.Data dictionary through the
            // bus with its nested structure intact; the emitter prefers it over reconstructing
            // Data from the string parameters (single-rail refactor plan §1.3).
            var effect = new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: payload,
                typedPayload: signal.TypedPayload);

            return new DecisionStep(newState, transition, new[] { effect });
        }

        private DecisionStep BuildInformationalEventDeadEnd(DecisionState state, DecisionSignal signal, string missingKey)
        {
            var bookkept = BumpStepBookkeeping(state, signal);
            var transition = BuildDeadEndTransition(
                state: state,
                signal: signal,
                nextStepIndex: bookkept.StepIndex,
                trigger: nameof(DecisionSignalKind.InformationalEvent),
                deadEndReason: $"informational_event_missing_{missingKey}");
            return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Plan §6 Fix 8 — gate for promoting to <see cref="SessionStage.AwaitingHello"/>
        /// on Classic V1 paths. Returns <c>true</c> only when the promotion is legitimate:
        /// <list type="bullet">
        ///   <item>AccountSetup has already been entered (the post-Account-ESP final exit case), or</item>
        ///   <item><see cref="EnrollmentScenarioObservations.SkipUserEsp"/> is observed as
        ///         <c>true</c> (Account-ESP phase is skipped; first esp_exiting IS the final
        ///         exit on device-only / full-skip flows).</item>
        /// </list>
        /// Otherwise returns <c>false</c> — a FinalizingSetup / EspExiting signal arriving
        /// before AccountSetup on a non-SkipUser enrollment is either a collector bug or the
        /// Device-ESP intermediate exit that Fix 7 otherwise swallows at the tracker layer.
        /// Keeping this helper in the reducer means even a regression in Fix 7 cannot drive a
        /// premature <c>AwaitingHello</c> + HelloSafety arm.
        /// <para>
        /// Codex follow-up #5 + post-#51 fix: the legacy <c>state.SkipUserEsp?.Value == true</c>
        /// check now reads <see cref="DecisionState.ScenarioObservations"/>.<see cref="EnrollmentScenarioObservations.SkipUserEsp"/>
        /// directly — NOT the derived <see cref="EnrollmentScenarioProfile.EspConfig"/> enum.
        /// The derived enum requires BOTH halves (skipUser + skipDevice) to be observed before
        /// it leaves <see cref="EspConfig.Unknown"/>, so a partial bootstrap payload carrying
        /// only <c>skipUser=true</c> would block the promotion indefinitely under the previous
        /// pass. The raw half-fact mirrors the old <c>SkipUserEsp?.Value</c> behaviour exactly.
        /// </para>
        /// </summary>
        private static bool ShouldTransitionToAwaitingHello(DecisionState state)
        {
            if (state.AccountSetupEnteredUtc != null) return true;
            if (state.ScenarioObservations.SkipUserEsp?.Value == true) return true;
            return false;
        }

        /// <summary>
        /// Floor a deadline-arming base timestamp at the current agent run's boot anchor.
        /// Replay-safety guard: when a signal carries a historical <c>OccurredAtUtc</c> from
        /// a CMTrace log entry or backfilled event-log record, naively using it as
        /// <c>dueAtUtc = signal.OccurredAtUtc + window</c> would produce a past-due deadline
        /// that the scheduler fires immediately, collapsing real-time semantics (Hello timeout,
        /// device-only ESP detection, finalizing grace) into "agent woke up just now and
        /// everything already expired".
        /// <para>
        /// Returns <c>signal.OccurredAtUtc</c> when it is at or after
        /// <see cref="DecisionState.AgentBootUtc"/>, otherwise the boot anchor. Falls back to
        /// the raw signal time when the state has no boot anchor (legacy snapshots from
        /// before this field existed) — preserves the prior, pre-fix behavior on those.
        /// </para>
        /// </summary>
        internal static DateTime EffectiveDeadlineBase(DecisionState state, DecisionSignal signal)
        {
            var boot = state.AgentBootUtc;
            if (boot.HasValue && signal.OccurredAtUtc < boot.Value)
                return boot.Value;
            return signal.OccurredAtUtc;
        }

        /// <summary>
        /// Determine the user-visible enrollment phase implied by an ESP phase-change signal.
        /// Plan §2.3 phase-fact mapping. Populated in M3.1 as Classic handlers come online.
        /// </summary>
        internal static EnrollmentPhase MapEspPhaseToEnrollmentPhase(string rawPhase)
        {
            if (string.IsNullOrEmpty(rawPhase)) return EnrollmentPhase.Unknown;
            return rawPhase switch
            {
                "DeviceSetup" => EnrollmentPhase.DeviceSetup,
                "AccountSetup" => EnrollmentPhase.AccountSetup,
                "FinalizingSetup" => EnrollmentPhase.FinalizingSetup,
                "Finalizing" => EnrollmentPhase.FinalizingSetup,
                "Complete" => EnrollmentPhase.Complete,
                _ => EnrollmentPhase.Unknown,
            };
        }
    }
}
