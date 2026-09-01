#nullable enable annotations
using System;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Point-in-time read model of the signal/decision pipeline for the
    /// <c>agent_metrics_snapshot</c> event. Motivated by the 2026-09-01 WG-seal soak
    /// (memory: project_wg_seal_postfix_soak_residual): sessions whose DecisionEngine never
    /// sealed were telemetrically indistinguishable from a deliberate Weak classifier verdict,
    /// because the snapshots carried no signal-pipeline counters at all.
    /// <para>
    /// The counters are MONOTONIC totals, not momentary gauges — a snapshot samples the
    /// integral, so any two snapshots prove whether the worker ran in between (a frozen
    /// worker keeps <see cref="PendingSignalCount"/> growing while
    /// <see cref="ProcessedSignalCount"/> / <see cref="LastAppliedSignalOrdinal"/> stand
    /// still). Momentary queue length is deliberately exported only as a peak.
    /// </para>
    /// </summary>
    public sealed class SignalPipelineHealth
    {
        /// <summary>Last SessionSignalOrdinal assigned by the ingress worker; −1 before the first signal.</summary>
        public long LastAssignedSignalOrdinal { get; set; }

        /// <summary>Total signals fully processed (reduce + effects). Monotonic.</summary>
        public long ProcessedSignalCount { get; set; }

        /// <summary>Signals accepted by Post but not yet fully processed (backlog gauge; pairs with the totals).</summary>
        public long PendingSignalCount { get; set; }

        /// <summary>Worker-thread re-entrant posts routed via the worker-local queue. Monotonic.</summary>
        public long ReentrantPostCount { get; set; }

        /// <summary>Highest channel queue length observed at any Post since Start. Monotonic non-decreasing.</summary>
        public long QueueLengthPeak { get; set; }

        /// <summary>Current DecisionState stage name (e.g. "EspDeviceSetup", "WhiteGloveSealed"). Null when no state is available.</summary>
        public string? DecisionStage { get; set; }

        /// <summary>The DecisionState's LastAppliedSignalOrdinal — the reducer-side twin of the ingress counters. Null when no state is available.</summary>
        public long? LastAppliedSignalOrdinal { get; set; }

        /// <summary>Last WhiteGlove-sealing classifier level ("Weak", "Confirmed", …). Null until the classifier produced a verdict.</summary>
        public string? WhiteGloveSealingLevel { get; set; }

        /// <summary>Score of the last WhiteGlove-sealing verdict. Null until the classifier produced a verdict.</summary>
        public int? WhiteGloveSealingScore { get; set; }
    }
}
