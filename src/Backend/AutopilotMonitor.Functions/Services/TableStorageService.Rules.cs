using System.Text.Json;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== RULE RESULTS METHODS =====

        /// <summary>
        /// Azure Table Storage hard-limits each property to 64 KiB (UTF-16). We trip the
        /// guard rail at 60 KiB to leave headroom for the property-name prefix + framing
        /// bytes the storage layer adds on the wire.
        /// </summary>
        private const int MatchedConditionsJsonByteLimit = 60 * 1024;

        /// <summary>
        /// Stores a rule evaluation result
        /// PartitionKey: {TenantId}_{SessionId}, RowKey: RuleId
        /// <para>
        /// Defense-in-depth: rule authors should keep <see cref="RuleResult.MatchedConditions"/>
        /// under <see cref="MatchedConditionsJsonByteLimit"/> bytes (correlation evidence
        /// already caps and slims its pairs). If a future rule still produces oversized
        /// output, we substitute a truncation marker rather than letting the entire write
        /// fail — the rule still fires, the UI sees the explanation + flag, and we get an
        /// observable warning instead of an opaque store failure.
        /// </para>
        /// </summary>
        public async Task<bool> StoreRuleResultAsync(RuleResult result)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleResults);
                var partitionKey = $"{result.TenantId}_{result.SessionId}";

                var matchedJson = JsonConvert.SerializeObject(result.MatchedConditions ?? new Dictionary<string, object>());
                if (matchedJson.Length > MatchedConditionsJsonByteLimit)
                {
                    _logger.LogWarning(
                        "Rule result {RuleId} (session {SessionId}) MatchedConditionsJson is {Bytes} bytes, exceeds {Limit} byte guard. Substituting truncation marker so the rule still persists.",
                        result.RuleId, result.SessionId, matchedJson.Length, MatchedConditionsJsonByteLimit);

                    var truncated = new Dictionary<string, object>
                    {
                        ["_truncated"] = true,
                        ["_originalBytes"] = matchedJson.Length,
                        ["_reason"] = "MatchedConditions exceeded Table Storage 64KB property limit"
                    };
                    matchedJson = JsonConvert.SerializeObject(truncated);
                }

                var entity = new TableEntity(partitionKey, result.RuleId)
                {
                    ["ResultId"] = result.ResultId,
                    ["SessionId"] = result.SessionId,
                    ["TenantId"] = result.TenantId,
                    ["RuleId"] = result.RuleId,
                    ["RuleTitle"] = result.RuleTitle ?? string.Empty,
                    ["Severity"] = result.Severity ?? string.Empty,
                    ["Category"] = result.Category ?? string.Empty,
                    ["ConfidenceScore"] = result.ConfidenceScore,
                    ["Explanation"] = result.Explanation ?? string.Empty,
                    ["RemediationJson"] = JsonConvert.SerializeObject(result.Remediation ?? new List<RemediationStep>()),
                    ["RelatedDocsJson"] = JsonConvert.SerializeObject(result.RelatedDocs ?? new List<RelatedDoc>()),
                    ["MatchedConditionsJson"] = matchedJson,
                    ["DetectedAt"] = result.DetectedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogInformation($"Stored rule result {result.RuleId} for session {result.SessionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store rule result {result.RuleId}");
                return false;
            }
        }

        /// <summary>
        /// Gets all rule results for a session
        /// </summary>
        public async Task<List<RuleResult>> GetRuleResultsAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleResults);
                var partitionKey = $"{tenantId}_{sessionId}";
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var results = new List<RuleResult>();
                await foreach (var entity in query)
                {
                    results.Add(new RuleResult
                    {
                        ResultId = entity.GetString("ResultId") ?? string.Empty,
                        SessionId = entity.GetString("SessionId") ?? string.Empty,
                        TenantId = entity.GetString("TenantId") ?? string.Empty,
                        RuleId = entity.GetString("RuleId") ?? entity.RowKey,
                        RuleTitle = entity.GetString("RuleTitle") ?? string.Empty,
                        Severity = entity.GetString("Severity") ?? string.Empty,
                        Category = entity.GetString("Category") ?? string.Empty,
                        ConfidenceScore = entity.GetInt32("ConfidenceScore") ?? 0,
                        Explanation = entity.GetString("Explanation") ?? string.Empty,
                        Remediation = DeserializeJson<List<RemediationStep>>(entity.GetString("RemediationJson")),
                        RelatedDocs = DeserializeJson<List<RelatedDoc>>(entity.GetString("RelatedDocsJson")),
                        MatchedConditions = DeserializeMatchedConditions(entity.GetString("MatchedConditionsJson")),
                        DetectedAt = entity.GetDateTimeOffset("DetectedAt")?.UtcDateTime ?? DateTime.UtcNow
                    });
                }

                return results.OrderByDescending(r => r.ConfidenceScore).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get rule results for session {sessionId}");
                return new List<RuleResult>();
            }
        }

        // ===== GATHER RULES METHODS =====

        /// <summary>
        /// Stores or updates a gather rule
        /// PartitionKey: TenantId (or "global" for built-in), RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreGatherRuleAsync(GatherRule rule, string tenantId = "global")
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GatherRules);

                var entity = new TableEntity(tenantId, rule.RuleId)
                {
                    ["Title"] = rule.Title ?? string.Empty,
                    ["Description"] = rule.Description ?? string.Empty,
                    ["Category"] = rule.Category ?? string.Empty,
                    ["Version"] = rule.Version ?? "1.0.0",
                    ["Author"] = rule.Author ?? "Autopilot Monitor",
                    ["Enabled"] = rule.Enabled,
                    ["IsBuiltIn"] = rule.IsBuiltIn,
                    ["IsCommunity"] = rule.IsCommunity,
                    ["Provenance"] = rule.Provenance ?? string.Empty,
                    ["CollectorType"] = rule.CollectorType ?? string.Empty,
                    ["Target"] = rule.Target ?? string.Empty,
                    ["ParametersJson"] = JsonConvert.SerializeObject(rule.Parameters ?? new Dictionary<string, string>()),
                    ["Trigger"] = rule.Trigger ?? string.Empty,
                    ["IntervalSeconds"] = rule.IntervalSeconds,
                    ["TriggerPhase"] = rule.TriggerPhase ?? string.Empty,
                    ["TriggerEventType"] = rule.TriggerEventType ?? string.Empty,
                    ["OutputEventType"] = rule.OutputEventType ?? string.Empty,
                    ["OutputSeverity"] = rule.OutputSeverity ?? "Info",
                    ["TagsJson"] = JsonConvert.SerializeObject(rule.Tags ?? new string[0]),
                    ["CreatedAt"] = rule.CreatedAt,
                    ["UpdatedAt"] = rule.UpdatedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored gather rule {rule.RuleId} for {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store gather rule {rule.RuleId}");
                return false;
            }
        }

        /// <summary>
        /// Gets gather rules for a partition (tenant or "global")
        /// </summary>
        public async Task<List<GatherRule>> GetGatherRulesAsync(string partitionKey)
        {
            if (partitionKey != "global")
                SecurityValidator.EnsureValidGuid(partitionKey, nameof(partitionKey));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GatherRules);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var rules = new List<GatherRule>();
                await foreach (var entity in query)
                {
                    rules.Add(MapToGatherRule(entity));
                }

                return rules;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get gather rules for {partitionKey}");
                return new List<GatherRule>();
            }
        }

        /// <summary>
        /// Deletes a gather rule
        /// </summary>
        public async Task<bool> DeleteGatherRuleAsync(string tenantId, string ruleId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GatherRules);
                await tableClient.DeleteEntityAsync(tenantId, ruleId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete gather rule {ruleId}");
                return false;
            }
        }

        private GatherRule MapToGatherRule(TableEntity entity)
        {
            return new GatherRule
            {
                RuleId = entity.RowKey,
                Title = entity.GetString("Title") ?? string.Empty,
                Description = entity.GetString("Description") ?? string.Empty,
                Category = entity.GetString("Category") ?? string.Empty,
                Version = entity.GetString("Version") ?? "1.0.0",
                Author = entity.GetString("Author") ?? "Autopilot Monitor",
                Enabled = entity.GetBoolean("Enabled") ?? true,
                IsBuiltIn = entity.GetBoolean("IsBuiltIn") ?? false,
                IsCommunity = entity.GetBoolean("IsCommunity") ?? false,
                // Absent column (pre-existing rows) → null → treated as "embedded" by RuleProvenance.
                Provenance = string.IsNullOrEmpty(entity.GetString("Provenance")) ? null : entity.GetString("Provenance"),
                CollectorType = entity.GetString("CollectorType") ?? string.Empty,
                Target = entity.GetString("Target") ?? string.Empty,
                Parameters = DeserializeJson<Dictionary<string, string>>(entity.GetString("ParametersJson")),
                Trigger = entity.GetString("Trigger") ?? string.Empty,
                IntervalSeconds = entity.GetInt32("IntervalSeconds"),
                TriggerPhase = entity.GetString("TriggerPhase") ?? string.Empty,
                TriggerEventType = entity.GetString("TriggerEventType") ?? string.Empty,
                OutputEventType = entity.GetString("OutputEventType") ?? string.Empty,
                OutputSeverity = entity.GetString("OutputSeverity") ?? "Info",
                Tags = DeserializeJsonArray(entity.GetString("TagsJson")),
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.UtcNow,
                UpdatedAt = entity.GetDateTimeOffset("UpdatedAt")?.UtcDateTime ?? DateTime.UtcNow
            };
        }

        // ===== RULE STATES METHODS =====

        /// <summary>
        /// Stores or updates the enabled/disabled state for a built-in or community rule per tenant
        /// PartitionKey: TenantId, RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreRuleStateAsync(string tenantId, string ruleId, RuleState state)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStates);

                var entity = new TableEntity(tenantId, ruleId)
                {
                    ["Enabled"] = state.Enabled,
                    ["UpdatedAt"] = DateTime.UtcNow
                };

                // Nullable override: write the bool when set; leave the property absent when cleared.
                // Use Replace mode so a cleared override actually wipes the column — Merge mode skips
                // null values, leaving a stale `true` on the row (reset would silently fail).
                if (state.MarkSessionAsFailed.HasValue)
                    entity["MarkSessionAsFailed"] = state.MarkSessionAsFailed.Value;
                if (state.Notify.HasValue)
                    entity["Notify"] = state.Notify.Value;
                if (!string.IsNullOrEmpty(state.NotifyChannelIdsJson))
                    entity["NotifyChannelIdsJson"] = state.NotifyChannelIdsJson;

                await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                _logger.LogDebug($"Stored rule state {ruleId} for {tenantId}: enabled={state.Enabled}, markAsFailed={state.MarkSessionAsFailed?.ToString() ?? "inherit"}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store rule state {ruleId} for {tenantId}");
                return false;
            }
        }

        /// <summary>
        /// Gets all rule states for a tenant as a dictionary of ruleId → RuleState.
        /// </summary>
        public async Task<Dictionary<string, RuleState>> GetRuleStatesAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStates);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var states = new Dictionary<string, RuleState>();
                await foreach (var entity in query)
                {
                    states[entity.RowKey] = new RuleState
                    {
                        Enabled = entity.GetBoolean("Enabled") ?? true,
                        MarkSessionAsFailed = entity.GetBoolean("MarkSessionAsFailed"),
                        Notify = entity.GetBoolean("Notify"),
                        NotifyChannelIdsJson = entity.GetString("NotifyChannelIdsJson")
                    };
                }

                return states;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get rule states for {tenantId}");
                return new Dictionary<string, RuleState>();
            }
        }

        /// <summary>
        /// Deletes the rule state for a tenant (resets to rule's default enabled state).
        /// Idempotent: a 404 (the row was already gone) counts as success so callers can
        /// retry safely without flipping their happy-path on the missing-row condition.
        /// </summary>
        public async Task<bool> DeleteRuleStateAsync(string tenantId, string ruleId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStates);
                await tableClient.DeleteEntityAsync(tenantId, ruleId);
                return true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete rule state {ruleId} for {tenantId}");
                return false;
            }
        }

        /// <summary>
        /// Cross-partition cleanup: deletes every RuleState row across all tenants whose
        /// RowKey matches <paramref name="ruleId"/>. Used as orphan-GC when a built-in
        /// rule is removed from the global catalog — without this, per-tenant
        /// <c>RuleState{Enabled=true}</c> overrides survive the sunset and would re-fire
        /// the moment the rule was ever re-introduced under the same ruleId.
        /// <para>
        /// Returns a tuple of <c>(deleted, failed)</c>. <c>deleted</c> counts both fresh
        /// deletes and 404 (the row was already gone — idempotent retry). <c>failed</c>
        /// is non-zero if any individual delete threw a non-404 error, or
        /// <c>-1</c> if the cross-partition enumeration itself failed (we can't tell how
        /// many rows are still out there). Callers must check <c>failed</c> before
        /// proceeding with the global rule delete — otherwise a partial GC followed by a
        /// successful catalog delete leaves orphan state with no way to re-enter the diff
        /// on a later seed cycle.
        /// </para>
        /// </summary>
        public async Task<(int deleted, int failed)> DeleteRuleStatesForRuleIdAcrossTenantsAsync(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId)) return (0, 0);
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStates);
                // Cross-partition scan is acceptable here — RuleStates is a small table
                // (one row per tenant × overridden rule). Deletion is rare (only on built-in
                // sunset), so we don't need a partition-key-indexed shortcut.
                var query = tableClient.QueryAsync<TableEntity>(filter: $"RowKey eq '{ruleId.Replace("'", "''")}'");

                var deleted = 0;
                var failed = 0;
                await foreach (var entity in query)
                {
                    try
                    {
                        await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                    {
                        // Row was deleted between the query and our delete — idempotent.
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to delete orphan RuleState {RuleId} for tenant {TenantId}",
                            ruleId, entity.PartitionKey);
                        failed++;
                    }
                }
                if (deleted > 0 || failed > 0)
                {
                    _logger.LogInformation(
                        "Orphan RuleState GC for {RuleId}: {Deleted} deleted, {Failed} failed",
                        ruleId, deleted, failed);
                }
                return (deleted, failed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate RuleStates for orphan GC of {RuleId}", ruleId);
                // -1 signals "we don't know" — caller must NOT proceed to delete the
                // global catalog row, otherwise the rule falls out of the diff and any
                // orphan state we couldn't enumerate lingers indefinitely.
                return (0, -1);
            }
        }

        // ===== ANALYZE RULES METHODS =====

        /// <summary>
        /// Stores or updates an analyze rule
        /// PartitionKey: TenantId (or "global" for built-in), RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreAnalyzeRuleAsync(AnalyzeRule rule, string tenantId = "global")
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AnalyzeRules);

                var entity = new TableEntity(tenantId, rule.RuleId)
                {
                    ["Title"] = rule.Title ?? string.Empty,
                    ["Description"] = rule.Description ?? string.Empty,
                    ["Severity"] = rule.Severity ?? string.Empty,
                    ["Category"] = rule.Category ?? string.Empty,
                    ["Version"] = rule.Version ?? "1.0.0",
                    ["Author"] = rule.Author ?? "Autopilot Monitor",
                    ["Enabled"] = rule.Enabled,
                    ["IsBuiltIn"] = rule.IsBuiltIn,
                    ["IsCommunity"] = rule.IsCommunity,
                    ["Provenance"] = rule.Provenance ?? string.Empty,
                    ["Trigger"] = rule.Trigger ?? "single",
                    ["PreconditionsJson"] = JsonConvert.SerializeObject(rule.Preconditions ?? new List<RulePrecondition>()),
                    ["ConditionsJson"] = JsonConvert.SerializeObject(rule.Conditions ?? new List<RuleCondition>()),
                    ["BaseConfidence"] = rule.BaseConfidence,
                    ["ConfidenceFactorsJson"] = JsonConvert.SerializeObject(rule.ConfidenceFactors ?? new List<ConfidenceFactor>()),
                    ["ConfidenceThreshold"] = rule.ConfidenceThreshold,
                    ["Explanation"] = rule.Explanation ?? string.Empty,
                    ["RemediationJson"] = JsonConvert.SerializeObject(rule.Remediation ?? new List<RemediationStep>()),
                    ["RelatedDocsJson"] = JsonConvert.SerializeObject(rule.RelatedDocs ?? new List<RelatedDoc>()),
                    ["TagsJson"] = JsonConvert.SerializeObject(rule.Tags ?? new string[0]),
                    ["TemplateVariablesJson"] = JsonConvert.SerializeObject(rule.TemplateVariables ?? new List<TemplateVariable>()),
                    ["DerivedFromTemplateRuleId"] = rule.DerivedFromTemplateRuleId ?? string.Empty,
                    ["MarkSessionAsFailedDefault"] = rule.MarkSessionAsFailedDefault,
                    ["NotifyDefault"] = rule.NotifyDefault,
                    // Channel ids are tenant-specific: populated only for tenant custom rules
                    // (built-in/community rules keep their targets in RuleStates instead).
                    ["NotifyChannelIdsJson"] = JsonConvert.SerializeObject(rule.NotifyChannelIds ?? new List<string>()),
                    ["CreatedAt"] = rule.CreatedAt,
                    ["UpdatedAt"] = rule.UpdatedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored analyze rule {rule.RuleId} for {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store analyze rule {rule.RuleId}");
                return false;
            }
        }

        /// <summary>
        /// Gets analyze rules for a partition (tenant or "global")
        /// </summary>
        public async Task<List<AnalyzeRule>> GetAnalyzeRulesAsync(string partitionKey)
        {
            if (partitionKey != "global")
                SecurityValidator.EnsureValidGuid(partitionKey, nameof(partitionKey));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AnalyzeRules);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var rules = new List<AnalyzeRule>();
                await foreach (var entity in query)
                {
                    rules.Add(MapToAnalyzeRule(entity));
                }

                return rules;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get analyze rules for {partitionKey}");
                return new List<AnalyzeRule>();
            }
        }

        /// <summary>
        /// Deletes an analyze rule. Idempotent: a 404 (the rule was already gone) counts
        /// as success so the sunset orchestration in
        /// <see cref="AnalyzeRuleService.EnsureBuiltInRulesSeededAsync"/> can retry without
        /// flipping its happy-path on the missing-row condition.
        /// </summary>
        public async Task<bool> DeleteAnalyzeRuleAsync(string tenantId, string ruleId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AnalyzeRules);
                await tableClient.DeleteEntityAsync(tenantId, ruleId);
                return true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete analyze rule {ruleId}");
                return false;
            }
        }

        // ===== RULE EXISTENCE CHECKS (Point Queries) =====

        /// <summary>
        /// Checks if an analyze rule exists via point query (PartitionKey + RowKey).
        /// O(1) in Table Storage — no partition scan needed.
        /// </summary>
        public async Task<bool> AnalyzeRuleExistsAsync(string partitionKey, string ruleId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AnalyzeRules);
                await tableClient.GetEntityAsync<TableEntity>(partitionKey, ruleId, select: new[] { "PartitionKey" });
                return true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if a gather rule exists via point query (PartitionKey + RowKey).
        /// O(1) in Table Storage — no partition scan needed.
        /// </summary>
        public async Task<bool> GatherRuleExistsAsync(string partitionKey, string ruleId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GatherRules);
                await tableClient.GetEntityAsync<TableEntity>(partitionKey, ruleId, select: new[] { "PartitionKey" });
                return true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        private AnalyzeRule MapToAnalyzeRule(TableEntity entity)
        {
            var derivedFrom = entity.GetString("DerivedFromTemplateRuleId");
            return new AnalyzeRule
            {
                RuleId = entity.RowKey,
                Title = entity.GetString("Title") ?? string.Empty,
                Description = entity.GetString("Description") ?? string.Empty,
                Severity = entity.GetString("Severity") ?? string.Empty,
                Category = entity.GetString("Category") ?? string.Empty,
                Version = entity.GetString("Version") ?? "1.0.0",
                Author = entity.GetString("Author") ?? "Autopilot Monitor",
                Enabled = entity.GetBoolean("Enabled") ?? true,
                IsBuiltIn = entity.GetBoolean("IsBuiltIn") ?? false,
                IsCommunity = entity.GetBoolean("IsCommunity") ?? false,
                // Absent column (pre-existing rows) → null → treated as "embedded" by RuleProvenance.
                Provenance = string.IsNullOrEmpty(entity.GetString("Provenance")) ? null : entity.GetString("Provenance"),
                Trigger = entity.GetString("Trigger") ?? "single",
                Preconditions = DeserializeJson<List<RulePrecondition>>(entity.GetString("PreconditionsJson")),
                Conditions = DeserializeJson<List<RuleCondition>>(entity.GetString("ConditionsJson")),
                BaseConfidence = entity.GetInt32("BaseConfidence") ?? 50,
                ConfidenceFactors = DeserializeJson<List<ConfidenceFactor>>(entity.GetString("ConfidenceFactorsJson")),
                ConfidenceThreshold = entity.GetInt32("ConfidenceThreshold") ?? 40,
                Explanation = entity.GetString("Explanation") ?? string.Empty,
                Remediation = DeserializeJson<List<RemediationStep>>(entity.GetString("RemediationJson")),
                RelatedDocs = DeserializeJson<List<RelatedDoc>>(entity.GetString("RelatedDocsJson")),
                Tags = DeserializeJsonArray(entity.GetString("TagsJson")),
                TemplateVariables = DeserializeJson<List<TemplateVariable>>(entity.GetString("TemplateVariablesJson")),
                DerivedFromTemplateRuleId = string.IsNullOrEmpty(derivedFrom) ? null : derivedFrom,
                MarkSessionAsFailedDefault = entity.GetBoolean("MarkSessionAsFailedDefault") ?? false,
                NotifyDefault = entity.GetBoolean("NotifyDefault") ?? false,
                NotifyChannelIds = DeserializeJson<List<string>>(entity.GetString("NotifyChannelIdsJson")),
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.UtcNow,
                UpdatedAt = entity.GetDateTimeOffset("UpdatedAt")?.UtcDateTime ?? DateTime.UtcNow
            };
        }

        // ===== IME LOG PATTERNS METHODS =====

        /// <summary>
        /// Stores or updates an IME log pattern
        /// PartitionKey: TenantId (or "global" for built-in), RowKey: PatternId
        /// </summary>
        public async Task<bool> StoreImeLogPatternAsync(ImeLogPattern pattern, string tenantId = "global")
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeLogPatterns);

                var entity = new TableEntity(tenantId, pattern.PatternId)
                {
                    ["Category"] = pattern.Category ?? string.Empty,
                    ["Pattern"] = pattern.Pattern ?? string.Empty,
                    ["Action"] = pattern.Action ?? string.Empty,
                    ["ParametersJson"] = JsonConvert.SerializeObject(pattern.Parameters ?? new Dictionary<string, string>()),
                    ["Enabled"] = pattern.Enabled,
                    ["Description"] = pattern.Description ?? string.Empty,
                    ["IsBuiltIn"] = pattern.IsBuiltIn
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored IME log pattern {pattern.PatternId} for {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store IME log pattern {pattern.PatternId}");
                return false;
            }
        }

        /// <summary>
        /// Gets IME log patterns for a partition (tenant or "global")
        /// </summary>
        public async Task<List<ImeLogPattern>> GetImeLogPatternsAsync(string partitionKey)
        {
            if (partitionKey != "global")
                SecurityValidator.EnsureValidGuid(partitionKey, nameof(partitionKey));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeLogPatterns);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var patterns = new List<ImeLogPattern>();
                await foreach (var entity in query)
                {
                    patterns.Add(MapToImeLogPattern(entity));
                }

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get IME log patterns for {partitionKey}");
                return new List<ImeLogPattern>();
            }
        }

        /// <summary>
        /// Deletes an IME log pattern
        /// </summary>
        public async Task<bool> DeleteImeLogPatternAsync(string tenantId, string patternId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeLogPatterns);
                await tableClient.DeleteEntityAsync(tenantId, patternId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete IME log pattern {patternId}");
                return false;
            }
        }

        private ImeLogPattern MapToImeLogPattern(TableEntity entity)
        {
            return new ImeLogPattern
            {
                PatternId = entity.RowKey,
                Category = entity.GetString("Category") ?? string.Empty,
                Pattern = entity.GetString("Pattern") ?? string.Empty,
                Action = entity.GetString("Action") ?? string.Empty,
                Parameters = DeserializeJson<Dictionary<string, string>>(entity.GetString("ParametersJson")),
                Enabled = entity.GetBoolean("Enabled") ?? true,
                Description = entity.GetString("Description") ?? string.Empty,
                IsBuiltIn = entity.GetBoolean("IsBuiltIn") ?? false
            };
        }

        // ===== VULNERABILITY REPORT METHODS =====

        /// <summary>
        // ===== VULNERABILITY REPORT METHODS =====

        /// <summary>
        /// Stores a vulnerability correlation report for a session.
        /// PK = {TenantId}_{SessionId}, RK = "report" (same pattern as RuleResults).
        /// Only called when there are actual findings — no empty reports stored.
        /// </summary>
        public async Task StoreVulnerabilityReportAsync(string tenantId, string sessionId, Dictionary<string, object> reportData)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VulnerabilityReports);
            var partitionKey = $"{tenantId}_{sessionId}";

            var scanSummary = reportData.ContainsKey("scan_summary")
                ? reportData["scan_summary"] as Dictionary<string, object>
                : null;

            var entity = new TableEntity(partitionKey, "report")
            {
                ["SessionId"] = sessionId,
                ["TenantId"] = tenantId,
                ["CreatedAt"] = DateTime.UtcNow.ToString("o"),
                ["OverallRisk"] = scanSummary?.GetValueOrDefault("overall_risk")?.ToString() ?? "none",
                ["TotalCvesFound"] = Convert.ToInt32(scanSummary?.GetValueOrDefault("total_cves_found") ?? 0),
                ["CriticalCves"] = Convert.ToInt32(scanSummary?.GetValueOrDefault("critical_cves") ?? 0),
                ["HighCves"] = Convert.ToInt32(scanSummary?.GetValueOrDefault("high_cves") ?? 0),
                ["KevMatches"] = Convert.ToInt32(scanSummary?.GetValueOrDefault("kev_matches") ?? 0),
                ["TotalScanned"] = Convert.ToInt32(scanSummary?.GetValueOrDefault("total_software_scanned") ?? 0),
                ["TotalMatched"] = Convert.ToInt32(scanSummary?.GetValueOrDefault("matched_to_cpe") ?? 0),
            };

            // Chunk ReportJson across multiple properties if it exceeds 30K chars
            var reportJson = JsonConvert.SerializeObject(reportData);
            foreach (var chunk in TableStorageChunking.ChunkProperty("ReportJson", reportJson))
                entity[chunk.Key] = chunk.Value;

            await tableClient.UpsertEntityAsync(entity);
            _logger.LogInformation("Stored vulnerability report for session {SessionId} (risk={Risk})", sessionId,
                scanSummary?.GetValueOrDefault("overall_risk")?.ToString() ?? "none");
        }

        /// <summary>
        /// Deletes the stored vulnerability report for a session.
        /// Used when a re-scan finds no vulnerabilities, to clear stale data.
        /// </summary>
        public async Task DeleteVulnerabilityReportAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VulnerabilityReports);
                var partitionKey = $"{tenantId}_{sessionId}";
                await tableClient.DeleteEntityAsync(partitionKey, "report");
                _logger.LogInformation("Deleted vulnerability report for session {SessionId}", sessionId);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted or never existed — nothing to do
            }
        }

        /// <summary>
        /// Gets the vulnerability correlation report for a session.
        /// Returns the deserialized report data, or null if no report exists.
        /// </summary>
        public async Task<Dictionary<string, object>?> GetVulnerabilityReportAsync(string tenantId, string sessionId)
        {
            var json = await GetVulnerabilityReportJsonAsync(tenantId, sessionId);
            if (json == null) return null;
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
        }

        /// <summary>
        /// Returns the raw ReportJson string without deserialization.
        /// Avoids Newtonsoft JToken → System.Text.Json serialization mismatch.
        /// </summary>
        public async Task<string?> GetVulnerabilityReportJsonAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VulnerabilityReports);
                var partitionKey = $"{tenantId}_{sessionId}";
                var response = await tableClient.GetEntityAsync<TableEntity>(partitionKey, "report");
                var entity = response?.Value;
                if (entity == null) return null;

                var reportJson = TableStorageChunking.ReassembleProperty(entity, "ReportJson", _logger, $"{partitionKey}/report");
                return string.IsNullOrEmpty(reportJson) ? null : reportJson;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        // ===== SOFTWARE INVENTORY METHODS =====

        /// <summary>
        /// Upserts software inventory entries for a tenant.
        /// PK = tenantId, RK = {normalizedVendor}:{normalizedProduct}:{normalizedVersion} (sanitized).
        /// If an entry already exists, increments SessionCount and updates LastSeenAt/LastSessionId.
        /// </summary>
        public async Task UpsertSoftwareInventoryAsync(string tenantId, List<Dictionary<string, object>> inventoryItems, string sessionId, Dictionary<string, string?>? cpeMappings = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SoftwareInventory);

                // Read all existing entities for this tenant to merge SessionCount
                var existingEntities = new Dictionary<string, TableEntity>(StringComparer.OrdinalIgnoreCase);
                var existingQuery = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");
                await foreach (var entity in existingQuery)
                {
                    existingEntities[entity.RowKey] = entity;
                }

                // Build the batch of entities to upsert (dictionary to deduplicate by RowKey)
                var entitiesToUpsert = new Dictionary<string, TableEntity>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in inventoryItems)
                {
                    var normalizedName = item.ContainsKey("normalizedName") ? item["normalizedName"]?.ToString() ?? "" : "";
                    var normalizedVendor = item.ContainsKey("normalizedPublisher") ? item["normalizedPublisher"]?.ToString() ?? "" : "";
                    var normalizedVersion = item.ContainsKey("normalizedVersion") ? item["normalizedVersion"]?.ToString() ?? "" : "";
                    var displayName = item.ContainsKey("displayName") ? item["displayName"]?.ToString() ?? "" : "";
                    var publisher = item.ContainsKey("publisher") ? item["publisher"]?.ToString() ?? "" : "";
                    var registrySource = item.ContainsKey("registrySource") ? item["registrySource"]?.ToString() ?? "" : "";
                    var normalizationConfidence = item.ContainsKey("normalizationConfidence") ? item["normalizationConfidence"]?.ToString() ?? "" : "";

                    var rowKey = SanitizeTableKey($"{normalizedVendor}:{normalizedName}:{normalizedVersion}");
                    if (string.IsNullOrWhiteSpace(rowKey) || rowKey == "::")
                        continue;

                    // Truncate RowKey to Table Storage limit (1KB)
                    if (rowKey.Length > 512)
                        rowKey = rowKey.Substring(0, 512);

                    var now = DateTime.UtcNow;
                    TableEntity entity;

                    if (existingEntities.TryGetValue(rowKey, out var existing))
                    {
                        // Update existing: increment SessionCount, update LastSeenAt
                        existing["SessionCount"] = (existing.GetInt32("SessionCount") ?? 0) + 1;
                        existing["LastSeenAt"] = now.ToString("o");
                        existing["LastSessionId"] = sessionId;

                        // Update CpeUri if we have a mapping and current is empty
                        if (cpeMappings != null && cpeMappings.TryGetValue(normalizedName, out var cpeUri) && !string.IsNullOrEmpty(cpeUri))
                        {
                            existing["CpeUri"] = cpeUri;
                        }

                        entity = existing;
                    }
                    else
                    {
                        // Create new entry
                        entity = new TableEntity(tenantId, rowKey)
                        {
                            ["DisplayName"] = displayName,
                            ["NormalizedName"] = normalizedName,
                            ["NormalizedVendor"] = normalizedVendor,
                            ["NormalizedVersion"] = normalizedVersion,
                            ["Publisher"] = publisher,
                            ["RegistrySource"] = registrySource,
                            ["NormalizationConfidence"] = normalizationConfidence,
                            ["FirstSeenAt"] = now.ToString("o"),
                            ["LastSeenAt"] = now.ToString("o"),
                            ["FirstSessionId"] = sessionId,
                            ["LastSessionId"] = sessionId,
                            ["SessionCount"] = 1,
                            ["CpeUri"] = cpeMappings != null && cpeMappings.TryGetValue(normalizedName, out var cpeUri) && !string.IsNullOrEmpty(cpeUri) ? cpeUri : ""
                        };
                    }

                    entitiesToUpsert[rowKey] = entity;
                }

                // Batch write (max 100 per batch, all same PK)
                var upsertList = entitiesToUpsert.Values.ToList();
                for (int i = 0; i < upsertList.Count; i += 100)
                {
                    var batch = upsertList.Skip(i).Take(100)
                        .Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e))
                        .ToList();

                    if (batch.Count > 0)
                    {
                        await tableClient.SubmitTransactionAsync(batch);
                    }
                }

                _logger.LogInformation("Upserted {Count} software inventory entries for tenant {TenantId}", upsertList.Count, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert software inventory for tenant {TenantId}", tenantId);
                throw;
            }
        }

        /// <summary>
        /// Gets all software inventory entries for a tenant.
        /// PK = tenantId. Returns all rows.
        /// </summary>
        public async Task<List<Dictionary<string, object>>> GetSoftwareInventoryAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SoftwareInventory);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var results = new List<Dictionary<string, object>>();
                await foreach (var entity in query)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "displayName", entity.GetString("DisplayName") ?? "" },
                        { "normalizedName", entity.GetString("NormalizedName") ?? "" },
                        { "normalizedVendor", entity.GetString("NormalizedVendor") ?? "" },
                        { "normalizedVersion", entity.GetString("NormalizedVersion") ?? "" },
                        { "publisher", entity.GetString("Publisher") ?? "" },
                        { "registrySource", entity.GetString("RegistrySource") ?? "" },
                        { "normalizationConfidence", entity.GetString("NormalizationConfidence") ?? "" },
                        { "cpeUri", entity.GetString("CpeUri") ?? "" },
                        { "firstSeenAt", entity.GetString("FirstSeenAt") ?? "" },
                        { "lastSeenAt", entity.GetString("LastSeenAt") ?? "" },
                        { "firstSessionId", entity.GetString("FirstSessionId") ?? "" },
                        { "lastSessionId", entity.GetString("LastSessionId") ?? "" },
                        { "sessionCount", entity.GetInt32("SessionCount") ?? 0 }
                    });
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get software inventory for tenant {TenantId}", tenantId);
                return new List<Dictionary<string, object>>();
            }
        }

        /// <summary>
        /// Sanitize a string for use as an Azure Table Storage key.
        /// Replaces /, \, #, ? with underscores.
        /// </summary>
        private static string SanitizeTableKey(string key)
        {
            return key
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace("#", "_")
                .Replace("?", "_");
        }

        // ===== CPE MAPPING SEED IMPORT =====

        /// <summary>
        /// <summary>
        /// Imports CPE seed mappings from a JSON string (fetched from GitHub).
        /// Deletes existing cpe_map_seed entries, then writes the new ones.
        /// Returns the number of entries imported.
        /// </summary>
        public async Task<int> ImportCpeMappingSeedFromJsonAsync(string json)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VulnerabilityCache);

            // Delete existing seed entries
            var existingEntities = tableClient.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'cpe_map_seed'",
                select: new[] { "PartitionKey", "RowKey" });

            await foreach (var entity in existingEntities)
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
            }

            // Parse the JSON
            var seed = System.Text.Json.JsonSerializer.Deserialize<CpeMappingSeed>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (seed?.Mappings == null || seed.Mappings.Count == 0)
                return 0;

            // Build entities
            var entities = new List<TableEntity>();
            foreach (var mapping in seed.Mappings)
            {
                var rowKey = SanitizeTableKey(
                    $"{(mapping.NormalizedVendor ?? "unknown")}:{(mapping.NormalizedProduct ?? "unknown")}".ToLowerInvariant());

                if (rowKey.Length > 512)
                    rowKey = rowKey.Substring(0, 512);

                entities.Add(new TableEntity("cpe_map_seed", rowKey)
                {
                    { "NormalizedVendor", mapping.NormalizedVendor ?? "" },
                    { "NormalizedProduct", mapping.NormalizedProduct ?? "" },
                    { "CpeVendor", mapping.CpeVendor ?? "" },
                    { "CpeProduct", CpeUriNormalizer.Normalize(mapping.CpeProduct) },
                    { "CpeUri", CpeUriNormalizer.Normalize(mapping.CpeUri) },
                    { "Category", mapping.Category ?? "" },
                    { "DisplayNamePatternsJson", System.Text.Json.JsonSerializer.Serialize(mapping.DisplayNamePatterns ?? new List<string>()) },
                    { "ExcludePatternsJson", System.Text.Json.JsonSerializer.Serialize(mapping.ExcludePatterns ?? new List<string>()) },
                    { "PublisherPatternsJson", System.Text.Json.JsonSerializer.Serialize(mapping.PublisherPatterns ?? new List<string>()) },
                    { "Source", "seed" },
                    { "ImportedAt", DateTime.UtcNow.ToString("o") }
                });
            }

            // Batch write (max 100 per batch, all same PK)
            for (int i = 0; i < entities.Count; i += 100)
            {
                var batch = entities.Skip(i).Take(100)
                    .Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e))
                    .ToList();

                if (batch.Count > 0)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                }
            }

            _logger.LogInformation("Imported {Count} CPE seed mapping entries from GitHub JSON into VulnerabilityCache table", entities.Count);
            return entities.Count;
        }

        /// <summary>
        /// Imports CPE community mappings from a JSON string (fetched from GitHub).
        /// Deletes existing cpe_map_community entries, then writes the new ones.
        /// Same format as the seed file (CpeMappingSeed with Mappings list).
        /// </summary>
        public async Task<int> ImportCpeCommunityMappingsFromJsonAsync(string json)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VulnerabilityCache);

            // Delete existing community entries
            var existingEntities = tableClient.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'cpe_map_community'",
                select: new[] { "PartitionKey", "RowKey" });

            await foreach (var entity in existingEntities)
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
            }

            // Parse the JSON (same format as seed)
            var seed = System.Text.Json.JsonSerializer.Deserialize<CpeMappingSeed>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (seed?.Mappings == null || seed.Mappings.Count == 0)
                return 0;

            // Build entities
            var entities = new List<TableEntity>();
            foreach (var mapping in seed.Mappings)
            {
                var rowKey = SanitizeTableKey(
                    $"{(mapping.NormalizedVendor ?? "unknown")}:{(mapping.NormalizedProduct ?? "unknown")}".ToLowerInvariant());

                if (rowKey.Length > 512)
                    rowKey = rowKey.Substring(0, 512);

                entities.Add(new TableEntity("cpe_map_community", rowKey)
                {
                    { "NormalizedVendor", mapping.NormalizedVendor ?? "" },
                    { "NormalizedProduct", mapping.NormalizedProduct ?? "" },
                    { "CpeVendor", mapping.CpeVendor ?? "" },
                    { "CpeProduct", mapping.CpeProduct ?? "" },
                    { "CpeUri", mapping.CpeUri ?? "" },
                    { "Category", mapping.Category ?? "community" },
                    { "DisplayNamePatternsJson", System.Text.Json.JsonSerializer.Serialize(mapping.DisplayNamePatterns ?? new List<string>()) },
                    { "ExcludePatternsJson", System.Text.Json.JsonSerializer.Serialize(mapping.ExcludePatterns ?? new List<string>()) },
                    { "PublisherPatternsJson", System.Text.Json.JsonSerializer.Serialize(mapping.PublisherPatterns ?? new List<string>()) },
                    { "Source", "community" },
                    { "ImportedAt", DateTime.UtcNow.ToString("o") }
                });
            }

            // Batch write (max 100 per batch, all same PK)
            for (int i = 0; i < entities.Count; i += 100)
            {
                var batch = entities.Skip(i).Take(100)
                    .Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e))
                    .ToList();

                if (batch.Count > 0)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                }
            }

            _logger.LogInformation("Imported {Count} CPE community mapping entries from GitHub JSON into VulnerabilityCache table", entities.Count);
            return entities.Count;
        }
    }
}
