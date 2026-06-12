using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Shared activity predicate for the two activity-gated hosts (PeriodicCollectorLifecycleHost
    /// idle-stop + StallProbeHost idle clock). Reviews PERF-H3 (the 30 s classifier_tick must NOT
    /// count as activity, or it defeats idle-stop) and MON-A2.
    /// </summary>
    public sealed class SignalActivityClassifierTests
    {
        [Fact]
        public void DeadlineFired_is_never_activity()
        {
            Assert.False(SignalActivityClassifier.IsRealActivity(DecisionSignalKind.DeadlineFired, null));
            // The classifier_tick is a DeadlineFired with a deadline-name payload — still not activity.
            Assert.False(SignalActivityClassifier.IsRealActivity(
                DecisionSignalKind.DeadlineFired,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = "classifier_tick" }));
        }

        [Theory]
        // Periodic self-emissions.
        [InlineData("performance_snapshot")]
        [InlineData("agent_metrics_snapshot")]
        [InlineData("performance_collector_stopped")]
        [InlineData("agent_metrics_collector_stopped")]
        [InlineData("stall_probe_check")]
        [InlineData("stall_probe_result")]
        [InlineData("session_stalled")]
        // OS-eventlog forwarding — observability, not enrollment progress (session 8bc1180f:
        // an EventID-100 burst must not keep the periodic collectors alive).
        [InlineData("modern_deployment_log")]
        // Agent health / control / transport — not device/enrollment progress (P2).
        [InlineData("collector_degraded")]
        [InlineData("telemetry_upload_poisoned")]
        [InlineData("telemetry_upload_blocked")]
        [InlineData("state_quarantine_recovered")]
        [InlineData("prior_run_died_with_state")]
        [InlineData("previous_crash_detected")]
        [InlineData("spool_pressure_detected")]
        [InlineData("ingress_backpressure")]
        [InlineData("agent_shutting_down")]
        public void Non_activity_informational_events_are_not_activity(string eventType)
        {
            var payload = new Dictionary<string, string> { [SignalPayloadKeys.EventType] = eventType };
            Assert.False(SignalActivityClassifier.IsRealActivity(DecisionSignalKind.InformationalEvent, payload));
        }

        [Fact]
        public void Periodic_match_is_case_insensitive()
        {
            var payload = new Dictionary<string, string> { [SignalPayloadKeys.EventType] = "Performance_Snapshot" };
            Assert.False(SignalActivityClassifier.IsRealActivity(DecisionSignalKind.InformationalEvent, payload));
        }

        [Fact]
        public void Non_periodic_informational_event_is_activity()
        {
            var payload = new Dictionary<string, string> { [SignalPayloadKeys.EventType] = "esp_phase_changed" };
            Assert.True(SignalActivityClassifier.IsRealActivity(DecisionSignalKind.InformationalEvent, payload));
        }

        [Theory]
        // Genuine ModernDeployment warnings/errors stay real activity — only the Info/Debug
        // `modern_deployment_log` forwarding is excluded.
        [InlineData("modern_deployment_warning")]
        [InlineData("modern_deployment_error")]
        public void Modern_deployment_warning_and_error_remain_activity(string eventType)
        {
            var payload = new Dictionary<string, string> { [SignalPayloadKeys.EventType] = eventType };
            Assert.True(SignalActivityClassifier.IsRealActivity(DecisionSignalKind.InformationalEvent, payload));
        }

        [Fact]
        public void Informational_event_without_payload_or_eventType_is_activity()
        {
            Assert.True(SignalActivityClassifier.IsRealActivity(DecisionSignalKind.InformationalEvent, null));
            Assert.True(SignalActivityClassifier.IsRealActivity(
                DecisionSignalKind.InformationalEvent, new Dictionary<string, string>()));
        }

        [Theory]
        [InlineData(DecisionSignalKind.EspPhaseChanged)]
        [InlineData(DecisionSignalKind.DesktopArrived)]
        [InlineData(DecisionSignalKind.HelloResolved)]
        [InlineData(DecisionSignalKind.AppInstallCompleted)]
        [InlineData(DecisionSignalKind.SystemRebootObserved)]
        public void Other_signal_kinds_are_activity(DecisionSignalKind kind)
        {
            Assert.True(SignalActivityClassifier.IsRealActivity(kind, null));
        }
    }
}
