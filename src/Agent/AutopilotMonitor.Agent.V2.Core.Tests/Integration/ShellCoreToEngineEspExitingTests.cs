using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Integration
{
    /// <summary>
    /// Plan §EspExiting Adapter — wire-through verification on TWO topologies:
    /// <list type="number">
    ///   <item>Single-tracker wiring: <c>ShellCoreTracker.EspExited</c> →
    ///         <c>ShellCoreTrackerAdapter</c> → ingress (test-only scenario).</item>
    ///   <item>Production wiring: <c>ShellCoreTracker.EspExited</c> → inner re-raise on
    ///         <c>EspAndHelloTracker.EspExited</c> → <c>EspAndHelloTrackerAdapter</c> → ingress.
    ///         This is the path the live V2-Host uses; without the coordinator forward the
    ///         new EspExiting signal would never reach the engine in production.</item>
    /// </list>
    /// Both paths end with a reducer step that arms the <c>HelloSafety</c> deadline when
    /// AccountSetup is already entered.
    /// </summary>
    public sealed class ShellCoreToEngineEspExitingTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 5, 12, 17, 45, 0, DateTimeKind.Utc);
        private const string EspExitingDescription =
            "CommercialOOBE_ESPProgress_Page_Exiting"; // matches EspExitingPattern in ShellCoreTracker

        [Fact]
        public void Tracker_backfill_record_drives_adapter_to_post_EspExiting_signal()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var ingress = new FakeSignalIngressSink();
            var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), clock);
            using var tracker = new ShellCoreTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: trackerPost,
                logger: logger,
                helloTracker: null);
            using var adapter = new ShellCoreTrackerAdapter(tracker, ingress, clock);

            // Source time IS in the past relative to the clock — verify the adapter forwards
            // the historical timestamp from the EventArgs rather than collapsing to Clock.UtcNow.
            var sourceTime = ClockNow.AddMinutes(-2);
            tracker.HandleBackfillRecord(EspExitingDescription, sourceTime);

            // One EspExiting on the ingress with the source timestamp preserved.
            Assert.Contains(ingress.Posted, p => p.Kind == DecisionSignalKind.EspExiting);
            var espExiting = ingress.Posted.FirstOrDefault(p => p.Kind == DecisionSignalKind.EspExiting)!;
            Assert.Equal("ShellCoreTracker", espExiting.SourceOrigin);
            Assert.Equal(sourceTime, espExiting.OccurredAtUtc);
        }

        [Fact]
        public void Tracker_backfill_then_reducer_step_arms_HelloSafety_when_AccountSetup_observed()
        {
            // End-to-end: real tracker event → adapter signal → engine reducer →
            // HelloSafety deadline. Builds the seed state with AccountSetup pre-entered so the
            // reducer's ShouldTransitionToAwaitingHello guard unlocks on the EspExiting forward.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var ingress = new FakeSignalIngressSink();
            var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), clock);
            using var tracker = new ShellCoreTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: trackerPost,
                logger: logger,
                helloTracker: null);
            using var adapter = new ShellCoreTrackerAdapter(tracker, ingress, clock);

            var sourceTime = ClockNow.AddMinutes(-1);
            tracker.HandleBackfillRecord(EspExitingDescription, sourceTime);

            var espExiting = ingress.Posted.FirstOrDefault(p => p.Kind == DecisionSignalKind.EspExiting);
            Assert.NotNull(espExiting);

            // Build a Classic state already at AccountSetup so the EspExiting guard unlocks.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("S1", "T1", agentBootUtc: sourceTime.AddMinutes(-5))
                .ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(2)
                .WithLastAppliedSignalOrdinal(1);
            seed.AccountSetupEnteredUtc = new SignalFact<DateTime>(sourceTime.AddMinutes(-2), 1);
            var state = seed.Build();

            // Re-wrap the adapter-posted signal with the ordinal the engine expects.
            var enrichedSignal = new DecisionSignal(
                sessionSignalOrdinal: 2,
                sessionTraceOrdinal: 2,
                kind: espExiting!.Kind,
                kindSchemaVersion: espExiting.KindSchemaVersion,
                occurredAtUtc: espExiting.OccurredAtUtc,
                sourceOrigin: espExiting.SourceOrigin,
                evidence: espExiting.Evidence,
                payload: espExiting.Payload);

            var step = engine.Reduce(state, enrichedSignal);

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            var helloSafety = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(sourceTime.AddSeconds(300), helloSafety.DueAtUtc);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline
                     && e.Deadline?.Name == DeadlineNames.HelloSafety);
        }

        // -------------------------------------------- Coordinator-pfad (production wiring) --

        [Fact]
        public void Coordinator_propagates_EspExited_to_EspAndHelloTrackerAdapter_as_EspExiting_signal()
        {
            // Production wiring: V2-Host instantiates EspAndHelloTracker (coordinator) which owns
            // a private ShellCoreTracker; EspAndHelloTrackerAdapter subscribes to the
            // coordinator's re-raised events. The new EspExited event must traverse the
            // coordinator and emerge on the adapter as DecisionSignalKind.EspExiting.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var ingress = new FakeSignalIngressSink();
            var coordinatorPost = new InformationalEventPost(new FakeSignalIngressSink(), clock);
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: coordinatorPost,
                logger: logger);
            // The coordinator's inner ShellCoreTracker comes alive on Start(); we can't drive
            // a real Event-Log record from a unit test, so simulate the inner forward by
            // raising EspExited on the coordinator via the test-only seam.
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, ingress, clock);

            adapter.TriggerEspExitingFromTest();

            var espExiting = ingress.Posted.FirstOrDefault(p => p.Kind == DecisionSignalKind.EspExiting);
            Assert.NotNull(espExiting);
            Assert.Equal("EspAndHelloTracker", espExiting!.SourceOrigin);
            Assert.Equal("ShellCoreTracker", espExiting.Evidence.DerivationInputs!["subSource"]);
        }

        [Fact]
        public void Coordinator_to_reducer_arms_HelloSafety_when_AccountSetup_observed()
        {
            // End-to-end through the production-wired coordinator pipeline + engine reducer.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var ingress = new FakeSignalIngressSink();
            var coordinatorPost = new InformationalEventPost(new FakeSignalIngressSink(), clock);
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: coordinatorPost,
                logger: logger);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, ingress, clock);

            adapter.TriggerEspExitingFromTest();

            var espExiting = ingress.Posted.FirstOrDefault(p => p.Kind == DecisionSignalKind.EspExiting);
            Assert.NotNull(espExiting);

            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("S1", "T1", agentBootUtc: ClockNow.AddMinutes(-5))
                .ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(2)
                .WithLastAppliedSignalOrdinal(1);
            seed.AccountSetupEnteredUtc = new SignalFact<DateTime>(ClockNow.AddMinutes(-2), 1);
            var state = seed.Build();

            var enriched = new DecisionSignal(
                sessionSignalOrdinal: 2,
                sessionTraceOrdinal: 2,
                kind: espExiting!.Kind,
                kindSchemaVersion: espExiting.KindSchemaVersion,
                occurredAtUtc: espExiting.OccurredAtUtc,
                sourceOrigin: espExiting.SourceOrigin,
                evidence: espExiting.Evidence,
                payload: espExiting.Payload);

            var step = engine.Reduce(state, enriched);

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }
    }
}
