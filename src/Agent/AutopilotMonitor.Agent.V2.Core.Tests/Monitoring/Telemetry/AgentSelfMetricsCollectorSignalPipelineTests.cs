using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Telemetry
{
    /// <summary>
    /// Pins the signal-pipeline block of <c>agent_metrics_snapshot</c> (2026-09-01 WG-seal
    /// soak: sessions whose DecisionEngine never sealed were telemetrically
    /// indistinguishable from a deliberate Weak classifier verdict, because the snapshots
    /// carried no pipeline counters). The fields are monotonic totals plus a monotonic
    /// queue peak — deliberately no momentary queue length — so two snapshots prove
    /// whether the ingress worker ran in between.
    /// </summary>
    public sealed class AgentSelfMetricsCollectorSignalPipelineTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static (AgentSelfMetricsCollector Collector, FakeSignalIngressSink Sink) Build(
            string tmpPath, Func<SignalPipelineHealth?>? probe)
        {
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance);
            var collector = new AgentSelfMetricsCollector(
                sessionId: "S1",
                tenantId: "T1",
                post: post,
                networkMetrics: new NetworkMetrics(),
                logger: NewLogger(tmpPath),
                agentVersion: "2.0.test",
                signalPipelineProbe: probe);
            return (collector, sink);
        }

        private static IDictionary<string, object> CollectSnapshotData(
            AgentSelfMetricsCollector collector, FakeSignalIngressSink sink)
        {
            collector.CollectSafe();
            var snapshot = sink.Posted.Single(p =>
                p.TypedPayload is IDictionary<string, object> d
                && d.ContainsKey("agent_version"));
            return (IDictionary<string, object>)snapshot.TypedPayload!;
        }

        [Fact]
        public void Snapshot_carries_pipeline_counters_stage_and_wg_verdict()
        {
            using var tmp = new TempDirectory();
            var (collector, sink) = Build(tmp.Path, () => new SignalPipelineHealth
            {
                LastAssignedSignalOrdinal = 41,
                ProcessedSignalCount = 42,
                PendingSignalCount = 3,
                ReentrantPostCount = 7,
                QueueLengthPeak = 12,
                DecisionStage = "EspDeviceSetup",
                LastAppliedSignalOrdinal = 40,
                WhiteGloveSealingLevel = "Weak",
                WhiteGloveSealingScore = 40,
            });

            var data = CollectSnapshotData(collector, sink);

            Assert.Equal(41L, data["signal_last_ordinal"]);
            Assert.Equal(42L, data["signal_processed_total"]);
            Assert.Equal(3L, data["signal_pending_count"]);
            Assert.Equal(7L, data["signal_reentrant_total"]);
            Assert.Equal(12L, data["signal_queue_peak"]);
            Assert.Equal("EspDeviceSetup", data["decision_stage"]);
            Assert.Equal(40L, data["decision_last_applied_ordinal"]);
            Assert.Equal("Weak", data["wg_sealing_level"]);
            Assert.Equal(40, data["wg_sealing_score"]);
        }

        [Fact]
        public void State_derived_fields_are_omitted_when_the_probe_has_no_state()
        {
            // Counters-only health (test fakes without a decision-state probe): the numeric
            // block still lands, the state-derived keys must be ABSENT — not null-valued.
            using var tmp = new TempDirectory();
            var (collector, sink) = Build(tmp.Path, () => new SignalPipelineHealth
            {
                ProcessedSignalCount = 5,
            });

            var data = CollectSnapshotData(collector, sink);

            Assert.Equal(5L, data["signal_processed_total"]);
            Assert.False(data.ContainsKey("decision_stage"));
            Assert.False(data.ContainsKey("decision_last_applied_ordinal"));
            Assert.False(data.ContainsKey("wg_sealing_level"));
            Assert.False(data.ContainsKey("wg_sealing_score"));
        }

        [Fact]
        public void Wg_fields_are_omitted_before_the_classifier_produced_a_verdict()
        {
            // Level stays null until the classifier ran — every non-WG session would
            // otherwise carry a meaningless Unknown/0 pair on each snapshot.
            using var tmp = new TempDirectory();
            var (collector, sink) = Build(tmp.Path, () => new SignalPipelineHealth
            {
                DecisionStage = "Monitoring",
                LastAppliedSignalOrdinal = 9,
            });

            var data = CollectSnapshotData(collector, sink);

            Assert.Equal("Monitoring", data["decision_stage"]);
            Assert.False(data.ContainsKey("wg_sealing_level"));
            Assert.False(data.ContainsKey("wg_sealing_score"));
        }

        [Fact]
        public void Probe_failure_degrades_to_a_snapshot_without_the_block()
        {
            using var tmp = new TempDirectory();
            var (collector, sink) = Build(tmp.Path,
                () => throw new InvalidOperationException("probe exploded"));

            var data = CollectSnapshotData(collector, sink);

            Assert.True(data.ContainsKey("agent_version")); // snapshot itself survives
            Assert.DoesNotContain(data.Keys, k => k.StartsWith("signal_", StringComparison.Ordinal));
        }

        [Fact]
        public void No_probe_means_no_pipeline_fields()
        {
            using var tmp = new TempDirectory();
            var (collector, sink) = Build(tmp.Path, probe: null);

            var data = CollectSnapshotData(collector, sink);

            Assert.DoesNotContain(data.Keys, k => k.StartsWith("signal_", StringComparison.Ordinal));
            Assert.False(data.ContainsKey("decision_stage"));
        }
    }
}
