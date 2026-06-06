using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class GetAuditLogsFunction
    {
        private readonly ILogger<GetAuditLogsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public GetAuditLogsFunction(ILogger<GetAuditLogsFunction> logger, IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("GetAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetAuditLogs function processing request");

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                string tenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = DateWindowPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = parsed.Error });
                    return bad;
                }

                // "Exclude deletions" view: drop per-session deletion bookkeeping
                // server-side so a cleanup-heavy window still fills a page with
                // real entries. Folded into the fingerprint scope so a token
                // minted for one view can't be replayed against the other.
                var excludeDeletions = string.Equals(query["excludeDeletions"], "true", StringComparison.OrdinalIgnoreCase);
                var fingerprintScope = excludeDeletions ? "audit:tenant:nodel" : "audit:tenant";

                // Optional exact-match field filters (action / performedBy / entityType /
                // entityId). Folded into the fingerprint (via filterExtras) so a token
                // minted for one filter set can't be replayed against another, and echoed
                // on nextLink so the follow-up request re-parses an identical set.
                var filters = AuditLogFilterRequest.Parse(query);
                var filterExtras = AuditLogFilterRequest.ToExtras(filters);

                _logger.LogInformation(
                    "Fetching audit logs (tenant={TenantId}, dateFrom={DateFrom}, dateTo={DateTo}, pageSize={PageSize}, hasContinuation={HasContinuation}, excludeDeletions={ExcludeDeletions}, filters={FilterCount}) for user {User}",
                    tenantId, parsed.DateFrom, parsed.DateTo,
                    parsed.PageSize?.ToString() ?? "all", parsed.Continuation != null, excludeDeletions, filterExtras.Count,
                    userIdentifier);

                if (parsed.PageSize == null)
                {
                    var logs = await _maintenanceRepo.GetAuditLogsAsync(tenantId, parsed.DateFrom, parsed.DateTo, filters);
                    return await req.OkAsync(new { success = true, count = logs.Count, logs });
                }

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!DateWindowPagination.TryAcceptContinuation(
                            parsed.Continuation,
                            scope: fingerprintScope,
                            callerTenantId: tenantId,
                            dateFrom: parsed.DateFrom,
                            dateTo: parsed.DateTo,
                            extras: filterExtras,
                            out azureToken,
                            out var rejectReason))
                    {
                        _logger.LogWarning("GetAuditLogs: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = await _maintenanceRepo.GetAuditLogsPageAsync(
                    tenantId, parsed.DateFrom, parsed.DateTo, parsed.PageSize.Value, azureToken, excludeDeletions, filters);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = DateWindowPagination.Fingerprint(
                        scope: fingerprintScope,
                        callerTenantId: tenantId,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo,
                        extras: filterExtras);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, tenantId, fp);
                    // nextLink must carry both the filter params (so the follow-up
                    // recomputes an identical fingerprint) and excludeDeletions (which
                    // is bound via the scope string, re-derived from the echoed param).
                    var nextLinkExtras = new List<KeyValuePair<string, string?>>(filterExtras);
                    if (excludeDeletions)
                        nextLinkExtras.Add(new KeyValuePair<string, string?>("excludeDeletions", "true"));
                    nextLink = DateWindowPagination.BuildNextLink(
                        basePath: "/api/audit/logs",
                        pageSize: parsed.PageSize.Value,
                        wireContinuation: wireToken,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo,
                        extras: nextLinkExtras);
                }

                return await req.OkAsync(new
                {
                    success = true,
                    count = page.Items.Count,
                    logs = page.Items,
                    nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get audit logs");
            }
        }
    }
}
