using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for rule definitions, results, and patterns.
    /// Covers: RuleResults, GatherRules, AnalyzeRules, ImeLogPatterns, RuleStates tables.
    /// </summary>
    public interface IRuleRepository
    {
        // --- Rule Results ---
        Task<bool> StoreRuleResultAsync(RuleResult result);
        Task<List<RuleResult>> GetRuleResultsAsync(string tenantId, string sessionId);

        // --- Gather Rules ---
        Task<bool> StoreGatherRuleAsync(GatherRule rule, string tenantId = "global");
        Task<List<GatherRule>> GetGatherRulesAsync(string partitionKey);
        Task<bool> DeleteGatherRuleAsync(string tenantId, string ruleId);

        // --- Rule States ---
        Task<bool> StoreRuleStateAsync(string tenantId, string ruleId, RuleState state);
        Task<Dictionary<string, RuleState>> GetRuleStatesAsync(string tenantId);
        Task<bool> DeleteRuleStateAsync(string tenantId, string ruleId);
        /// <summary>
        /// Removes every <c>RuleStates</c> row (across all tenants) whose RowKey equals
        /// <paramref name="ruleId"/>. Used as orphan-GC when a built-in / community rule
        /// is sunset (deleted from the global catalog): tenants that previously toggled
        /// the rule in the UI have a per-tenant <c>RuleState</c> row that would otherwise
        /// linger as dead state.
        /// <para>
        /// Returns <c>(deleted, failed)</c>. A 404 on an individual row is counted as
        /// deleted (idempotent retry). <c>failed</c> is non-zero on per-row errors;
        /// <c>-1</c> signals the cross-partition enumeration itself failed — callers
        /// MUST then refuse to delete the global catalog row, or the rule falls out of
        /// the sunset-diff and unreachable orphan state lingers forever.
        /// </para>
        /// </summary>
        Task<(int deleted, int failed)> DeleteRuleStatesForRuleIdAcrossTenantsAsync(string ruleId);

        // --- Analyze Rules ---
        Task<bool> StoreAnalyzeRuleAsync(AnalyzeRule rule, string tenantId = "global");
        Task<List<AnalyzeRule>> GetAnalyzeRulesAsync(string partitionKey);
        Task<bool> DeleteAnalyzeRuleAsync(string tenantId, string ruleId);

        // --- Rule Existence Checks (Point Queries) ---
        /// <summary>
        /// Checks if an analyze rule with the given ID exists in the specified partition.
        /// Uses a point query (O(1)) instead of loading the full partition.
        /// </summary>
        Task<bool> AnalyzeRuleExistsAsync(string partitionKey, string ruleId);

        /// <summary>
        /// Checks if a gather rule with the given ID exists in the specified partition.
        /// Uses a point query (O(1)) instead of loading the full partition.
        /// </summary>
        Task<bool> GatherRuleExistsAsync(string partitionKey, string ruleId);

        // --- IME Log Patterns ---
        Task<bool> StoreImeLogPatternAsync(ImeLogPattern pattern, string tenantId = "global");
        Task<List<ImeLogPattern>> GetImeLogPatternsAsync(string partitionKey);
        Task<bool> DeleteImeLogPatternAsync(string tenantId, string patternId);
    }
}
