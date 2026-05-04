#nullable enable
using System;
using System.Collections.Generic;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Allowlist of EventTypes that get DecisionState-snapshot enrichment in the
    /// <c>EventTimelineEmitter</c> (Plan §A — Edge-Triggered State Snapshots,
    /// 2026-05-03). Only events on this list have their <c>Data</c> dict augmented
    /// with a <c>decisionState</c> field carrying <see cref="DecisionStateSnapshotBuilder.Build"/>'s
    /// output at the emit-time state.
    /// <para>
    /// Excluded by design: <c>enrollment_complete</c> / <c>enrollment_failed</c>
    /// (already carry <see cref="DecisionAuditTrailBuilder"/> output as
    /// <c>typedPayload</c> — would clobber); App-Install, Performance-Snapshot and
    /// other high-frequency events (would dominate the Events table volume).
    /// </para>
    /// <para>
    /// Implementation note: <see cref="System.Collections.Generic.IReadOnlySet{T}"/>
    /// is .NET 5+ only. DecisionCore targets netstandard2.0, so the set lives behind
    /// a <c>private</c> field and is reached via the <see cref="Contains"/> wrapper.
    /// </para>
    /// </summary>
    public static class LifecycleAnchorEventTypes
    {
        private static readonly HashSet<string> Anchors = new HashSet<string>(StringComparer.Ordinal)
        {
            SharedConstants.EventTypes.AgentStarted,                  // agent_started — useful for Run-2 recovered-state anchor
            SharedConstants.EventTypes.EspPhaseChanged,               // esp_phase_changed
            SharedConstants.EventTypes.NetworkStateChange,            // network_state_change
            SharedConstants.EventTypes.DesktopArrived,                // desktop_arrived
            SharedConstants.EventTypes.AadPlaceholderUserDetected,    // aad_placeholder_user_detected
            SharedConstants.EventTypes.AadUserJoinedObserved,         // aad_user_joined_observed
            SharedConstants.EventTypes.HybridLoginPending,            // hybrid_login_pending
            SharedConstants.EventTypes.AgentShuttingDown,             // agent_shutting_down
            SharedConstants.EventTypes.SystemRebootDetected,          // system_reboot_detected
            SharedConstants.EventTypes.PerformanceCollectorStopped,   // performance_collector_stopped
            SharedConstants.EventTypes.AgentMetricsCollectorStopped,  // agent_metrics_collector_stopped
            SharedConstants.EventTypes.PriorRunDiedWithState,         // prior_run_died_with_state — self-anchored too
        };

        /// <summary>
        /// Returns <c>true</c> when <paramref name="eventType"/> should receive
        /// DecisionState-snapshot enrichment. Null / empty inputs return <c>false</c>.
        /// </summary>
        public static bool Contains(string? eventType) =>
            !string.IsNullOrEmpty(eventType) && Anchors.Contains(eventType!);

        /// <summary>
        /// Test-only access to the anchor set count; kept internal-via-public for the
        /// allowlist smoke test which needs to verify the expected anchor count.
        /// </summary>
        public static int Count => Anchors.Count;
    }
}
