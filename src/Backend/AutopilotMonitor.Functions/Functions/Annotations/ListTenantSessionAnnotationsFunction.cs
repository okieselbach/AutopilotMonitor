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
    /// Tenant-scoped annotation list — backs the portal's Annotations overview page
    /// ("which sessions has my team already judged?"). Same filter surface as the global
    /// evaluation stream, always bound to the middleware-validated target tenant. The
    /// platform-internal globaladmin lane is excluded in the OData query for callers
    /// without global scope, so hidden rows never consume page budget.
    /// </summary>
    public class ListTenantSessionAnnotationsFunction
    {
        private readonly ILogger<ListTenantSessionAnnotationsFunction> _logger;
        private readonly ISessionAnnotationRepository _annotationRepo;

        public ListTenantSessionAnnotationsFunction(
            ILogger<ListTenantSessionAnnotationsFunction> logger,
            ISessionAnnotationRepository annotationRepo)
        {
            _logger = logger;
            _annotationRepo = annotationRepo;
        }

        // Route lives under the /api/sessions prefix so the platform cert-exclusion list
        // (Azure-portal-only config) needs no new entry — same convention as
        // /api/diagnostics/files. Three segments on purpose: a two-segment literal
        // ("sessions/annotations") would be eaten by the sibling "sessions/{sessionId}"
        // template (see the /api/stats/sessions incident note in lib/api.ts).
        [Function("ListTenantSessionAnnotations")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/annotations/list")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var callerTenantId = TenantHelper.GetTenantId(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsedRaw = SessionAnnotationsPagination.ParseQuery(query);
                if (parsedRaw.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = parsedRaw.Error });
                    return bad;
                }

                // The tenant filter is never caller-chosen here: TargetTenantId is the
                // middleware-validated scope (own tenant; the ?tenantId drill-in is validated
                // there too). A body/query value can never widen it.
                var parsed = new SessionAnnotationsPagination.Parsed
                {
                    FilterTenantId = requestCtx.TargetTenantId,
                    Lane = parsedRaw.Lane,
                    Verdict = parsedRaw.Verdict,
                    RuleId = parsedRaw.RuleId,
                    Query = parsedRaw.Query,
                    DateFrom = parsedRaw.DateFrom,
                    DateTo = parsedRaw.DateTo,
                    PageSize = parsedRaw.PageSize,
                    Continuation = parsedRaw.Continuation,
                };

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!SessionAnnotationsPagination.TryAcceptContinuation(
                            parsed, callerTenantId, out azureToken, out var rejectReason))
                    {
                        _logger.LogWarning("ListTenantSessionAnnotations: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var (items, nextRawToken) = await _annotationRepo.QueryPageAsync(
                    parsed.FilterTenantId, parsed.Lane, parsed.Verdict, parsed.RuleId, parsed.Query,
                    parsed.DateFrom, parsed.DateTo, parsed.PageSize, azureToken,
                    excludeGlobalAdminLane: !requestCtx.HasGlobalScope);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(nextRawToken))
                {
                    var wireToken = SessionAnnotationsPagination.EncodeContinuation(
                        parsed, callerTenantId, nextRawToken!);
                    nextLink = SessionAnnotationsPagination.BuildNextLink(
                        parsed, wireToken, SessionAnnotationsPagination.TenantBasePath);
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
                _logger.LogError(ex, "Error listing tenant session annotations");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
