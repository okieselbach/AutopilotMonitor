using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Session 8bc1180f (2026-06-12) — AdvisoryCompletion deadline + IME user-session
    /// correlation gate. Coverage:
    /// <list type="bullet">
    ///   <item>The advisory-defang path arms the <c>advisory_completion</c> deadline
    ///         (30 min, AgentBoot-floored) and emits a ScheduleDeadline effect.</item>
    ///   <item>The enrichment-duplicate path does NOT re-arm / shift the deadline.</item>
    ///   <item>Deadline fire with the full real-user conjunction (Desktop + Hello disabled +
    ///         IME-user-session-completed at-or-after AccountSetupEntered) → Finalizing →
    ///         Completed with synthesized HelloOutcome=Skipped.</item>
    ///   <item>defaultuser0-ghost: an IME completion timestamped BEFORE AccountSetupEntered
    ///         must NOT satisfy the conjunction → Failed.</item>
    ///   <item>Missing desktop / missing IME evidence → Failed with un-defanged
    ///         <c>enrollment_failed</c> + LastFailureTrigger=EspTerminalFailure (likely-stuck
    ///         promotion parity in EnrollmentTerminationHandler).</item>
    ///   <item>Stale-fire guards (no advisory anchor / deadline retired / Finalizing in flight).</item>
    ///   <item><c>ImeUserSessionCompletedUtc</c> fact recording (set-once) + serialization
    ///         compatibility (roundtrip + legacy snapshot without the field).</item>
    /// </list>
    /// </summary>
    public sealed class AdvisoryCompletionDeadlineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null,
            string sourceOrigin = "test")
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: sourceOrigin,
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionSignal DeadlineFired(long ordinal, DateTime occurredAtUtc, string deadlineName) =>
            MakeSignal(ordinal, DecisionSignalKind.DeadlineFired, occurredAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = deadlineName });

        private static DecisionEffect SingleTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Single(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        private static ActiveDeadline? FindDeadline(DecisionState state, string name) =>
            state.Deadlines.FirstOrDefault(d => d.Name == name);

        /// <summary>
        /// Replays the session-8bc1180f shape up to (but excluding) the ESP terminal failure:
        /// ContinueAnyway profile, Hello policy disabled, DeviceSetup → AccountSetup,
        /// real-user desktop arrival (DAD-validated upstream), IME user-session completion.
        /// Individual steps can be skipped to produce the negative variants.
        /// </summary>
        private static DecisionState SetupAdvisoryEligibleSession(
            DecisionEngine engine,
            bool helloPolicyDisabled = true,
            bool desktopArrives = true,
            bool imeUserSessionCompletes = true,
            DateTime? imeUserSessionCompletedAt = null)
        {
            var state = DecisionState.CreateInitial("sess-8bc1180f", "tenant-8bc1180f", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "60",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "true",
                })).NewState;
            if (helloPolicyDisabled)
            {
                state = engine.Reduce(state, MakeSignal(
                    8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                    new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            }
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            // Intermediate Device→Account esp_exiting (session-8bc1180f shape: 09:24:15, before
            // AccountSetup at 09:24:33). Records EspFinalExitUtc without a stage transition —
            // which is also what lets HandleAccountSetupProvisioningCompleteV1's deferred-promote
            // path run in the post-terminal-guard tests below.
            state = engine.Reduce(state, MakeSignal(
                15, DecisionSignalKind.EspExiting, T0.AddMinutes(16.5))).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(17),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            if (desktopArrives)
            {
                // Real-user desktop (defaultuser0/SYSTEM are excluded by the DAD before this
                // signal is ever posted). With the strong AccountSetup gate unsatisfied the
                // Hello-disabled fast-path defers — the fact is recorded, stage unchanged.
                state = engine.Reduce(state, MakeSignal(
                    30, DecisionSignalKind.DesktopArrived, T0.AddMinutes(18))).NewState;
            }
            if (imeUserSessionCompletes)
            {
                state = engine.Reduce(state, MakeSignal(
                    40, DecisionSignalKind.ImeUserSessionCompleted, imeUserSessionCompletedAt ?? T0.AddMinutes(27),
                    new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" })).NewState;
            }
            return state;
        }

        private static DecisionState ApplyEspTerminalFailure(DecisionEngine engine, DecisionState state, long ordinal = 50) =>
            engine.Reduce(state, MakeSignal(
                ordinal, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(27.5),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["errorCode"] = "0x87d1041c",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                })).NewState;

        // ===================================================== deadline arming ====

        [Fact]
        public void Advisory_ArmsAdvisoryCompletionDeadline_30MinFromSignal()
        {
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine);

            var step = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(27.5),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["errorCode"] = "0x87d1041c",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                }));

            // Advisory recorded, session stays non-terminal.
            Assert.NotNull(step.NewState.EspAdvisoryFailureRecordedUtc);
            Assert.Null(step.NewState.Outcome);

            // Deadline armed in state at signal-time + 30 min.
            var deadline = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(deadline);
            Assert.Equal(T0.AddMinutes(27.5).AddMinutes(30), deadline!.DueAtUtc);

            // ScheduleDeadline effect emitted so the wall-clock scheduler arms the timer.
            var scheduleEffect = step.Effects.Single(e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, scheduleEffect.Deadline!.Name);

            // The advisory timeline event is still emitted alongside.
            SingleTimelineEffect(step, "esp_failure_advisory");
        }

        [Fact]
        public void Advisory_DeadlineBase_FlooredAtAgentBoot_OnReplayedSignal()
        {
            // A replayed EspTerminalFailure carries a historical OccurredAtUtc. The deadline
            // must be floored at AgentBootUtc so it can't fire the moment the agent boots.
            var engine = new DecisionEngine();
            var bootUtc = T0.AddHours(2);
            var state = DecisionState.CreateInitial("sess-replay", "tenant-replay", bootUtc);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspAllowContinueAnyway] = "true" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(17),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(27),
                new Dictionary<string, string> { ["failedSubcategory"] = "Apps", ["category"] = "AccountSetup" }));

            var deadline = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(deadline);
            Assert.Equal(bootUtc.AddMinutes(30), deadline!.DueAtUtc);
        }

        [Fact]
        public void Advisory_EnrichmentDuplicate_DoesNotReArmDeadline()
        {
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine);

            // First failure: sparse ShellCore shape — arms the deadline.
            state = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(27.5),
                new Dictionary<string, string> { ["reason"] = "esp_terminal_failure_account_setup" })).NewState;
            var armed = FindDeadline(state, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(armed);

            // Enrichment duplicate 5 minutes later: registry detail arrives. The advisory anchor
            // stays at the first signal — and so must the resolution deadline.
            var step = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(32.5),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["errorCode"] = "0x87d1041c",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                }));

            var afterEnrichment = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(afterEnrichment);
            Assert.Equal(armed!.DueAtUtc, afterEnrichment!.DueAtUtc);
            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.ScheduleDeadline);
        }

        // ============================================ deadline fired — completion ====

        [Fact]
        public void DeadlineFired_FullConjunction_CompletesThroughFinalizing()
        {
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine);
            state = ApplyEspTerminalFailure(engine, state);

            var fireAt = T0.AddMinutes(57.5);
            var step = engine.Reduce(state, DeadlineFired(70, fireAt, DeadlineNames.AdvisoryCompletion));

            // Routes through the non-terminal Finalizing stage (FinalizingGrace pattern).
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.Equal($"DeadlineFired:{DeadlineNames.AdvisoryCompletion}", step.Transition.Trigger);

            // Hello synthesized as Skipped (policy disabled, never resolved live).
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);

            // The fired deadline is retired; FinalizingGrace took its place.
            Assert.Null(FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion));
            Assert.NotNull(FindDeadline(step.NewState, DeadlineNames.FinalizingGrace));

            // Drive FinalizingGrace → Completed + enrollment_complete on the wire.
            var final = engine.Reduce(step.NewState, DeadlineFired(80, fireAt.AddSeconds(5), DeadlineNames.FinalizingGrace));
            Assert.Equal(SessionStage.Completed, final.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, final.NewState.Outcome);
            SingleTimelineEffect(final, "enrollment_complete");
        }

        [Fact]
        public void DeadlineFired_HelloResolvedLive_KeepsRealHelloOutcome()
        {
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, helloPolicyDisabled: false);
            // Hello resolves live before the failure (e.g. PIN enrollment finished).
            state = engine.Reduce(state, MakeSignal(
                45, DecisionSignalKind.HelloResolved, T0.AddMinutes(26),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;
            // HelloResolved + DesktopArrived would normally complete — but the strong gate
            // deferral keeps Classic sessions in EspAccountSetup; sanity-check we are not
            // terminal yet before the failure arrives.
            Assert.Null(state.Outcome);
            state = ApplyEspTerminalFailure(engine, state);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Success", step.NewState.HelloOutcome!.Value);
        }

        // =============================================== deadline fired — failed ====

        [Fact]
        public void DeadlineFired_ImeCompletedBeforeAccountSetup_DefaultUser0Ghost_Fails()
        {
            // The IME user-session completion happened BEFORE AccountSetup was entered —
            // that is the defaultuser0/OOBE frame, not the real user's session. The
            // conjunction must reject it even though desktop + hello are satisfied.
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(
                engine,
                imeUserSessionCompletedAt: T0.AddMinutes(10)); // AccountSetup entered at +17
            state = ApplyEspTerminalFailure(engine, state);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
        }

        [Fact]
        public void DeadlineFired_NoDesktop_FailsWithUnDefangedTerminalEvent()
        {
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, desktopArrives: false);
            state = ApplyEspTerminalFailure(engine, state);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            Assert.Empty(step.NewState.Deadlines);

            // LastFailureTrigger carries EspTerminalFailure (NOT the deadline) — parity with
            // the direct failure path so EnrollmentTerminationHandler's likely-stuck app
            // promotion discriminator fires.
            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), step.NewState.LastFailureTrigger!.Value);

            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_terminal_failure", failed.Parameters!["reason"]);
            Assert.Equal("advisory_completion_window_expired_without_completion_evidence",
                failed.Parameters!["advisoryReason"]);
            // ContinueAnyway hints re-derived from observations.
            Assert.Equal("true", failed.Parameters!["espAllowContinueAnyway"]);
            Assert.Equal("true", failed.Parameters!["mayHaveContinuedAnyway"]);
            Assert.Equal("60", failed.Parameters!["espSyncFailureTimeoutMinutes"]);
        }

        [Fact]
        public void DeadlineFired_NoImeEvidence_Fails()
        {
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, imeUserSessionCompletes: false);
            state = ApplyEspTerminalFailure(engine, state);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
        }

        // ======================================================= stale-fire guards ====

        [Fact]
        public void DeadlineFired_WithoutAnyAnchor_DeadEnds()
        {
            // Neither anchor fact exists: no advisory recorded and no esp_exiting ever observed.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-noanchor", "tenant-noanchor", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.False(step.Transition.Taken);
            Assert.Equal("advisory_completion_without_anchor", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
            Assert.Equal(state.Stage, step.NewState.Stage);
        }

        [Fact]
        public void DeadlineFired_IntermediateExitOnly_DeadEndsAsStaleNotArmed()
        {
            // L5 (delta review 2026-07-02): the anchor is presence-only now — the intermediate
            // pre-AccountSetup exit plus AccountSetup entry passes the anchor check, and the
            // UNARMED window is what dead-ends this stray fire.
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.False(step.Transition.Taken);
            Assert.Equal("advisory_completion_stale_deadline_not_armed", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
            Assert.Equal(state.Stage, step.NewState.Stage);
        }

        [Fact]
        public void DeadlineFired_EspExitVariant_BackdatedExitTimestamp_StillResolves()
        {
            // L5 (delta review 2026-07-02): a guard-blocked post-AccountSetup exit whose SOURCE
            // timestamp predates AccountSetup entry (replayed CMTrace content / clamped clock)
            // arms the window; the fire must resolve the session, not dead-end on
            // advisory_completion_without_anchor (which re-opened the idle-until-max-lifetime
            // dead-end this deadline exists to close).
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine); // desktop + post-anchor IME evidence

            var armStep = engine.Reduce(state, MakeSignal(50, DecisionSignalKind.EspExiting, T0.AddMinutes(16)));
            Assert.NotNull(FindDeadline(armStep.NewState, DeadlineNames.AdvisoryCompletion));
            Assert.True(armStep.NewState.EspFinalExitUtc!.Value < armStep.NewState.AccountSetupEnteredUtc!.Value);

            var step = engine.Reduce(armStep.NewState, DeadlineFired(70, T0.AddMinutes(46), DeadlineNames.AdvisoryCompletion));

            Assert.True(step.Transition.Taken);
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
        }

        [Fact]
        public void DeadlineFired_AfterNormalCompletion_DeadEndsViaPostTerminalGuard()
        {
            // The session completes via the normal AccountSetupProvisioningComplete path before
            // the advisory deadline fires (e.g. user pressed "Try again" and the apps recovered).
            // The stray timer fire after Completed must dead-end via the post-terminal guard.
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine);
            state = ApplyEspTerminalFailure(engine, state);

            // Strong gate arrives late → deferred-completion parity path → Finalizing → Completed.
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(40))).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);
            state = engine.Reduce(state, DeadlineFired(65, T0.AddMinutes(40).AddSeconds(5), DeadlineNames.FinalizingGrace)).NewState;
            Assert.Equal(SessionStage.Completed, state.Stage);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.False(step.Transition.Taken);
            Assert.Equal("signal_after_terminal:Completed", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void DeadlineFired_WhileFinalizingInFlight_DeadEnds()
        {
            // Race: a normal completion path entered Finalizing (5 s grace) and the advisory
            // timer fires inside that window. Must not double-drive completion.
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine);
            state = ApplyEspTerminalFailure(engine, state);
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(40))).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(40).AddSeconds(2), DeadlineNames.AdvisoryCompletion));

            Assert.False(step.Transition.Taken);
            Assert.Equal("advisory_completion_finalizing_already_in_flight", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
        }

        // ==================================== esp-exit arming variant (1ec8f4c6) ====

        /// <summary>
        /// Drives the session-1ec8f4c6 shape up to the guard-blocked FINAL esp_exiting:
        /// no ESP failure anywhere — the page closes normally (errorCode=0) while the
        /// AccountSetup Apps subcategory is still in_progress (one user app never started).
        /// </summary>
        private static DecisionState SetupEspExitDeadEndSession(
            DecisionEngine engine,
            bool desktopArrives = true,
            bool imeUserSessionCompletes = true)
        {
            var state = SetupAdvisoryEligibleSession(
                engine,
                desktopArrives: desktopArrives,
                imeUserSessionCompletes: false);
            // Final esp_exiting AFTER AccountSetup entry (1ec8f4c6: 10:34:07, AccountSetup
            // 10:31:52) — guard-blocked because the strong gate never opened (Apps in_progress).
            state = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspExiting, T0.AddMinutes(19.5))).NewState;
            if (imeUserSessionCompletes)
            {
                // IME user-session completion right after the page closed (1ec8f4c6: 10:34:20).
                state = engine.Reduce(state, MakeSignal(
                    55, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(19.7),
                    new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" })).NewState;
            }
            return state;
        }

        [Fact]
        public void EspExiting_PostAccountSetup_GuardBlocked_ArmsResolutionDeadline()
        {
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, imeUserSessionCompletes: false);
            // Sanity: the intermediate pre-AccountSetup exit in the setup helper did NOT arm.
            Assert.Null(FindDeadline(state, DeadlineNames.AdvisoryCompletion));

            var exitAt = T0.AddMinutes(19.5);
            var step = engine.Reduce(state, MakeSignal(50, DecisionSignalKind.EspExiting, exitAt));

            // Stage unchanged (guard still blocks), but the resolution window is armed.
            Assert.Equal(state.Stage, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            var deadline = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(deadline);
            Assert.Equal(exitAt.AddMinutes(30), deadline!.DueAtUtc);
            var scheduleEffect = step.Effects.Single(e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, scheduleEffect.Deadline!.Name);
        }

        [Fact]
        public void EspExiting_RepeatedPostAccountSetupExit_DoesNotReArm()
        {
            // Shell-Core 62407 can fire more than once. The esp-exit site is fire-once: a
            // second blocked exit must not push the already-armed window out.
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, imeUserSessionCompletes: false);
            state = engine.Reduce(state, MakeSignal(50, DecisionSignalKind.EspExiting, T0.AddMinutes(19.5))).NewState;
            var armed = FindDeadline(state, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(armed);

            var step = engine.Reduce(state, MakeSignal(60, DecisionSignalKind.EspExiting, T0.AddMinutes(25)));

            var after = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(after);
            Assert.Equal(armed!.DueAtUtc, after!.DueAtUtc);
            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.ScheduleDeadline);
        }

        [Fact]
        public void EspExiting_LaterAdvisory_ReBasesTheWindow()
        {
            // Ordering: blocked post-AccountSetup exit arms first, then the ESP flips a
            // subcategory to failed → advisory. The advisory site deliberately replaces the
            // window (freshest dead-end signal owns it).
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEndSession(engine);
            var armed = FindDeadline(state, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(armed);

            var step = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(25),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                }));

            Assert.NotNull(step.NewState.EspAdvisoryFailureRecordedUtc);
            var after = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(after);
            Assert.Equal(T0.AddMinutes(25).AddMinutes(30), after!.DueAtUtc);
        }

        [Fact]
        public void DeadlineFired_EspExitVariant_FullConjunction_CompletesThroughFinalizing()
        {
            // The 1ec8f4c6 replay: DeviceSetup → intermediate exit → AccountSetup → real-user
            // desktop → final exit (normal, guard-blocked) → IME user session completed →
            // 30 min later the resolution deadline completes the session.
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEndSession(engine);

            var fireAt = T0.AddMinutes(49.5);
            var step = engine.Reduce(state, DeadlineFired(70, fireAt, DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);

            var final = engine.Reduce(step.NewState, DeadlineFired(80, fireAt.AddSeconds(5), DeadlineNames.FinalizingGrace));
            Assert.Equal(SessionStage.Completed, final.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, final.NewState.Outcome);
            SingleTimelineEffect(final, "enrollment_complete");
        }

        [Fact]
        public void DeadlineFired_EspExitVariant_NoEvidence_FailsWithDistinctReason()
        {
            // ESP page closed normally but neither desktop nor IME evidence arrived within the
            // window. The failure must NOT claim esp_terminal_failure (no ESP failure existed)
            // and must NOT activate the likely-stuck app promotion (LastFailureTrigger stays
            // the deadline, not EspTerminalFailure).
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEndSession(engine, desktopArrives: false, imeUserSessionCompletes: false);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(49.5), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            Assert.Equal(nameof(DecisionSignalKind.DeadlineFired), step.NewState.LastFailureTrigger!.Value);

            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_exit_without_completion_evidence", failed.Parameters!["reason"]);
            Assert.Equal("esp_exit_resolution_window_expired_without_completion_evidence",
                failed.Parameters!["advisoryReason"]);
            // No ContinueAnyway failure-screen hints — there was no failure screen.
            Assert.False(failed.Parameters!.ContainsKey("mayHaveContinuedAnyway"));
            Assert.False(failed.Parameters!.ContainsKey("continueAnywayHint"));
        }

        [Fact]
        public void DeadlineFired_AdvisoryVariant_NoEvidence_KeepsEspTerminalFailureSemantics()
        {
            // Regression guard for the variant split: the advisory-armed path must keep the
            // un-defang semantics (reason=esp_terminal_failure + LastFailureTrigger=
            // EspTerminalFailure for the likely-stuck promotion parity).
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, desktopArrives: false, imeUserSessionCompletes: false);
            state = ApplyEspTerminalFailure(engine, state);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), step.NewState.LastFailureTrigger!.Value);
            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_terminal_failure", failed.Parameters!["reason"]);
            Assert.Equal("advisory_completion_window_expired_without_completion_evidence",
                failed.Parameters!["advisoryReason"]);
        }

        // ===================================================== fact recording ====

        [Fact]
        public void ImeUserSessionCompleted_RecordsFact_SetOnce()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-fact", "tenant-fact", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(5),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" })).NewState;

            Assert.NotNull(state.ImeUserSessionCompletedUtc);
            Assert.Equal(T0.AddMinutes(5), state.ImeUserSessionCompletedUtc!.Value);
            Assert.Equal(10, state.ImeUserSessionCompletedUtc!.SourceSignalOrdinal);

            // A replayed second observation keeps the original anchor (no AccountSetup entry
            // recorded — the post-anchor upgrade rule cannot apply).
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(9),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" })).NewState;

            Assert.Equal(T0.AddMinutes(5), state.ImeUserSessionCompletedUtc!.Value);
            Assert.Equal(10, state.ImeUserSessionCompletedUtc!.SourceSignalOrdinal);
        }

        [Fact]
        public void ImeUserSessionCompleted_PreAnchorGhost_UpgradedByFirstPostAnchorObservation()
        {
            // M2 (delta review 2026-07-02): a defaultuser0 completion observed BEFORE
            // AccountSetup entry must not permanently poison the conjunction — the first
            // at-or-after-anchor observation upgrades the fact, then it freezes.
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, imeUserSessionCompletedAt: T0.AddMinutes(10));
            Assert.Equal(T0.AddMinutes(10), state.ImeUserSessionCompletedUtc!.Value); // ghost recorded

            // Real user's IME session completes post-AccountSetup (+17) → upgrade.
            state = engine.Reduce(state, MakeSignal(
                45, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(27),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" })).NewState;
            Assert.Equal(T0.AddMinutes(27), state.ImeUserSessionCompletedUtc!.Value);
            Assert.Equal(45, state.ImeUserSessionCompletedUtc!.SourceSignalOrdinal);

            // Frozen after the upgrade: a third observation does not re-base the genuine stamp.
            state = engine.Reduce(state, MakeSignal(
                48, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(29),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" })).NewState;
            Assert.Equal(T0.AddMinutes(27), state.ImeUserSessionCompletedUtc!.Value);
            Assert.Equal(45, state.ImeUserSessionCompletedUtc!.SourceSignalOrdinal);
        }

        [Fact]
        public void DeadlineFired_GhostThenRealImeCompletion_CompletesThroughFinalizing()
        {
            // End-to-end M2: ghost pre-anchor completion, then the real user's post-anchor
            // completion — the AdvisoryCompletion conjunction must hold and the session must
            // complete instead of resolving Failed on poisoned evidence.
            var engine = new DecisionEngine();
            var state = SetupAdvisoryEligibleSession(engine, imeUserSessionCompletedAt: T0.AddMinutes(10));
            state = engine.Reduce(state, MakeSignal(
                45, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(27),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" })).NewState;
            state = ApplyEspTerminalFailure(engine, state, ordinal: 50);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(57.5), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
        }

        // ================================================== serialization compat ====

        [Fact]
        public void StateSerializer_Roundtrip_PreservesImeUserSessionCompletedUtc()
        {
            var builder = DecisionState.CreateInitial("sess-ser", "tenant-ser", T0).ToBuilder();
            builder.ImeUserSessionCompletedUtc = new SignalFact<DateTime>(T0.AddMinutes(27), sourceSignalOrdinal: 40);
            var state = builder.Build();

            var roundtripped = StateSerializer.Deserialize(StateSerializer.Serialize(state));

            Assert.NotNull(roundtripped.ImeUserSessionCompletedUtc);
            Assert.Equal(T0.AddMinutes(27), roundtripped.ImeUserSessionCompletedUtc!.Value);
            Assert.Equal(40, roundtripped.ImeUserSessionCompletedUtc!.SourceSignalOrdinal);
        }

        [Fact]
        public void Deserialize_v4Snapshot_withoutImeUserSessionCompletedUtc_field_yieldsNullFact()
        {
            // Rollout-safety: ImeUserSessionCompletedUtc is an additive nullable field appended
            // to the end of the DecisionState ctor. Persisted snapshots from versions before
            // 2026-06-12 do not contain the property; rehydration must succeed with the fact null.
            const string priorV4Json = @"{
                ""SessionId"": ""sess-rehydrate-ime"",
                ""TenantId"": ""tenant-rehydrate-ime"",
                ""Stage"": ""EspAccountSetup"",
                ""Outcome"": null,
                ""CurrentEnrollmentPhase"": null,
                ""DeviceSetupEnteredUtc"": null,
                ""AccountSetupEnteredUtc"": null,
                ""FinalizingEnteredUtc"": null,
                ""AccountSetupProvisioningSucceededUtc"": null,
                ""EspFinalExitUtc"": null,
                ""DesktopArrivedUtc"": null,
                ""HelloResolvedUtc"": null,
                ""SystemRebootUtc"": null,
                ""HelloOutcome"": null,
                ""ImeMatchedPatternId"": null,
                ""Deadlines"": [],
                ""LastAppliedSignalOrdinal"": 42,
                ""StepIndex"": 100,
                ""AppInstallFacts"": null,
                ""ScenarioProfile"": null,
                ""ScenarioObservations"": null,
                ""ClassifierOutcomes"": null,
                ""HelloPolicyEnabled"": null,
                ""AgentBootUtc"": ""2026-06-12T08:00:00Z"",
                ""LastFailureTrigger"": null,
                ""RealmJoinFacts"": null,
                ""DeviceSetupResolvedUtc"": null,
                ""EspAdvisoryFailureRecordedUtc"": null,
                ""SchemaVersion"": ""v4""
            }";

            var deserialized = StateSerializer.Deserialize(priorV4Json);

            Assert.Null(deserialized.ImeUserSessionCompletedUtc);
            Assert.Equal("sess-rehydrate-ime", deserialized.SessionId);
            Assert.Equal(SessionStage.EspAccountSetup, deserialized.Stage);
        }

        // =============================================== audit-trail census ====

        [Fact]
        public void SignalCensus_IncludesImeUserSessionCompleted_WhenFactSet()
        {
            var builder = DecisionState.CreateInitial("sess-census", "tenant-census", T0).ToBuilder();
            builder.ImeUserSessionCompletedUtc = new SignalFact<DateTime>(T0.AddMinutes(27), sourceSignalOrdinal: 40);
            var census = DecisionStateSignalCensus.Build(builder.Build());

            Assert.Contains("ime_user_session_completed", census.SignalsSeen);
            Assert.True(census.SignalTimestamps.ContainsKey("imeUserSessionCompleted"));
            Assert.True(census.SignalEvidence.ContainsKey("imeUserSessionCompleted"));
        }
    }
}
