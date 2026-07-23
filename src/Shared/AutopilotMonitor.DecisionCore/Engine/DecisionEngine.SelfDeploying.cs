using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // SelfDeploying-v1 + Device-Only handlers. Plan §2.5 partial-class layout.
    public sealed partial class DecisionEngine
    {
        // 5-min wait between "DeviceSetup resolved" (DeviceSetupProvisioningComplete signal) and
        // the SelfDeploying-terminal decision. The signal alone is NO LONGER terminal (88a53223
        // defang) — it just sets the DeviceSetupResolvedUtc anchor + arms this deadline. The
        // deadline-fired handler is now the sole SelfDeploying-terminal entry point and re-checks
        // all guards (Stage.IsTerminal, AccountSetupEntered, monotonic mode conflict) before
        // committing. This trades a 5-min completion delay on real SelfDeploying devices for
        // robust protection against false-positive SelfDeploying terminations on Classic flows
        // where Windows transitioned slowly DeviceSetup→AccountSetup (the original 88a53223 bug).
        internal static readonly TimeSpan s_deviceOnlyEspDetectionWindow = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Hypothesis-reason tokens for <see cref="DecisionState.DeviceOnlyDeployment"/>.
        /// Plan §2.3 values UserPresent / DeviceOnly / Ambiguous — expressed through the
        /// <see cref="Hypothesis.Reason"/> field so we do not need a new enum.
        /// </summary>
        internal static class DeviceOnlyReasons
        {
            public const string UserPresent = "user_present";
            public const string DeviceOnly = "device_only";
            public const string Ambiguous = "ambiguous";
        }

        /// <summary>
        /// True when the scenario profile carries the registry-deterministic self-deploying
        /// classification: the <c>CloudAssignedOobeConfig</c> 0x20|0x40 seed applied via
        /// <see cref="DecisionSignalKind.EnrollmentFactsObserved"/> (reason
        /// <c>oobe_config_self_deploying</c>). Pre-terminal, SelfDeploying@High can ONLY come
        /// from that seed — <see cref="EnrollmentScenarioProfileUpdater.ApplySelfDeployingDeadlineConfirmed"/>
        /// sets it exclusively inside the terminal commit of
        /// <see cref="HandleDeviceOnlyEspDetectionDeadlineFired"/>.
        /// <para>
        /// Session 320b3bf7 (kiosk): the IME log tracker emits a false-positive
        /// <c>EspPhaseChanged(AccountSetup)</c> for the kioskUser0 autologon session even
        /// though user ESP never runs (AccountSetup ESP registry stays all-notStarted). When
        /// this predicate holds, that signal must not suppress the DeviceOnlyEspDetection
        /// completion path — platform sweep 2026-07-02: 436 of 487 failed self-deploying
        /// sessions (90%) died at the 5h backend timeout through exactly this suppression.
        /// Deliberately Mode/Confidence-based rather than reason-string-based:
        /// <see cref="EnrollmentScenarioProfileUpdater.ApplyEspConfigDetected"/> overwrites
        /// <c>Reason</c> while leaving Mode/Confidence intact.
        /// </para>
        /// </summary>
        internal static bool HasHighConfidenceSelfDeployingProfile(DecisionState state) =>
            state.ScenarioProfile.Mode == EnrollmentMode.SelfDeploying
            && state.ScenarioProfile.Confidence == ProfileConfidence.High;

        /// <summary>
        /// The AccountSetup-entered fact blocks the SelfDeploying path (short-circuits the
        /// deadline arm, trips race guard B) — EXCEPT when the profile is registry-confirmed
        /// self-deploying and user ESP never made real progress. In that combination the
        /// AccountSetup entry is the known IME false positive (see
        /// <see cref="HasHighConfidenceSelfDeployingProfile"/>) and must not veto completion.
        /// A genuine <see cref="DecisionState.AccountSetupProvisioningSucceededUtc"/> re-enables
        /// the veto: if a real user ESP succeeded on a self-deploying-bits session (anomaly),
        /// the engine falls back to the Classic completion path.
        /// </summary>
        private static bool AccountSetupEntryVetoesSelfDeploying(DecisionState state) =>
            state.AccountSetupEnteredUtc != null
            && !(HasHighConfidenceSelfDeployingProfile(state)
                 && state.AccountSetupProvisioningSucceededUtc == null);

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/>.
        /// <para>
        /// <b>88a53223 defang (Plan v9)</b>: this handler NO LONGER terminates. It only records
        /// the <see cref="DecisionState.DeviceSetupResolvedUtc"/> anchor and (cancel-then-)arms
        /// the <see cref="DeadlineNames.DeviceOnlyEspDetection"/> deadline at
        /// <c>signal.OccurredAtUtc + 5min</c>. The deadline-fired handler is the sole terminal
        /// entry point and re-checks all guards.
        /// </para>
        /// <para>
        /// Why: the previous "terminate immediately when AccountSetupEnteredUtc == null" logic
        /// mis-classified Classic UserDriven flows as SelfDeploying when the ProvisioningStatusTracker
        /// 30-s fallback fired DeviceSetupProvisioningComplete before Windows transitioned
        /// DeviceSetup→AccountSetup (session 88a53223-9795-4188-8352-7df9f0af9bb7). Defanging
        /// to deadline-only terminal classification protects against this — real SelfDeploying
        /// devices still complete (5 min later), real Classic flows continue normally and the
        /// AccountSetup signal cancels the deadline before it fires.
        /// </para>
        /// </summary>
        private DecisionStep HandleDeviceSetupProvisioningCompleteV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;

            // Step 1 — Idempotency: anchor already set means we've already processed a previous
            // DeviceSetupProvisioningComplete signal in this session. Replay-safe; the deadline
            // was already armed (or already cancelled by AccountSetup). Pass-through.
            if (state.DeviceSetupResolvedUtc != null)
            {
                var passthroughBuilder = state.ToBuilder()
                    .WithStepIndex(nextStep)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
                // Session 4910a5a5 recovery hook: a duplicate arrival (e.g. the post-reboot
                // registry re-read) can still be the first evidence that the advisory's failed
                // category recovered — set-once + category-match inside the helper keep this
                // a no-op everywhere else.
                var passthroughResolveEffect = TryResolveAdvisoryOnCategoryRecovery(
                    state, passthroughBuilder, signal, resolvedCategory: "DeviceSetup");
                var passthroughState = passthroughBuilder.Build();
                var passthroughTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.DeviceSetupProvisioningComplete) + ":AnchorAlreadySet");
                return new DecisionStep(
                    passthroughState,
                    passthroughTransition,
                    passthroughResolveEffect != null
                        ? new[] { passthroughResolveEffect }
                        : Array.Empty<DecisionEffect>());
            }

            // Step 2 — Set the DeviceSetupResolvedUtc anchor UNCONDITIONALLY. Even when Classic
            // already entered AccountSetup (step 3 below), we record the anchor for observability
            // (DecisionStateSignalCensus surfaces it in enrollment_complete audit).
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.DeviceSetupResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            // Session 4910a5a5 recovery hook: DeviceSetup resolving to success while a
            // DeviceSetup advisory failure is on record means the failure un-happened (user
            // "Try again" retry). Sets the set-once resolved fact + emits the
            // esp_failure_advisory_resolved story event; no-op otherwise.
            var advisoryResolveEffect = TryResolveAdvisoryOnCategoryRecovery(
                state, builder, signal, resolvedCategory: "DeviceSetup");

            // Step 3 — Classic-path short-circuit: AccountSetup already started. The DeviceOnly
            // deadline is moot (already cancelled by HandleEspPhaseChangedV1's AccountSetup branch,
            // or never armed under the new code). No deadline arm here.
            //
            // Kiosk waiver (session 320b3bf7): on a registry-confirmed self-deploying profile
            // the AccountSetup entry is the IME false positive — fall through to step 4 and
            // arm the deadline anyway, unless user ESP genuinely progressed
            // (AccountSetupEntryVetoesSelfDeploying).
            if (AccountSetupEntryVetoesSelfDeploying(state))
            {
                var classicState = builder.Build();
                var classicTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.DeviceSetupProvisioningComplete) + ":AccountSetupAlreadyEntered");
                return new DecisionStep(
                    classicState,
                    classicTransition,
                    advisoryResolveEffect != null
                        ? new[] { advisoryResolveEffect }
                        : Array.Empty<DecisionEffect>());
            }

            // Step 4 — Arm the deadline. Cancel any existing DeviceOnlyEspDetection first
            // (rollout-safety: handles snapshots loaded from old-code sessions where the deadline
            // was armed at DeviceSetup-START rather than -END). EffectiveDeadlineBase floors the
            // 5-min window at AgentBootUtc so a replayed signal from an older CMTrace log doesn't
            // collapse the deadline to immediate-fire at boot.
            builder.CancelDeadline(DeadlineNames.DeviceOnlyEspDetection);
            var deadline = BuildDeviceOnlyEspDetectionDeadline(EffectiveDeadlineBase(state, signal));
            builder.AddDeadline(deadline);

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.DeviceSetupProvisioningComplete) + ":DeadlineArmed");

            var effects = new List<DecisionEffect>(3)
            {
                new DecisionEffect(DecisionEffectKind.CancelDeadline, cancelDeadlineName: DeadlineNames.DeviceOnlyEspDetection),
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: deadline),
            };
            if (advisoryResolveEffect != null) effects.Add(advisoryResolveEffect);

            return new DecisionStep(newState, transition, effects.ToArray());
        }

        /// <summary>
        /// Sole SelfDeploying-terminal entry point. Plan v9.
        /// <para>
        /// Fires 5 min after <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/>
        /// armed the <see cref="DeadlineNames.DeviceOnlyEspDetection"/> deadline. Three stale-fire
        /// guards (a/b/c) + three race guards precede the terminal branch — when any trips, the
        /// handler dead-ends (no Stage change, no effects). Only when all guards pass does the
        /// terminal SelfDeploying classification commit + emit phase declarations +
        /// <c>enrollment_complete</c>.
        /// </para>
        /// </summary>
        private DecisionStep HandleDeviceOnlyEspDetectionDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            // Look up the active deadline once — used by guards b/c and by the cancel-on-terminal
            // builder mutation below.
            ActiveDeadline? activeDeadline = null;
            foreach (var d in state.Deadlines)
            {
                if (string.Equals(d.Name, DeadlineNames.DeviceOnlyEspDetection, StringComparison.Ordinal))
                {
                    activeDeadline = d;
                    break;
                }
            }

            // --- Stale-fire guard A: no anchor ---
            // The signal that should have armed our new-style deadline never arrived. This is the
            // rollout race: a session whose deadline was armed by old code (at DeviceSetup-START)
            // fires under new code before DeviceSetupProvisioningComplete has set the anchor.
            // Dead-end + state-side cancel the deadline (BumpStepBookkeeping alone wouldn't — and
            // a persisted snapshot with the stale deadline would re-arm via the scheduler).
            if (state.DeviceSetupResolvedUtc == null)
            {
                var staleNoAnchorState = state.ToBuilder()
                    .WithStepIndex(state.StepIndex + 1)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .CancelDeadline(DeadlineNames.DeviceOnlyEspDetection)
                    .Build();
                var staleNoAnchorTransition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: staleNoAnchorState.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}",
                    deadEndReason: "device_only_esp_detection_stale_no_anchor");
                return new DecisionStep(staleNoAnchorState, staleNoAnchorTransition, Array.Empty<DecisionEffect>());
            }

            // --- Stale-fire guard B: deadline not armed ---
            // Explicit cancel already happened (AccountSetup cancelled it, idempotent
            // HandleDeviceSetupProvisioningCompleteV1 re-arm cancelled the previous one, …) but a
            // stale DeadlineFired raced through. No state cleanup needed (nothing to cancel).
            if (activeDeadline == null)
            {
                var bookkept = BumpStepBookkeeping(state, signal);
                var transition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: bookkept.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}",
                    deadEndReason: "device_only_esp_detection_stale_deadline_not_armed");
                return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
            }

            // --- Stale-fire guard C: DueAtUtc mismatch ---
            // Cancel-then-rearm race: an old DeadlineFired (from a deadline incarnation that was
            // replaced by a later HandleDeviceSetupProvisioningCompleteV1 arming) is processed
            // after the rearm. DeadlineScheduler posts OccurredAtUtc == deadline.DueAtUtc, so the
            // OLD fire carries the OLD DueAtUtc. Dead-end WITHOUT cancelling the active deadline
            // (the whole point is to keep the new one for its real fire).
            if (activeDeadline.DueAtUtc != signal.OccurredAtUtc)
            {
                var bookkept = BumpStepBookkeeping(state, signal);
                var transition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: bookkept.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}",
                    deadEndReason: "device_only_esp_detection_stale_due_at_mismatch");
                return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
            }

            // From here on the active deadline matched (guard C passed) and just fired. ANY
            // dead-end below MUST state-side CancelDeadline(DeviceOnlyEspDetection) — otherwise
            // BumpStepBookkeeping (which only advances Step/Ordinal) leaves the past-due deadline
            // in state.Deadlines, the snapshot persists it, and on reload the scheduler re-arms
            // it → repeat dead-end loop. This is distinct from the stale-fire DueAtUtc-mismatch
            // path above, which intentionally KEEPS the newer active deadline.

            // --- Race guard A: Stage already terminal ---
            // Hello+Desktop Classic completion, AdminPreemption, or ESP failure won the race
            // between deadline-arm and deadline-fire. Don't double-terminate.
            if (state.Stage.IsTerminal())
            {
                var raceAState = state.ToBuilder()
                    .WithStepIndex(state.StepIndex + 1)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .CancelDeadline(DeadlineNames.DeviceOnlyEspDetection)
                    .Build();
                var transition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: raceAState.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}",
                    deadEndReason: "device_only_esp_detection_stage_already_terminal");
                return new DecisionStep(raceAState, transition, Array.Empty<DecisionEffect>());
            }

            // --- Race guard B: AccountSetup entered between deadline-arm and -fire ---
            // Defensive: the AccountSetup-cancel-deadline path normally prevents this; if it
            // didn't, we still must not classify as SelfDeploying.
            //
            // Kiosk waiver (session 320b3bf7): on a registry-confirmed self-deploying profile
            // the AccountSetup entry is the IME false positive and does not veto the terminal
            // classification — unless user ESP genuinely progressed
            // (AccountSetupEntryVetoesSelfDeploying).
            if (AccountSetupEntryVetoesSelfDeploying(state))
            {
                var raceBState = state.ToBuilder()
                    .WithStepIndex(state.StepIndex + 1)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .CancelDeadline(DeadlineNames.DeviceOnlyEspDetection)
                    .Build();
                var transition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: raceBState.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}",
                    deadEndReason: "device_only_esp_detection_account_setup_entered");
                return new DecisionStep(raceBState, transition, Array.Empty<DecisionEffect>());
            }

            // --- Race guard C: monotonic mode conflict ---
            // Prior signal already classified the session more strongly on a different Mode
            // (e.g. ImeUserSessionCompleted → Classic/High, or WhiteGloveSealingConfirmed →
            // WhiteGlove/High). The session is genuinely not SelfDeploying. Don't relabel.
            // Mirrors the precedent in ApplyImeUserSessionCompleted's monotonic guard.
            if (state.ScenarioProfile.Confidence == ProfileConfidence.High
                && state.ScenarioProfile.Mode != EnrollmentMode.Unknown
                && state.ScenarioProfile.Mode != EnrollmentMode.SelfDeploying)
            {
                var raceCState = state.ToBuilder()
                    .WithStepIndex(state.StepIndex + 1)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .CancelDeadline(DeadlineNames.DeviceOnlyEspDetection)
                    .Build();
                var transition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: raceCState.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}",
                    deadEndReason: "device_only_esp_detection_monotonic_mode_conflict");
                return new DecisionStep(raceCState, transition, Array.Empty<DecisionEffect>());
            }

            // --- Race guard D: registry says NOT self-deploying (session 62e603c9) ---
            // The deterministic CloudAssignedOobeConfig 0x20|0x40 probe (posted every run as
            // EnrollmentFactsObserved isSelfDeployingProfile) explicitly read `false`. That is
            // authoritative — validated platform-wide as zero-false-positive — so the weak
            // behavioural 5-min deadline must not override it. Typical trigger: a user-driven
            // Hybrid-Join WhiteGlove Part-2 with SkipUser=True where the user ESP is hidden and
            // the end user has not signed in yet; completing here seals the session before the
            // real Hello/desktop. Only an EXPLICIT false vetoes — an unobserved (null) fact
            // leaves the legacy behaviour intact so registry-blind sessions still terminate.
            if (state.ScenarioObservations.RegistrySelfDeployingProfile?.Value == false)
            {
                var raceDState = state.ToBuilder()
                    .WithStepIndex(state.StepIndex + 1)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .CancelDeadline(DeadlineNames.DeviceOnlyEspDetection)
                    .Build();
                var transition = BuildDeadEndTransition(
                    state: state,
                    signal: signal,
                    nextStepIndex: raceDState.StepIndex,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}",
                    deadEndReason: "device_only_esp_detection_registry_not_self_deploying");
                return new DecisionStep(raceDState, transition, Array.Empty<DecisionEffect>());
            }

            // All guards passed → this is a real SelfDeploying device.

            var nextStep = state.StepIndex + 1;
            var hasUserPresence =
                (state.ScenarioObservations.AadUserJoinWithUserObserved != null
                 && state.ScenarioObservations.AadUserJoinWithUserObserved.Value) ||
                state.HelloResolvedUtc != null ||
                state.DesktopArrivedUtc != null;
            var deviceOnlyReason = hasUserPresence
                ? DeviceOnlyReasons.UserPresent
                : DeviceOnlyReasons.DeviceOnly;
            var confirmedDeviceOnly = state.ClassifierOutcomes.DeviceOnlyDeployment.With(
                level: HypothesisLevel.Confirmed,
                reason: deviceOnlyReason,
                score: 100,
                lastUpdatedUtc: signal.OccurredAtUtc);

            // RealmJoin gate: when RJ is detected and unresolved, defer the terminal transition.
            // The deferred flag drives CompleteIfDeferredOrBookkeep when RJ resolves/timeouts.
            // Plan v9: this is the V9 location for RJ-deferral (was at signal-time previously;
            // the move avoids a premature RJ-resolve-before-5min completing the session).
            if (!RealmJoinGateOpen(state))
            {
                var deferredBuilder = state.ToBuilder()
                    .WithStepIndex(nextStep)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .CancelDeadline(DeadlineNames.DeviceOnlyEspDetection);
                deferredBuilder.ClassifierOutcomes = state.ClassifierOutcomes.WithDeviceOnlyDeployment(confirmedDeviceOnly);
                deferredBuilder.RealmJoinFacts = state.RealmJoinFacts.WithSelfDeployingDeferred(signal.SessionSignalOrdinal);

                var deferredState = deferredBuilder.Build();
                var deferredTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}:RealmJoinGateClosed");
                return new DecisionStep(deferredState, deferredTransition, Array.Empty<DecisionEffect>());
            }

            // Terminal SelfDeploying branch.
            var terminalBuilder = state.ToBuilder()
                .WithStage(SessionStage.Completed)
                .WithOutcome(SessionOutcome.EnrollmentComplete)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .ClearDeadlines();
            terminalBuilder.ClassifierOutcomes = state.ClassifierOutcomes.WithDeviceOnlyDeployment(confirmedDeviceOnly);
            terminalBuilder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplySelfDeployingDeadlineConfirmed(
                state.ScenarioProfile, signal);

            var terminalState = terminalBuilder.Build();
            var terminalTransition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Completed,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}");

            // Effects sequence — UI phase coverage (Plan v9 Phase 4): emit FinalizingSetup +
            // Complete phase declarations BEFORE enrollment_complete so the Web timeline opens
            // both phase bars for SelfDeploying terminal sessions (was missing entirely before).
            var effects = new DecisionEffect[]
            {
                BuildPhaseTransitionEffect(EnrollmentPhase.FinalizingSetup, terminalState, $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}"),
                BuildPhaseTransitionEffect(EnrollmentPhase.Complete, terminalState, $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}"),
                BuildEnrollmentCompleteEffect(terminalState, $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}"),
            };

            return new DecisionStep(terminalState, terminalTransition, effects);
        }

        // ============================================================== internal helpers

        /// <summary>
        /// Build the <see cref="DeadlineNames.DeviceOnlyEspDetection"/> active deadline. Plan v9:
        /// armed by <see cref="HandleDeviceSetupProvisioningCompleteV1"/> at DeviceSetup-END (not
        /// at DeviceSetup-START like the legacy v1 code).
        /// </summary>
        internal ActiveDeadline BuildDeviceOnlyEspDetectionDeadline(DateTime fromUtc) =>
            new ActiveDeadline(
                name: DeadlineNames.DeviceOnlyEspDetection,
                dueAtUtc: fromUtc.Add(s_deviceOnlyEspDetectionWindow),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection,
                });
    }
}
