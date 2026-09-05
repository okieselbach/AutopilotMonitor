using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Daily per-rule telemetry entry.
    /// Stored in Azure Table Storage for rule effectiveness analysis and tenant dashboards.
    /// PartitionKey = "{TenantId}_{Date}" (tenant-scoped) or "global_{Date}" (cross-tenant aggregate), Date as YYYY-MM-DD
    /// RowKey = RuleId (layout D-199; rows before 2026-09-06 used PartitionKey = Date, RowKey = "{TenantId}_{RuleId}")
    /// </summary>
    public class RuleStatsEntry
    {
        /// <summary>
        /// Date of the stats entry (YYYY-MM-DD) — the PartitionKey suffix
        /// </summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>
        /// Tenant ID or "global" for cross-tenant — the PartitionKey prefix
        /// </summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// Rule identifier (e.g., "ANALYZE-NET-001" or "GATHER-REG-001")
        /// </summary>
        public string RuleId { get; set; } = string.Empty;

        /// <summary>
        /// "analyze" or "gather"
        /// </summary>
        public string RuleType { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable rule title (denormalized for display)
        /// </summary>
        public string RuleTitle { get; set; } = string.Empty;

        /// <summary>
        /// Rule category (network, identity, enrollment, apps, esp, device)
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Rule severity (info, warning, high, critical)
        /// </summary>
        public string Severity { get; set; } = string.Empty;

        /// <summary>
        /// Number of times this rule fired (produced a result/event) on this day
        /// </summary>
        public int FireCount { get; set; }

        /// <summary>
        /// Number of times this rule was evaluated on this day (analyze rules only; 0 for gather rules)
        /// </summary>
        public int EvaluationCount { get; set; }

        /// <summary>
        /// Number of distinct sessions where this rule was evaluated
        /// </summary>
        public int SessionsEvaluated { get; set; }

        /// <summary>
        /// Average confidence score when the rule fired (analyze rules only; 0 for gather rules)
        /// </summary>
        public double AvgConfidenceScore { get; set; }

        /// <summary>
        /// Sum of confidence scores — used for incremental average computation.
        /// AvgConfidenceScore = ConfidenceScoreSum / FireCount
        /// </summary>
        public long ConfidenceScoreSum { get; set; }

        /// <summary>
        /// When this entry was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
