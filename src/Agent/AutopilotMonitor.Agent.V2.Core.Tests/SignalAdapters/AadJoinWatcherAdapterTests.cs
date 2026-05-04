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
    public sealed class AadJoinWatcherAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public AadJoinWatcher Watcher { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Watcher = new AadJoinWatcher(Logger);
            }

            public void Dispose()
            {
                Watcher.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void TriggerFromTest_emits_AadUserJoinedLate_with_domain_not_full_email()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("alice@contoso.com", "abcd1234");

            // Codex review 2026-05-01: dual-emission — decision signal + informational event.
            var decisionSignal = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.AadUserJoinedLate);
            Assert.Equal("AadJoinWatcher", decisionSignal.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, decisionSignal.Evidence.Kind);
            Assert.Equal("contoso.com", decisionSignal.Payload!["userDomain"]);
            Assert.Equal("true", decisionSignal.Payload["hasThumbprint"]);
            // Session 67963703 debrief 2026-05-04: AadJoinWatcher only fires AadUserJoined
            // for non-placeholder accounts (placeholder goes via PlaceholderUserDetected).
            // The withUser flag must therefore always be true here, so HandleAadUserJoinedLateV1
            // records aad_user_joined_with_user (not _device_only) in the audit trail.
            Assert.Equal("true", decisionSignal.Payload[SignalPayloadKeys.AadJoinedWithUser]);

            // PII guard: full email MUST NOT appear in either rail's payload.
            Assert.DoesNotContain("alice@", string.Concat(decisionSignal.Payload.Values));
        }

        [Fact]
        public void TriggerFromTest_also_emits_aad_user_joined_observed_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("alice@contoso.com", "abcd1234");

            // FailureSnapshotBuilder reads the Events table — without this rail it cannot
            // see that the real AAD user was ever observed (HandleAadUserJoinedLateV1 is
            // observation-only, no timeline effect). Snapshot would always say
            // aadJoinState=placeholder/unknown.
            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.AadUserJoinedObserved);
            Assert.Equal("AadJoinWatcher", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("true", info.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("Info", info.Payload[SignalPayloadKeys.Severity]);
            Assert.Equal("contoso.com", info.Payload["userDomain"]);
            Assert.Equal("true", info.Payload["hasThumbprint"]);

            // PII guard: the message must not glue local-part to domain.
            Assert.DoesNotContain("alice@", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void Empty_thumbprint_is_reflected_in_payload()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("user@example.org", "");

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.AadUserJoinedLate);
            Assert.Equal("false", decision.Payload!["hasThumbprint"]);
        }

        [Fact]
        public void Malformed_email_falls_back_to_unknown_domain()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("no-at-sign", "x");

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.AadUserJoinedLate);
            Assert.Equal("unknown", decision.Payload!["userDomain"]);
        }

        [Fact]
        public void Duplicate_trigger_is_deduplicated_on_both_rails()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("alice@contoso.com", "x");
            adapter.TriggerFromTest("alice@contoso.com", "x");

            // Exactly one decision signal + one informational event for the real-user
            // path (fire-once semantics on _fired).
            Assert.Single(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AadUserJoinedLate);
            Assert.Single(
                f.Ingress.Posted,
                p => p.Kind == DecisionSignalKind.InformationalEvent
                    && p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == SharedEventTypes.AadUserJoinedObserved);
            Assert.Equal(2, f.Ingress.Posted.Count);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new AadJoinWatcherAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new AadJoinWatcherAdapter(f.Watcher, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new AadJoinWatcherAdapter(f.Watcher, f.Ingress, null!));
        }

        // ============================================================================
        // Placeholder path (Hybrid completion-gap fix, 2026-05-01)
        // ============================================================================

        [Fact]
        public void TriggerPlaceholderFromTest_emits_aad_placeholder_user_detected_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerPlaceholderFromTest("foouser@fabrikam.onmicrosoft.com");

            var info = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.InformationalEvent);
            Assert.Equal(SharedEventTypes.AadPlaceholderUserDetected, info.Payload![SignalPayloadKeys.EventType]);
            Assert.Equal("AadJoinWatcher", info.Payload[SignalPayloadKeys.Source]);
            Assert.Equal("true", info.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("foouser", info.Payload["placeholderType"]);
            Assert.Equal("fabrikam.onmicrosoft.com", info.Payload["userDomain"]);
            Assert.Contains("registryKey", info.Payload.Keys);

            // PII guard: the local part of the email must not appear (only domain).
            Assert.DoesNotContain("foouser@", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void TriggerPlaceholderFromTest_classifies_autopilot_placeholder()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerPlaceholderFromTest("autopilot@contoso.com");

            var info = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.InformationalEvent);
            Assert.Equal("autopilot", info.Payload!["placeholderType"]);
            Assert.Equal("contoso.com", info.Payload["userDomain"]);
        }

        [Fact]
        public void Duplicate_placeholder_trigger_is_deduplicated()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerPlaceholderFromTest("foouser@example.com");
            adapter.TriggerPlaceholderFromTest("foouser@example.com");
            adapter.TriggerPlaceholderFromTest("foouser@example.com");

            Assert.Single(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.InformationalEvent);
        }

        [Fact]
        public void Placeholder_and_real_user_emit_separate_signals_independently()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerPlaceholderFromTest("foouser@fabrikam.com");
            adapter.TriggerFromTest("alice@fabrikam.com", "abcd");

            // 3 posts total after Codex review 2026-05-01 dual-emission:
            //   - placeholder informational event
            //   - real-user decision signal (AadUserJoinedLate)
            //   - real-user informational event (aad_user_joined_observed)
            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.Single(f.Ingress.Posted, p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et1)
                && et1 == SharedEventTypes.AadPlaceholderUserDetected);
            Assert.Single(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AadUserJoinedLate);
            Assert.Single(f.Ingress.Posted, p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et2)
                && et2 == SharedEventTypes.AadUserJoinedObserved);
        }

        // ============================================================================
        // onRealUserJoined callback (Pkt 5 — DesktopArrivalDetector reset wiring)
        // ============================================================================

        [Fact]
        public void Real_user_join_invokes_onRealUserJoined_callback_exactly_once()
        {
            using var f = new Fixture();
            int invocations = 0;
            using var adapter = new AadJoinWatcherAdapter(
                f.Watcher, f.Ingress, f.Clock,
                onRealUserJoined: () => invocations++);

            adapter.TriggerFromTest("alice@contoso.com", "x");
            adapter.TriggerFromTest("alice@contoso.com", "x"); // dedup — callback must not fire twice

            Assert.Equal(1, invocations);
        }

        [Fact]
        public void Placeholder_does_not_invoke_onRealUserJoined_callback()
        {
            using var f = new Fixture();
            int invocations = 0;
            using var adapter = new AadJoinWatcherAdapter(
                f.Watcher, f.Ingress, f.Clock,
                onRealUserJoined: () => invocations++);

            adapter.TriggerPlaceholderFromTest("foouser@example.com");

            Assert.Equal(0, invocations);
        }

        [Fact]
        public void Throwing_onRealUserJoined_callback_does_not_break_signal_post()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(
                f.Watcher, f.Ingress, f.Clock,
                onRealUserJoined: () => throw new InvalidOperationException("boom"));

            // The signal post completes BEFORE the callback fires — even if the callback
            // throws, the decision signal must already be on the bus.
            adapter.TriggerFromTest("alice@contoso.com", "x");

            Assert.Single(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AadUserJoinedLate);
        }
    }
}
