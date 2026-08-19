using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Device Preparation (WDP) engine semantics — afee7ae0 audit (2026-08-18).
    /// WDP has no ESP, so the ESP-derived arms/gates/deadlines can never fire there.
    /// Coverage:
    /// <list type="bullet">
    ///   <item>Profile seeding: the deterministic WDP marker seeds High confidence; the
    ///         CloudAssigned* fallback rules stay Medium.</item>
    ///   <item><c>completion_waiting</c> never lists <c>account_setup_provisioning_complete</c>
    ///         on WDP (unsatisfiable by construction — no ESP categories).</item>
    ///   <item>Desktop-first arming of the <see cref="DeadlineNames.DevicePrepCompletion"/>
    ///         backstop, its fire semantics (synthetic Hello timeout + Finalizing), and the
    ///         no-rearm/no-classic-leak guards.</item>
    ///   <item>Arm D: the Hello-disabled fast-path completes at desktop arrival on WDP.</item>
    /// </list>
    /// </summary>
    public sealed class DevicePreparationEngineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionSignal DeadlineFired(long ordinal, DateTime occurredAtUtc, string deadlineName) =>
            MakeSignal(ordinal, DecisionSignalKind.DeadlineFired, occurredAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = deadlineName });

        private static List<DecisionEffect> CompletionWaitingEffects(DecisionStep step) =>
            step.Effects.Where(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == "completion_waiting").ToList();

        /// <summary>Session seeded as WDP via the EnrollmentFactsObserved signal rail.</summary>
        private static DecisionState SeedWdpSession(DecisionEngine engine, bool deterministic = true)
        {
            var state = DecisionState.CreateInitial("sess-wdp", "tenant-wdp", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                1, DecisionSignalKind.EnrollmentFactsObserved, T0,
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v2",
                    [SignalPayloadKeys.EnrollmentTypeDeterministic] = deterministic ? "true" : "false",
                })).NewState;
            return state;
        }

        // ===================================================== profile seeding

        [Fact]
        public void DeterministicV2Seed_SetsDevicePreparationAtHighConfidence()
        {
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine, deterministic: true);

            Assert.Equal(EnrollmentMode.DevicePreparation, state.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, state.ScenarioProfile.Confidence);
        }

        [Fact]
        public void FallbackV2Seed_StaysAtMediumConfidence()
        {
            // CloudAssigned* fallback rules (no deterministic marker) — Medium, as before
            // d0ab3eee: an ESP-less classic profile could in principle trip them.
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine, deterministic: false);

            Assert.Equal(EnrollmentMode.DevicePreparation, state.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.Medium, state.ScenarioProfile.Confidence);
        }

        [Fact]
        public void LegacyV2Payload_WithoutDeterministicKey_StaysAtMediumConfidence()
        {
            // Older agents post enrollmentType=v2 without the new key — must behave like
            // the fallback (Medium), not throw or over-promote.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-legacy", "tenant-wdp", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                1, DecisionSignalKind.EnrollmentFactsObserved, T0,
                new Dictionary<string, string> { [SignalPayloadKeys.EnrollmentType] = "v2" })).NewState;

            Assert.Equal(EnrollmentMode.DevicePreparation, state.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.Medium, state.ScenarioProfile.Confidence);
        }

        // ===================================================== desktop-first backstop

        [Fact]
        public void DesktopFirst_OnWdp_ArmsDevicePrepCompletionBackstop()
        {
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine);

            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(9)));

            // Hello policy unknown → no fast-path; stage parked, backstop armed.
            var backstop = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.DevicePrepCompletion);
            Assert.Equal(T0.AddMinutes(9).AddMinutes(30), backstop.DueAtUtc);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.DevicePrepCompletion);
        }

        [Fact]
        public void DesktopFirst_OnWdp_WaitingListsOnlyHello_NeverTheEspProvisioningGate()
        {
            // account_setup_provisioning_complete is unsatisfiable on WDP (no ESP categories)
            // and must not appear as a missing prerequisite (afee7ae0 listed it).
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine);

            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(9)));

            var waiting = Assert.Single(CompletionWaitingEffects(step));
            Assert.Equal("hello_resolution", waiting.Parameters!["missingPrerequisites"]);
        }

        [Fact]
        public void DuplicateDesktopSignal_DoesNotRearmTheBackstop()
        {
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(9))).NewState;

            var second = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10)));

            Assert.Single(second.NewState.Deadlines, d => d.Name == DeadlineNames.DevicePrepCompletion);
            Assert.DoesNotContain(second.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.DevicePrepCompletion);
        }

        [Fact]
        public void ClassicDesktopFirst_DoesNotArmTheBackstop()
        {
            // Classic regression guard: a non-WDP session parked desktop-first keeps the
            // pre-change deadline picture (nothing armed by this handler).
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-classic", "tenant-classic", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(9)));

            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.DevicePrepCompletion);
        }

        // ===================================================== backstop fire semantics

        [Fact]
        public void BackstopFired_WithoutHello_SynthesizesTimeoutAndCompletesThroughFinalizing()
        {
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(9))).NewState;

            var fired = engine.Reduce(state, DeadlineFired(20, T0.AddMinutes(39), DeadlineNames.DevicePrepCompletion));

            Assert.Equal(SessionStage.Finalizing, fired.NewState.Stage);
            Assert.NotNull(fired.NewState.HelloResolvedUtc);
            Assert.Equal("Timeout", fired.NewState.HelloOutcome!.Value);
            Assert.DoesNotContain(fired.NewState.Deadlines, d => d.Name == DeadlineNames.DevicePrepCompletion);
            Assert.Contains(fired.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);

            // FinalizingGrace completes the session — the full WDP no-Hello resolution chain.
            var completed = engine.Reduce(fired.NewState,
                DeadlineFired(21, T0.AddMinutes(39).AddSeconds(5), DeadlineNames.FinalizingGrace));
            Assert.Equal(SessionStage.Completed, completed.NewState.Stage);
        }

        [Fact]
        public void BackstopFired_WithoutDesktop_ParksInAwaitingDesktopUntilDesktopCompletes()
        {
            // The arming site (HandleDesktopArrivedV1) guarantees desktop-first, so this
            // fires only on a stale/replayed timer — Dispatch routes DeadlineFired purely on
            // the payload name, no armed-state check. The fallback must keep the handler
            // total: synthetic Hello timeout recorded, session parks in AwaitingDesktop, and
            // a later real desktop arrival still completes it.
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine);

            var fired = engine.Reduce(state, DeadlineFired(20, T0.AddMinutes(39), DeadlineNames.DevicePrepCompletion));

            Assert.Equal(SessionStage.AwaitingDesktop, fired.NewState.Stage);
            Assert.NotNull(fired.NewState.HelloResolvedUtc);
            Assert.Equal("Timeout", fired.NewState.HelloOutcome!.Value);
            Assert.DoesNotContain(fired.NewState.Deadlines, d => d.Name == DeadlineNames.DevicePrepCompletion);

            // Hello is satisfied (synthetically) — only the desktop is still missing.
            var waiting = Assert.Single(CompletionWaitingEffects(fired));
            Assert.Equal("desktop_arrival", waiting.Parameters!["missingPrerequisites"]);

            var completed = engine.Reduce(fired.NewState,
                MakeSignal(21, DecisionSignalKind.DesktopArrived, T0.AddMinutes(45)));
            Assert.Equal(SessionStage.Finalizing, completed.NewState.Stage);
        }

        [Fact]
        public void HelloResolved_BeforeBackstopFires_CompletesNormally()
        {
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(9))).NewState;

            var resolved = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.HelloResolved, T0.AddMinutes(10),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "completed" }));

            Assert.Equal(SessionStage.Finalizing, resolved.NewState.Stage);
            Assert.Equal("completed", resolved.NewState.HelloOutcome!.Value);
        }

        // ===================================================== arm D fast-path

        [Fact]
        public void HelloDisabledFastPath_OnWdp_CompletesAtDesktopArrival()
        {
            // Arm D: no ESP facts exist on WDP, yet a disabled Hello policy + real-user
            // desktop is a legitimate completion — previously arms A–C all failed and the
            // session parked despite Hello being disabled.
            var engine = new DecisionEngine();
            var state = SeedWdpSession(engine);
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;

            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(9)));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
        }
    }
}
