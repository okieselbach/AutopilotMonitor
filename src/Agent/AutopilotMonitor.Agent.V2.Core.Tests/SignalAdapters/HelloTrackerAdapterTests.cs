using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class HelloTrackerAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public HelloTracker Tracker { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                // Separate throwaway ingress for tracker emissions so the adapter's DecisionSignal
                // assertions on Ingress.Posted stay unpolluted by InformationalEvent pass-through.
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                Tracker = new HelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger);
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void TriggerFromTest_emits_HelloResolved_with_outcome_in_payload()
        {
            using var f = new Fixture();
            using var adapter = new HelloTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFromTest("completed");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.HelloResolved, posted.Kind);
            Assert.Equal(Fixed, posted.OccurredAtUtc);
            Assert.Equal("HelloTracker", posted.SourceOrigin);
            Assert.Equal("completed", posted.Payload![SignalPayloadKeys.HelloOutcome]);
            Assert.Contains("completed", posted.Evidence.Summary);
        }

        [Theory]
        [InlineData("completed")]
        [InlineData("skipped")]
        [InlineData("timeout")]
        [InlineData("not_configured")]
        [InlineData("wizard_not_started")]
        public void Outcome_propagates_verbatim_to_payload(string outcome)
        {
            using var f = new Fixture();
            using var adapter = new HelloTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFromTest(outcome);

            Assert.Equal(outcome, f.Ingress.Posted[0].Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Fact]
        public void Null_or_empty_outcome_falls_back_to_unknown()
        {
            using var f = new Fixture();
            using var adapter = new HelloTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFromTest(null!);

            Assert.Equal("unknown", f.Ingress.Posted[0].Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Fact]
        public void Duplicate_trigger_is_deduplicated()
        {
            using var f = new Fixture();
            using var adapter = new HelloTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFromTest("completed");
            adapter.TriggerFromTest("timeout");   // even with different outcome, we're fire-once

            Assert.Single(f.Ingress.Posted);
            Assert.Equal("completed", f.Ingress.Posted[0].Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new HelloTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new HelloTrackerAdapter(f.Tracker, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new HelloTrackerAdapter(f.Tracker, f.Ingress, null!));
        }
    }
}
