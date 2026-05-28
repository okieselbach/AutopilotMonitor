using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing analyze rules (how to interpret collected events)
    /// Merges global built-in/community rules with tenant-specific custom rules.
    /// Enabled/disabled state for built-in and community rules is stored separately
    /// in the RuleStates table, so rule definitions can be updated centrally without
    /// losing per-tenant enabled/disabled preferences.
    /// </summary>
    /// <summary>
    /// Outcome of <see cref="AnalyzeRuleService.ProcessSunsetRuleAsync"/>. Callers use
    /// this to decide whether the seed cycle is fully consistent (only
    /// <see cref="Completed"/>) or whether a retry on the next cycle is needed (any
    /// other value). Order of the failure values mirrors the safe-state → GC → delete
    /// sequence — earlier failures indicate less state was changed.
    /// </summary>
    public enum SunsetOutcome
    {
        /// <summary>Safe-state, orphan-GC, and global-delete all succeeded.</summary>
        Completed,
        /// <summary>Safe-state (set global Enabled=false) failed. No GC or delete attempted. Rule keeps whatever Enabled value it had before — at worst this leaves it active, never silently flipping a tenant opt-out.</summary>
        SkippedOnSafeStateFailure,
        /// <summary>Safe-state succeeded, but the cross-tenant orphan-GC failed (per-row or enumeration). Global rule stays as Enabled=false so for the typical default-disabled sunset case the rule is already effectively dead; default-enabled sunsets are also safe because tenant opt-outs still win over the new Enabled=false global default until the GC retry deletes them next cycle.</summary>
        SkippedOnGcFailure,
        /// <summary>Safe-state + GC succeeded, but the global tombstone delete failed. Rule is effectively dead (no tenant overrides, global Enabled=false); next seed cycle will retry the tombstone.</summary>
        SkippedOnGlobalDeleteFailure,
    }

    public class AnalyzeRuleService
    {
        private readonly IRuleRepository _ruleRepo;
        private readonly ILogger<AnalyzeRuleService> _logger;
        private bool _seeded = false;

        public AnalyzeRuleService(IRuleRepository ruleRepo, ILogger<AnalyzeRuleService> logger)
        {
            _ruleRepo = ruleRepo;
            _logger = logger;
        }

        /// <summary>
        /// Drives the sunset sequence for a single built-in / community rule that is no
        /// longer in the code catalog. Three steps, executed in order, with strict
        /// "later step requires earlier success" semantics so a partial failure can
        /// never silently flip a tenant's enabled/disabled preference for the rule:
        /// <list type="number">
        ///   <item><b>Safe-state</b>: re-write the global row with
        ///     <c>Enabled = false</c> and <c>MarkSessionAsFailedDefault = false</c>.
        ///     Defends future default-ENABLED sunset rules: even if the next two
        ///     steps fail, the global default is now off, so tenants with
        ///     <c>RuleState{Enabled=false}</c> (explicit opt-outs) keep their opt-out
        ///     behaviour (override matches global default, both off).</item>
        ///   <item><b>Orphan GC</b>: delete every per-tenant <c>RuleState</c> row whose
        ///     RowKey matches the sunset rule. Skipped if safe-state failed — we never
        ///     destroy tenant state before the global default has been neutralised.</item>
        ///   <item><b>Tombstone</b>: delete the global rule row. Skipped if GC reported
        ///     any failures, so the rule remains in the sunset diff for the next seed
        ///     cycle's retry.</item>
        /// </list>
        /// </summary>
        /// <param name="rule">The current global rule object (will be mutated by the
        /// safe-state step; callers should not reuse it).</param>
        /// <returns>An outcome + count of orphan RuleState rows that this attempt
        /// actually deleted (always &gt;= 0). The count surfaces partial progress even
        /// on a <see cref="SunsetOutcome.SkippedOnGcFailure"/> result so operators can
        /// see the rule getting cleaned up across cycles.</returns>
        public async Task<(SunsetOutcome outcome, int orphanStatesGcd)> ProcessSunsetRuleAsync(AnalyzeRule rule)
        {
            // Step 1: safe-state. Neutralise the global default first so any orphan
            // state we fail to clean below is harmless.
            rule.Enabled = false;
            rule.MarkSessionAsFailedDefault = false;
            rule.UpdatedAt = DateTime.UtcNow;
            var safeStateOk = await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
            if (!safeStateOk)
            {
                _logger.LogWarning(
                    "Sunset safe-state write failed for {RuleId}; skipping GC + tombstone, will retry next seed cycle",
                    rule.RuleId);
                return (SunsetOutcome.SkippedOnSafeStateFailure, 0);
            }

            // Step 2: orphan-GC.
            var (gcDeleted, gcFailed) = await _ruleRepo.DeleteRuleStatesForRuleIdAcrossTenantsAsync(rule.RuleId);
            if (gcFailed != 0)
            {
                _logger.LogWarning(
                    "Sunset GC partial failure for {RuleId} (deleted={Deleted}, failed={Failed}); rule is now safe-stated (Enabled=false) globally, will retry GC + tombstone next seed cycle",
                    rule.RuleId, gcDeleted, gcFailed);
                return (SunsetOutcome.SkippedOnGcFailure, gcDeleted);
            }

            // Step 3: tombstone.
            var globalDeleted = await _ruleRepo.DeleteAnalyzeRuleAsync("global", rule.RuleId);
            if (!globalDeleted)
            {
                _logger.LogWarning(
                    "Sunset GC clean but global delete failed for {RuleId}; rule is effectively dead (Enabled=false + no tenant overrides) but the row will be retried next seed cycle",
                    rule.RuleId);
                return (SunsetOutcome.SkippedOnGlobalDeleteFailure, gcDeleted);
            }

            return (SunsetOutcome.Completed, gcDeleted);
        }

        /// <summary>
        /// Gets all active analyze rules for a tenant (enabled only)
        /// </summary>
        public async Task<List<AnalyzeRule>> GetActiveRulesForTenantAsync(string tenantId)
        {
            var rules = await GetAllRulesForTenantAsync(tenantId);
            return rules.Where(r => r.Enabled).ToList();
        }

        /// <summary>
        /// Gets all analyze rules for a tenant (including disabled) for portal display
        /// </summary>
        public async Task<List<AnalyzeRule>> GetAllRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            // Global rules: built-in + community (single source of truth for definitions)
            var globalRules = await _ruleRepo.GetAnalyzeRulesAsync("global");

            // Tenant rules: only custom rules (IsBuiltIn=false, IsCommunity=false)
            var tenantRules = await _ruleRepo.GetAnalyzeRulesAsync(tenantId);
            var customRules = tenantRules.Where(r => !r.IsBuiltIn && !r.IsCommunity).ToList();

            // Per-tenant enabled/disabled states for global rules
            var ruleStates = await _ruleRepo.GetRuleStatesAsync(tenantId);

            var mergedRules = new List<AnalyzeRule>();

            // Apply tenant state overrides to global rules
            foreach (var rule in globalRules)
            {
                if (ruleStates.TryGetValue(rule.RuleId, out var state))
                {
                    rule.Enabled = state.Enabled;
                    rule.MarkSessionAsFailed = state.MarkSessionAsFailed;
                }
                mergedRules.Add(rule);
            }

            // Tenant custom rules carry their own MarkSessionAsFailedDefault; no override needed
            // since the tenant already fully owns the rule definition.
            mergedRules.AddRange(customRules);

            return mergedRules;
        }

        /// <summary>
        /// Creates a custom analyze rule for a tenant.
        /// Throws if a rule with the same ID already exists (global or tenant partition).
        /// Uses point queries (O(1)) instead of loading all rules.
        /// </summary>
        public async Task<bool> CreateRuleAsync(string tenantId, AnalyzeRule rule)
        {
            if (await _ruleRepo.AnalyzeRuleExistsAsync("global", rule.RuleId)
                || await _ruleRepo.AnalyzeRuleExistsAsync(tenantId, rule.RuleId))
            {
                throw new InvalidOperationException($"A rule with ID '{rule.RuleId}' already exists.");
            }

            rule.IsBuiltIn = false;
            rule.IsCommunity = false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = DateTime.UtcNow;
            return await _ruleRepo.StoreAnalyzeRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Updates an analyze rule.
        /// For built-in/community rules: only the enabled/disabled state is stored (per tenant).
        /// For custom rules: the full rule is updated in the tenant partition.
        /// </summary>
        public async Task<bool> UpdateRuleAsync(string tenantId, AnalyzeRule rule)
        {
            if (rule.IsBuiltIn || rule.IsCommunity)
            {
                var state = new RuleState
                {
                    Enabled = rule.Enabled,
                    MarkSessionAsFailed = rule.MarkSessionAsFailed
                };
                return await _ruleRepo.StoreRuleStateAsync(tenantId, rule.RuleId, state);
            }

            rule.UpdatedAt = DateTime.UtcNow;
            return await _ruleRepo.StoreAnalyzeRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Creates a tenant custom rule from a template rule, substituting template variables
        /// with tenant-specific values. The original template remains disabled for the tenant.
        /// </summary>
        public async Task<AnalyzeRule> CreateFromTemplateAsync(
            string tenantId,
            string templateRuleId,
            Dictionary<string, string> variableValues)
        {
            var allRules = await GetAllRulesForTenantAsync(tenantId);
            var template = allRules.FirstOrDefault(r => r.RuleId == templateRuleId);

            if (template == null)
                throw new InvalidOperationException($"Rule '{templateRuleId}' not found.");

            if (template.TemplateVariables == null || template.TemplateVariables.Count == 0)
                throw new InvalidOperationException($"Rule '{templateRuleId}' is not a template rule.");

            // Check if a custom copy already exists for this tenant (by lineage)
            var existingCopy = allRules.FirstOrDefault(r => r.DerivedFromTemplateRuleId == templateRuleId);
            if (existingCopy != null)
                throw new InvalidOperationException($"A custom copy of '{templateRuleId}' already exists: '{existingCopy.RuleId}'.");

            // Check for RuleId collision via point query (e.g., someone manually created a rule with the same ID)
            var targetRuleId = $"{templateRuleId}-CUSTOM";
            if (await _ruleRepo.AnalyzeRuleExistsAsync("global", targetRuleId)
                || await _ruleRepo.AnalyzeRuleExistsAsync(tenantId, targetRuleId))
                throw new InvalidOperationException($"A rule with ID '{targetRuleId}' already exists. Delete or rename it first.");

            // Validate all template variables have values
            foreach (var tv in template.TemplateVariables)
            {
                if (!variableValues.TryGetValue(tv.Name, out var val) || string.IsNullOrWhiteSpace(val))
                    throw new ArgumentException($"Missing required value for template variable '{tv.Name}'.");
            }

            // Deep-clone the template
            var customRule = JsonConvert.DeserializeObject<AnalyzeRule>(
                JsonConvert.SerializeObject(template))
                ?? throw new InvalidOperationException("Failed to clone template rule.");

            customRule.RuleId = $"{templateRuleId}-CUSTOM";
            customRule.IsBuiltIn = false;
            customRule.IsCommunity = false;
            customRule.Enabled = true;
            customRule.DerivedFromTemplateRuleId = templateRuleId;
            customRule.TemplateVariables = new List<TemplateVariable>();
            customRule.CreatedAt = DateTime.UtcNow;
            customRule.UpdatedAt = DateTime.UtcNow;

            // Substitute variable values into conditions
            foreach (var tv in template.TemplateVariables)
            {
                var userValue = variableValues[tv.Name];

                if (tv.ConditionIndex < 0 || tv.ConditionIndex >= customRule.Conditions.Count)
                {
                    _logger.LogWarning("Template variable '{Name}' has invalid conditionIndex {Index}", tv.Name, tv.ConditionIndex);
                    continue;
                }

                var condition = customRule.Conditions[tv.ConditionIndex];
                switch (tv.Field?.ToLowerInvariant())
                {
                    case "value": condition.Value = userValue; break;
                    case "eventtype": condition.EventType = userValue; break;
                    case "datafield": condition.DataField = userValue; break;
                    case "eventafiltervalue": condition.EventAFilterValue = userValue; break;
                    default:
                        _logger.LogWarning("Template variable '{Name}' has unknown field '{Field}'", tv.Name, tv.Field);
                        break;
                }
            }

            // Store the custom rule in the tenant partition
            var success = await _ruleRepo.StoreAnalyzeRuleAsync(customRule, tenantId);
            if (!success)
                throw new InvalidOperationException("Failed to store custom rule.");

            // Ensure the template rule is disabled for this tenant
            await _ruleRepo.StoreRuleStateAsync(tenantId, templateRuleId, new RuleState { Enabled = false });

            _logger.LogInformation("Created custom rule '{CustomRuleId}' from template '{TemplateRuleId}' for tenant '{TenantId}'",
                customRule.RuleId, templateRuleId, tenantId);

            return customRule;
        }

        /// <summary>
        /// Deletes an analyze rule.
        /// For built-in/community rules: removes the tenant's state override (resets to rule default).
        /// For custom rules: deletes the rule from the tenant partition.
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string tenantId, AnalyzeRule rule)
        {
            if (rule.IsBuiltIn || rule.IsCommunity)
            {
                return await _ruleRepo.DeleteRuleStateAsync(tenantId, rule.RuleId);
            }

            return await _ruleRepo.DeleteAnalyzeRuleAsync(tenantId, rule.RuleId);
        }

        /// <summary>
        /// Re-imports all built-in analyze rules into the global partition. The previous
        /// "delete all built-ins, then re-import" approach is preserved for the rules
        /// that survive the new catalog; for SUNSET rules (existed in DB as built-in but
        /// no longer in <see cref="BuiltInAnalyzeRules"/>) the sequence is inverted —
        /// orphan-state GC first, global delete only on a clean GC — so a partial
        /// failure leaves the rule in the diff to be retried on the next seed cycle.
        /// <para>
        /// Returns <c>(deleted, written, orphanStatesGcd)</c> so the admin endpoint can
        /// surface the cleanup count to the operator.
        /// </para>
        /// </summary>
        public async Task<(int deleted, int written, int orphanStatesGcd)> ReseedBuiltInRulesAsync()
        {
            _logger.LogInformation("Reseeding built-in analyze rules (full re-import)...");

            var existingGlobalRules = await _ruleRepo.GetAnalyzeRulesAsync("global");
            var builtInRules = BuiltInAnalyzeRules.GetAll();
            var newCatalogIds = builtInRules.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

            // Survivors: existing built-ins that ARE in the new catalog — these get the
            // legacy "delete then re-write" treatment (preserves UpdatedAt etc.).
            // Sunset:   existing built-ins NOT in the new catalog — these get the
            // retry-safe "GC first, global delete on clean GC" treatment.
            var survivors = existingGlobalRules.Where(r => r.IsBuiltIn && newCatalogIds.Contains(r.RuleId)).ToList();
            var sunset = existingGlobalRules.Where(r => r.IsBuiltIn && !newCatalogIds.Contains(r.RuleId)).ToList();

            var deleted = 0;

            // Survivor path: same as before — delete then write.
            foreach (var rule in survivors)
            {
                await _ruleRepo.DeleteAnalyzeRuleAsync("global", rule.RuleId);
                deleted++;
            }

            // Sunset path: full safe-state -> GC -> tombstone sequence via the shared
            // helper. Each rule that reaches Completed contributes one to `deleted`.
            // Partial-failure paths leave the global row in place for the next cycle.
            var orphanStatesGcd = 0;
            var outcomeCounts = new Dictionary<SunsetOutcome, int>();
            foreach (var sunsetRule in sunset)
            {
                var (outcome, gcd) = await ProcessSunsetRuleAsync(sunsetRule);
                orphanStatesGcd += gcd;
                outcomeCounts[outcome] = outcomeCounts.GetValueOrDefault(outcome) + 1;
                if (outcome == SunsetOutcome.Completed)
                    deleted++;
            }

            _logger.LogInformation(
                "Reseed analyze rules: {Deleted} rows deleted, {Survivors} survivors, {Sunset} sunset (outcomes {Outcomes}, orphan states removed={OrphanGcd})",
                deleted, survivors.Count, sunset.Count, FormatOutcomes(outcomeCounts), orphanStatesGcd);

            foreach (var rule in builtInRules)
            {
                await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
            }
            _logger.LogInformation($"Written {builtInRules.Count} built-in analyze rules from code");

            _seeded = false;

            return (deleted, builtInRules.Count, orphanStatesGcd);
        }

        /// <summary>
        /// Seeds built-in analyze rules if not already done. Also updates existing
        /// built-in rules when the code definitions change (version or key fields),
        /// removes built-in rules that no longer exist in the code (sunset), and GCs
        /// per-tenant RuleState rows for sunset rules so no orphan overrides linger.
        /// </summary>
        private async Task EnsureBuiltInRulesSeededAsync()
        {
            if (_seeded) return;

            var existingRules = await _ruleRepo.GetAnalyzeRulesAsync("global");
            var builtInRules = BuiltInAnalyzeRules.GetAll();
            var newCatalogIds = builtInRules.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

            if (existingRules.Count == 0)
            {
                _logger.LogInformation("Seeding built-in analyze rules...");
                foreach (var rule in builtInRules)
                {
                    await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
                }
                _logger.LogInformation($"Seeded {builtInRules.Count} built-in analyze rules");
            }
            else
            {
                var existingLookup = existingRules.ToDictionary(r => r.RuleId, r => r);
                var updated = 0;

                foreach (var rule in builtInRules)
                {
                    if (existingLookup.TryGetValue(rule.RuleId, out var existing))
                    {
                        if (existing.Version != rule.Version
                            || existing.Title != rule.Title
                            || existing.Description != rule.Description
                            || existing.Severity != rule.Severity
                            || existing.Trigger != rule.Trigger)
                        {
                            await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
                            updated++;
                        }
                    }
                    else
                    {
                        // New built-in rule added in code
                        await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    _logger.LogInformation($"Updated {updated} built-in analyze rules from code definitions");
                }

                // Sunset detection: rules that existed as built-in but are no longer in
                // the code catalog. ProcessSunsetRuleAsync handles the safe-state -> GC
                // -> tombstone sequence per rule with strict ordering guarantees (see
                // its XML doc). Any non-Completed outcome means there's still work to
                // do — we leave _seeded = false so the next GetAllRulesForTenantAsync
                // call retries the seed without waiting for a process restart.
                var sunsetRules = existingRules
                    .Where(r => r.IsBuiltIn && !newCatalogIds.Contains(r.RuleId))
                    .ToList();

                var allSunsetsClean = true;
                if (sunsetRules.Count > 0)
                {
                    var orphanStatesGcd = 0;
                    var outcomeCounts = new Dictionary<SunsetOutcome, int>();
                    foreach (var sunset in sunsetRules)
                    {
                        var (outcome, gcd) = await ProcessSunsetRuleAsync(sunset);
                        orphanStatesGcd += gcd;
                        outcomeCounts[outcome] = outcomeCounts.GetValueOrDefault(outcome) + 1;
                        if (outcome != SunsetOutcome.Completed)
                            allSunsetsClean = false;
                    }
                    _logger.LogInformation(
                        "Sunset {RuleCount} built-in analyze rule(s); orphan RuleState rows GC'd {OrphanCount}; outcomes {Outcomes}",
                        sunsetRules.Count, orphanStatesGcd, FormatOutcomes(outcomeCounts));
                }

                // Only flip the seed-once flag when EVERY sunset rule reached the
                // Completed terminal. If any was partial, the next call will retry —
                // important on long-running Function-App instances that don't recycle
                // often. (Survivors / new-rule writes above are idempotent: they all
                // go through Upsert and don't churn DB state on retry.)
                if (!allSunsetsClean)
                {
                    _logger.LogInformation("One or more sunset rules did not complete; leaving _seeded=false so the next GetAllRulesForTenantAsync retries.");
                    return;
                }
            }

            _seeded = true;
        }

        /// <summary>
        /// Compact log-friendly rendering of the sunset-outcome histogram. Inline so
        /// the LogInformation site stays readable. Order matches the SunsetOutcome enum
        /// (Completed first, then failures in execution-step order).
        /// </summary>
        private static string FormatOutcomes(Dictionary<SunsetOutcome, int> counts)
        {
            return string.Join(", ",
                Enum.GetValues<SunsetOutcome>()
                    .Where(o => counts.GetValueOrDefault(o) > 0)
                    .Select(o => $"{o}={counts[o]}"));
        }
    }
}
