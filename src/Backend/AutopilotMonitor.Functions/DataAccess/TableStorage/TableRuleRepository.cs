using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IRuleRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableRuleRepository : IRuleRepository
    {
        private readonly TableStorageService _storage;
        private readonly IDataEventPublisher _publisher;

        public TableRuleRepository(TableStorageService storage, IDataEventPublisher publisher)
        {
            _storage = storage;
            _publisher = publisher;
        }

        public async Task<bool> StoreRuleResultAsync(RuleResult result)
        {
            var success = await _storage.StoreRuleResultAsync(result);
            if (success)
                await _publisher.PublishAsync("rule.evaluated", result, result.TenantId);
            return success;
        }

        public Task<List<RuleResult>> GetRuleResultsAsync(string tenantId, string sessionId)
            => _storage.GetRuleResultsAsync(tenantId, sessionId);

        public Task<bool> StoreGatherRuleAsync(GatherRule rule, string tenantId = "global")
            => _storage.StoreGatherRuleAsync(rule, tenantId);

        public Task<List<GatherRule>> GetGatherRulesAsync(string partitionKey)
            => _storage.GetGatherRulesAsync(partitionKey);

        public Task<bool> DeleteGatherRuleAsync(string tenantId, string ruleId)
            => _storage.DeleteGatherRuleAsync(tenantId, ruleId);

        public Task<bool> StoreRuleStateAsync(string tenantId, string ruleId, RuleState state)
            => _storage.StoreRuleStateAsync(tenantId, ruleId, state);

        public Task<Dictionary<string, RuleState>> GetRuleStatesAsync(string tenantId)
            => _storage.GetRuleStatesAsync(tenantId);

        public Task<bool> DeleteRuleStateAsync(string tenantId, string ruleId)
            => _storage.DeleteRuleStateAsync(tenantId, ruleId);

        public Task<(int deleted, int failed)> DeleteRuleStatesForRuleIdAcrossTenantsAsync(string ruleId)
            => _storage.DeleteRuleStatesForRuleIdAcrossTenantsAsync(ruleId);

        public Task<bool> StoreAnalyzeRuleAsync(AnalyzeRule rule, string tenantId = "global")
            => _storage.StoreAnalyzeRuleAsync(rule, tenantId);

        public Task<List<AnalyzeRule>> GetAnalyzeRulesAsync(string partitionKey)
            => _storage.GetAnalyzeRulesAsync(partitionKey);

        public Task<bool> DeleteAnalyzeRuleAsync(string tenantId, string ruleId)
            => _storage.DeleteAnalyzeRuleAsync(tenantId, ruleId);

        public Task<bool> AnalyzeRuleExistsAsync(string partitionKey, string ruleId)
            => _storage.AnalyzeRuleExistsAsync(partitionKey, ruleId);

        public Task<bool> GatherRuleExistsAsync(string partitionKey, string ruleId)
            => _storage.GatherRuleExistsAsync(partitionKey, ruleId);

        public Task<bool> StoreImeLogPatternAsync(ImeLogPattern pattern, string tenantId = "global")
            => _storage.StoreImeLogPatternAsync(pattern, tenantId);

        public Task<List<ImeLogPattern>> GetImeLogPatternsAsync(string partitionKey)
            => _storage.GetImeLogPatternsAsync(partitionKey);

        public Task<bool> DeleteImeLogPatternAsync(string tenantId, string patternId)
            => _storage.DeleteImeLogPatternAsync(tenantId, patternId);
    }
}
