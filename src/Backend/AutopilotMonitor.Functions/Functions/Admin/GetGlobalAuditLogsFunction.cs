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
    public class GetGlobalAuditLogsFunction
    {
        private readonly ILogger<GetGlobalAuditLogsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public GetGlobalAuditLogsFunction(
            ILogger<GetGlobalAuditLogsFunction> logger,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("GetGlobalAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetGlobalAuditLogs function processing request (Global Admin Mode)");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var callerTenantId = TenantHelper.GetTenantId(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = DateWindowPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = parsed.Error });
                    return bad;
                }

                // Optional tenantId filter — when set, GA scopes the cross-tenant view to one
                // tenant. Routing falls through to the tenant-scoped repository methods so
                // the partition-key lookup stays cheap. Fingerprint scope is widened with the
                // filter value so cross-token reuse is rejected.
                var filterTenantId = query["tenantId"];
                // "Exclude deletions" view — same semantics as the tenant endpoint. The
                // flag is folded into the fingerprint scope (so a token can't cross views)
                // and echoed on nextLink for self-contained follow-up requests.
                var excludeDeletions = string.Equals(query["excludeDeletions"], "true", StringComparison.OrdinalIgnoreCase);
                var baseScope = string.IsNullOrEmpty(filterTenantId)
                    ? "audit:global"
                    : $"audit:global:tenant:{filterTenantId}";
                var fingerprintScope = excludeDeletions ? $"{baseScope}:nodel" : baseScope;
                // Optional exact-match field filters (action / performedBy / entityType /
                // entityId). Appended to nextLinkExtras so they are bound into the
                // fingerprint (cross-filter token replay is rejected) AND echoed on
                // nextLink for self-contained follow-up requests — same mechanism the
                // tenantId/excludeDeletions discriminators already use here.
                var filters = AuditLogFilterRequest.Parse(query);
                var filterExtras = AuditLogFilterRequest.ToExtras(filters);
                var nextLinkExtras = new List<KeyValuePair<string, string?>>();
                if (!string.IsNullOrEmpty(filterTenantId))
                    nextLinkExtras.Add(new KeyValuePair<string, string?>("tenantId", filterTenantId));
                if (excludeDeletions)
                    nextLinkExtras.Add(new KeyValuePair<string, string?>("excludeDeletions", "true"));
                nextLinkExtras.AddRange(filterExtras);

                _logger.LogInformation(
                    "Fetching global audit logs (dateFrom={DateFrom}, dateTo={DateTo}, pageSize={PageSize}, filterTenant={Filter}, excludeDeletions={ExcludeDeletions}, filters={FilterCount}) for {User}",
                    parsed.DateFrom, parsed.DateTo, parsed.PageSize?.ToString() ?? "all",
                    filterTenantId ?? "(none)", excludeDeletions, filterExtras.Count, userEmail);

                if (parsed.PageSize == null)
                {
                    var logs = string.IsNullOrEmpty(filterTenantId)
                        ? await _maintenanceRepo.GetAllAuditLogsAsync(parsed.DateFrom, parsed.DateTo, filters)
                        : await _maintenanceRepo.GetAuditLogsAsync(filterTenantId, parsed.DateFrom, parsed.DateTo, filters);
                    return await req.OkAsync(new { success = true, count = logs.Count, logs });
                }

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!DateWindowPagination.TryAcceptContinuation(
                            parsed.Continuation,
                            scope: fingerprintScope,
                            callerTenantId: callerTenantId,
                            dateFrom: parsed.DateFrom,
                            dateTo: parsed.DateTo,
                            extras: nextLinkExtras,
                            out azureToken,
                            out var rejectReason))
                    {
                        _logger.LogWarning("GetGlobalAuditLogs: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = string.IsNullOrEmpty(filterTenantId)
                    ? await _maintenanceRepo.GetAllAuditLogsPageAsync(
                        parsed.DateFrom, parsed.DateTo, parsed.PageSize.Value, azureToken, excludeDeletions, filters)
                    : await _maintenanceRepo.GetAuditLogsPageAsync(
                        filterTenantId, parsed.DateFrom, parsed.DateTo, parsed.PageSize.Value, azureToken, excludeDeletions, filters);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = DateWindowPagination.Fingerprint(
                        scope: fingerprintScope,
                        callerTenantId: callerTenantId,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo,
                        extras: nextLinkExtras);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                    nextLink = DateWindowPagination.BuildNextLink(
                        basePath: "/api/global/audit/logs",
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
                return await req.InternalServerErrorAsync(_logger, ex, "Get global audit logs");
            }
        }
    }
}
