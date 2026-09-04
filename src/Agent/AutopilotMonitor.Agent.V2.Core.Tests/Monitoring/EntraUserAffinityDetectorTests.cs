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
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Entra user affinity after the real-user desktop (2026-09-04, session a7140f98). The
    /// contracts that matter: arm only on Hybrid devices and only at the desktop; the first
    /// IME user-token line AFTER the desktop cancels and posts <c>ime_user_token_acquired</c>;
    /// token lines from before the desktop (device phase) are ignored; the timer posts
    /// <c>entra_user_affinity_pending</c> with the failure codes seen since the desktop; both
    /// emissions are single-shot per process.
    /// </summary>
    public sealed class EntraUserAffinityDetectorTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 4, 13, 54, 6, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);
            public InformationalEventPost Post { get; }
            public bool Hybrid { get; set; } = true;
            public bool PlaceholderActive { get; set; } = true;

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Post = new InformationalEventPost(Ingress, Clock);
            }

            public EntraUserAffinityDetector Build() => new EntraUserAffinityDetector(
                Post, Logger,
                isHybridJoinProbe: () => Hybrid,
                placeholderActiveProbe: () => PlaceholderActive,
                delay: TimeSpan.FromMinutes(10),
                utcNow: () => Clock.UtcNow);

            public void Dispose() => Tmp.Dispose();
        }

        private static FakeSignalIngressSink.PostedSignal? Find(FakeSignalIngressSink ingress, string eventType) =>
            ingress.Posted.FirstOrDefault(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType);

        [Fact]
        public void Desktop_on_hybrid_device_arms()
        {
            using var f = new Fixture();
            using var d = f.Build();

            d.NotifyRealUserDesktop();

            Assert.True(d.IsArmedForTest);
        }

        [Fact]
        public void Desktop_on_non_hybrid_device_does_not_arm()
        {
            using var f = new Fixture { Hybrid = false };
            using var d = f.Build();

            d.NotifyRealUserDesktop();
            d.TriggerFromTest();

            Assert.False(d.IsArmedForTest);
            Assert.Null(Find(f.Ingress, SharedEventTypes.EntraUserAffinityPending));
        }

        [Fact]
        public void Timer_without_token_emits_pending_with_failure_codes()
        {
            using var f = new Fixture();
            using var d = f.Build();

            d.NotifyRealUserDesktop();
            d.NotifyTokenFailureLine("3399548929", f.Clock.UtcNow.AddMinutes(1));
            d.NotifyTokenFailureLine("3399548929", f.Clock.UtcNow.AddMinutes(3));
            d.NotifyTokenFailureLine("3400073247", f.Clock.UtcNow.AddMinutes(5));
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            d.TriggerFromTest();

            var pending = Find(f.Ingress, SharedEventTypes.EntraUserAffinityPending);
            Assert.NotNull(pending);
            Assert.Equal("Warning", pending!.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", pending.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("EntraUserAffinityDetector", pending.Payload[SignalPayloadKeys.Source]);
            Assert.Equal("10", pending.Payload["delayMinutes"]);
            Assert.Equal("timer_fired", pending.Payload["reason"]);
            Assert.Equal("3", pending.Payload["tokenFailureCount"]);
            Assert.Equal("3399548929,3400073247", pending.Payload["tokenFailureCodes"]);
            Assert.Equal("true", pending.Payload["placeholderActive"]);
            Assert.Equal("10.0", pending.Payload["minutesSinceDesktop"]);
            Assert.True(d.HasFiredForTest);
            Assert.Null(Find(f.Ingress, SharedEventTypes.ImeUserTokenAcquired));
        }

        [Fact]
        public void Token_after_desktop_cancels_and_posts_token_acquired()
        {
            using var f = new Fixture();
            using var d = f.Build();

            d.NotifyRealUserDesktop();
            d.NotifyTokenFailureLine("3399548929", f.Clock.UtcNow.AddSeconds(30));
            var tokenAt = f.Clock.UtcNow.AddMinutes(3.7);
            d.NotifyUserTokenAcquired(tokenAt);
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            d.TriggerFromTest();

            var acquired = Find(f.Ingress, SharedEventTypes.ImeUserTokenAcquired);
            Assert.NotNull(acquired);
            Assert.Equal("Info", acquired!.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("3.7", acquired.Payload["minutesAfterDesktop"]);
            Assert.Equal("1", acquired.Payload["tokenFailuresBeforeSuccess"]);
            Assert.Equal(tokenAt, acquired.OccurredAtUtc);
            Assert.True(d.TokenAcquiredPostedForTest);
            Assert.False(d.HasFiredForTest);
            Assert.Null(Find(f.Ingress, SharedEventTypes.EntraUserAffinityPending));
        }

        [Fact]
        public void Token_lines_from_before_the_desktop_are_ignored()
        {
            // Device-phase token lines ("Failed to get AAD token" is routine there; a success
            // can be the device check-in) must neither count nor cancel — the 2-min tolerance
            // only covers the desktop poll interval.
            using var f = new Fixture();
            using var d = f.Build();

            d.NotifyRealUserDesktop();
            d.NotifyUserTokenAcquired(f.Clock.UtcNow.AddMinutes(-20));
            d.NotifyTokenFailureLine("3399548929", f.Clock.UtcNow.AddMinutes(-15));
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            d.TriggerFromTest();

            Assert.Null(Find(f.Ingress, SharedEventTypes.ImeUserTokenAcquired));
            var pending = Find(f.Ingress, SharedEventTypes.EntraUserAffinityPending);
            Assert.NotNull(pending);
            Assert.Equal("0", pending!.Payload!["tokenFailureCount"]);
            Assert.Equal(string.Empty, pending.Payload["tokenFailureCodes"]);
        }

        [Fact]
        public void Token_within_desktop_poll_tolerance_counts_as_after_desktop()
        {
            using var f = new Fixture();
            using var d = f.Build();

            d.NotifyRealUserDesktop();
            d.NotifyUserTokenAcquired(f.Clock.UtcNow.AddSeconds(-90));

            Assert.NotNull(Find(f.Ingress, SharedEventTypes.ImeUserTokenAcquired));
            Assert.Equal("0.0", Find(f.Ingress, SharedEventTypes.ImeUserTokenAcquired)!.Payload!["minutesAfterDesktop"]);
        }

        [Fact]
        public void Token_without_arm_is_ignored()
        {
            using var f = new Fixture();
            using var d = f.Build();

            d.NotifyUserTokenAcquired(f.Clock.UtcNow);

            Assert.Null(Find(f.Ingress, SharedEventTypes.ImeUserTokenAcquired));
        }

        [Fact]
        public void Pending_fires_once_and_a_late_token_still_resolves_it_once()
        {
            // A token that arrives AFTER the warning is the most useful thing the timeline can
            // show ("affinity came late") and lets the backend rule drop the finding — so it is
            // posted, but like the warning only once per process.
            using var f = new Fixture();
            using var d = f.Build();

            d.NotifyRealUserDesktop();
            d.NotifyRealUserDesktop();
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            d.TriggerFromTest();
            d.TriggerFromTest();
            d.NotifyUserTokenAcquired(f.Clock.UtcNow);
            d.NotifyUserTokenAcquired(f.Clock.UtcNow.AddMinutes(1));

            Assert.Single(f.Ingress.Posted, p =>
                p.Payload != null && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.EntraUserAffinityPending);
            Assert.Single(f.Ingress.Posted, p =>
                p.Payload != null && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.ImeUserTokenAcquired);
            Assert.Equal("10.0", Find(f.Ingress, SharedEventTypes.ImeUserTokenAcquired)!.Payload!["minutesAfterDesktop"]);
        }

        [Fact]
        public void Hybrid_probe_throwing_does_not_arm()
        {
            using var f = new Fixture();
            using var d = new EntraUserAffinityDetector(
                f.Post, f.Logger,
                isHybridJoinProbe: () => throw new InvalidOperationException("registry"),
                utcNow: () => f.Clock.UtcNow);

            d.NotifyRealUserDesktop();

            Assert.False(d.IsArmedForTest);
        }

        [Fact]
        public void Dispose_after_arm_does_not_throw_and_blocks_emission()
        {
            using var f = new Fixture();
            var d = f.Build();

            d.NotifyRealUserDesktop();
            d.Dispose();
            d.TriggerFromTest();

            Assert.Null(Find(f.Ingress, SharedEventTypes.EntraUserAffinityPending));
        }
    }
}
