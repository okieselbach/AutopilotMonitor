using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Result of an analyze rule evaluation against a session's events
    /// Stored in the RuleResults table and displayed in the session detail UI
    /// </summary>
    public class RuleResult
    {
        /// <summary>
        /// Unique identifier for this result
        /// </summary>
        public string ResultId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Session this result belongs to
        /// </summary>
        public string SessionId { get; set; } = default!;

        /// <summary>
        /// Tenant this result belongs to
        /// </summary>
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// The rule that produced this result
        /// </summary>
        public string RuleId { get; set; } = default!;

        /// <summary>
        /// Human-readable title of the rule
        /// </summary>
        public string RuleTitle { get; set; } = default!;

        /// <summary>
        /// Severity level: "info", "warning", "high", "critical"
        /// </summary>
        public string Severity { get; set; } = default!;

        /// <summary>
        /// Rule category: network, identity, enrollment, apps, esp, device
        /// </summary>
        public string Category { get; set; } = default!;

        /// <summary>
        /// Confidence score (0-100)
        /// Higher = more confident this issue is the root cause
        /// </summary>
        public int ConfidenceScore { get; set; }

        /// <summary>
        /// Detailed explanation of the detected issue
        /// </summary>
        public string Explanation { get; set; } = default!;

        /// <summary>
        /// Remediation steps for the detected issue
        /// </summary>
        public List<RemediationStep> Remediation { get; set; } = new List<RemediationStep>();

        /// <summary>
        /// Links to relevant documentation
        /// </summary>
        public List<RelatedDoc> RelatedDocs { get; set; } = new List<RelatedDoc>();

        /// <summary>
        /// Evidence: which conditions matched and their values
        /// </summary>
        public Dictionary<string, object> MatchedConditions { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// When this issue was detected
        /// </summary>
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

        // ===== EVALUATION LIFECYCLE (evaluateOn interim triggers) =====
        // All nullable: rows written before the feature read as final/terminal legacy results.
        // See internal/docs/rules/analyze-rule-triggers.md.

        /// <summary>
        /// When this finding FIRST fired for the session (stable across interim refreshes and
        /// the terminal finalization pass). Null on legacy rows — treat DetectedAt as the anchor.
        /// </summary>
        public DateTime? FirstDetectedAt { get; set; }

        /// <summary>When the rule was last (re-)evaluated for this session. Null on legacy rows.</summary>
        public DateTime? LastEvaluatedAt { get; set; }

        /// <summary>
        /// True while the finding comes from an interim run (whiteglove_sealed / on_event trigger)
        /// and has not yet been confirmed by the terminal finalization pass. UI renders these
        /// with a "preliminary" badge.
        /// </summary>
        public bool IsInterim { get; set; }

        /// <summary>
        /// Set when a later evaluation no longer fired the rule (the session healed). Resolved
        /// rows are kept for audit but excluded from issue counts and hidden by default.
        /// </summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>
        /// Set once the finding's channel notification was sent. The notification dedupe anchors
        /// here (one notification per session+rule), decoupled from row existence so interim
        /// refreshes and the manual reanalyze rebuild can never re-arm a duplicate send.
        /// </summary>
        public DateTime? NotifiedAt { get; set; }
    }
}
