using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing IME log patterns (regex patterns for IME log parsing).
    /// Merges global built-in patterns with tenant-specific overrides.
    /// </summary>
    public class ImeLogPatternService
    {
        private readonly IRuleRepository _ruleRepo;
        private readonly ILogger<ImeLogPatternService> _logger;
        private bool _seeded = false;

        public ImeLogPatternService(IRuleRepository ruleRepo, ILogger<ImeLogPatternService> logger)
        {
            _ruleRepo = ruleRepo;
            _logger = logger;
        }

        /// <summary>
        /// Gets all active IME log patterns for a tenant.
        /// Merges global built-in patterns with tenant-specific overrides.
        /// </summary>
        public async Task<List<ImeLogPattern>> GetActivePatternsForTenantAsync(string tenantId)
        {
            await EnsureBuiltInPatternsSeededAsync();

            var globalPatterns = await _ruleRepo.GetImeLogPatternsAsync("global");
            var tenantPatterns = await _ruleRepo.GetImeLogPatternsAsync(tenantId);

            var tenantOverrides = tenantPatterns.ToDictionary(p => p.PatternId, p => p);

            var mergedPatterns = new List<ImeLogPattern>();

            foreach (var globalPattern in globalPatterns)
            {
                if (tenantOverrides.TryGetValue(globalPattern.PatternId, out var tenantOverride))
                {
                    mergedPatterns.Add(tenantOverride);
                    tenantOverrides.Remove(globalPattern.PatternId);
                }
                else
                {
                    mergedPatterns.Add(globalPattern);
                }
            }

            foreach (var customPattern in tenantOverrides.Values)
            {
                mergedPatterns.Add(customPattern);
            }

            return mergedPatterns.Where(p => p.Enabled).ToList();
        }

        /// <summary>
        /// Gets all IME log patterns for a tenant (including disabled) for portal display.
        /// </summary>
        public async Task<List<ImeLogPattern>> GetAllPatternsForTenantAsync(string tenantId)
        {
            await EnsureBuiltInPatternsSeededAsync();

            var globalPatterns = await _ruleRepo.GetImeLogPatternsAsync("global");
            var tenantPatterns = await _ruleRepo.GetImeLogPatternsAsync(tenantId);
            var tenantOverrides = tenantPatterns.ToDictionary(p => p.PatternId, p => p);

            var mergedPatterns = new List<ImeLogPattern>();

            foreach (var globalPattern in globalPatterns)
            {
                if (tenantOverrides.TryGetValue(globalPattern.PatternId, out var tenantOverride))
                {
                    mergedPatterns.Add(tenantOverride);
                    tenantOverrides.Remove(globalPattern.PatternId);
                }
                else
                {
                    mergedPatterns.Add(globalPattern);
                }
            }

            foreach (var customPattern in tenantOverrides.Values)
            {
                mergedPatterns.Add(customPattern);
            }

            return mergedPatterns;
        }

        /// <summary>
        /// Creates a custom IME log pattern for a tenant.
        /// </summary>
        public async Task<bool> CreatePatternAsync(string tenantId, ImeLogPattern pattern)
        {
            pattern.IsBuiltIn = false;
            return await _ruleRepo.StoreImeLogPatternAsync(pattern, tenantId);
        }

        /// <summary>
        /// Updates an IME log pattern (enable/disable or modify).
        /// For built-in patterns, creates a tenant override.
        /// </summary>
        public async Task<bool> UpdatePatternAsync(string tenantId, ImeLogPattern pattern)
        {
            return await _ruleRepo.StoreImeLogPatternAsync(pattern, tenantId);
        }

        /// <summary>
        /// Updates a built-in IME log pattern globally (Global Admin only).
        /// Modifies the global definition that all tenants inherit.
        /// </summary>
        public async Task<bool> UpdateGlobalPatternAsync(ImeLogPattern pattern)
        {
            pattern.IsBuiltIn = true;
            return await _ruleRepo.StoreImeLogPatternAsync(pattern, "global");
        }

        /// <summary>
        /// Deletes a custom IME log pattern (cannot delete built-in patterns).
        /// </summary>
        public async Task<bool> DeletePatternAsync(string tenantId, string patternId)
        {
            return await _ruleRepo.DeleteImeLogPatternAsync(tenantId, patternId);
        }

        /// <summary>
        /// Deletes a built-in IME log pattern from the global partition (Global Admin only).
        /// </summary>
        public async Task<bool> DeleteGlobalPatternAsync(string patternId)
        {
            _seeded = false;
            return await _ruleRepo.DeleteImeLogPatternAsync("global", patternId);
        }

        /// <summary>
        /// Re-imports all built-in IME log patterns into the global partition.
        /// Deletes old global built-in patterns and writes current code definitions.
        /// </summary>
        public async Task<(int deleted, int written)> ReseedBuiltInPatternsAsync()
        {
            _logger.LogInformation("Reseeding built-in IME log patterns (full re-import)...");

            var existingGlobalPatterns = await _ruleRepo.GetImeLogPatternsAsync("global");

            var deleted = 0;
            foreach (var pattern in existingGlobalPatterns.Where(p => p.IsBuiltIn))
            {
                await _ruleRepo.DeleteImeLogPatternAsync("global", pattern.PatternId);
                deleted++;
            }
            _logger.LogInformation($"Deleted {deleted} old global built-in IME log patterns");

            var builtInPatterns = BuiltInImeLogPatterns.GetAll();
            foreach (var pattern in builtInPatterns)
            {
                pattern.IsBuiltIn = true;
                await _ruleRepo.StoreImeLogPatternAsync(pattern, "global");
            }
            _logger.LogInformation($"Written {builtInPatterns.Count} built-in IME log patterns from code");

            _seeded = false;

            return (deleted, builtInPatterns.Count);
        }

        /// <summary>
        /// Seeds built-in IME log patterns if not already done.
        /// Also updates existing built-in patterns when the code definitions change.
        /// </summary>
        private async Task EnsureBuiltInPatternsSeededAsync()
        {
            if (_seeded) return;

            var existingPatterns = await _ruleRepo.GetImeLogPatternsAsync("global");
            var builtInPatterns = BuiltInImeLogPatterns.GetAll();

            if (existingPatterns.Count == 0)
            {
                _logger.LogInformation("Seeding built-in IME log patterns...");
                foreach (var pattern in builtInPatterns)
                {
                    pattern.IsBuiltIn = true;
                    await _ruleRepo.StoreImeLogPatternAsync(pattern, "global");
                }
                _logger.LogInformation($"Seeded {builtInPatterns.Count} built-in IME log patterns");
            }
            else
            {
                var existingLookup = existingPatterns.ToDictionary(p => p.PatternId, p => p);
                var updated = 0;

                foreach (var pattern in builtInPatterns)
                {
                    pattern.IsBuiltIn = true;
                    if (existingLookup.TryGetValue(pattern.PatternId, out var existing))
                    {
                        if (existing.Pattern != pattern.Pattern || existing.Action != pattern.Action
                            || existing.Category != pattern.Category || existing.Description != pattern.Description
                            || existing.Enabled != pattern.Enabled
                            || !ParametersEqual(existing.Parameters, pattern.Parameters))
                        {
                            await _ruleRepo.StoreImeLogPatternAsync(pattern, "global");
                            updated++;
                        }
                    }
                    else
                    {
                        await _ruleRepo.StoreImeLogPatternAsync(pattern, "global");
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    _logger.LogInformation($"Updated {updated} built-in IME log patterns from code definitions");
                }
            }

            _seeded = true;
        }

        private static bool ParametersEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
        {
            if ((a?.Count ?? 0) != (b?.Count ?? 0)) return false;
            if (a == null || b == null) return true;

            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var value) || value != kv.Value) return false;
            }

            return true;
        }
    }
}
