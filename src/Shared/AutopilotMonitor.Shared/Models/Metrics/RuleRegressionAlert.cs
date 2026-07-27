using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The dominant dimension value among a regression's hit sessions — CORRELATION only, and
    /// every consumer's wording must say so ("correlated — not necessarily causal", insights
    /// spec §F3 / truthfulness rule 6). Null on an alert means "no clear dimension
    /// concentration" — the radar never stretches for one.
    /// </summary>
    public class RuleRegressionDimension
    {
        /// <summary>"osBuild", "model", "agentVersion" or "imeVersion".</summary>
        public string Dimension { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        /// <summary>Hit sessions carrying this value (gate: ≥5).</summary>
        public int HitCount { get; set; }

        public double HitSharePct { get; set; }
        public double AllSharePct { get; set; }

        /// <summary>HitShare ÷ AllShare (gate: ≥2.0).</summary>
        public double Lift { get; set; }
    }

    /// <summary>
    /// One ACTIVE rule-frequency regression (F3, insights spec §F3): an analyze rule whose
    /// 7-day hit rate rose ≥2× over its 28-day baseline with disjoint Wilson intervals.
    /// Persisted as the <c>ruleregression|{ruleId}</c> keyspace of the notification tracker
    /// table — the row IS the dedup (one bell per episode), the badge state (rules page) and
    /// the <c>regressions[]</c> payload (rule-stats response). Deleted when the rate re-arms
    /// (falls under 1.5× baseline or stops firing) or by the tracker's 30-day retention sweep
    /// (spec: retention cleanup re-arms). Numbers are refreshed on every radar pass while the
    /// episode stays active; <see cref="FirstNotifiedAt"/> never moves.
    /// </summary>
    public class RuleRegressionAlert
    {
        public string TenantId { get; set; } = string.Empty;
        public string RuleId { get; set; } = string.Empty;
        public string RuleTitle { get; set; } = string.Empty;

        public int WindowFireCount { get; set; }
        public int WindowSessionCount { get; set; }
        public int BaselineFireCount { get; set; }
        public int BaselineSessionCount { get; set; }

        public double WindowRatePct { get; set; }
        public double BaselineRatePct { get; set; }

        /// <summary>Window rate ÷ baseline rate. Null when the baseline rate is 0 (a NEW signal has no finite lift — never invented).</summary>
        public double? Lift { get; set; }

        /// <summary>Trailing 7-day window ("yyyy-MM-dd", inclusive) the current numbers describe.</summary>
        public string WindowStartDate { get; set; } = string.Empty;
        public string WindowEndDate { get; set; } = string.Empty;

        /// <summary>Dimension concentration captured when the alert FIRST fired (stable story); null = no clear concentration.</summary>
        public RuleRegressionDimension? Dimension { get; set; }

        /// <summary>When the episode first fired (bell + ops event moment). Never moves on refresh; drives the 30d retention re-arm.</summary>
        public DateTime FirstNotifiedAt { get; set; }

        /// <summary>Last radar pass that re-confirmed/refreshed this episode.</summary>
        public DateTime LastEvaluatedAt { get; set; }
    }
}
