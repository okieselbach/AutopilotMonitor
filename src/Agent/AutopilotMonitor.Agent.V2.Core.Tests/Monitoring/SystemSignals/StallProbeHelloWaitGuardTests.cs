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
    /// A tenant Hello wait runs up to 60 min — exactly the default session_stalled threshold. While
    /// the engine's hello_safety window is pending the session waits on a bounded timer that
    /// resolves it either way, so the stall probe must not report it Stalled; once the window is
    /// over (or without the probe) the report fires as before. The collector runs with no scan
    /// sources, so only the session_stalled decision is exercised.
    /// </summary>
    public sealed class StallProbeHelloWaitGuardTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
        private const double IdleAtStallThreshold = 60;

        [Fact]
        public void Session_stalled_is_skipped_while_the_hello_wait_is_pending()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            var collector = NewCollector(tmp, ingress, helloWaitPending: () => true);

            collector.CheckAndRunProbes(IdleAtStallThreshold);

            Assert.Equal(0, StalledCount(ingress));
        }

        [Fact]
        public void Session_stalled_fires_in_the_next_idle_window_once_the_hello_wait_is_over()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            var pending = true;
            var collector = NewCollector(tmp, ingress, helloWaitPending: () => pending);

            collector.CheckAndRunProbes(IdleAtStallThreshold);
            Assert.Equal(0, StalledCount(ingress));

            // Real activity starts a new idle window; the skip did not consume the fire-once report.
            pending = false;
            collector.ResetProbes();
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
