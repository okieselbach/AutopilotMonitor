using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class GetAllTenantConfigurationsFunction
    {
        private readonly ILogger<GetAllTenantConfigurationsFunction> _logger;
        private readonly TenantConfigurationService _configService;

        public GetAllTenantConfigurationsFunction(
            ILogger<GetAllTenantConfigurationsFunction> logger,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _configService = configService;
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
                    var allowed = new System.Collections.Generic.HashSet<string>(
                        requestCtx.AllowedTenantIds, StringComparer.OrdinalIgnoreCase);
                    var all = await _configService.GetAllConfigurationsAsync();
                    var subset = all.Where(c => allowed.Contains(c.TenantId)).ToList();
                    _logger.LogInformation("GetAllTenantConfigurations (delegated subset, {Count} tenants) by {User}",
                        subset.Count, userIdentifier);

                    if (parsed.PageSize == null)
                    {
                        var redacted = subset.Select(c => c.RedactedCopyForReader()).ToList();
                        var resp = req.CreateResponse(HttpStatusCode.OK);
                        await resp.WriteAsJsonAsync(redacted);
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
    }
}
