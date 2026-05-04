using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Replay-safety regression: when the agent reads accumulated CMTrace / Shell-Core log
    /// content on first boot, signals carry historical <c>OccurredAtUtc</c> values that
    /// would otherwise drive the reducer to arm deadlines already in the past — collapsing
    /// the Hello-safety window, the device-only ESP detection window, and the FinalizingGrace
    /// window to immediate fires. <see cref="DecisionEngine.EffectiveDeadlineBase"/> floors
    /// the arming base at <see cref="DecisionState.AgentBootUtc"/>; this suite locks every
    /// affected reducer site against a regression that bypasses the helper.
    /// </summary>
    public sealed class BootAnchorDeadlineFloorTests
    {
        private static readonly DateTime LogPast = new DateTime(2026, 4, 20, 9, 30, 0, DateTimeKind.Utc);
        private static readonly DateTime AgentBoot = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionState FreshAtBoot() =>
            DecisionState.CreateInitial("sess-boot", "tenant-boot", AgentBoot);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"replay-{kind}-{ordinal}", "synthetic"),
                payload: payload);

        [Fact]
        public void HelloSafety_armed_from_replayed_EspExiting_floors_at_AgentBootUtc()
        {
            // Setup: state already in EspAccountSetup so EspExiting takes the AwaitingHello
            // path. Build it with a non-replayed AccountSetup signal so the boot-anchor floor
            // only matters for the EspExiting arming step.
            var engine = new DecisionEngine();
            var state = FreshAtBoot();
            state = engine.Reduce(state,
                MakeSignal(0, DecisionSignalKind.SessionStarted, AgentBoot)).NewState;
            state = engine.Reduce(state,
                MakeSignal(1, DecisionSignalKind.EspPhaseChanged, AgentBoot.AddSeconds(1),
                    new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            // Replayed CMTrace EspExiting from before the agent booted.
            var replayed = MakeSignal(2, DecisionSignalKind.EspExiting, LogPast);
            var step = engine.Reduce(state, replayed);

            var helloSafety = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            // Without the floor: dueAt = LogPast + 300s = 9:35 (past-due → fires immediately).
            // With the floor:    dueAt = AgentBoot + 300s = 10:05 (the proper grace window).
            Assert.Equal(AgentBoot.AddSeconds(300), helloSafety.DueAtUtc);
        }

        [Fact]
        public void DeviceOnlyEspDetection_armed_from_replayed_DeviceSetup_floors_at_AgentBootUtc()
        {
            var engine = new DecisionEngine();
            var state = FreshAtBoot();
            state = engine.Reduce(state,
                MakeSignal(0, DecisionSignalKind.SessionStarted, AgentBoot)).NewState;

            // Replayed DeviceSetup phase change carrying a 30-min-old CMTrace timestamp.
            var replayed = MakeSignal(1, DecisionSignalKind.EspPhaseChanged, LogPast,
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" });
            var step = engine.Reduce(state, replayed);

            var devOnly = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            // Without the floor: dueAt = LogPast + 5min = 9:35 → fires immediately, classifier
            // promotes DeviceOnlyDeployment to Strong. With the floor: dueAt = AgentBoot + 5min.
            Assert.Equal(AgentBoot.AddMinutes(5), devOnly.DueAtUtc);
        }

        [Fact]
        public void FinalizingGrace_armed_from_replayed_HelloDesktop_pair_floors_at_AgentBootUtc()
        {
            // Drive state to AwaitingHello via current-clock signals, then deliver a replayed
            // HelloResolved + DesktopArrived pair from log-tail. The TransitionToFinalizing
            // path arms FinalizingGrace; without the floor that fires immediately and emits
            // a premature enrollment_complete on agent boot.
            var engine = new DecisionEngine();
            var state = FreshAtBoot();
            state = engine.Reduce(state,
                MakeSignal(0, DecisionSignalKind.SessionStarted, AgentBoot)).NewState;
            state = engine.Reduce(state,
                MakeSignal(1, DecisionSignalKind.EspPhaseChanged, AgentBoot.AddSeconds(1),
                    new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state,
                MakeSignal(2, DecisionSignalKind.EspExiting, AgentBoot.AddSeconds(2))).NewState;

            // Replayed HelloResolved (parks in AwaitingDesktop) and replayed DesktopArrived
            // (drives Finalizing — this is the deadline-arming site we're testing).
            state = engine.Reduce(state,
                MakeSignal(3, DecisionSignalKind.HelloResolved, LogPast,
                    new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;
            var step = engine.Reduce(state,
                MakeSignal(4, DecisionSignalKind.DesktopArrived, LogPast.AddSeconds(1)));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            var grace = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            // Without the floor: dueAt = LogPast + 5s → fires immediately, premature complete.
            // With the floor:    dueAt = AgentBoot + 5s.
            Assert.Equal(AgentBoot.AddSeconds(5), grace.DueAtUtc);
        }

        [Fact]
        public void Live_signal_after_boot_uses_signalOccurredAtUtc_not_AgentBootUtc()
        {
            // Sanity: the floor is one-sided. A signal whose OccurredAtUtc is *after* the
            // boot anchor gets its raw timestamp through — the floor only kicks in when the
            // signal is older than the boot.
            var engine = new DecisionEngine();
            var state = FreshAtBoot();
            state = engine.Reduce(state,
                MakeSignal(0, DecisionSignalKind.SessionStarted, AgentBoot)).NewState;

            var liveAfterBoot = AgentBoot.AddMinutes(2);
            var step = engine.Reduce(state,
                MakeSignal(1, DecisionSignalKind.EspPhaseChanged, liveAfterBoot,
                    new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" }));

            var devOnly = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(liveAfterBoot.AddMinutes(5), devOnly.DueAtUtc);
        }

        [Fact]
        public void Legacy_state_with_null_AgentBootUtc_falls_back_to_signalOccurredAtUtc()
        {
            // Backward-compat: snapshots from before this field existed deserialize with
            // AgentBootUtc=null. The helper must fall back to signal.OccurredAtUtc so legacy
            // sessions reproduce their pre-fix deadline values exactly (no replay correction).
            var engine = new DecisionEngine();
            var legacy = new DecisionState(
                sessionId: "sess-legacy",
                tenantId: "tenant-legacy",
                stage: SessionStage.SessionStarted,
                outcome: null,
                currentEnrollmentPhase: null,
                deviceSetupEnteredUtc: null,
                accountSetupEnteredUtc: null,
                finalizingEnteredUtc: null,
                espFinalExitUtc: null,
                desktopArrivedUtc: null,
                helloResolvedUtc: null,
                systemRebootUtc: null,
                helloOutcome: null,
                imeMatchedPatternId: null,
                deadlines: Array.Empty<ActiveDeadline>(),
                lastAppliedSignalOrdinal: -1,
                stepIndex: 0,
                agentBootUtc: null);

            var step = engine.Reduce(legacy,
                MakeSignal(0, DecisionSignalKind.SessionStarted, LogPast));
            var tick = Assert.Single(step.NewState.Deadlines);

            Assert.Null(step.NewState.AgentBootUtc);
            Assert.Equal(LogPast.AddSeconds(30), tick.DueAtUtc);
        }
    }
}
