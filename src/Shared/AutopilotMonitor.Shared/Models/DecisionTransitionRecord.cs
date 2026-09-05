using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Backend storage record for a single DecisionTransition journal step (Plan §M5).
    /// Flat shape projected from the agent's DecisionTransition — see <see cref="SignalRecord"/>
    /// for the overall projection pattern.
    /// <para>
    /// PK = <c>{TenantId}_{SessionId}</c>, RK = <c>{StepIndex:D10}</c>.
    /// </para>
    /// <para>
    /// The discriminator columns (<see cref="IsTerminal"/>, <see cref="ClassifierVerdictId"/>,
    /// <see cref="ClassifierHypothesisLevel"/>) are projected eagerly so readers such as the
    /// decision-graph builder never re-parse <see cref="PayloadJson"/>.
    /// </para>
    /// </summary>
    public sealed class DecisionTransitionRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Monotonic per Journal. Drives table RowKey ordering.</summary>
        public int StepIndex { get; set; }

        /// <summary>Session-wide monotonic across Event + Signal + Transition.</summary>
        public long SessionTraceOrdinal { get; set; }

        /// <summary>Plan §2.2 cross-reference — which signal caused this transition.</summary>
        public long SignalOrdinalRef { get; set; }

        public DateTime OccurredAtUtc { get; set; }

        public string Trigger { get; set; } = string.Empty;

        /// <summary>SessionStage enum name.</summary>
        public string FromStage { get; set; } = string.Empty;
        public string ToStage { get; set; } = string.Empty;

        /// <summary>false = dead-end (blocked path, <see cref="DeadEndReason"/> non-null).</summary>
        public bool Taken { get; set; }

        public string? DeadEndReason { get; set; }

        public string ReducerVersion { get; set; } = string.Empty;

        /// <summary>Projected index discriminator — true when <see cref="ToStage"/> is a terminal stage.</summary>
        public bool IsTerminal { get; set; }

        /// <summary>Projected index discriminator — non-null when this step embeds a classifier verdict.</summary>
        public string? ClassifierVerdictId { get; set; }
        public string? ClassifierHypothesisLevel { get; set; }

        /// <summary>Agent-serialized DecisionTransition JSON (Guards + EmittedEventSequences + ClassifierVerdict included).</summary>
        public string PayloadJson { get; set; } = string.Empty;
    }
}
