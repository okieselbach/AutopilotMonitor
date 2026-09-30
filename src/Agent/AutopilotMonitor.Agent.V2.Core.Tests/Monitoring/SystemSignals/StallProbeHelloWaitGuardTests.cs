#nullable enable
using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// A tenant-extended Hello wait runs up to 60 min — the default session_stalled threshold. While
    /// it is pending the session waits on a bounded timer that resolves it either way, so the stall
    /// probe holds the report back instead of flagging the session Stalled; once the wait is over
    /// and the session is still idle the report fires, and real activity drops it. The collector
    /// runs with no scan sources, so only the session_stalled decision is exercised.
    /// </summary>
    public sealed class StallProbeHelloWaitGuardTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
        private const double IdleAtStallThreshold = 60;

        [Fact]
        public void Session_stalled_is_held_back_while_the_hello_wait_is_pending()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            var collector = NewCollector(tmp, ingress, helloWaitPending: () => true);

            collector.CheckAndRunProbes(IdleAtStallThreshold);
            collector.CheckAndRunProbes(IdleAtStallThreshold + 1);

            Assert.Equal(0, StalledCount(ingress));
        }

        [Fact]
        public void Held_back_report_fires_once_the_hello_wait_is_over_and_the_session_is_still_idle()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            var pending = true;
            var collector = NewCollector(tmp, ingress, helloWaitPending: () => pending);

            collector.CheckAndRunProbes(IdleAtStallThreshold);
            Assert.Equal(0, StalledCount(ingress));

            // hello_safety resolved (a deadline fire is no activity, so no ResetProbes): the next
            // tick emits the held-back report — once.
            pending = false;
            collector.CheckAndRunProbes(IdleAtStallThreshold + 1);
            collector.CheckAndRunProbes(IdleAtStallThreshold + 2);

            Assert.Equal(1, StalledCount(ingress));
        }

        [Fact]
        public void Real_activity_drops_the_held_back_report()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            var pending = true;
            var collector = NewCollector(tmp, ingress, helloWaitPending: () => pending);

            collector.CheckAndRunProbes(IdleAtStallThreshold);
            pending = false;
            collector.ResetProbes();
            collector.CheckAndRunProbes(1);
            Assert.Equal(0, StalledCount(ingress));

            // A new idle window reaches the threshold again — the fire-once report is still unused.
            collector.CheckAndRunProbes(IdleAtStallThreshold);
            Assert.Equal(1, StalledCount(ingress));
        }

        [Fact]
        public void Without_a_hello_wait_probe_session_stalled_fires_as_before()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            var collector = NewCollector(tmp, ingress, helloWaitPending: null);

            collector.CheckAndRunProbes(IdleAtStallThreshold);

            Assert.Equal(1, StalledCount(ingress));
        }

        [Fact]
        public void A_throwing_hello_wait_probe_never_costs_the_stall_report()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            var collector = NewCollector(tmp, ingress, helloWaitPending: () => throw new InvalidOperationException("probe down"));

            collector.CheckAndRunProbes(IdleAtStallThreshold);

            Assert.Equal(1, StalledCount(ingress));
        }

        private static StallProbeCollector NewCollector(TempDirectory tmp, FakeSignalIngressSink ingress, Func<bool>? helloWaitPending) =>
            new StallProbeCollector(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(ingress, new VirtualClock(Fixed)),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info),
                thresholdsMinutes: new[] { 60 },
                traceIndices: Array.Empty<int>(),
                sources: Array.Empty<string>(),
                sessionStalledAfterProbeIndex: 1,
                helloWaitPending: helloWaitPending);

        private static int StalledCount(FakeSignalIngressSink ingress) =>
            ingress.Posted.Count(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == Constants.EventTypes.SessionStalled);
    }
}
