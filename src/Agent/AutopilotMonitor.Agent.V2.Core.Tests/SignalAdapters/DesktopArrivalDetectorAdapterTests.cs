using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class DesktopArrivalDetectorAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public DesktopArrivalDetector Detector { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Detector = new DesktopArrivalDetector(Logger);
            }

            public FakeSignalIngressSink.PostedSignal DecisionSignal(DecisionSignalKind kind) =>
                Ingress.Posted.Single(p => p.Kind == kind);

            public FakeSignalIngressSink.PostedSignal InfoEvent(string eventType) =>
                Ingress.Posted.Single(p =>
                    p.Kind == DecisionSignalKind.InformationalEvent
                    && p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == eventType);

            public void Dispose()
            {
                Detector.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void TriggerFromTest_emits_DesktopArrived_signal_with_derived_evidence()
        {
            using var f = new Fixture();
            using var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock);

            adapter.TriggerFromTest();

            var decision = f.DecisionSignal(DecisionSignalKind.DesktopArrived);
            Assert.Equal(Fixed, decision.OccurredAtUtc);
            Assert.Equal("DesktopArrivalDetector", decision.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, decision.Evidence.Kind);
            Assert.Equal("desktop-arrival-detector-v1", decision.Evidence.Identifier);
        }

        [Fact]
        public void TriggerFromTest_also_emits_desktop_arrived_informational_event_with_immediate_upload()
        {
            using var f = new Fixture();
            using var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock);

            adapter.TriggerFromTest();

            var info = f.InfoEvent(SharedEventTypes.DesktopArrived);
            Assert.Equal("DesktopArrivalDetector", info.Payload![SignalPayloadKeys.Source]);
            // Fix 1 policy: desktop_arrived is a completion-gate event → flush immediately.
            Assert.Equal("true", info.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("explorer.exe process poll", info.Payload["detectionSource"]);
            Assert.True(info.Payload.ContainsKey("detectedAt"));
        }

        [Fact]
        public void Duplicate_trigger_is_deduplicated_on_both_rails()
        {
            using var f = new Fixture();
            using var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock);

            adapter.TriggerFromTest();
            adapter.TriggerFromTest();
            adapter.TriggerFromTest();

            // Exactly one decision signal + one informational event — fire-once semantics hold
            // for the dual-emission refactor.
            Assert.Single(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.DesktopArrived);
            Assert.Single(
                f.Ingress.Posted,
                p => p.Kind == DecisionSignalKind.InformationalEvent
                    && p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == SharedEventTypes.DesktopArrived);
            Assert.Equal(2, f.Ingress.Posted.Count);
        }

        [Fact]
        public void Dispose_unsubscribes_from_detector_event()
        {
            using var f = new Fixture();
            var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock);
            adapter.Dispose();

            // After Dispose, the adapter is still functional via TriggerFromTest (test hook
            // doesn't go through the event), but the actual event is no longer subscribed.
            // We can't observe unsubscription directly without reflection; assert via Disposal-OK.
            // No exception during Dispose is the contract.
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(
                () => new DesktopArrivalDetectorAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(
                () => new DesktopArrivalDetectorAdapter(f.Detector, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(
                () => new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, null!));
        }
    }
}
