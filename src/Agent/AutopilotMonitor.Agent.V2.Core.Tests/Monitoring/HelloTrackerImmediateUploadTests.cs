using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// First-session fix PR 2 / Fix 1: assert ImmediateUpload is forwarded correctly on the
    /// policy-level Hello event that must reach the backend within seconds
    /// (<c>hello_policy_detected</c>) and on terminal events
    /// (<c>hello_provisioning_completed/failed/blocked</c>, <c>hello_skipped</c>); and stays
    /// false on the snapshot-type <c>hello_pin_status</c> ticker so transient re-registrations
    /// don't trigger a flush each time.
    /// <para>
    /// The <c>willlaunch</c> / <c>willnotlaunch</c> snapshots (EventID 358/360) are no longer
    /// emitted at all — they flip multiple times per session, create pure timeline noise, and
    /// are documented as non-evidence in <c>project_hello_willlaunch_unreliable</c>. Suppression
    /// is asserted below.
    /// </para>
    /// </summary>
    public sealed class HelloTrackerImmediateUploadTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);
            public HelloTracker Tracker { get; }

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                var post = new InformationalEventPost(Ingress, Clock);
                Tracker = new HelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: Logger);
            }

            public FakeSignalIngressSink.PostedSignal InfoEvent(string eventType) =>
                Ingress.Posted.Single(p =>
                    p.Kind == DecisionSignalKind.InformationalEvent
                    && p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == eventType);

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void SetPolicyForTest_emits_hello_policy_detected_with_immediate_upload_true()
        {
            using var f = new Fixture();

            f.Tracker.SetPolicyForTest(helloEnabled: true, source: "GPO");

            var info = f.InfoEvent("hello_policy_detected");
            Assert.Equal("true", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Theory]
        [InlineData(304, "join phase")]
        [InlineData(305, "authentication phase")]
        public void ProcessHelloEvent_304_305_emit_device_registration_event_with_immediate_upload(int eventId, string phaseLabel)
        {
            // Hybrid affinity companion (2026-09-04): UDR 304/305 ride the already-armed
            // watcher and surface as device_registration_event — a Warning, flushed at once,
            // and never a Hello completion.
            using var f = new Fixture();

            f.Tracker.ProcessHelloEvent(eventId, Fixed, "Microsoft-Windows-User Device Registration", isBackfill: false);

            var info = f.InfoEvent("device_registration_event");
            Assert.Equal("Warning", info.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", info.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Contains(phaseLabel, info.Payload[SignalPayloadKeys.Message]);
            Assert.DoesNotContain(f.Ingress.Posted, p =>
                p.Payload != null && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et.StartsWith("hello_provisioning"));
        }

        [Fact]
        public void ProcessHelloEvent_358_willlaunch_is_suppressed()
        {
            using var f = new Fixture();

            // Event 358 = ProvisioningWillLaunch. Snapshot-only prerequisites-passed flag per
            // project_hello_willlaunch_unreliable — flips multiple times per session (session
            // 9ed7021e saw 6× 358, three within 232 ms) and is never decision-relevant. The
            // tracker now suppresses the backend event entirely; only a DEBUG log line remains
            // so diagnostics can still reconstruct the sequence.
            f.Tracker.ProcessHelloEvent(358, Fixed, providerName: "prov", isBackfill: false);

            Assert.DoesNotContain(f.Ingress.Posted, p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "hello_provisioning_willlaunch");
        }

        [Fact]
        public void ProcessHelloEvent_300_emits_completed_with_immediate_upload_true()
        {
            using var f = new Fixture();

            // Event 300 = NGC key registered → triggers Hello completion → already ImmediateUpload.
            f.Tracker.ProcessHelloEvent(300, Fixed, providerName: "prov", isBackfill: false);

            var info = f.InfoEvent("hello_provisioning_completed");
            Assert.Equal("true", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void ProcessHelloEvent_301_emits_failed_with_immediate_upload_true()
        {
            using var f = new Fixture();

            // Event 301 = NGC key registration failed. Existing contract kept: failure → immediate.
            f.Tracker.ProcessHelloEvent(301, Fixed, providerName: "prov", isBackfill: false);

            var info = f.InfoEvent("hello_provisioning_failed");
            Assert.Equal("true", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void ProcessHelloEvent_360_willnotlaunch_is_suppressed()
        {
            using var f = new Fixture();

            // Event 360 = ProvisioningWillNotLaunch — same snapshot-only character as 358, and
            // suppressed for the same reason. No backend event emitted.
            f.Tracker.ProcessHelloEvent(360, Fixed, providerName: "prov", isBackfill: false);

            Assert.DoesNotContain(f.Ingress.Posted, p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "hello_provisioning_willnotlaunch");
        }

        [Fact]
        public void ProcessHelloEvent_376_pin_status_stays_batched()
        {
            using var f = new Fixture();

            // Event 376 = PinStatus. Informational ticker, not a completion gate.
            f.Tracker.ProcessHelloEvent(376, Fixed, providerName: "prov", isBackfill: false);

            var info = f.InfoEvent("hello_pin_status");
            Assert.Equal("false", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }
    }
}
