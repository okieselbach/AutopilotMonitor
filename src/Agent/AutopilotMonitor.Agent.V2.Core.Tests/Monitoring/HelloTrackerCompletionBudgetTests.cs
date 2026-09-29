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
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// The completion timer a started Hello wizard runs against is the tenant's Hello wait
    /// (<c>HelloWaitTimeoutSeconds</c>), resolved to 300..3600 s — the same budget the engine's
    /// hello_safety window spends from the ESP exit, so a tenant allowing an hour is not cut off
    /// by a fixed 5-min tracker timer while the user is still inside the wizard.
    /// </summary>
    public sealed class HelloTrackerCompletionBudgetTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData(30, 300)]     // the old default, still stored for many tenants → the built-in window
        [InlineData(0, 300)]
        [InlineData(300, 300)]
        [InlineData(1800, 1800)]
        [InlineData(3600, 3600)]
        [InlineData(86400, 3600)] // clamped to the ceiling
        public void Completion_budget_is_the_resolved_tenant_hello_wait(int configured, int expected)
        {
            using var tmp = new TempDirectory();
            using var tracker = NewTracker(tmp, new FakeSignalIngressSink(), configured);

            Assert.Equal(expected, tracker.CompletionTimeoutSecondsForTest);
        }

        [Fact]
        public void Completion_timeout_event_reports_the_tenant_budget()
        {
            using var tmp = new TempDirectory();
            var ingress = new FakeSignalIngressSink();
            using var tracker = NewTracker(tmp, ingress, helloWaitTimeoutSeconds: 2400);

            tracker.NotifyHelloWizardStarted();
            Assert.True(tracker.IsCompletionTimerActiveForTest);
            tracker.TriggerCompletionTimeoutForTest();

            var timeout = ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == Constants.EventTypes.HelloCompletionTimeout);
            var data = Assert.IsAssignableFrom<IDictionary<string, object>>(timeout.TypedPayload);
            Assert.Equal(2400, data["timeoutSeconds"]);
            Assert.Equal("timeout", tracker.HelloOutcome);
        }

        private static HelloTracker NewTracker(TempDirectory tmp, FakeSignalIngressSink ingress, int helloWaitTimeoutSeconds) =>
            new HelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(ingress, new VirtualClock(Fixed)),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info),
                helloWaitTimeoutSeconds: helloWaitTimeoutSeconds);
    }
}
