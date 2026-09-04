using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Queue-message envelope for the <c>analyze-on-enrollment-end</c> queue. Replaces the
    /// previous in-function fire-and-forget Task.Run that ran the rule engine after
    /// session-terminal events — the Task.Run could be killed mid-flight by Functions
    /// scale-in, leaving sessions without rule results until the user clicked "Analyze Now".
    /// <para>
    /// The handler branches on <see cref="Reason"/>: <c>enrollment_complete</c> /
    /// <c>enrollment_failed</c> run the full terminal path, <c>vulnerability_correlated</c>
    /// is the incremental rerun (no stats), and the interim reasons
    /// <c>whiteglove_sealed</c> / <c>interim_trigger</c> run the evaluateOn-filtered
    /// interim path (notify yes, KO + stats suppressed) — see
    /// internal/docs/rules/analyze-rule-triggers.md.
    /// </para>
    /// </summary>
    public sealed class AnalyzeOnEnrollmentEndEnvelope
    {
        /// <summary>Schema version — bump on breaking envelope changes so consumers can reject or migrate.</summary>
        public string EnvelopeVersion { get; set; } = "1";

        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>What caused the enqueue. The handler branches on this (see class doc).</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>UTC time the producer enqueued the message; useful for measuring queue lag.</summary>
        public DateTime EnqueuedAt { get; set; }

        /// <summary>
        /// For <c>interim_trigger</c> envelopes: the distinct trigger event types the ingest batch
        /// contained (intersection of batch event types with the tenant's on_event trigger
        /// registry). Null/empty on all other reasons. Additive field — older consumers ignore it.
        /// </summary>
        public System.Collections.Generic.List<string>? TriggerEventTypes { get; set; }
    }
}
