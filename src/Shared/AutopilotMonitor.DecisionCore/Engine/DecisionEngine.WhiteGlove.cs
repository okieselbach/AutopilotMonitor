using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // WhiteGlove Part 1 handlers + classifier-verdict routing. Plan §2.5 / §2.4.
    public sealed partial class DecisionEngine
    {
        // Plan §2.6 — ClassifierTick recurrence. 30 s is the legacy polling cadence.
        internal static readonly TimeSpan s_classifierTickInterval = TimeSpan.FromSeconds(30);

        // Option 3 (WG Part 1 graceful-exit hardening, 2026-04-30): inline classifier
        // instance, used only by the strong-signal fast-path in
        // <see cref="HandleWhiteGloveShellCoreSuccessV1"/>. Stateless / pure — safe to share.
        private static readonly WhiteGloveSealingClassifier s_whiteGloveSealingClassifier =
            new WhiteGloveSealingClassifier();

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.WhiteGloveShellCoreSuccess"/>. Records the
        /// <see cref="DecisionState.ShellCoreWhiteGloveSuccessSeen"/> fact.
        /// <para>
        /// <b>Fast-path</b> (Option 3 of the WG Part 1 graceful-exit hardening, 2026-04-30):
        /// the WG-sealing classifier is a pure function of the snapshot built from this state,
        /// so we can run it inline. When ShellCoreSuccess arrives in a state that scores
        /// <see cref="HypothesisLevel.Confirmed"/> on its own (the typical WG Part 1 case —
        /// no AAD-with-user / desktop / hello observed yet), we transition straight to
        /// <see cref="SessionStage.WhiteGloveSealed"/> + <see cref="SessionOutcome.WhiteGlovePart1Sealed"/>
        /// in this single reducer step and emit <c>whiteglove_complete</c>. This collapses
        /// the previous two-step round-trip (RunClassifier effect → ClassifierVerdictIssued
        /// signal → reducer applies verdict) into one, eliminating the journal+snapshot
        /// write between the two, which buys us tens to hundreds of milliseconds before the
        /// admin-triggered reseal-reboot pre-empts the agent.
        /// </para>
        /// <para>
        /// <b>Slow-path fallback</b>: when an excluding observation is already present (e.g.
        /// <c>AadUserJoinWithUserObserved</c>) the inline classifier scores below
        /// <see cref="WhiteGloveSealingClassifier.HighThreshold"/>, so we keep the legacy
        /// behaviour — emit a <see cref="DecisionEffectKind.RunClassifier"/> effect + arm
        /// the <see cref="DeadlineNames.ClassifierTick"/> deadline. The asymmetric-
        /// conservative semantics still apply: only Confirmed transitions sealed.
        /// </para>
        /// </summary>
        private DecisionStep HandleWhiteGloveShellCoreSuccessV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.ScenarioObservations = builder.ScenarioObservations.WithShellCoreWhiteGloveSuccessSeen(signal.SessionSignalOrdinal);

            // Option 3 fast-path: inline classifier evaluation on the strong WG signal.
            var afterObservation = builder.Build();
            var inlineSnapshot = BuildWhiteGloveSealingSnapshot(afterObservation);
            var inlineVerdict = s_whiteGloveSealingClassifier.Classify(inlineSnapshot);

            if (inlineVerdict.Level == HypothesisLevel.Confirmed)
            {
                var sealedBuilder = afterObservation.ToBuilder();

                // Mirror the WG decision in the profile: Mode=WhiteGlove @ High confidence —
                // identical to the slow-path branch in HandleClassifierVerdictIssuedV1 so
                // downstream consumers see the same ScenarioProfile regardless of which
                // path produced the verdict.
                sealedBuilder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplyWhiteGloveSealingConfirmed(
                    sealedBuilder.ScenarioProfile, signal);

                // Record the inline verdict on the classifier outcome — same shape as the
                // slow path. The verdict's InputHash also seeds the EffectRunner anti-loop
                // table (via ClassifierVerdictLookup) so any RunClassifier effect that might
                // still be in-flight (it shouldn't be, since we don't emit one here) would
                // skip with snapshotHash unchanged.
                var inlineSealing = afterObservation.ClassifierOutcomes.WhiteGloveSealing.With(
                    level: HypothesisLevel.Confirmed,
                    reason: inlineVerdict.Reason,
                    score: inlineVerdict.Score,
                    lastUpdatedUtc: signal.OccurredAtUtc,
                    lastClassifierVerdictId: inlineVerdict.InputHash);
                sealedBuilder.ClassifierOutcomes = afterObservation.ClassifierOutcomes.WithWhiteGloveSealing(inlineSealing);

                sealedBuilder
                    .WithStage(SessionStage.WhiteGloveSealed)
                    .WithOutcome(SessionOutcome.WhiteGlovePart1Sealed)
                    .ClearDeadlines();

                var sealedState = sealedBuilder.Build();
                var fastPathTrigger = $"{nameof(DecisionSignalKind.WhiteGloveShellCoreSuccess)}:FastPath:Confirmed";
                var sealedTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: SessionStage.WhiteGloveSealed,
                    nextStepIndex: nextStep,
                    trigger: fastPathTrigger);

                // Codex review follow-up (Finding 1, 2026-04-30) — the slow path attaches the
                // full DecisionAuditTrailBuilder.Build(...) payload (decisionSource, trigger,
                // sessionStage, signalsSeen, signalEvidence, scenario, classifier verdict,
                // classifierInputs). Without this the new normal WG Part 1 path emits a
                // degraded audit trail compared to the legacy two-step path. We replicate
                // the exact same build here using the inline verdict so the timeline event
                // (and any downstream forensics that rely on TypedPayload) are byte-stable
                // across the fast/slow paths.
                var inlineVerdictInfo = new ClassifierVerdictInfo(
                    id: inlineVerdict.ClassifierId,
                    level: inlineVerdict.Level.ToString(),
                    score: inlineVerdict.Score,
                    reason: inlineVerdict.Reason,
                    inputHash: inlineVerdict.InputHash);

                var sealedEffects = new[]
                {
                    new DecisionEffect(
                        DecisionEffectKind.EmitEventTimelineEntry,
                        parameters: new Dictionary<string, string> { ["eventType"] = "whiteglove_complete" },
                        typedPayload: DecisionAuditTrailBuilder.Build(
                            postState: sealedState,
                            decidedStage: SessionStage.WhiteGloveSealed,
                            trigger: fastPathTrigger,
                            classifier: inlineVerdictInfo,
                            classifierInputs: inlineSnapshot)),
                };

                return new DecisionStep(sealedState, sealedTransition, sealedEffects);
            }

            // Slow path — legacy behaviour, preserved as fallback when an excluding signal
            // already lives in state and pulls the inline score below HighThreshold.
            var (newState, effects) = AttachWhiteGloveClassifierEffects(afterObservation, signal);

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: newState.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.WhiteGloveShellCoreSuccess));

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.WhiteGloveSealingPatternDetected"/>. Signal-
        /// correlated WhiteGlove path (IME-pattern based). Records the fact and emits a
        /// <see cref="DecisionEffectKind.RunClassifier"/> effect.
        /// </summary>
        private DecisionStep HandleWhiteGloveSealingPatternDetectedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.ScenarioObservations = builder.ScenarioObservations.WithWhiteGloveSealingPatternSeen(signal.SessionSignalOrdinal);

            var (newState, effects) = AttachWhiteGloveClassifierEffects(builder.Build(), signal);

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: newState.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.WhiteGloveSealingPatternDetected));

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.ClassifierVerdictIssued"/>. Produced by the
        /// effect runner after it executed a <see cref="DecisionEffectKind.RunClassifier"/>
        /// effect. The payload carries the verdict; the handler updates
        /// <see cref="DecisionState.WhiteGloveSealing"/> (plan §2.3 hypothesis) and, on
        /// <see cref="HypothesisLevel.Confirmed"/>, transitions to
        /// <see cref="SessionStage.WhiteGloveSealed"/> + <see cref="SessionOutcome.WhiteGlovePart1Sealed"/>
        /// and emits the <c>whiteglove_complete</c> event.
        /// </summary>
        private DecisionStep HandleClassifierVerdictIssuedV1(DecisionState state, DecisionSignal signal)
        {
            var p = signal.Payload ?? new Dictionary<string, string>();
            var classifier = GetPayload(p, "classifier", "unknown");
            var levelRaw = GetPayload(p, "level", HypothesisLevel.Unknown.ToString());
            var scoreRaw = GetPayload(p, "score", "0");
            var reason = GetPayload(p, "reason", classifier);
            var inputHash = GetPayload(p, "inputHash", string.Empty);

            if (!Enum.TryParse<HypothesisLevel>(levelRaw, ignoreCase: true, out var level))
            {
                level = HypothesisLevel.Unknown;
            }
            if (!int.TryParse(scoreRaw, out var score))
            {
                score = 0;
            }

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            if (classifier == WhiteGloveSealingClassifier.ClassifierId)
            {
                var updatedSealing = state.ClassifierOutcomes.WhiteGloveSealing.With(
                    level: level,
                    reason: reason,
                    score: score,
                    lastUpdatedUtc: signal.OccurredAtUtc,
                    lastClassifierVerdictId: inputHash);
                builder.ClassifierOutcomes = state.ClassifierOutcomes.WithWhiteGloveSealing(updatedSealing);

                if (level == HypothesisLevel.Confirmed)
                {
                    // Mirror the WG decision in the profile: Mode=WhiteGlove @ High confidence.
                    builder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplyWhiteGloveSealingConfirmed(
                        builder.ScenarioProfile, signal);

                    builder
                        .WithStage(SessionStage.WhiteGloveSealed)
                        .WithOutcome(SessionOutcome.WhiteGlovePart1Sealed)
                        .ClearDeadlines();

                    var sealedState = builder.Build();
                    var sealedTransition = BuildTakenTransition(
                        before: state,
                        signal: signal,
                        toStage: SessionStage.WhiteGloveSealed,
                        nextStepIndex: nextStep,
                        trigger: $"ClassifierVerdictIssued:{classifier}:Confirmed");

                    var verdictInfo = new ClassifierVerdictInfo(classifier, level.ToString(), score, reason, inputHash);
                    var sealedEffects = new[]
                    {
                        new DecisionEffect(
                            DecisionEffectKind.EmitEventTimelineEntry,
                            parameters: new Dictionary<string, string> { ["eventType"] = "whiteglove_complete" },
                            typedPayload: DecisionAuditTrailBuilder.Build(
                                postState: sealedState,
                                decidedStage: SessionStage.WhiteGloveSealed,
                                trigger: $"ClassifierVerdictIssued:{classifier}:Confirmed",
                                classifier: verdictInfo,
                                classifierInputs: BuildWhiteGloveSealingSnapshot(sealedState))),
                    };

                    return new DecisionStep(sealedState, sealedTransition, sealedEffects);
                }
            }

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: $"ClassifierVerdictIssued:{classifier}:{level}");

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        // ============================================================ classifier-tick support

        /// <summary>
        /// Classifier-tick deadline fired. Re-evaluates the WhiteGlove classifier with the
        /// current snapshot (picks up late-arriving facts the original handlers did not see)
        /// and re-arms itself unless a terminal stage has been reached.
        /// </summary>
        private DecisionStep HandleClassifierTickDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.ClassifierTick);

            var effects = new List<DecisionEffect>
            {
                BuildRunClassifierEffect(state),
            };

            var reachedTerminal = state.Stage == SessionStage.Completed
                                  || state.Stage == SessionStage.Failed
                                  || state.Stage == SessionStage.WhiteGloveSealed;

            if (!reachedTerminal)
            {
                // The DeadlineFired signal carries DueAtUtc as OccurredAtUtc, so for a tick
                // armed in the current run this is already current-clock-equivalent. The
                // EffectiveDeadlineBase guard still fires correctly across restart, where
                // a stale DueAtUtc from the prior run could otherwise re-arm the next tick
                // in the past.
                var rearm = BuildClassifierTickDeadline(EffectiveDeadlineBase(state, signal));
                builder.AddDeadline(rearm);
                effects.Add(new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: rearm));
            }

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.ClassifierTick}");

            return new DecisionStep(newState, transition, effects.ToArray());
        }

        // ============================================================ internal helpers

        /// <summary>
        /// Attach WhiteGlove-classifier effects (RunClassifier + optional ClassifierTick arm)
        /// to a just-built state. Shared between <see cref="HandleWhiteGloveShellCoreSuccessV1"/>
        /// and <see cref="HandleWhiteGloveSealingPatternDetectedV1"/>.
        /// </summary>
        private (DecisionState NewState, DecisionEffect[] Effects) AttachWhiteGloveClassifierEffects(
            DecisionState state,
            DecisionSignal triggerSignal)
        {
            var effects = new List<DecisionEffect> { BuildRunClassifierEffect(state) };

            // Arm ClassifierTick on first WG-relevant signal if not already scheduled.
            var hasTick = false;
            foreach (var d in state.Deadlines)
            {
                if (d.Name == DeadlineNames.ClassifierTick) { hasTick = true; break; }
            }

            if (!hasTick)
            {
                // Replay-safety: a WG-relevant signal coming from a CMTrace replay would
                // otherwise arm the first classifier tick in the past — fires immediately,
                // pulls in a stale snapshot, no real harm but pollutes the timeline.
                var tick = BuildClassifierTickDeadline(EffectiveDeadlineBase(state, triggerSignal));
                state = state.ToBuilder().AddDeadline(tick).Build();
                effects.Add(new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: tick));
            }

            return (state, effects.ToArray());
        }

        private static DecisionEffect BuildRunClassifierEffect(DecisionState state) =>
            new DecisionEffect(
                kind: DecisionEffectKind.RunClassifier,
                classifierId: WhiteGloveSealingClassifier.ClassifierId,
                classifierSnapshot: BuildWhiteGloveSealingSnapshot(state));

        private static ActiveDeadline BuildClassifierTickDeadline(DateTime fromUtc) =>
            new ActiveDeadline(
                name: DeadlineNames.ClassifierTick,
                dueAtUtc: fromUtc.Add(s_classifierTickInterval),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.ClassifierTick,
                });

        /// <summary>
        /// Build a <see cref="WhiteGloveSealingSnapshot"/> from the current <see cref="DecisionState"/>.
        /// Exposed to the effect runner so the verdict snapshot carried with the
        /// <see cref="DecisionEffectKind.RunClassifier"/> effect is deterministic from state.
        /// <para>
        /// Codex follow-up #5 — the snapshot fields are unchanged, only their sources moved:
        /// the three Boolean signal-observation inputs come from
        /// <see cref="DecisionState.ScenarioObservations"/>, the device-only input from
        /// <see cref="DecisionState.ClassifierOutcomes"/>. The input-hash canonicalization in
        /// <see cref="WhiteGloveSealingSnapshot.ComputeInputHash"/> is therefore byte-stable
        /// across the refactor, which preserves the anti-loop semantics.
        /// </para>
        /// </summary>
        internal static WhiteGloveSealingSnapshot BuildWhiteGloveSealingSnapshot(DecisionState state) =>
            new WhiteGloveSealingSnapshot(
                shellCoreWhiteGloveSuccessSeen: state.ScenarioObservations.ShellCoreWhiteGloveSuccessSeen?.Value == true,
                whiteGloveSealingPatternSeen: state.ScenarioObservations.WhiteGloveSealingPatternSeen?.Value == true,
                aadJoinedWithUser: state.ScenarioObservations.AadUserJoinWithUserObserved?.Value == true,
                desktopArrived: state.DesktopArrivedUtc != null,
                helloResolved: state.HelloResolvedUtc != null,
                hasAccountSetupActivity: state.AccountSetupEnteredUtc != null,
                isDeviceOnlyDeploymentHypothesis:
                    state.ClassifierOutcomes.DeviceOnlyDeployment.Level >= HypothesisLevel.Strong &&
                    state.ClassifierOutcomes.DeviceOnlyDeployment.Reason == DeviceOnlyReasons.DeviceOnly,
                systemRebootUtc: state.SystemRebootUtc?.Value,
                currentEnrollmentPhase: state.CurrentEnrollmentPhase?.Value);

        private static string GetPayload(IReadOnlyDictionary<string, string> p, string key, string fallback) =>
            p.TryGetValue(key, out var v) ? v : fallback;
    }
}
