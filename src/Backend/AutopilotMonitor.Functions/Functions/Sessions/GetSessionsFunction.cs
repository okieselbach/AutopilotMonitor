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
    public class GetSessionsFunction
    {
        private readonly ILogger<GetSessionsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionsFunction(ILogger<GetSessionsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions")] HttpRequestData req)
        {
            _logger.LogInformation("GetSessions function processing request");

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);
                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

                var parsed = SessionListPagination.ParseQuery(query, acceptFilterTenantId: false);
                if (parsed.Error != null)
                {
                    return await req.BadRequestAsync(parsed.Error);
                }

                _logger.LogInformation(
                    "Fetching sessions (tenant={TenantId}, days={Days}, pageSize={PageSize}, hasContinuation={HasContinuation})",
                    tenantId, parsed.Days, parsed.PageSize, parsed.Continuation != null);

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!SessionListPagination.TryAcceptContinuation(
                            parsed.Continuation, scope: "sessions:tenant",
                            callerTenantId: tenantId, days: parsed.Days, filterTenantId: null,
                            out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("GetSessions: continuation rejected ({Reason})", rejectReason);
                        return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                    }
                }

                var page = await _sessionRepo.GetSessionsPageAsync(tenantId, parsed.Days, parsed.PageSize, azureToken);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = SessionListPagination.Fingerprint(
                        scope: "sessions:tenant", callerTenantId: tenantId, days: parsed.Days);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, tenantId, fp);
                    nextLink = SessionListPagination.BuildNextLink(
                        basePath: "/api/sessions",
                        pageSize: parsed.PageSize,
                        wireContinuation: wireToken,
                        days: parsed.Days,
                        filterTenantId: null);
                }

                return await req.OkAsync(new SessionListResponse
                {
                    Success = true,
                    Count = page.Items.Count,
                    Sessions = page.Items,
                    NextLink = nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetSessions");
            }
        }
    }
}
