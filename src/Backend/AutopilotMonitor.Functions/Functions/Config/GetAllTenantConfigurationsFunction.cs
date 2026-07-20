using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class GetAllTenantConfigurationsFunction
    {
        // TTL for the "this managed id has NO config row in any casing" verdict a rescue scan proved.
        // Bounds how long a tenant onboarded moments after that scan stays absent from a delegated
        // caller's config/all (matches the positive config cache TTL in TenantConfigurationService).
        private static readonly TimeSpan MissingConfigNegativeCacheDuration = TimeSpan.FromMinutes(5);

        private readonly ILogger<GetAllTenantConfigurationsFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly IMemoryCache _cache;

        public GetAllTenantConfigurationsFunction(
            ILogger<GetAllTenantConfigurationsFunction> logger,
            TenantConfigurationService configService,
            IMemoryCache cache)
        {
            _logger = logger;
            _configService = configService;
            _cache = cache;
        }

        [Function("GetAllTenantConfigurations")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/all")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = TenantConfigPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = parsed.Error });
                    return bad;
                }

                // Delegated ("MSP") caller: the middleware (GlobalReadOrDelegatedSubset tier) admitted them
                // and published their managed tenant set on AllowedTenantIds. BIND the response to that
                // subset — a delegated admin must never see a tenant they do not manage. Secrets are always
                // redacted for them (they are never a Global Admin). The subset is small, so we return it in
                // one shot (no server pagination) in whichever shape the caller requested.
                var requestCtx = req.GetRequestContext();
                if (requestCtx.AllowedTenantIds != null)
                {
                    // READ-bounded, not just response-bounded: point-read ONLY the caller's managed tenants
                    // instead of scanning every tenant config and filtering in memory. A delegated MSP scoped
                    // to k tenants triggers k point reads (cached, non-creating — GetConfigurationIfExistsAsync
                    // never writes a default row). Only a true "no config row" (404) drops an id; a storage
                    // READ FAILURE propagates to the outer catch → 500, so a transient error can never
                    // silently drop a managed tenant from a success response. Ids are deduped case-
                    // insensitively (the old scan+filter semantics) so a duplicated grant list cannot
                    // produce duplicate config entries.
                    var managedIds = requestCtx.AllowedTenantIds
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var reads = await Task.WhenAll(managedIds.Select(tid =>
                        _configService.GetConfigurationIfExistsAsync(tid)));
                    var subset = ExistingManagedConfigs(reads);

                    // AllowedTenantIds is lowercased while the config PartitionKey casing is not guaranteed
                    // (agent paths auto-create rows with the caller-supplied id verbatim) — the same caveat
                    // the delegated sessions fan-out handles case-insensitively. A point-read is exact-case,
                    // so any id it missed gets ONE scan+filter rescue pass (the pre-point-read behavior).
                    // An id a previous scan already proved missing in ANY casing is negative-cached and
                    // skipped for the TTL — otherwise a single stale grant (offboarded / never-onboarded
                    // tenant) would make EVERY config/all call pay a full table scan.
                    if (subset.Count < managedIds.Count)
                    {
                        var unproven = MissingManagedIds(managedIds, subset)
                            .Where(id => !_cache.TryGetValue(MissingConfigCacheKey(id), out _))
                            .ToList();
                        if (unproven.Count > 0)
                        {
                            subset = MergeRescuedConfigs(managedIds, subset, await _configService.GetAllConfigurationsAsync());
                            foreach (var id in MissingManagedIds(unproven, subset))
                                _cache.Set(MissingConfigCacheKey(id), true, MissingConfigNegativeCacheDuration);
                        }
                    }
                    _logger.LogInformation("GetAllTenantConfigurations (delegated subset, {Count} tenants) by {User}",
                        subset.Count, userIdentifier);

                    if (parsed.PageSize == null)
                    {
                        var resp = req.CreateResponse(HttpStatusCode.OK);
                        await resp.WriteAsJsonAsync(DelegatedBareArrayView(subset));
                        return resp;
                    }

                    var projected = TenantConfigProjection.ProjectAll(subset, parsed.Fields);
                    return await req.OkAsync(new
                    {
                        count = projected.Count,
                        tenants = projected,
                        nextLink = (string?)null,
                    });
                }

                // Legacy/default mode: no pageSize → unpaginated bare full-config array.
                // Web consumers (tenant selectors + admin config editor) depend on this shape.
                if (parsed.PageSize == null)
                {
                    _logger.LogInformation($"GetAllTenantConfigurations (full) by {userIdentifier}");
                    var configurations = await _configService.GetAllConfigurationsAsync();

                    // A read-only GlobalReader gets per-tenant secrets (SAS / webhook URLs / custom
                    // headers) redacted; a Global Admin gets the full configs. The paginated mode below
                    // is already a secret-stripped keep-list projection, so it needs no role branch.
                    if (req.GetRequestContext().IsGlobalReader)
                        configurations = configurations.Select(c => c.RedactedCopyForReader()).ToList();

                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteAsJsonAsync(configurations);
                    return response;
                }

                // Paginated mode: opt-in via ?pageSize=. Returns a secret-stripped projection
                // so tenant secrets (webhook/SAS/branding URLs, allow-lists) never leave the
                // backend. Consumed by the MCP list_tenants tool.
                _logger.LogInformation($"GetAllTenantConfigurations (page, size {parsed.PageSize}) by Global Admin {userIdentifier}");

                var callerTenantId = TenantHelper.GetTenantId(req);

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!TenantConfigPagination.TryAcceptContinuation(
                            parsed.Continuation, callerTenantId, out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("GetAllTenantConfigurations: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            error = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = await _configService.GetConfigurationsPageAsync(parsed.PageSize.Value, azureToken);

                // Keep-list projection — only non-sensitive fields, optionally narrowed to the
                // caller's fields= subset. Secrets can never be selected (intersection only).
                var tenants = TenantConfigProjection.ProjectAll(page.Items, parsed.Fields);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = TenantConfigPagination.Fingerprint(callerTenantId);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                    nextLink = TenantConfigPagination.BuildNextLink(parsed.PageSize.Value, wireToken, parsed.Fields);
                }

                return await req.OkAsync(new
                {
                    count = tenants.Count,
                    tenants,
                    nextLink,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all tenant configurations");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>
        /// From the per-tenant point-read results, keep only the managed tenants that actually HAVE a config
        /// row (non-null). A delegated caller's AllowedTenantIds is the authoritative bound; an id with no row
        /// (offboarded / never onboarded) is silently dropped rather than surfaced as an empty default.
        /// Storage errors never reach this seam — GetConfigurationIfExistsAsync throws, failing the request.
        /// Pure + testable seam (handler HTTP entry is not unit-tested — see GetAllBlockedDevicesFunctionTests).
        /// </summary>
        internal static List<TenantConfiguration> ExistingManagedConfigs(
            IEnumerable<TenantConfiguration?> reads)
            => reads.Where(c => c != null).Select(c => c!).ToList();

        /// <summary>
        /// The managed ids not present (case-insensitively) in <paramref name="configs"/> — before the
        /// rescue scan these are the point-read misses, after it the proven-missing ids to negative-cache.
        /// Pure + testable.
        /// </summary>
        internal static List<string> MissingManagedIds(
            IEnumerable<string> managedIds, IReadOnlyCollection<TenantConfiguration> configs)
        {
            var have = new HashSet<string>(configs.Select(c => c.TenantId), StringComparer.OrdinalIgnoreCase);
            return managedIds.Where(id => !have.Contains(id)).ToList();
        }

        /// <summary>Negative-cache key for a managed id a rescue scan proved has no config row.
        /// Lowercased so case variants of the same grant share one verdict.</summary>
        internal static string MissingConfigCacheKey(string tenantId)
            => $"tenant-config-missing:{tenantId.ToLowerInvariant()}";

        /// <summary>
        /// Case-variant rescue for managed ids the exact-case point-reads missed: from the full config scan,
        /// add every config whose TenantId matches a managed id case-insensitively and is not already in the
        /// point-read hits. The managed-id bound stays authoritative — an unmanaged config is never added, so
        /// this can only ever RESTORE tenants the pre-point-read scan+filter would have returned. Pure + testable.
        /// </summary>
        internal static List<TenantConfiguration> MergeRescuedConfigs(
            IReadOnlyCollection<string> managedIds,
            IReadOnlyCollection<TenantConfiguration> pointReadHits,
            IEnumerable<TenantConfiguration> allConfigs)
        {
            var managed = new HashSet<string>(managedIds, StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(pointReadHits.Select(c => c.TenantId), StringComparer.OrdinalIgnoreCase);
            var merged = new List<TenantConfiguration>(pointReadHits);
            merged.AddRange(allConfigs.Where(c => managed.Contains(c.TenantId) && !found.Contains(c.TenantId)));
            return merged;
        }

        /// <summary>
        /// The delegated bare-array view of config/all: every managed-tenant config with its secrets
        /// (SAS / webhook URLs / custom headers) redacted. A delegated admin is never a Global Admin, so it
        /// must NEVER receive unredacted secrets for a tenant it merely manages. Pure + testable.
        /// </summary>
        internal static List<TenantConfiguration> DelegatedBareArrayView(
            IEnumerable<TenantConfiguration> existingManaged)
            => existingManaged.Select(c => c.RedactedCopyForReader()).ToList();
    }
}
