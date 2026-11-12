using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared;
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
        private readonly IRuleRepository _ruleRepo;
        private readonly CpeMappingImporter _cpeImporter;
        private readonly SoftwareInventoryCpeReconciler _reconciler;

        public ReseedFromGitHubFunction(
            ILogger<ReseedFromGitHubFunction> logger,
            GitHubRuleRepository gitHubRepo,
            GatherRuleService gatherRuleService,
            AnalyzeRuleService analyzeRuleService,
            IRuleRepository ruleRepo,
            CpeMappingImporter cpeImporter,
            SoftwareInventoryCpeReconciler reconciler)
        {
            _logger = logger;
            _gitHubRepo = gitHubRepo;
            _gatherRuleService = gatherRuleService;
            _analyzeRuleService = analyzeRuleService;
            _ruleRepo = ruleRepo;
            _cpeImporter = cpeImporter;
            _reconciler = reconciler;
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

                var gatherResult = new ReseedRuleCountsNode { Deleted = 0, Written = 0, OrphanStatesGcd = 0, SunsetSkipped = 0 };
                var analyzeResult = new ReseedRuleCountsNode { Deleted = 0, Written = 0, OrphanStatesGcd = 0, SunsetSkipped = 0 };
                var imeResult = new ReseedTableCountsNode { Deleted = 0, Written = 0 };

                if (typeParam == "all" || typeParam == "gather")
                {
                    var rules = await _gitHubRepo.FetchGatherRulesAsync();
                    var (d, w, gcd, skipped) = await ReseedGatherAsync(rules);
                    gatherResult = new ReseedRuleCountsNode { Deleted = d, Written = w, OrphanStatesGcd = gcd, SunsetSkipped = skipped };
                }

                if (typeParam == "all" || typeParam == "analyze")
                {
                    var rules = await _gitHubRepo.FetchAnalyzeRulesAsync();
                    var (d, w, gcd, skipped) = await ReseedAnalyzeAsync(rules);
                    analyzeResult = new ReseedRuleCountsNode { Deleted = d, Written = w, OrphanStatesGcd = gcd, SunsetSkipped = skipped };
                }

                if (typeParam == "all" || typeParam == "ime")
                {
                    var patterns = await _gitHubRepo.FetchImeLogPatternsAsync();
                    var (d, w) = await ReseedImeAsync(patterns);
                    imeResult = new ReseedTableCountsNode { Deleted = d, Written = w };
                }

                var cpeMappingsResult = new ReseedTableCountsNode { Deleted = 0, Written = 0 };
                var cpeSeedResult = new ReseedTableCountsNode { Deleted = 0, Written = 0 };
                ReseedInventoryNode? cpeInventory = null;
                List<string>? cpeProblems = null;
                if (typeParam == "all" || typeParam == "cpe")
                {
                    // Each list is fetched and checked before anything is written; a list that is
                    // refused leaves the mappings in service as they were.
                    var community = await _cpeImporter.ImportCommunityAsync();
                    var seed = await _cpeImporter.ImportSeedAsync();
                    cpeMappingsResult = new ReseedTableCountsNode { Deleted = community.Removed, Written = community.Written };
                    cpeSeedResult = new ReseedTableCountsNode { Deleted = seed.Removed, Written = seed.Written };

                    var problems = community.Problems.Select(p => "community list: " + p)
                        .Concat(seed.Problems.Select(p => "seed list: " + p))
                        .ToList();

                    try
                    {
                        var reconciled = await _reconciler.ReconcileAsync("reseed");
                        cpeInventory = new ReseedInventoryNode
                        {
                            Rows = reconciled.Rows,
                            Set = reconciled.Set,
                            Cleared = reconciled.Cleared,
                            Failed = reconciled.Failed,
                            ProductsNoted = reconciled.ProductsNoted,
                        };
                    }
                    catch (CpeMappingUnavailableException)
                    {
                        // After a refused first list no mapping is in service: there is nothing to
                        // bring the rows in line with, and the answer still has to say why the list
                        // was refused. After an import the same exception means the table could
                        // not be read back, and that is said.
                        if (seed.Written + community.Written > 0)
                            problems.Add("inventory rows not brought in line yet: the mappings could not be read back; the nightly run does it");
                    }
                    if (problems.Count > 0) cpeProblems = problems;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new ReseedFromGitHubResponse
                {
                    Success = cpeProblems == null,
                    Message = cpeProblems == null
                        ? "Reseed from GitHub complete"
                        : "Reseed from GitHub complete, except for the CPE mappings",
                    Gather = gatherResult,
                    Analyze = analyzeResult,
                    Ime = imeResult,
                    CpeCommunityMappings = cpeMappingsResult,
                    CpeSeedMappings = cpeSeedResult,
                    CpeInventory = cpeInventory,
                    CpeProblems = cpeProblems
                });
                return response;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Failed to fetch rules from GitHub");
                return await req.ErrorAsync(HttpStatusCode.BadGateway, Constants.ApiErrorCodes.UpstreamError, "Failed to fetch rules from GitHub. GitHub CDN may cache responses for up to 5 minutes after a merge.");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "ReseedFromGitHub");
            }
        }

        private async Task<(int deleted, int written, int orphanStatesGcd, int sunsetSkipped)> ReseedGatherAsync(List<AutopilotMonitor.Shared.Models.GatherRule> rules)
        {
            // Global-partition rules must stay inside the reserved built-in namespace — an ID
            // outside it (e.g. GATHER-CUSTOM-*) would silently shadow every tenant's same-ID
            // custom rule at merge time (global wins). Reject instead of import.
            var rejectedGather = rules.Where(r => !AutopilotMonitor.Shared.Models.RuleIdPolicy.IsReservedBuiltInId(r.RuleId)).ToList();
            if (rejectedGather.Count > 0)
            {
                _logger.LogError(
                    "GitHub reseed gather: rejected {Count} rule(s) outside the reserved built-in namespace: {Ids}",
                    rejectedGather.Count, string.Join(", ", rejectedGather.Select(r => r.RuleId)));
                rules = rules.Except(rejectedGather).ToList();
            }

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
            await _ruleRepo.SetRuleCatalogStampAsync(AutopilotMonitor.Functions.Services.RuleCatalogSeedGate.Stamp(
                AutopilotMonitor.Shared.Models.RuleCatalogStamp.KindGather, AutopilotMonitor.Shared.Models.RuleCatalogStamp.SourceGitHub, rules.Count));
            return (deleted, rules.Count, orphanStatesGcd, sunsetSkipped);
        }

        private async Task<(int deleted, int written, int orphanStatesGcd, int sunsetSkipped)> ReseedAnalyzeAsync(List<AutopilotMonitor.Shared.Models.AnalyzeRule> rules)
        {
            // Global-partition rules must stay inside the reserved built-in namespace — an ID
            // outside it (e.g. ANALYZE-CUSTOM-*) would silently shadow every tenant's same-ID
            // custom rule at merge time (global wins). Reject instead of import.
            var rejectedAnalyze = rules.Where(r => !AutopilotMonitor.Shared.Models.RuleIdPolicy.IsReservedBuiltInId(r.RuleId)).ToList();
            if (rejectedAnalyze.Count > 0)
            {
                _logger.LogError(
                    "GitHub reseed analyze: rejected {Count} rule(s) outside the reserved built-in namespace: {Ids}",
                    rejectedAnalyze.Count, string.Join(", ", rejectedAnalyze.Select(r => r.RuleId)));
                rules = rules.Except(rejectedAnalyze).ToList();
            }

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
            await _ruleRepo.SetRuleCatalogStampAsync(AutopilotMonitor.Functions.Services.RuleCatalogSeedGate.Stamp(
                AutopilotMonitor.Shared.Models.RuleCatalogStamp.KindAnalyze, AutopilotMonitor.Shared.Models.RuleCatalogStamp.SourceGitHub, rules.Count));
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
            await _ruleRepo.SetRuleCatalogStampAsync(AutopilotMonitor.Functions.Services.RuleCatalogSeedGate.Stamp(
                AutopilotMonitor.Shared.Models.RuleCatalogStamp.KindIme, AutopilotMonitor.Shared.Models.RuleCatalogStamp.SourceGitHub, patterns.Count));
            return (deleted, patterns.Count);
        }
    }
}
