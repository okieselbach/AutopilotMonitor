using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Plan §4.x M4.4.4 — the adapter's <c>ReadHelloOutcome</c> now forwards from
    /// <see cref="EspAndHelloTracker.HelloOutcome"/> (which itself delegates to the inner
    /// <c>HelloTracker</c>) instead of hardcoded <c>"unknown"</c>. Verified via an internal
    /// test hook that exercises the full event-handler emit path without driving the inner
    /// HelloTracker's live event sources.
    /// </summary>
    public sealed class EspAndHelloTrackerAdapterHelloOutcomeTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public EspAndHelloTracker Coordinator { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                Coordinator = new EspAndHelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger);
            }

            public void Dispose()
            {
                Coordinator.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void No_inner_HelloTracker_yet_started_falls_back_to_unknown()
        {
            // Without Coordinator.Start(), the inner HelloTracker is null → HelloOutcome is null
            // → adapter falls back to "unknown".
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloFromCoordinatorPropertyForTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.HelloResolved, posted.Kind);
            Assert.Equal("unknown", posted.Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Fact]
        public void Coordinator_HelloOutcome_set_via_ForceMarkHelloCompleted_is_forwarded_to_signal()
        {
            // Start() initializes the inner HelloTracker; ForceMarkHelloCompleted sets
            // HelloOutcome without firing the HelloCompleted event (used as a safety timeout
            // path in production). The adapter then reads the forwarded property — M4.4.4 fix.
            using var f = new Fixture();
            f.Coordinator.Start();
            try
            {
                f.Coordinator.ForceMarkHelloCompleted("completed");

                using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);
                adapter.TriggerHelloFromCoordinatorPropertyForTest();

                var posted = Assert.Single(f.Ingress.Posted);
                Assert.Equal("completed", posted.Payload![SignalPayloadKeys.HelloOutcome]);
                Assert.Contains("completed", posted.Evidence.Summary);
            }
            finally
            {
                // Tear down inner tracker timers / event-log watchers.
                f.Coordinator.Dispose();
            }
        }

        [Fact]
        public void Custom_outcome_strings_round_trip_through_the_signal_payload()
        {
            // Any non-null HelloOutcome value should flow through — the adapter doesn't enforce
            // a specific enum, it just forwards. Verifies the M4.4.4 path doesn't insert a
            // mapping layer (signal payload carries the raw coordinator value).
            using var f = new Fixture();
            f.Coordinator.Start();
            try
            {
                f.Coordinator.ForceMarkHelloCompleted("timeout");

                using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);
                adapter.TriggerHelloFromCoordinatorPropertyForTest();

                var posted = Assert.Single(f.Ingress.Posted);
                Assert.Equal(DecisionSignalKind.HelloResolved, posted.Kind);
                Assert.Equal("timeout", posted.Payload![SignalPayloadKeys.HelloOutcome]);
            }
            finally
            {
                f.Coordinator.Dispose();
            }
        }
    }
}
