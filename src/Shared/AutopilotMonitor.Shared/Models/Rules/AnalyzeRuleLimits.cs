namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Size limits of a tenant-authored analyze rule, enforced on dry-run, create, update and
    /// template copy. The rule's shape sets how much work every analysis of the tenant's sessions
    /// does on the shared backend, so it is bounded where the rule enters. The schema carries the
    /// two counts.
    /// </summary>
    public static class AnalyzeRuleLimits
    {
        /// <summary>Maximum number of conditions.</summary>
        public const int MaxConditions = 20;

        /// <summary>Maximum number of preconditions.</summary>
        public const int MaxPreconditions = 10;

        /// <summary>Maximum length of a value compared with the regex or not_regex operator,
        /// wherever a rule can hold one (condition value, filter value, event A filter value,
        /// precondition value). Same cap as the logparser pattern test.</summary>
        public const int MaxRegexLength = 2000;
    }
}
