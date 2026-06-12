#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Single source of truth for "is this posted signal real enrollment activity?" — i.e. the
    /// kind of device/enrollment progress that an activity-gated collector should treat as
    /// keeping the session alive.
    /// <para>
    /// Shared by <see cref="PeriodicCollectorLifecycleHost"/> (idle-stop of the Performance /
    /// AgentSelfMetrics collectors) and <see cref="StallProbeHost"/> (idle clock + probe reset)
    /// so the two never drift apart. Previously each host carried its own denylist; the
    /// <see cref="PeriodicCollectorLifecycleHost"/> copy also failed to exclude internal scheduler
    /// ticks, which let the 30 s <c>classifier_tick</c> reset the idle clock on every session and
    /// silently defeat idle-stop (review PERF-H3), and <see cref="StallProbeHost"/> measured agent
    /// uptime instead of idleness (review MON-A2).
    /// </para>
    /// </summary>
    internal static class SignalActivityClassifier
    {
        /// <summary>
        /// Event types that are NOT real device/enrollment progress and must therefore NOT reset
        /// the idle clocks. Two families:
        /// <list type="bullet">
        ///   <item><b>Periodic self-emissions</b> by the activity-gated collectors themselves —
        ///         otherwise a collector's own snapshot would keep its idle window alive forever.</item>
        ///   <item><b>Agent health / control / transport</b> events — these report the agent's own
        ///         state (degraded collector, upload poisoned/blocked, quarantine recovery, prior
        ///         crash, spool/ingress back-pressure, shutdown). They are not enrollment progress;
        ///         counting them would push out stall detection and keep the periodic collectors
        ///         running on an otherwise-idle device (P2 review).</item>
        /// </list>
        /// </summary>
        private static readonly HashSet<string> NonActivityEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Periodic self-emissions.
            "performance_snapshot",
            "agent_metrics_snapshot",
            SharedConstants.EventTypes.PerformanceCollectorStopped,
            SharedConstants.EventTypes.AgentMetricsCollectorStopped,
            SharedConstants.EventTypes.StallProbeCheck,
            SharedConstants.EventTypes.StallProbeResult,
            SharedConstants.EventTypes.SessionStalled,

            // OS-eventlog forwarding — observability, not device/enrollment progress. Windows
            // re-reads its Autopilot policy cache at arbitrary times (observed: 689 EventID-100
            // records in one minute, session 8bc1180f) entirely decoupled from enrollment
            // activity; letting those bursts reset the idle clocks would keep the periodic
            // collectors alive indefinitely. Only the Info/Debug-severity `modern_deployment_log`
            // type is excluded — `modern_deployment_warning` / `modern_deployment_error` stay
            // real activity (rare, and worth keeping diagnostics running for).
            SharedConstants.EventTypes.ModernDeploymentLog,

            // Agent health / control / transport — not device/enrollment progress.
            SharedConstants.EventTypes.CollectorDegraded,
            SharedConstants.EventTypes.TelemetryUploadPoisoned,
            SharedConstants.EventTypes.TelemetryUploadBlocked,
            SharedConstants.EventTypes.StateQuarantineRecovered,
            SharedConstants.EventTypes.PriorRunDiedWithState,
            SharedConstants.EventTypes.PreviousCrashDetected,
            SharedConstants.EventTypes.SpoolPressureDetected,
            SharedConstants.EventTypes.IngressBackpressure,
            SharedConstants.EventTypes.AgentShuttingDown,
        };

        /// <summary>
        /// Returns <c>true</c> when the posted signal represents real enrollment activity.
        /// </summary>
        /// <remarks>
        /// Excludes two classes of "non-activity":
        /// <list type="number">
        ///   <item><see cref="DecisionSignalKind.DeadlineFired"/> — internal scheduler ticks
        ///         (notably the 30 s <c>classifier_tick</c>). These are the agent's own timers, not
        ///         device activity; any real <i>consequence</i> of a deadline (a state transition)
        ///         flows back through the ingress as its own signal and DOES count, so excluding the
        ///         raw tick is safe and necessary.</item>
        ///   <item><see cref="DecisionSignalKind.InformationalEvent"/> whose <c>eventType</c> is in
        ///         the periodic denylist — the snapshots emitted by the activity-gated collectors
        ///         themselves.</item>
        /// </list>
        /// Everything else — ESP phase, IME session, Hello, desktop arrival, classifier verdict,
        /// session lifecycle, and all non-periodic informational events — is real activity.
        /// </remarks>
        public static bool IsRealActivity(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            if (kind == DecisionSignalKind.DeadlineFired)
                return false;

            if (kind == DecisionSignalKind.InformationalEvent
                && payload != null
                && payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)
                && !string.IsNullOrEmpty(eventType)
                && NonActivityEventTypes.Contains(eventType))
            {
                return false;
            }

            return true;
        }
    }
}
