using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetAllSessionsFunction
    {
        private readonly ILogger<GetAllSessionsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetAllSessionsFunction(
            ILogger<GetAllSessionsFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetAllSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/sessions")] HttpRequestData req)
        {
            _logger.LogInformation("GetAllSessions function processing request (Global Admin Mode)");

            try
            {
                // Authentication + authorization enforced by PolicyEnforcementMiddleware (GlobalReadOrDelegatedSubset).
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var callerTenantId = TenantHelper.GetTenantId(req);
                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

                // Delegated ("MSP") callers are admitted by the subset tier with AllowedTenantIds set; the
                // aggregate (no ?tenantId=) is then bounded to that managed subset. Null for GA/Reader = all
                // tenants. When a single ?tenantId= IS named, the repo takes the single-tenant path and this
                // bound is irrelevant (the middleware already validated the target is in the caller's scope).
                var allowedTenantIds = req.GetRequestContext().AllowedTenantIds;

                var parsed = SessionListPagination.ParseQuery(query, acceptFilterTenantId: true);
                if (parsed.Error != null)
                {
                    return await req.BadRequestAsync(parsed.Error);
                }

                if (!string.IsNullOrEmpty(parsed.FilterTenantId) && !Guid.TryParse(parsed.FilterTenantId, out _))
                {
                    return await req.BadRequestAsync("Invalid tenantId format");
                }

                _logger.LogInformation(
                    "Fetching sessions cross-tenant (filterTenantId={FilterTenant}, user={User}, days={Days}, pageSize={PageSize}, hasContinuation={HasContinuation})",
                    parsed.FilterTenantId ?? "none", userEmail, parsed.Days, parsed.PageSize, parsed.Continuation != null);

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!SessionListPagination.TryAcceptContinuation(
                            parsed.Continuation, scope: "sessions:global",
                            callerTenantId: callerTenantId, days: parsed.Days,
                            filterTenantId: parsed.FilterTenantId,
                            out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("GetAllSessions: continuation rejected ({Reason})", rejectReason);
                        return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                    }
                }

                var page = await _sessionRepo.GetAllSessionsPageAsync(
                    parsed.FilterTenantId, parsed.Days, parsed.PageSize, azureToken, allowedTenantIds);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = SessionListPagination.Fingerprint(
                        scope: "sessions:global", callerTenantId: callerTenantId,
                        days: parsed.Days, filterTenantId: parsed.FilterTenantId);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                    nextLink = SessionListPagination.BuildNextLink(
                        basePath: "/api/global/sessions",
                        pageSize: parsed.PageSize,
                        wireContinuation: wireToken,
                        days: parsed.Days,
                        filterTenantId: parsed.FilterTenantId);
                }

                return await req.OkAsync(new SessionListResponse
                {
                    Success = true,
                    Count = page.Items.Count,
                    Sessions = page.Items,
                    NextLink = nextLink,
                    // MCP fleet aggregate only: managed tenants the quota layer dropped (exhausted organization
                    // budget). The bound above is already narrowed; this just tells the caller what is missing.
                    QuotaExcludedTenants = req.GetRequestContext().QuotaExcludedTenantIds?.ToArray(),
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetAllSessions");
            }
        }
    }
}
