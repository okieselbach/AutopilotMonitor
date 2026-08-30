using System;
using System.Linq;
using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Annotations
{
    /// <summary>
    /// Cross-tenant evaluation stream over all annotation lanes (the flywheel read:
    /// verdicts per rule, false-positive rates, labeled sessions). Platform scope for the
    /// aggregate; a delegated ("MSP") reader reaches it only on its managed ?tenantId= path
    /// and never sees the platform-internal globaladmin lane.
    /// </summary>
    public class ListSessionAnnotationsFunction
    {
        private readonly ILogger<ListSessionAnnotationsFunction> _logger;
        private readonly ISessionAnnotationRepository _annotationRepo;

        public ListSessionAnnotationsFunction(
            ILogger<ListSessionAnnotationsFunction> logger,
            ISessionAnnotationRepository annotationRepo)
        {
            _logger = logger;
            _annotationRepo = annotationRepo;
        }

        [Function("ListSessionAnnotations")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/session-annotations")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var callerTenantId = TenantHelper.GetTenantId(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = SessionAnnotationsPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = parsed.Error });
                    return bad;
                }

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!SessionAnnotationsPagination.TryAcceptContinuation(
                            parsed, callerTenantId, out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("ListSessionAnnotations: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                // Not only GA/Reader reach this route: the delegated ("MSP") read rescue admits a
                // delegated caller on its managed ?tenantId= path. The platform-internal globaladmin
                // lane stays global-scope-only, so it is excluded for everyone else.
                var (items, nextRawToken) = await _annotationRepo.QueryPageAsync(
                    parsed.FilterTenantId, parsed.Lane, parsed.Verdict, parsed.RuleId,
                    parsed.DateFrom, parsed.DateTo, parsed.PageSize, azureToken,
                    excludeGlobalAdminLane: !requestCtx.HasGlobalScope);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(nextRawToken))
                {
                    var wireToken = SessionAnnotationsPagination.EncodeContinuation(
                        parsed, callerTenantId, nextRawToken!);
                    nextLink = SessionAnnotationsPagination.BuildNextLink(parsed, wireToken);
                }

                return await req.OkAsync(new SessionAnnotationListResponse
                {
                    Success = true,
                    Count = items.Count,
                    Annotations = items.Select(AnnotationWire.ToWireWithScope).ToList(),
                    NextLink = nextLink,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing session annotations");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
