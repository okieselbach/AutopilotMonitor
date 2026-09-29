using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// V2 race-fix (10c8e0bf debrief, 2026-04-26) — verifies that
    /// <see cref="AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostEnrollmentFactsObserved"/>
    /// emits a <see cref="DecisionSignalKind.EnrollmentFactsObserved"/> signal carrying the
    /// registry-derived enrollment facts so the reducer can seed
    /// <see cref="State.EnrollmentScenarioProfile"/> via the stage-agnostic facts handler.
    /// </summary>
    public sealed class PostEnrollmentFactsObservedSignalTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        [Fact]
        public void Posts_enrollment_facts_observed_kind_with_default_schema_version()
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var logger = NewLogger(tmp.Path);

            AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostEnrollmentFactsObserved(sink, HelloWaitTimeout.DefaultSeconds, logger);

            Assert.Single(sink.Posted);
            var posted = sink.Posted[0];
            Assert.Equal(DecisionSignalKind.EnrollmentFactsObserved, posted.Kind);
            Assert.Equal(1, posted.KindSchemaVersion);
            Assert.Equal("Program.RunAgent", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Synthetic, posted.Evidence.Kind);
            Assert.Equal("enrollment_registry_facts_read", posted.Evidence.Identifier);
        }

        [Fact]
        public void Payload_carries_enrollment_type_and_hybrid_join_keys()
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var logger = NewLogger(tmp.Path);

            AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostEnrollmentFactsObserved(sink, HelloWaitTimeout.DefaultSeconds, logger);

            var payload = sink.Posted[0].Payload;
            Assert.NotNull(payload);
            // Test runs off-device — registry detector returns the safe defaults
            // ("v1" / "false") since the Autopilot keys are absent on the build host.
            // The contract under test is the payload SHAPE, not the detected values.
            Assert.Contains("enrollmentType", (IDictionary<string, string>)payload!);
            Assert.Contains("isHybridJoin", (IDictionary<string, string>)payload);
            Assert.Contains("isSelfDeployingProfile", (IDictionary<string, string>)payload);
            // Value sanity — defaults must be parseable booleans / known type literals.
            var hybrid = payload["isHybridJoin"];
            Assert.True(hybrid == "true" || hybrid == "false");
            var type = payload["enrollmentType"];
            Assert.True(type == "v1" || type == "v2");
            // Session 320b3bf7 kiosk fix — the OobeConfig self-deploying marker rides the
            // same facts signal (safe default "false" off-device).
            var selfDeploying = payload["isSelfDeployingProfile"];
            Assert.True(selfDeploying == "true" || selfDeploying == "false");
            // W365 — the Cloud PC marker fact rides the same signal (safe default "false"
            // off-device; a build host is not a Cloud PC).
            Assert.Contains("isCloudPc", (IDictionary<string, string>)payload);
            var cloudPc = payload["isCloudPc"];
            Assert.True(cloudPc == "true" || cloudPc == "false");
        }

        [Theory]
        [InlineData(300)] // the default — the payload stays exactly what it was before the key existed
        [InlineData(30)]  // the old default, still stored for many tenants
        [InlineData(0)]
        public void Hello_wait_not_extending_the_built_in_window_is_not_stamped(int helloWaitTimeoutSeconds)
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();

            AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostEnrollmentFactsObserved(sink, helloWaitTimeoutSeconds, NewLogger(tmp.Path));

            Assert.DoesNotContain(SignalPayloadKeys.HelloWaitTimeoutSeconds, (IDictionary<string, string>)sink.Posted[0].Payload!);
        }

        [Theory]
        [InlineData(1800, "1800")]
        [InlineData(3600, "3600")]
        [InlineData(86400, "3600")] // the effective (clamped) value is what the engine should arm
        public void Extended_hello_wait_is_stamped_as_its_effective_value(int helloWaitTimeoutSeconds, string expected)
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();

            AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostEnrollmentFactsObserved(sink, helloWaitTimeoutSeconds, NewLogger(tmp.Path));

            Assert.Equal(expected, sink.Posted[0].Payload![SignalPayloadKeys.HelloWaitTimeoutSeconds]);
        }

        [Fact]
        public void Swallows_sink_exceptions()
        {
            // Helper runs on the hot startup path — a misbehaving sink must not crash the agent.
            using var tmp = new TempDirectory();
            var sink = new ThrowingSink();
            var logger = NewLogger(tmp.Path);

            var ex = Record.Exception(() =>
                AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostEnrollmentFactsObserved(sink, HelloWaitTimeout.DefaultSeconds, logger));

            Assert.Null(ex);
        }

        private sealed class ThrowingSink : ISignalIngressSink
        {
            public void Post(
                DecisionSignalKind kind,
                System.DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
            {
                throw new System.InvalidOperationException("ingress unavailable");
            }
        }
    }
}
