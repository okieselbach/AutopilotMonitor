using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Central reseed endpoint that fetches all rule types from GitHub and writes them to Table Storage.
    /// Replaces the old per-type reseed endpoints (gather-rules/reseed, analyze-rules/reseed).
    /// </summary>
    public class ReseedFromGitHubFunction
    {
        private readonly ILogger<ReseedFromGitHubFunction> _logger;
        private readonly GitHubRuleRepository _gitHubRepo;
        private readonly GatherRuleService _gatherRuleService;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly ImeLogPatternService _imeLogPatternService;
        private readonly IVulnerabilityRepository _vulnRepo;
        private readonly IRuleRepository _ruleRepo;

        public ReseedFromGitHubFunction(
            ILogger<ReseedFromGitHubFunction> logger,
            GitHubRuleRepository gitHubRepo,
            GatherRuleService gatherRuleService,
            AnalyzeRuleService analyzeRuleService,
            ImeLogPatternService imeLogPatternService,
            IVulnerabilityRepository vulnRepo,
            IRuleRepository ruleRepo)
        {
            _logger = logger;
            _gitHubRepo = gitHubRepo;
            _gatherRuleService = gatherRuleService;
            _analyzeRuleService = analyzeRuleService;
            _imeLogPatternService = imeLogPatternService;
            _vulnRepo = vulnRepo;
            _ruleRepo = ruleRepo;
        }

        [Function("ReseedFromGitHub")]
        public async Task<HttpResponseData> Reseed(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/reseed-from-github")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var upn = TenantHelper.GetUserIdentifier(req);

                // Parse ?type= parameter (default: all)
                var typeParam = "all";
                var queryString = req.Url.Query;
                if (queryString.Contains("type=", StringComparison.OrdinalIgnoreCase))
                {
                    var typeStart = queryString.IndexOf("type=", StringComparison.OrdinalIgnoreCase) + 5;
                    var typeEnd = queryString.IndexOf('&', typeStart);
                    typeParam = (typeEnd > 0 ? queryString.Substring(typeStart, typeEnd - typeStart) : queryString.Substring(typeStart)).ToLowerInvariant();
                }

                _logger.LogInformation($"Reseed from GitHub triggered by Global Admin {upn}, type={typeParam}");

                var gatherResult = new { deleted = 0, written = 0, orphanStatesGcd = 0, sunsetSkipped = 0 };
                var analyzeResult = new { deleted = 0, written = 0, orphanStatesGcd = 0, sunsetSkipped = 0 };
                var imeResult = new { deleted = 0, written = 0 };

                if (typeParam == "all" || typeParam == "gather")
                {
                    var rules = await _gitHubRepo.FetchGatherRulesAsync();
                    var (d, w, gcd, skipped) = await ReseedGatherAsync(rules);
                    gatherResult = new { deleted = d, written = w, orphanStatesGcd = gcd, sunsetSkipped = skipped };
                }

                if (typeParam == "all" || typeParam == "analyze")
                {
                    var rules = await _gitHubRepo.FetchAnalyzeRulesAsync();
                    var (d, w, gcd, skipped) = await ReseedAnalyzeAsync(rules);
                    analyzeResult = new { deleted = d, written = w, orphanStatesGcd = gcd, sunsetSkipped = skipped };
                }

                if (typeParam == "all" || typeParam == "ime")
                {
                    var patterns = await _gitHubRepo.FetchImeLogPatternsAsync();
                    var (d, w) = await ReseedImeAsync(patterns);
                    imeResult = new { deleted = d, written = w };
                }

                var cpeMappingsResult = new { deleted = 0, written = 0 };
                if (typeParam == "all" || typeParam == "cpe")
                {
                    var (d, w) = await ReseedCpeCommunityMappingsAsync();
                    cpeMappingsResult = new { deleted = d, written = w };
                }

                var cpeSeedResult = new { deleted = 0, written = 0 };
                if (typeParam == "all" || typeParam == "cpe")
                {
                    var (d, w) = await ReseedCpeSeedMappingsAsync();
                    cpeSeedResult = new { deleted = d, written = w };
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Reseed from GitHub complete",
                    gather = gatherResult,
                    analyze = analyzeResult,
                    ime = imeResult,
                    cpeCommunityMappings = cpeMappingsResult,
                    cpeSeedMappings = cpeSeedResult
                });
                return response;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Failed to fetch rules from GitHub");
                var response = req.CreateResponse(HttpStatusCode.BadGateway);
                await response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to fetch rules from GitHub. GitHub CDN may cache responses for up to 5 minutes after a merge."
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during GitHub reseed");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success = false, message = "Failed to reseed from GitHub" });
                return response;
            }
        }

        private async Task<(int deleted, int written, int orphanStatesGcd, int sunsetSkipped)> ReseedGatherAsync(List<AutopilotMonitor.Shared.Models.GatherRule> rules)
        {
            var existing = await _ruleRepo.GetGatherRulesAsync("global");
            var newCatalogIds = rules.Select(r => r.RuleId).ToHashSet(System.StringComparer.Ordinal);
            // A rule the deployed binary already ships IDENTICALLY is embedded-provenance; one whose
            // id the binary lacks OR whose content the binary hasn't caught up to is github-ahead →
            // exempt from the embedded catalog sunset/filter until a redeploy makes the embedded
            // content match. Content check (not just id) so a GitHub version bump on an existing id
            // is protected too. See RuleProvenance.
            var embeddedById = AutopilotMonitor.Functions.Services.BuiltInGatherRules.GetAll()
                .ToDictionary(r => r.RuleId, r => r, System.StringComparer.Ordinal);

            // Split existing built-in / community rows into survivors (in new catalog → re-imported
            // below) and sunsets (NOT in new catalog → orphan-state GC first, global delete only on
            // clean GC). Mirrors ReseedAnalyzeAsync / GatherRuleService.EnsureBuiltInRulesSeededAsync
            // so every reseed path leaves the same DB invariants and partial failures retry.
            var survivors = existing.Where(r => (r.IsBuiltIn || r.IsCommunity) && newCatalogIds.Contains(r.RuleId)).ToList();
            var sunset = existing.Where(r => (r.IsBuiltIn || r.IsCommunity) && !newCatalogIds.Contains(r.RuleId)).ToList();

            var deleted = 0;

            // Survivor path: delete then re-write (legacy behaviour).
            foreach (var rule in survivors)
            {
                await _ruleRepo.DeleteGatherRuleAsync("global", rule.RuleId);
                deleted++;
            }

            // Sunset path: shared helper on GatherRuleService — same safe-state -> GC -> tombstone
            // ordering as the in-process EnsureSeed and ReseedBuiltIn paths.
            var orphanStatesGcd = 0;
            var sunsetSkipped = 0;
            foreach (var sunsetRule in sunset)
            {
                var (outcome, gcd) = await _gatherRuleService.ProcessSunsetGatherRuleAsync(sunsetRule);
                orphanStatesGcd += gcd;
                if (outcome == AutopilotMonitor.Functions.Services.SunsetOutcome.Completed)
                    deleted++;
                else
                    sunsetSkipped++;
            }

            foreach (var rule in rules)
            {
                // Community rules keep IsCommunity=true from JSON; all others are built-in
                if (!rule.IsCommunity)
                    rule.IsBuiltIn = true;
                rule.Provenance = (embeddedById.TryGetValue(rule.RuleId, out var binaryGather)
                        && AutopilotMonitor.Functions.Services.GatherRuleService.ContentEquivalent(binaryGather, rule))
                    ? AutopilotMonitor.Shared.Models.RuleProvenance.Embedded
                    : AutopilotMonitor.Shared.Models.RuleProvenance.GitHubAhead;
                rule.CreatedAt = DateTime.UtcNow;
                rule.UpdatedAt = DateTime.UtcNow;
                await _ruleRepo.StoreGatherRuleAsync(rule, "global");
            }

            _logger.LogInformation(
                "GitHub reseed gather: {Deleted} deleted, {Written} written, {OrphanCount} orphan per-tenant RuleState(s) cleaned across {SunsetTotal} sunset rule(s) ({Skipped} skipped on failure for retry)",
                deleted, rules.Count, orphanStatesGcd, sunset.Count, sunsetSkipped);
            return (deleted, rules.Count, orphanStatesGcd, sunsetSkipped);
        }

        private async Task<(int deleted, int written, int orphanStatesGcd, int sunsetSkipped)> ReseedAnalyzeAsync(List<AutopilotMonitor.Shared.Models.AnalyzeRule> rules)
        {
            var existing = await _ruleRepo.GetAnalyzeRulesAsync("global");
            var newCatalogIds = rules.Select(r => r.RuleId).ToHashSet(System.StringComparer.Ordinal);
            // A rule the deployed binary already ships IDENTICALLY is embedded-provenance; one whose
            // id the binary lacks OR whose content the binary hasn't caught up to is github-ahead →
            // exempt from the embedded catalog sunset/filter until a redeploy makes the embedded
            // content match. Content check (not just id) so a GitHub version bump on an existing id
            // is protected too. See RuleProvenance.
            var embeddedById = AutopilotMonitor.Functions.Services.BuiltInAnalyzeRules.GetAll()
                .ToDictionary(r => r.RuleId, r => r, System.StringComparer.Ordinal);

            // Split existing built-in / community rows into survivors (in new catalog
            // → re-imported below) and sunsets (NOT in new catalog → orphan-state GC
            // first, global delete only on clean GC). The sunset ordering mirrors
            // AnalyzeRuleService.EnsureBuiltInRulesSeededAsync — kept symmetric so every
            // reseed path leaves the same DB invariants and partial failures retry.
            var survivors = existing.Where(r => (r.IsBuiltIn || r.IsCommunity) && newCatalogIds.Contains(r.RuleId)).ToList();
            var sunset = existing.Where(r => (r.IsBuiltIn || r.IsCommunity) && !newCatalogIds.Contains(r.RuleId)).ToList();

            var deleted = 0;

            // Survivor path: delete then re-write (legacy behaviour).
            foreach (var rule in survivors)
            {
                await _ruleRepo.DeleteAnalyzeRuleAsync("global", rule.RuleId);
                deleted++;
            }

            // Sunset path: shared helper on AnalyzeRuleService — same safe-state ->
            // GC -> tombstone ordering as the in-process EnsureSeed and ReseedBuiltIn
            // paths, so every reseed flavor leaves the same DB invariants.
            var orphanStatesGcd = 0;
            var sunsetSkipped = 0;
            foreach (var sunsetRule in sunset)
            {
                var (outcome, gcd) = await _analyzeRuleService.ProcessSunsetRuleAsync(sunsetRule);
                orphanStatesGcd += gcd;
                if (outcome == AutopilotMonitor.Functions.Services.SunsetOutcome.Completed)
                {
                    deleted++;
                }
                else
                {
                    sunsetSkipped++;
                }
            }

            foreach (var rule in rules)
            {
                // Community rules keep IsCommunity=true from JSON; all others are built-in
                if (!rule.IsCommunity)
                    rule.IsBuiltIn = true;
                rule.Provenance = (embeddedById.TryGetValue(rule.RuleId, out var binaryAnalyze)
                        && AutopilotMonitor.Functions.Services.AnalyzeRuleService.ContentEquivalent(binaryAnalyze, rule))
                    ? AutopilotMonitor.Shared.Models.RuleProvenance.Embedded
                    : AutopilotMonitor.Shared.Models.RuleProvenance.GitHubAhead;
                rule.CreatedAt = DateTime.UtcNow;
                rule.UpdatedAt = DateTime.UtcNow;
                await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
            }

            _logger.LogInformation(
                "GitHub reseed analyze: {Deleted} deleted, {Written} written, {OrphanCount} orphan per-tenant RuleState(s) cleaned across {SunsetTotal} sunset rule(s) ({Skipped} skipped on failure for retry)",
                deleted, rules.Count, orphanStatesGcd, sunset.Count, sunsetSkipped);
            return (deleted, rules.Count, orphanStatesGcd, sunsetSkipped);
        }

        private async Task<(int deleted, int written)> ReseedImeAsync(List<AutopilotMonitor.Shared.Models.ImeLogPattern> patterns)
        {
            var existing = await _ruleRepo.GetImeLogPatternsAsync("global");
            var deleted = 0;
            foreach (var pattern in existing.Where(p => p.IsBuiltIn))
            {
                await _ruleRepo.DeleteImeLogPatternAsync("global", pattern.PatternId);
                deleted++;
            }

            foreach (var pattern in patterns)
            {
                pattern.IsBuiltIn = true;
                await _ruleRepo.StoreImeLogPatternAsync(pattern, "global");
            }

            _logger.LogInformation($"GitHub reseed IME: {deleted} deleted, {patterns.Count} written");
            return (deleted, patterns.Count);
        }

        private async Task<(int deleted, int written)> ReseedCpeCommunityMappingsAsync()
        {
            // Delete existing community CPE mapping entries
            var deleted = await _vulnRepo.DeleteCpeMappingsByPartitionAsync("cpe_map_community");

            // Fetch community CPE mappings from GitHub and import
            var json = await _gitHubRepo.FetchCpeCommunityMappingsAsync();
            var written = await _vulnRepo.ImportCpeCommunityMappingsFromJsonAsync(json);

            // Reset the static seed-loaded flag so the next correlation picks up fresh data
            VulnerabilityCorrelationService.ResetMappingsCache();

            _logger.LogInformation("GitHub reseed community CPE mappings: {Deleted} deleted, {Written} written", deleted, written);
            return (deleted, written);
        }

        private async Task<(int deleted, int written)> ReseedCpeSeedMappingsAsync()
        {
            // Count existing seed entries before deletion (for reporting)
            var existingEntries = await _vulnRepo.GetCpeMappingsByPartitionAsync("cpe_map_seed");
            var deleted = existingEntries.Count;

            // Fetch CPE seed mappings from GitHub and import (deletes + writes in one call)
            var json = await _gitHubRepo.FetchCpeSeedMappingsAsync();
            var written = await _vulnRepo.ImportCpeMappingSeedFromJsonAsync(json);

            // Reset the static seed-loaded flag so the next correlation picks up fresh data
            VulnerabilityCorrelationService.ResetMappingsCache();

            _logger.LogInformation("GitHub reseed CPE seed mappings: {Deleted} deleted, {Written} written", deleted, written);
            return (deleted, written);
        }
    }
}
