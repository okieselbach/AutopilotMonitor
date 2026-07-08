using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing gather rules (what data the agent should collect)
    /// Merges global built-in/community rules with tenant-specific custom rules.
    /// Enabled/disabled state for built-in and community rules is stored separately
    /// in the RuleStates table, so rule definitions can be updated centrally without
    /// losing per-tenant enabled/disabled preferences.
    /// </summary>
    public class GatherRuleService
    {
        private readonly IRuleRepository _ruleRepo;
        private readonly ILogger<GatherRuleService> _logger;
        private bool _seeded = false;

        // Cached set of currently-shipped built-in gather rule IDs from
        // <see cref="BuiltInGatherRules.GetAll"/>. Used by the runtime sunset filter in
        // <see cref="GetAllRulesForTenantAsync"/> to hide rules whose code definition has been
        // removed but whose DB row hasn't been tombstoned yet (sunset GC may still be retrying).
        // Mirrors AnalyzeRuleService.LiveCatalogIds — same lazy-cache rationale (GetAll()
        // re-deserialises the embedded JSON on every call).
        private HashSet<string>? _liveCatalogIds;
        private readonly object _liveCatalogLock = new();

        public GatherRuleService(IRuleRepository ruleRepo, ILogger<GatherRuleService> logger)
        {
            _ruleRepo = ruleRepo;
            _logger = logger;
        }

        /// <summary>
        /// Lazy accessor for the currently-shipped built-in gather rule ID set. See
        /// AnalyzeRuleService.LiveCatalogIds for the caching rationale.
        /// </summary>
        private HashSet<string> LiveCatalogIds
        {
            get
            {
                if (_liveCatalogIds != null) return _liveCatalogIds;
                lock (_liveCatalogLock)
                {
                    _liveCatalogIds ??= BuiltInGatherRules.GetAll()
                        .Select(r => r.RuleId)
                        .ToHashSet(StringComparer.Ordinal);
                }
                return _liveCatalogIds;
            }
        }

        /// <summary>
        /// Gets all active gather rules for a tenant (enabled only)
        /// </summary>
        public async Task<List<GatherRule>> GetActiveRulesForTenantAsync(string tenantId)
        {
            var rules = await GetAllRulesForTenantAsync(tenantId);
            return rules.Where(r => r.Enabled).ToList();
        }

        /// <summary>
        /// Gets all gather rules for a tenant (including disabled) for portal display
        /// </summary>
        public async Task<List<GatherRule>> GetAllRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            // Global rules: built-in + community (single source of truth for definitions)
            var globalRules = await _ruleRepo.GetGatherRulesAsync("global");

            // Tenant rules: only custom rules (IsBuiltIn=false, IsCommunity=false)
            var tenantRules = await _ruleRepo.GetGatherRulesAsync(tenantId);
            var customRules = tenantRules.Where(r => !r.IsBuiltIn && !r.IsCommunity).ToList();

            // Per-tenant enabled/disabled states for global rules
            var ruleStates = await _ruleRepo.GetRuleStatesAsync(tenantId);

            var mergedRules = new List<GatherRule>();

            // Apply tenant state overrides to global rules.
            // MarkSessionAsFailed is analyze-rule-only; gather rules ignore it.
            //
            // Runtime sunset filter (mirrors AnalyzeRuleService): a built-in / community rule
            // removed from the code catalog stays in the global table until the sunset GC
            // tombstones it. If the GC is mid-retry, a surviving RuleState{Enabled=true} would
            // re-enable the rule via the override below — the "resurrected zombie" the sunset
            // path prevents. Hide rules whose code definition is gone regardless of any override;
            // the DB row stays so the next seed cycle still finds it in the sunset diff.
            var liveCatalog = LiveCatalogIds;
            foreach (var rule in globalRules)
            {
                // GitHub-ahead rows (reseeded, not yet in this binary's catalog) are exempt — they are
                // legitimately present, not sunset-pending, so the catalog filter must not hide them.
                if ((rule.IsBuiltIn || rule.IsCommunity)
                    && !liveCatalog.Contains(rule.RuleId)
                    && !RuleProvenance.IsGitHubAhead(rule.Provenance))
                    continue; // sunset-pending: definition removed, GC not yet completed

                if (ruleStates.TryGetValue(rule.RuleId, out var state))
                    rule.Enabled = state.Enabled;
                mergedRules.Add(rule);
            }

            // Add tenant-specific custom rules
            mergedRules.AddRange(customRules);

            return mergedRules;
        }

        /// <summary>
        /// Creates a custom gather rule for a tenant.
        /// Throws if a rule with the same ID already exists (global or tenant partition).
        /// Uses point queries (O(1)) instead of loading all rules.
        /// </summary>
        public async Task<bool> CreateRuleAsync(string tenantId, GatherRule rule)
        {
            if (await _ruleRepo.GatherRuleExistsAsync("global", rule.RuleId)
                || await _ruleRepo.GatherRuleExistsAsync(tenantId, rule.RuleId))
            {
                throw new InvalidOperationException($"A rule with ID '{rule.RuleId}' already exists.");
            }

            rule.IsBuiltIn = false;
            rule.IsCommunity = false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = DateTime.UtcNow;
            return await _ruleRepo.StoreGatherRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Updates a gather rule.
        /// For built-in/community rules: only the enabled/disabled state is stored (per tenant).
        /// For custom rules: the full rule is updated in the tenant partition.
        /// The rule type is determined from existing data (not the incoming payload) to prevent
        /// partial payloads (e.g. toggle requests with only { enabled: bool }) from being
        /// misclassified and overwriting rule data.
        /// </summary>
        public async Task<bool> UpdateRuleAsync(string tenantId, GatherRule rule)
        {
            // Always check global rules for type determination — the incoming payload
            // may not include isBuiltIn/isCommunity (e.g. toggle requests).
            var globalRules = await _ruleRepo.GetGatherRulesAsync("global");
            var globalRule = globalRules.FirstOrDefault(r => r.RuleId == rule.RuleId);

            if (globalRule != null && (globalRule.IsBuiltIn || globalRule.IsCommunity))
            {
                // Built-in/community rule: only persist enabled state per tenant
                return await _ruleRepo.StoreRuleStateAsync(tenantId, rule.RuleId, new RuleState { Enabled = rule.Enabled });
            }

            // Ensure custom rules keep correct flags (the incoming payload may
            // omit them, causing them to default to IsBuiltIn=true in the model).
            rule.IsBuiltIn = false;
            rule.IsCommunity = false;
            rule.UpdatedAt = DateTime.UtcNow;
            return await _ruleRepo.StoreGatherRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Deletes a gather rule.
        /// For built-in/community rules: removes the tenant's state override (resets to rule default).
        /// For custom rules: deletes the rule from the tenant partition.
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string tenantId, GatherRule rule)
        {
            if (rule.IsBuiltIn || rule.IsCommunity)
            {
                return await _ruleRepo.DeleteRuleStateAsync(tenantId, rule.RuleId);
            }

            return await _ruleRepo.DeleteGatherRuleAsync(tenantId, rule.RuleId);
        }

        /// <summary>
        /// Drives the sunset sequence for a single built-in / community gather rule that is no
        /// longer in the code catalog. Mirror of <c>AnalyzeRuleService.ProcessSunsetRuleAsync</c>
        /// (minus the analyze-only MarkSessionAsFailedDefault): three ordered steps with strict
        /// "later step requires earlier success" semantics so a partial failure can never leave a
        /// tenant's enabled/disabled preference pointing at a resurrected rule.
        /// <list type="number">
        ///   <item><b>Safe-state</b>: re-write the global row with <c>Enabled=false</c>.</item>
        ///   <item><b>Orphan GC</b>: delete every per-tenant <c>RuleState</c> row for the ruleId
        ///     (shared RuleStates table, keyed by ruleId). Skipped if safe-state failed.</item>
        ///   <item><b>Tombstone</b>: delete the global rule row. Skipped if GC reported failures,
        ///     so the rule stays in the sunset diff for the next cycle's retry.</item>
        /// </list>
        /// </summary>
        public async Task<(SunsetOutcome outcome, int orphanStatesGcd)> ProcessSunsetGatherRuleAsync(GatherRule rule)
        {
            // Step 1: safe-state. Neutralise the global default first so any orphan state we fail
            // to clean below is harmless.
            rule.Enabled = false;
            rule.UpdatedAt = DateTime.UtcNow;
            var safeStateOk = await _ruleRepo.StoreGatherRuleAsync(rule, "global");
            if (!safeStateOk)
            {
                _logger.LogWarning(
                    "Sunset safe-state write failed for gather rule {RuleId}; skipping GC + tombstone, will retry next seed cycle",
                    rule.RuleId);
                return (SunsetOutcome.SkippedOnSafeStateFailure, 0);
            }

            // Step 2: orphan-GC (cross-tenant RuleState rows for this ruleId).
            var (gcDeleted, gcFailed) = await _ruleRepo.DeleteRuleStatesForRuleIdAcrossTenantsAsync(rule.RuleId);
            if (gcFailed != 0)
            {
                _logger.LogWarning(
                    "Sunset GC partial failure for gather rule {RuleId} (deleted={Deleted}, failed={Failed}); rule is safe-stated (Enabled=false) globally, will retry GC + tombstone next seed cycle",
                    rule.RuleId, gcDeleted, gcFailed);
                return (SunsetOutcome.SkippedOnGcFailure, gcDeleted);
            }

            // Step 3: tombstone.
            var globalDeleted = await _ruleRepo.DeleteGatherRuleAsync("global", rule.RuleId);
            if (!globalDeleted)
            {
                _logger.LogWarning(
                    "Sunset GC clean but global delete failed for gather rule {RuleId}; rule is effectively dead (Enabled=false + no tenant overrides) but the row will be retried next seed cycle",
                    rule.RuleId);
                return (SunsetOutcome.SkippedOnGlobalDeleteFailure, gcDeleted);
            }

            return (SunsetOutcome.Completed, gcDeleted);
        }

        /// <summary>
        /// Re-imports all built-in gather rules into the global partition. Survivors (still in the
        /// code catalog) get the legacy delete-then-rewrite; sunset rules (no longer shipped) get
        /// the retry-safe safe-state → GC → tombstone sequence so per-tenant RuleState orphans are
        /// cleaned. Mirror of <c>AnalyzeRuleService.ReseedBuiltInRulesAsync</c>. Tenant RuleStates
        /// for surviving rules are preserved. Returns <c>(deleted, written, orphanStatesGcd)</c>.
        /// </summary>
        public async Task<(int deleted, int written, int orphanStatesGcd)> ReseedBuiltInRulesAsync()
        {
            _logger.LogInformation("Reseeding built-in gather rules (full re-import)...");

            var existingGlobalRules = await _ruleRepo.GetGatherRulesAsync("global");
            var builtInRules = BuiltInGatherRules.GetAll();
            var newCatalogIds = builtInRules.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);
            foreach (var r in builtInRules) r.Provenance = RuleProvenance.Embedded;

            var survivors = existingGlobalRules.Where(r => r.IsBuiltIn && newCatalogIds.Contains(r.RuleId)).ToList();
            // GitHub-ahead rows are exempt: this embedded reseed is not authoritative for them.
            var sunset = existingGlobalRules.Where(r => r.IsBuiltIn && !newCatalogIds.Contains(r.RuleId)
                                                        && !RuleProvenance.IsGitHubAhead(r.Provenance)).ToList();

            var deleted = 0;

            // Survivor path: delete then re-write (legacy behaviour).
            foreach (var rule in survivors)
            {
                await _ruleRepo.DeleteGatherRuleAsync("global", rule.RuleId);
                deleted++;
            }

            // Sunset path: safe-state -> GC -> tombstone via the shared helper.
            var orphanStatesGcd = 0;
            var outcomeCounts = new Dictionary<SunsetOutcome, int>();
            foreach (var sunsetRule in sunset)
            {
                var (outcome, gcd) = await ProcessSunsetGatherRuleAsync(sunsetRule);
                orphanStatesGcd += gcd;
                outcomeCounts[outcome] = outcomeCounts.GetValueOrDefault(outcome) + 1;
                if (outcome == SunsetOutcome.Completed)
                    deleted++;
            }

            _logger.LogInformation(
                "Reseed gather rules: {Deleted} rows deleted, {Survivors} survivors, {Sunset} sunset (outcomes {Outcomes}, orphan states removed={OrphanGcd})",
                deleted, survivors.Count, sunset.Count, FormatOutcomes(outcomeCounts), orphanStatesGcd);

            foreach (var rule in builtInRules)
            {
                await _ruleRepo.StoreGatherRuleAsync(rule, "global");
            }
            _logger.LogInformation($"Written {builtInRules.Count} built-in gather rules from code");

            _seeded = false;

            return (deleted, builtInRules.Count, orphanStatesGcd);
        }

        /// <summary>
        /// Seeds built-in gather rules if not already done.
        /// Also updates existing built-in rules when the code definitions change (version or key fields).
        /// </summary>
        private async Task EnsureBuiltInRulesSeededAsync()
        {
            if (_seeded) return;

            var existingRules = await _ruleRepo.GetGatherRulesAsync("global");
            var builtInRules = BuiltInGatherRules.GetAll();
            var newCatalogIds = builtInRules.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);
            // Everything the embedded binary ships is embedded-provenance. This is what lets the
            // sunset tell "removed from the binary" apart from a GitHub-ahead reseed, and reclaims a
            // formerly-GitHub-ahead rule once the binary catches up.
            foreach (var r in builtInRules) r.Provenance = RuleProvenance.Embedded;

            if (existingRules.Count == 0)
            {
                _logger.LogInformation("Seeding built-in gather rules...");
                foreach (var rule in builtInRules)
                {
                    await _ruleRepo.StoreGatherRuleAsync(rule, "global");
                }
                _logger.LogInformation($"Seeded {builtInRules.Count} built-in gather rules");
            }
            else
            {
                var existingLookup = existingRules.ToDictionary(r => r.RuleId, r => r);
                var updated = 0;

                foreach (var rule in builtInRules)
                {
                    if (existingLookup.TryGetValue(rule.RuleId, out var existing))
                    {
                        var contentDiffers = !ContentEquivalent(existing, rule);

                        // Protect a GitHub-ahead row the binary hasn't caught up to yet: a GitHub
                        // reseed may have shipped a NEWER version/content for an id the (older) binary
                        // still has embedded. Do NOT overwrite it with the binary's stale definition.
                        // It stays GitHub-ahead until a redeploy makes the embedded content match.
                        if (RuleProvenance.IsGitHubAhead(existing.Provenance) && contentDiffers)
                            continue;

                        // Write on a real content change, OR to reclaim a now-matching GitHub-ahead
                        // row back to embedded provenance (content equal, only provenance differs).
                        if (contentDiffers
                            || !RuleProvenance.AreEquivalent(existing.Provenance, rule.Provenance))
                        {
                            await _ruleRepo.StoreGatherRuleAsync(rule, "global");
                            updated++;
                        }
                    }
                    else
                    {
                        // New built-in rule added in code
                        await _ruleRepo.StoreGatherRuleAsync(rule, "global");
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    _logger.LogInformation($"Updated {updated} built-in gather rules from code definitions");
                }

                // Sunset detection: built-in rules that existed in the DB but are no longer in the
                // code catalog. ProcessSunsetGatherRuleAsync runs safe-state -> GC -> tombstone with
                // strict ordering. Any non-Completed outcome means work remains — leave _seeded=false
                // so the next GetAllRulesForTenantAsync retries without a process restart. Mirrors
                // AnalyzeRuleService.EnsureBuiltInRulesSeededAsync.
                var sunsetRules = existingRules
                    .Where(r => r.IsBuiltIn && !newCatalogIds.Contains(r.RuleId)
                                && !RuleProvenance.IsGitHubAhead(r.Provenance))
                    .ToList();

                var allSunsetsClean = true;
                if (sunsetRules.Count > 0)
                {
                    var orphanStatesGcd = 0;
                    var outcomeCounts = new Dictionary<SunsetOutcome, int>();
                    foreach (var sunset in sunsetRules)
                    {
                        var (outcome, gcd) = await ProcessSunsetGatherRuleAsync(sunset);
                        orphanStatesGcd += gcd;
                        outcomeCounts[outcome] = outcomeCounts.GetValueOrDefault(outcome) + 1;
                        if (outcome != SunsetOutcome.Completed)
                            allSunsetsClean = false;
                    }
                    _logger.LogInformation(
                        "Sunset {RuleCount} built-in gather rule(s); orphan RuleState rows GC'd {OrphanCount}; outcomes {Outcomes}",
                        sunsetRules.Count, orphanStatesGcd, FormatOutcomes(outcomeCounts));
                }

                if (!allSunsetsClean)
                {
                    _logger.LogInformation("One or more sunset gather rules did not complete; leaving _seeded=false so the next GetAllRulesForTenantAsync retries.");
                    return;
                }
            }

            _seeded = true;
        }

        /// <summary>
        /// Compact log-friendly rendering of the sunset-outcome histogram. Mirror of
        /// AnalyzeRuleService.FormatOutcomes.
        /// </summary>
        private static string FormatOutcomes(Dictionary<SunsetOutcome, int> counts)
        {
            return string.Join(", ",
                Enum.GetValues<SunsetOutcome>()
                    .Where(o => counts.GetValueOrDefault(o) > 0)
                    .Select(o => $"{o}={counts[o]}"));
        }

        /// <summary>
        /// True when two gather rule definitions are the same content (ignoring provenance / tenant
        /// state / timestamps) — the seed's "did the definition change?" test. Also used by the
        /// GitHub reseed to decide whether a fetched rule is already shipped identically by the
        /// binary (embedded) or is ahead of it (github). Keeping both sites on ONE comparison is what
        /// makes the ahead/reclaim handshake consistent.
        /// </summary>
        internal static bool ContentEquivalent(GatherRule a, GatherRule b)
        {
            return a.Version == b.Version
                && a.Target == b.Target
                && a.Description == b.Description
                && a.Title == b.Title
                && a.CollectorType == b.CollectorType
                && a.Trigger == b.Trigger
                && a.TriggerPhase == b.TriggerPhase;
        }
    }
}
