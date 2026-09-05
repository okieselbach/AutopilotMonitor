namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One rule evaluation to fold into the daily <c>RuleStats</c> counters. Collected per
    /// session and written as one batch per scope (tenant or <c>global</c>) so an analysis
    /// costs one partition read plus one transaction instead of two round-trips per rule.
    /// Plain class: Shared also targets netstandard2.0 (agent), which has no record support.
    /// </summary>
    public sealed class RuleStatIncrement
    {
        public RuleStatIncrement(
            string ruleId, string ruleType, string ruleTitle, string category, string severity,
            bool fired, int? confidenceScore)
        {
            RuleId = ruleId;
            RuleType = ruleType;
            RuleTitle = ruleTitle;
            Category = category;
            Severity = severity;
            Fired = fired;
            ConfidenceScore = confidenceScore;
        }

        public string RuleId { get; }
        public string RuleType { get; }
        public string RuleTitle { get; }
        public string Category { get; }
        public string Severity { get; }
        public bool Fired { get; }
        public int? ConfidenceScore { get; }
    }
}
