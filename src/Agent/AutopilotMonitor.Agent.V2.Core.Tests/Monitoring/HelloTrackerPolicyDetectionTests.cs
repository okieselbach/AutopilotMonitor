#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// PR4 (882fef64 debrief) — HelloTracker exposes a HelloPolicyDetected event so the
    /// adapter can post a DecisionSignalKind.HelloPolicyDetected signal, and adjusts the
    /// post-ESP-exit wait window when the policy is explicitly disabled. Mismatch detection
    /// fires when a Hello terminal arrives despite policy=disabled.
    /// </summary>
    public sealed class HelloTrackerPolicyDetectionTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);

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

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void SetPolicyForTest_fires_HelloPolicyDetected_event()
        {
            using var f = new Fixture();
            (bool, string)? captured = null;
            f.Tracker.HelloPolicyDetected += (enabled, source) => captured = (enabled, source);

            f.Tracker.SetPolicyForTest(helloEnabled: true, source: "GPO");

            Assert.NotNull(captured);
            Assert.True(captured!.Value.Item1);
            Assert.Equal("GPO", captured.Value.Item2);
        }

        [Fact]
        public void SetPolicyForTest_disabled_fires_event_with_false()
        {
            using var f = new Fixture();
            (bool, string)? captured = null;
            f.Tracker.HelloPolicyDetected += (enabled, source) => captured = (enabled, source);

            f.Tracker.SetPolicyForTest(helloEnabled: false, source: "CSP/Intune (user-scoped)");

            Assert.NotNull(captured);
            Assert.False(captured!.Value.Item1);
            Assert.Equal("CSP/Intune (user-scoped)", captured.Value.Item2);
        }

        [Fact]
        public void StartHelloWaitTimer_uses_short_grace_when_policy_disabled()
        {
            // Drive the wait timer with policy=disabled and verify the inner trigger semantics.
            // We can't observe the configured timer interval directly, so the assertion is
            // indirect: when policy=disabled, OnHelloWaitTimeout immediately marks Hello as
            // not_configured (existing branch); when policy=enabled, it extends the wait via
            // the long completion timer. Both reach a different end-state, which proves the
            // branching pre-condition is set correctly.
            using var f = new Fixture();
            f.Tracker.SetPolicyForTest(helloEnabled: false, source: "CSP/Intune");
            f.Tracker.StartHelloWaitTimer();

            // Manually trigger the wait-timeout to skip the real 10s wall-clock without
            // changing production timer code. The post-condition (HelloOutcome=not_configured
            // + IsHelloCompleted=true) tells us policy=disabled drove the immediate path.
            f.Tracker.TriggerWaitTimeoutForTest();

            Assert.True(f.Tracker.IsHelloCompleted);
            Assert.Equal("not_configured", f.Tracker.HelloOutcome);
        }

        [Fact]
        public void StartHelloWaitTimer_extends_wait_when_policy_enabled()
        {
            using var f = new Fixture();
            f.Tracker.SetPolicyForTest(helloEnabled: true, source: "CSP/Intune");
            f.Tracker.StartHelloWaitTimer();
            f.Tracker.TriggerWaitTimeoutForTest();

            // Policy=enabled → wait is extended via the long completion timer instead of
            // declaring not_configured. IsHelloCompleted stays false at this point.
            Assert.False(f.Tracker.IsHelloCompleted);
            Assert.Null(f.Tracker.HelloOutcome);
        }

        // ---- MON-C2: policy-not-yet-detected must not force premature not_configured ----

        [Fact]
        public void Wait_timeout_with_unknown_policy_grants_grace_and_defers_completion()
        {
            // Policy never detected (slow MDM/CSP sync). The first wait timeout must NOT
            // resolve Hello to not_configured — it grants one bounded grace re-arm and keeps
            // waiting so a late policy read can still land.
            using var f = new Fixture();
            // Deliberately skip SetPolicyForTest so _isPolicyConfigured stays false (unknown).
            f.Tracker.StartHelloWaitTimer();

            f.Tracker.TriggerWaitTimeoutForTest();

            Assert.False(f.Tracker.IsHelloCompleted);
            Assert.Null(f.Tracker.HelloOutcome);
            // Wait timer re-armed for the grace window; completion timer NOT started.
            Assert.True(f.Tracker.IsWaitTimerActiveForTest);
            Assert.False(f.Tracker.IsCompletionTimerActiveForTest);
        }

        [Fact]
        public void Wait_timeout_unknown_policy_after_grace_completes_not_configured()
        {
            // If the policy is STILL unknown after the one-shot grace window, the device is
            // genuinely not configured (DetectHelloPolicy returns null forever) — resolve to
            // not_configured so enrollment is not stranded.
            using var f = new Fixture();
            f.Tracker.StartHelloWaitTimer();

            f.Tracker.TriggerWaitTimeoutForTest();   // grace re-arm
            Assert.False(f.Tracker.IsHelloCompleted);

            f.Tracker.TriggerWaitTimeoutForTest();   // grace exhausted, still unknown

            Assert.True(f.Tracker.IsHelloCompleted);
            Assert.Equal("not_configured", f.Tracker.HelloOutcome);
        }

        [Fact]
        public void Wait_timeout_unknown_then_policy_enabled_during_grace_extends_wait()
        {
            // The slow-sync race the grace exists for: policy lands as ENABLED during the grace
            // window. The next wait timeout must take the extended-wait path (long completion
            // timer), never not_configured.
            using var f = new Fixture();
            f.Tracker.StartHelloWaitTimer();

            f.Tracker.TriggerWaitTimeoutForTest();   // unknown → grace re-arm

            f.Tracker.SetPolicyForTest(helloEnabled: true, source: "CSP/Intune (device-scoped)");
            f.Tracker.TriggerWaitTimeoutForTest();   // now known-enabled → extend

            Assert.False(f.Tracker.IsHelloCompleted);
            Assert.Null(f.Tracker.HelloOutcome);
            Assert.True(f.Tracker.IsCompletionTimerActiveForTest);
        }

        [Fact]
        public void Esp_resumption_reset_re_grants_the_unknown_policy_grace()
        {
            // The one-shot grace flag must reset on ESP resumption (hybrid-join mid-enrollment
            // reboot). Without the reset a fresh post-resume wait that hits an undetected policy
            // would skip its grace and resolve straight to not_configured.
            using var f = new Fixture();
            f.Tracker.StartHelloWaitTimer();
            f.Tracker.TriggerWaitTimeoutForTest();   // unknown → grace consumed
            Assert.False(f.Tracker.IsHelloCompleted);

            f.Tracker.ResetForEspResumption();

            f.Tracker.StartHelloWaitTimer();
            f.Tracker.TriggerWaitTimeoutForTest();   // unknown again → must re-grant grace, not complete

            Assert.False(f.Tracker.IsHelloCompleted);
            Assert.Null(f.Tracker.HelloOutcome);
            Assert.True(f.Tracker.IsWaitTimerActiveForTest);
        }

        [Fact]
        public void HelloTerminalEvent_after_policy_disabled_emits_mismatch_warning()
        {
            // PR4: a Hello terminal arriving while the tracker still believes policy=disabled
            // means the policy value was stale or changed during enrollment (sessions
            // 772fe502/1696e416 — flip-flopping user-scoped WHfB CSP). The tracker emits
            // hello_policy_detection_mismatch before forwarding HelloCompleted.
            using var f = new Fixture();
            f.Tracker.SetPolicyForTest(helloEnabled: false, source: "CSP/Intune");

            // Drive the 6045 user-skip path — that's a Hello terminal that exercises
            // ProcessHelloForBusinessEvent.
            f.Tracker.ProcessHelloForBusinessEvent(
                eventId: 6045,
                timestamp: Fixed.AddSeconds(5),
                description: "Hello skipped 0x801C044F",
                providerName: "HelloForBusiness",
                isBackfill: false);

            var mismatch = f.Ingress.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                .Where(p => p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var v) && v == "hello_policy_detection_mismatch")
                .ToList();

            Assert.Single(mismatch);
            Assert.Equal("Warning", mismatch[0].Payload![SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void HelloTerminalEvent_after_policy_enabled_does_not_emit_mismatch()
        {
            // The mismatch branch is gated on policy=disabled — when policy=enabled, the
            // arriving terminal is the expected path and no warning fires.
            using var f = new Fixture();
            f.Tracker.SetPolicyForTest(helloEnabled: true, source: "CSP/Intune");

            f.Tracker.ProcessHelloForBusinessEvent(
                eventId: 6045,
                timestamp: Fixed.AddSeconds(5),
                description: "Hello skipped 0x801C044F",
                providerName: "HelloForBusiness",
                isBackfill: false);

            var mismatch = f.Ingress.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                .Where(p => p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var v) && v == "hello_policy_detection_mismatch");

            Assert.Empty(mismatch);
        }

        // ---- Session 772fe502: confirmation second read before committing policy=disabled ----

        [Fact]
        public void ApplyPolicyRead_disabled_first_read_only_marks_pending()
        {
            using var f = new Fixture();
            var invokes = 0;
            f.Tracker.HelloPolicyDetected += (_, _) => invokes++;

            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");

            Assert.True(f.Tracker.IsPolicyDisabledPendingForTest);
            Assert.False(f.Tracker.IsPolicyConfigured);
            Assert.Equal(0, invokes);
            Assert.Empty(PolicyEvents(f));
        }

        [Fact]
        public void ApplyPolicyRead_disabled_confirmed_by_second_read_commits_disabled()
        {
            using var f = new Fixture();
            var invoked = new List<(bool, string)>();
            f.Tracker.HelloPolicyDetected += (enabled, source) => invoked.Add((enabled, source));

            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");
            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");

            Assert.True(f.Tracker.IsPolicyConfigured);
            Assert.False(f.Tracker.IsPolicyDisabledPendingForTest);
            var invoke = Assert.Single(invoked);
            Assert.False(invoke.Item1);

            var evt = Assert.Single(PolicyEvents(f));
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(
                ExtractEventData(evt));
            Assert.Equal(false, data["helloEnabled"]);
            Assert.Equal(2, data["confirmedReads"]);
            Assert.False(data.ContainsKey("flipFlopDetected"));
        }

        [Fact]
        public void ApplyPolicyRead_disabled_then_enabled_commits_enabled_and_flags_flipFlop()
        {
            // The exact session-772fe502 catch: the flip-flopping user-scoped CSP read
            // "disabled" once; the confirmation read sees the real value.
            using var f = new Fixture();
            var invoked = new List<(bool, string)>();
            f.Tracker.HelloPolicyDetected += (enabled, source) => invoked.Add((enabled, source));

            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");
            f.Tracker.ApplyPolicyRead(isEnabled: true, source: "CSP/Intune (user-scoped)");

            Assert.True(f.Tracker.IsPolicyConfigured);
            var invoke = Assert.Single(invoked);
            Assert.True(invoke.Item1);

            var evt = Assert.Single(PolicyEvents(f));
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(
                ExtractEventData(evt));
            Assert.Equal(true, data["helloEnabled"]);
            Assert.Equal(1, data["confirmedReads"]);
            Assert.Equal(true, data["flipFlopDetected"]);
        }

        [Fact]
        public void ApplyPolicyRead_disabled_then_null_clears_pending_and_keeps_polling()
        {
            // A vanished value is itself flip-flop evidence — the confirmation chain restarts.
            using var f = new Fixture();
            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");
            Assert.True(f.Tracker.IsPolicyDisabledPendingForTest);

            f.Tracker.ApplyPolicyRead(isEnabled: null, source: null);
            Assert.False(f.Tracker.IsPolicyDisabledPendingForTest);
            Assert.False(f.Tracker.IsPolicyConfigured);
            Assert.Empty(PolicyEvents(f));

            // Re-established disabled chain commits on its own second read.
            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");
            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");
            Assert.True(f.Tracker.IsPolicyConfigured);
            Assert.Single(PolicyEvents(f));
        }

        [Fact]
        public void ApplyPolicyRead_enabled_first_read_commits_immediately()
        {
            using var f = new Fixture();
            var invokes = 0;
            f.Tracker.HelloPolicyDetected += (_, _) => invokes++;

            f.Tracker.ApplyPolicyRead(isEnabled: true, source: "GPO");

            Assert.True(f.Tracker.IsPolicyConfigured);
            Assert.Equal(1, invokes);
            var evt = Assert.Single(PolicyEvents(f));
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(
                ExtractEventData(evt));
            Assert.Equal(1, data["confirmedReads"]);
            Assert.False(data.ContainsKey("flipFlopDetected"));
        }

        [Fact]
        public void ApplyPolicyRead_pending_disabled_keeps_unknown_wait_cadence()
        {
            // While the disabled read awaits confirmation, the tracker must behave as
            // policy-UNKNOWN: the wait timeout grants the one-shot grace instead of resolving
            // not_configured on the short disabled cadence.
            using var f = new Fixture();
            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");
            f.Tracker.StartHelloWaitTimer();

            f.Tracker.TriggerWaitTimeoutForTest();

            Assert.False(f.Tracker.IsHelloCompleted);
            Assert.Null(f.Tracker.HelloOutcome);
            Assert.True(f.Tracker.IsWaitTimerActiveForTest);
        }

        [Fact]
        public void ApplyPolicyRead_after_commit_is_noop()
        {
            using var f = new Fixture();
            var invokes = 0;
            f.Tracker.HelloPolicyDetected += (_, _) => invokes++;

            f.Tracker.ApplyPolicyRead(isEnabled: true, source: "GPO");
            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");
            f.Tracker.ApplyPolicyRead(isEnabled: false, source: "CSP/Intune (user-scoped)");

            Assert.Equal(1, invokes);
            Assert.Single(PolicyEvents(f));
            Assert.False(f.Tracker.IsPolicyDisabledPendingForTest);
        }

        private static List<FakeSignalIngressSink.PostedSignal> PolicyEvents(Fixture f) =>
            f.Ingress.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                .Where(p => p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var v) && v == "hello_policy_detected")
                .ToList();

        /// <summary>
        /// The EnrollmentEvent.Data dictionary rides through InformationalEventPost as the
        /// signal's typed payload — extract it for the confirmation-audit assertions.
        /// </summary>
        private static IReadOnlyDictionary<string, object> ExtractEventData(FakeSignalIngressSink.PostedSignal signal)
        {
            Assert.NotNull(signal.TypedPayload);
            return Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(signal.TypedPayload);
        }

        [Fact]
        public void HelloTerminalEvent_when_policy_unknown_does_not_emit_mismatch()
        {
            // Policy never detected → mismatch must NOT fire (we don't know whether to expect
            // Hello or not, so the arrival proves nothing about the policy value).
            using var f = new Fixture();
            // Deliberately skip SetPolicyForTest so _isPolicyConfigured stays false.

            f.Tracker.ProcessHelloForBusinessEvent(
                eventId: 6045,
                timestamp: Fixed.AddSeconds(5),
                description: "Hello skipped 0x801C044F",
                providerName: "HelloForBusiness",
                isBackfill: false);

            var mismatch = f.Ingress.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                .Where(p => p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var v) && v == "hello_policy_detection_mismatch");

            Assert.Empty(mismatch);
        }
    }
}
