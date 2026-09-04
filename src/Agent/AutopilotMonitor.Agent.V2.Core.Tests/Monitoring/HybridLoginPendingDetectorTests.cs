#nullable enable
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
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Tests for the single-shot Hybrid User-Driven login-pending detector. Single-shot
    /// semantics, conditional emission, and cancel-on-real-user are the contracts that
    /// matter — the detector exists explicitly to NOT add periodicity / timer sprawl.
    /// </summary>
    public sealed class HybridLoginPendingDetectorTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 5, 1, 14, 11, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public AadJoinWatcher Watcher { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);
            public InformationalEventPost Post { get; }
            // Adapter wires the watcher's AadUserJoined into a real AadUserJoinedLate signal —
            // tests need it so TriggerFromTest("...") fires the watcher event the detector
            // is subscribed to.
            public AadJoinWatcherAdapter Adapter { get; }

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Watcher = new AadJoinWatcher(Logger);
                Post = new InformationalEventPost(Ingress, Clock);
                Adapter = new AadJoinWatcherAdapter(Watcher, Ingress, Clock);
            }

            public void Dispose()
            {
                Adapter.Dispose();
                Watcher.Dispose();
                Tmp.Dispose();
            }
        }

        private static FakeSignalIngressSink.PostedSignal? FindHybridLoginPending(FakeSignalIngressSink ingress) =>
            ingress.Posted.FirstOrDefault(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.HybridLoginPending);

        // ============================================================================
        // Single-shot emission path
        // ============================================================================

        [Fact]
        public void Arm_then_TriggerFromTest_emits_hybrid_login_pending_with_warning_severity()
        {
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger, TimeSpan.FromMinutes(10));

            detector.Arm();
            detector.TriggerFromTest();

            var info = FindHybridLoginPending(f.Ingress);
            Assert.NotNull(info);
            Assert.Equal("HybridLoginPendingDetector", info!.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("Warning", info.Payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", info.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("10", info.Payload["delayMinutes"]);
            Assert.Equal("timer_fired", info.Payload["reason"]);
            Assert.Equal("true", info.Payload["isHybridJoin"]);
            Assert.True(detector.HasFiredForTest);
        }

        [Fact]
        public void TriggerFromTest_without_Arm_emits_nothing()
        {
            // Codex review 2026-05-01: contract tightened — emission requires a deliberate
            // Arm(). The earlier behavior (emit-regardless) had a misleading test name and
            // a fragile contract; production was safe only because Arm() was the sole timer
            // creator. Now the guard is explicit so any future code path that bypasses Arm()
            // can't silently produce a hybrid_login_pending event.
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.TriggerFromTest();

            Assert.Null(FindHybridLoginPending(f.Ingress));
            Assert.False(detector.HasFiredForTest);
        }

        [Fact]
        public void Arm_is_idempotent()
        {
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.Arm();
            detector.Arm();
            detector.Arm();

            // No hybrid_login_pending fires from arming alone — arming just starts a timer.
            Assert.Null(FindHybridLoginPending(f.Ingress));
            Assert.True(detector.IsArmedForTest);
        }

        [Fact]
        public void Multiple_TriggerFromTest_calls_emit_only_once()
        {
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.Arm();
            detector.TriggerFromTest();
            detector.TriggerFromTest();
            detector.TriggerFromTest();

            // Single-shot — exactly one informational event regardless of how many times
            // the timer logic is re-entered.
            var matches = f.Ingress.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.HybridLoginPending).ToList();
            Assert.Single(matches);
        }

        // ============================================================================
        // Cancel-on-real-user path
        // ============================================================================

        [Fact]
        public void RealUserJoined_before_TriggerFromTest_cancels_emission()
        {
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.Arm();
            detector.TriggerRealUserJoinedFromTest();
            detector.TriggerFromTest();

            Assert.True(detector.IsCancelledByRealUserForTest);
            Assert.False(detector.HasFiredForTest);
            Assert.Null(FindHybridLoginPending(f.Ingress));
        }

        [Fact]
        public void RealUserJoined_before_Arm_short_circuits_Arm()
        {
            // The watcher could in principle see a real user before Arm() is called
            // (composition-time race). The detector subscribes in its constructor so the
            // _cancelledByRealUser flag is set early and Arm() respects it.
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.TriggerRealUserJoinedFromTest();
            detector.Arm();
            detector.TriggerFromTest();

            Assert.True(detector.IsCancelledByRealUserForTest);
            Assert.False(detector.IsArmedForTest);
            Assert.False(detector.HasFiredForTest);
            Assert.Null(FindHybridLoginPending(f.Ingress));
        }

        [Fact]
        public void Placeholder_user_does_not_cancel_emission()
        {
            // Placeholder is the EXPECTED state at the time the detector arms — only the
            // appearance of a REAL AAD user (not foouser/autopilot) should cancel.
            // The detector subscribes to AadUserJoined (the real-user event) only — the
            // placeholder pathway never fires that event, so we don't need to simulate it
            // here; just verifying the timer fires through unchanged.
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.Arm();
            // No TriggerRealUserJoinedFromTest() — only a placeholder appeared in JoinInfo.
            detector.TriggerFromTest();

            Assert.False(detector.IsCancelledByRealUserForTest);
            Assert.True(detector.HasFiredForTest);
            Assert.NotNull(FindHybridLoginPending(f.Ingress));
        }

        // ============================================================================
        // Real-user desktop cancel path (2026-09-04, session a7140f98)
        // ============================================================================

        [Fact]
        public void Real_user_desktop_after_Arm_cancels_emission()
        {
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.Arm();
            detector.NotifyRealUserDesktop();
            detector.TriggerFromTest();

            Assert.True(detector.IsCancelledByDesktopForTest);
            Assert.False(detector.HasFiredForTest);
            Assert.Null(FindHybridLoginPending(f.Ingress));
        }

        [Fact]
        public void Real_user_desktop_before_Arm_short_circuits_Arm()
        {
            // The DAD first poll runs seconds after start; the arm request comes from the
            // runtime host slightly later. A desktop seen first must make the arm a no-op.
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.NotifyRealUserDesktop();
            detector.Arm();
            detector.TriggerFromTest();

            Assert.False(detector.IsArmedForTest);
            Assert.Null(FindHybridLoginPending(f.Ingress));
        }

        [Fact]
        public void Emission_reports_desktop_absence_and_placeholder_as_fact()
        {
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);

            detector.Arm();
            detector.TriggerFromTest();

            var info = FindHybridLoginPending(f.Ingress);
            Assert.NotNull(info);
            Assert.Equal("false", info!.Payload!["realUserDesktopSeen"]);
            Assert.Equal("false", info.Payload["placeholderActive"]); // watcher never saw JoinInfo in this fixture
            Assert.Contains("no real-user desktop", info.Payload[SignalPayloadKeys.Message]);
            Assert.DoesNotContain("placeholder still active", info.Payload[SignalPayloadKeys.Message]);
        }

        // ============================================================================
        // Lifecycle / Dispose
        // ============================================================================

        [Fact]
        public void Dispose_after_Arm_does_not_throw()
        {
            using var f = new Fixture();
            var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);
            detector.Arm();
            detector.Dispose();

            // No emission expected — dispose ate the timer before it fired.
            Assert.Null(FindHybridLoginPending(f.Ingress));
        }

        [Fact]
        public void TriggerFromTest_after_Dispose_does_not_emit()
        {
            using var f = new Fixture();
            var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger);
            detector.Arm();
            detector.Dispose();

            detector.TriggerFromTest();

            Assert.Null(FindHybridLoginPending(f.Ingress));
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new HybridLoginPendingDetector(null!, f.Post, f.Logger));
            Assert.Throws<ArgumentNullException>(() => new HybridLoginPendingDetector(f.Watcher, null!, f.Logger));
            Assert.Throws<ArgumentNullException>(() => new HybridLoginPendingDetector(f.Watcher, f.Post, null!));
        }

        // ============================================================================
        // Custom delay
        // ============================================================================

        [Fact]
        public void Custom_delay_is_reflected_in_payload()
        {
            using var f = new Fixture();
            using var detector = new HybridLoginPendingDetector(f.Watcher, f.Post, f.Logger, TimeSpan.FromMinutes(15));

            detector.Arm();
            detector.TriggerFromTest();

            var info = FindHybridLoginPending(f.Ingress);
            Assert.NotNull(info);
            Assert.Equal("15", info!.Payload!["delayMinutes"]);
        }
    }
}
