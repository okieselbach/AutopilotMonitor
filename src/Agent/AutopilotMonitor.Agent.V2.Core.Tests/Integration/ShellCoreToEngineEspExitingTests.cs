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
    /// Plan §EspExiting Adapter — wire-through verification of the production EspExiting path:
    /// <c>ShellCoreTracker.HandleBackfillRecord</c> → <c>EspExited</c> event → inner re-raise on
    /// <c>EspAndHelloTracker.EspExited</c> → <c>EspAndHelloTrackerAdapter</c> → ingress → engine
    /// reducer. This is the path the live V2-Host uses; without the coordinator forward the
    /// EspExiting signal would never reach the engine in production.
    /// <para>
    /// The first test isolates the tracker's <c>EspExited</c> contract (source-event timestamp
    /// preserved through backfill); the coordinator tests drive the full production chain through
    /// to the <c>HelloSafety</c> deadline when AccountSetup is already entered.
    /// </para>
    /// </summary>
    public sealed class ShellCoreToEngineEspExitingTests
    {
        private static readonly DateTime ClockNow = new DateTime(2026, 5, 12, 17, 45, 0, DateTimeKind.Utc);
        private const string EspExitingDescription =
            "CommercialOOBE_ESPProgress_Page_Exiting"; // matches EspExitingPattern in ShellCoreTracker

        [Fact]
        public void Tracker_backfill_record_raises_EspExited_with_source_timestamp()
        {
            // Tracker-level contract: HandleBackfillRecord raises EspExited carrying the source
            // event time (not Clock.UtcNow). The EspExited→EspExiting signal mapping and SourceOrigin
            // are covered by the coordinator tests below; here we isolate the backfill→event link
            // that the production coordinator depends on.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var clock = new VirtualClock(ClockNow);
            var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), clock);
            using var tracker = new ShellCoreTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: trackerPost,
                logger: logger,
                helloTracker: null);

            EspExitedEventArgs captured = null;
            tracker.EspExited += (_, args) => captured = args;

            // Source time IS in the past relative to the clock — verify the tracker carries the
            // historical timestamp on the event rather than collapsing to Clock.UtcNow.
            var sourceTime = ClockNow.AddMinutes(-2);
            tracker.HandleBackfillRecord(EspExitingDescription, sourceTime);

            Assert.NotNull(captured);
            Assert.Equal(sourceTime, captured!.OccurredAtUtc);
        }

        // -------------------------------------------- Coordinator-pfad (production wiring) --
        //
        // These tests drive the coordinator's OnEspExited inner-handler via the
        // EspAndHelloTracker.TriggerEspExitedForTest seam, which matches the live signature
        // (DateTime args) used by the inner ShellCoreTracker.EspExited event. Going through
        // the coordinator handler exercises:
        //   1. LastEventOccurredAtUtc mirroring on the args timestamp
        //   2. The public EspAndHelloTracker.EspExited re-raise
        //   3. The adapter's coordinator subscription
        //   4. ResolveOccurredAt source-time preservation through the adapter
        // Going through adapter.TriggerEspExitingFromTest() (sibling adapter-only tests in
        // SignalAdapters/EspAndHelloTrackerAdapterTests) does NOT exercise step 1-3.

        [Fact]
        public void Coordinator_EspExited_handler_re_raises_to_adapter_as_EspExiting_signal()
        {
            // Production wiring: V2-Host instantiates EspAndHelloTracker (coordinator) which owns
            // a private ShellCoreTracker; EspAndHelloTrackerAdapter subscribes to the
            // coordinator's re-raised events. Trigger the coordinator's inner OnEspExited handler
            // directly — that's the exact code path .NET runtime invokes when the inner
            // ShellCoreTracker.EspExited delegate fires.
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

            var sourceTime = ClockNow.AddMinutes(-3);
            coordinator.TriggerEspExitedForTest(sourceTime);

            var espExiting = ingress.Posted.FirstOrDefault(p => p.Kind == DecisionSignalKind.EspExiting);
            Assert.NotNull(espExiting);
            Assert.Equal("EspAndHelloTracker", espExiting!.SourceOrigin);
            Assert.Equal("ShellCoreTracker", espExiting.Evidence.DerivationInputs!["subSource"]);

            // Critical: source-event timestamp made it through Coordinator.OnEspExited's
            // LastEventOccurredAtUtc mirror into the adapter's ResolveOccurredAt — proving the
            // re-raise contract end-to-end, not just the adapter emit.
            Assert.Equal(sourceTime, espExiting.OccurredAtUtc);
            Assert.NotEqual(clock.UtcNow, espExiting.OccurredAtUtc);
            // No clock-fallback tag — would only be set if LastEventOccurredAtUtc was null.
            Assert.False(espExiting.Evidence.DerivationInputs.ContainsKey("derivedTimestamp"));
        }

        [Fact]
        public void Coordinator_EspExited_handler_clears_LastEventOccurredAtUtc_after_re_raise()
        {
            // Defensive contract: after OnEspExited's finally-block, LastEventOccurredAtUtc is
            // back to null so a subsequent Hello / Provisioning forward (which intentionally sets
            // LastEventOccurredAtUtc = null) cannot bleed a stale ShellCore timestamp into the
            // adapter's ResolveOccurredAt.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(new FakeSignalIngressSink(), new VirtualClock(ClockNow)),
                logger: logger);

            coordinator.TriggerEspExitedForTest(ClockNow.AddMinutes(-3));

            Assert.Null(coordinator.LastEventOccurredAtUtc);
        }

        [Fact]
        public void Coordinator_to_reducer_arms_HelloSafety_when_AccountSetup_observed()
        {
            // End-to-end through the production-wired coordinator pipeline + engine reducer.
            // Uses the coordinator's TriggerEspExitedForTest so the assertion actually proves
            // ShellCoreTracker→Coordinator→Adapter→Engine, not just Adapter→Engine.
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

            var sourceTime = ClockNow.AddMinutes(-1);
            coordinator.TriggerEspExitedForTest(sourceTime);

            var espExiting = ingress.Posted.FirstOrDefault(p => p.Kind == DecisionSignalKind.EspExiting);
            Assert.NotNull(espExiting);
            Assert.Equal(sourceTime, espExiting!.OccurredAtUtc);

            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("S1", "T1", agentBootUtc: sourceTime.AddMinutes(-5))
                .ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(2)
                .WithLastAppliedSignalOrdinal(1);
            seed.AccountSetupEnteredUtc = new SignalFact<DateTime>(sourceTime.AddMinutes(-2), 1);
            seed.AccountSetupProvisioningSucceededUtc = new SignalFact<DateTime>(sourceTime.AddMinutes(-1).AddSeconds(-30), 1);
            var state = seed.Build();

            var enriched = new DecisionSignal(
                sessionSignalOrdinal: 2,
                sessionTraceOrdinal: 2,
                kind: espExiting.Kind,
                kindSchemaVersion: espExiting.KindSchemaVersion,
                occurredAtUtc: espExiting.OccurredAtUtc,
                sourceOrigin: espExiting.SourceOrigin,
                evidence: espExiting.Evidence,
                payload: espExiting.Payload);

            var step = engine.Reduce(state, enriched);

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            var helloSafety = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            // HelloSafety floored at the source-time, not wall-clock-now — proves the source
            // timestamp made it through coordinator→adapter→engine, not just adapter→engine.
            Assert.Equal(sourceTime.AddSeconds(300), helloSafety.DueAtUtc);
        }
    }
}
