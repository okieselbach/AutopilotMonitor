using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// sits-d Cloud-PC fix (2026-08-20, sessions 8110e262 / a89aac2d / e7ba63c9 / cb4a485a /
    /// 3d6278fb). On a SkipUser=true flow Windows never renders the user ESP page: Shell-Core
    /// 62407 does not fire and the AccountSetup provisioning categories are never written —
    /// verified live (Shell-Core watcher armed across three agent runs, zero 62407 in six
    /// hours; SkipUserStatusPage read 10+ times across all runs, True every time). Arm B of
    /// <c>ShouldTransitionToAwaitingHello</c> was therefore open from the bootstrap
    /// EspConfigDetected onward, but every site that evaluates the gate hangs off a signal the
    /// flow structurally cannot produce, so five sessions sat at "waiting on: hello_resolution"
    /// until the max-lifetime watchdog.
    /// <para>
    /// The fix lets the observed skip stand in for the final-exit evidence inside
    /// <c>HandleImeUserSessionCompletedV1</c>'s completion attempt. These tests replay the real
    /// 8110e262 signal ordering end to end (including the Fix-10 AccountSetup bounce-back a
    /// restart re-emission triggers) and pin the negative space: no skip observed, no desktop,
    /// ghost IME completion, and Device Preparation flows are all unchanged.
    /// </para>
    /// </summary>
    public sealed class ClassicSkipUserEspUserSessionCompletionTests
    {
        // 8110e262 run 1: agent_started 08:30:08Z.
        private static readonly DateTime T0 = new DateTime(2026, 8, 19, 8, 30, 8, DateTimeKind.Utc);

        [Fact]
        public void Sits_d_signal_ordering_completes_via_hello_safety()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial(
                "8110e262-da57-4282-9290-bb0c96d05614", "5ca2b350", T0);

            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;

            // Bootstrap EspConfigDetected — SkipUserStatusPage=True, read from FirstSync 2 s in.
            state = engine.Reduce(state, MakeEspConfigSignal(1, skipUser: "true")).NewState;
            Assert.True(state.ScenarioObservations.SkipUserEsp!.Value);

            // Hello policy enabled (device-scoped CSP) — the reason the desktop fast-path
            // could never take this flow.
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddSeconds(1),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "true" })).NewState;

            // Desktop 5 s after agent start (the user ESP page does not exist — the user is
            // simply logged on). Pins that desktop arrival alone does NOT promote: the knock
            // deliberately lives on the IME user-session edge, because a desktop-side promotion
            // would be undone by Fix 10's AccountSetup bounce-back two signals later (the IME
            // AccountSetup phase line lands AFTER the desktop on this flow).
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DesktopArrived, T0.AddSeconds(5), null)).NewState;
            Assert.NotEqual(SessionStage.AwaitingHello, state.Stage);
            Assert.DoesNotContain(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            // IME's AccountSetup phase line (source ts 08:29:56 — before agent start; the IME
            // logs the phase for its user-session app processing even though no page is shown).
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspPhaseChanged, T0.AddSeconds(-12),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);

            // 08:36:59 — IME user session completed (138/138 apps, 0 failed). THE knock:
            // skip-user stands in for the exit that cannot exist; Hello enabled + unresolved
            // promotes to AwaitingHello with HelloSafety armed.
            var promoted = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.ImeUserSessionCompleted,
                T0.AddMinutes(6).AddSeconds(51), null));
            Assert.Equal(SessionStage.AwaitingHello, promoted.NewState.Stage);
            var helloSafety = Assert.Single(promoted.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Contains(promoted.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.HelloSafety);

            // The Hello wizard never appears over RDP — HelloSafety resolves synthetically.
            var afterSafety = engine.Reduce(promoted.NewState,
                MakeSignal(6, DecisionSignalKind.DeadlineFired, helloSafety.DueAtUtc,
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety }));
            Assert.Equal(SessionStage.Finalizing, afterSafety.NewState.Stage);
            Assert.Equal("Timeout", afterSafety.NewState.HelloOutcome!.Value);

            var completed = engine.Reduce(afterSafety.NewState,
                MakeSignal(7, DecisionSignalKind.DeadlineFired, helloSafety.DueAtUtc.AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));
            Assert.Equal(SessionStage.Completed, completed.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, completed.NewState.Outcome);
            Assert.Contains(completed.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
        }

        [Fact]
        public void Fix10_bounceback_is_healed_by_the_ime_reemission()
        {
            // Restart shape (8110e262 run 3): the promoted AwaitingHello gets bounced back by a
            // re-emitted AccountSetup phase line (Fix 10 cancels HelloSafety, deliberately — it
            // guards the premature-promotion case). The IME phase line always precedes the
            // user-session-complete line in log order, so the re-emitted completion re-knocks.
            var engine = new DecisionEngine();
            var state = ProgressToPromotedAwaitingHello(engine);

            var bounced = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspPhaseChanged,
                T0.AddMinutes(18).AddSeconds(10),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" }));
            Assert.Equal(SessionStage.EspAccountSetup, bounced.NewState.Stage);
            Assert.DoesNotContain(bounced.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            var reknocked = engine.Reduce(bounced.NewState,
                MakeSignal(11, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(6).AddSeconds(51), null));
            Assert.Equal(SessionStage.AwaitingHello, reknocked.NewState.Stage);
            Assert.Single(reknocked.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Theory]
        // No EspConfigDetected at all — SkipUserEsp unknown.
        [InlineData(null)]
        // Explicit SkipUser=false (the two sits-d machines that succeeded had this shape and
        // a REAL page: exit + registry evidence. Without the exit the gate must stay shut.)
        [InlineData("false")]
        public void No_promotion_without_an_observed_skip(string? skipUser)
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            if (skipUser != null)
            {
                state = engine.Reduce(state, MakeEspConfigSignal(1, skipUser)).NewState;
            }
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddSeconds(1),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "true" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DesktopArrived, T0.AddSeconds(5), null)).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspPhaseChanged, T0.AddSeconds(10),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state,
                MakeSignal(5, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(7), null));

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void No_promotion_without_the_desktop()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeEspConfigSignal(1, skipUser: "true")).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddSeconds(10),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state,
                MakeSignal(3, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(7), null));

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void A_ghost_pre_anchor_ime_completion_does_not_knock()
        {
            // defaultuser0-ghost guard stays load-bearing: an IME completion whose source time
            // precedes the AccountSetup anchor is the OOBE/technician frame, not the user.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeEspConfigSignal(1, skipUser: "true")).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DesktopArrived, T0.AddSeconds(5), null)).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state,
                MakeSignal(4, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(1), null));

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void Device_preparation_flows_keep_their_own_rails()
        {
            // WDP has no ESP at all — completion rides the Hello+Desktop conjunction and the
            // DevicePrepCompletion backstop (afee7ae0). Even a (spurious) SkipUser=true read
            // must not route WDP through the classic skip-user knock.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t", T0)
                .ToBuilder()
                .WithStage(SessionStage.SessionStarted)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .WithScenarioProfile(EnrollmentScenarioProfile.Empty.With(
                    mode: EnrollmentMode.DevicePreparation,
                    confidence: ProfileConfidence.High,
                    evidenceOrdinal: 1,
                    reason: "test"));
            seed.ScenarioObservations = EnrollmentScenarioObservations.Empty
                .WithSkipUserEsp(true, 1);
            seed.AccountSetupEnteredUtc = new SignalFact<DateTime>(T0.AddMinutes(1), 2);
            seed.DesktopArrivedUtc = new SignalFact<DateTime>(T0.AddMinutes(2), 3);
            var state = seed.Build();

            var step = engine.Reduce(state,
                MakeSignal(5, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(3), null));

            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.NotEqual(SessionStage.AwaitingHello, step.NewState.Stage);
        }

        [Fact]
        public void Hello_disabled_skip_user_completes_directly_from_the_knock()
        {
            // HelloSatisfiedForCompletion holds (policy disabled, no wizard observed) — the
            // knock completes through Finalizing with the synthetic Skipped outcome instead of
            // parking 5 minutes in AwaitingHello.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeEspConfigSignal(1, skipUser: "true")).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddSeconds(1),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddSeconds(10),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // Desktop arrives; the hello-disabled fast-path already promotes/completes via the
            // gate's arm B on this ordering, so the knock is not even needed here — assert the
            // flow terminates either way (belt for orderings where desktop precedes the config).
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null)).NewState;

            if (state.Stage != SessionStage.Finalizing && !state.Stage.IsTerminal())
            {
                var step = engine.Reduce(state,
                    MakeSignal(5, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(7), null));
                Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
                Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
            }
            else
            {
                Assert.Equal(SessionStage.Finalizing, state.Stage);
            }
        }

        // ------------------------------------------------------------------------------

        private static DecisionState ProgressToPromotedAwaitingHello(DecisionEngine engine)
        {
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeEspConfigSignal(1, skipUser: "true")).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddSeconds(1),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "true" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DesktopArrived, T0.AddSeconds(5), null)).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspPhaseChanged, T0.AddSeconds(-12),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.ImeUserSessionCompleted,
                T0.AddMinutes(6).AddSeconds(51), null)).NewState;
            Assert.Equal(SessionStage.AwaitingHello, state.Stage);
            return state;
        }

        private static DecisionSignal MakeEspConfigSignal(long ordinal, string skipUser) =>
            MakeSignal(ordinal, DecisionSignalKind.EspConfigDetected, T0.AddSeconds(ordinal),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = skipUser,
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                });

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"test-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);
        }
    }
}
