using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order (STJ serializes in declaration order; the MCP hands
    // raw response JSON to an LLM, so key order is part of the contract).

    /// <summary>
    /// Response of <c>GET metrics/rule-stats</c> and <c>GET global/metrics/rule-stats</c>:
    /// per-rule firing aggregates over the requested window, the active rule-frequency
    /// regression episodes (tenant scope only — empty on the cross-tenant aggregate), and
    /// window totals.
    /// </summary>
    public class RuleStatsResponse : IApiResponse
    {
        public IReadOnlyList<RuleStatsRuleAggregate> Rules { get; set; } = default!;
        public IReadOnlyList<RuleRegressionAlert> Regressions { get; set; } = default!;
        public RuleStatsSummary Summary { get; set; } = default!;
    }

    /// <summary>One rule's aggregate across all dates in the window (fires descending).</summary>
    public class RuleStatsRuleAggregate
    {
        public string RuleId { get; set; } = string.Empty;
        public string RuleType { get; set; } = string.Empty;
        public string RuleTitle { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public int FireCount { get; set; }
        public int EvaluationCount { get; set; }
        public int SessionsEvaluated { get; set; }
        /// <summary>Fires per evaluation as a percentage (one decimal); 0 with no evaluations.</summary>
        public double HitRate { get; set; }
        /// <summary>Average confidence score across fires (one decimal); 0 with no fires.</summary>
        public double AvgConfidenceScore { get; set; }
        /// <summary>Per-day trend rows, oldest first (one row per stored date).</summary>
        public IReadOnlyList<RuleTrendPoint> Trend { get; set; } = default!;
    }

    /// <summary>One day of a rule's trend ("yyyy-MM-dd").</summary>
    public class RuleTrendPoint
    {
        public string Date { get; set; } = string.Empty;
        public int FireCount { get; set; }
        public int EvaluationCount { get; set; }
    }

    /// <summary>Window totals of a rule-stats response.</summary>
    public class RuleStatsSummary
    {
        public int TotalEvaluations { get; set; }
        public int TotalFires { get; set; }
        public double OverallHitRate { get; set; }
        /// <summary>Rule id with the most fires; absent when the window holds no rules.</summary>
        public string? TopRuleByFireCount { get; set; }
        /// <summary>Distinct rule count — set on the global route only (the key is absent on the tenant route, preserving its historical shape).</summary>
        public int? UniqueRules { get; set; }
        public RuleStatsPeriod Period { get; set; } = default!;
    }

    /// <summary>Echo of the effective date window ("yyyy-MM-dd").</summary>
    public class RuleStatsPeriod
    {
        public string Start { get; set; } = string.Empty;
        public string End { get; set; } = string.Empty;
    }
}
