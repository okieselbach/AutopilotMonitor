using System.Collections.Generic;
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
    /// <summary>
    /// Global Admin endpoint: returns operational events from the OpsEvents table.
    /// Supports optional category + dateFrom/dateTo filters and opt-in pagination.
    /// Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetOpsEventsFunction
    {
        private readonly ILogger<GetOpsEventsFunction> _logger;
        private readonly IOpsEventRepository _repository;

        public GetOpsEventsFunction(
            ILogger<GetOpsEventsFunction> logger,
            IOpsEventRepository repository)
        {
            _logger = logger;
            _repository = repository;
        }

        [Function("GetOpsEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/ops-events")] HttpRequestData req)
        {
            try
            {
                var callerTenantId = TenantHelper.GetTenantId(req);
                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var category = query["category"];
                var filterTenantId = query["tenantId"];

                var parsed = DateWindowPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = parsed.Error });
                    return bad;
                }

                // OpsEvents are partitioned by category, not tenant — tenantId filter is applied
                // post-fetch. Pages may report fewer items than pageSize when the filter is
                // narrow (Azure scan returns pageSize raw rows; we filter and emit what passes).
                var extrasList = new List<KeyValuePair<string, string?>>();
                if (!string.IsNullOrEmpty(category))
                    extrasList.Add(new KeyValuePair<string, string?>("category", category));
                if (!string.IsNullOrEmpty(filterTenantId))
                    extrasList.Add(new KeyValuePair<string, string?>("tenantId", filterTenantId));
                var extras = extrasList.Count > 0 ? extrasList.ToArray() : null;

                static IEnumerable<OpsEventEntry> ApplyTenantFilter(
                    IEnumerable<OpsEventEntry> source, string? tenantFilter) =>
                    string.IsNullOrEmpty(tenantFilter)
                        ? source
                        : source.Where(e => string.Equals(e.TenantId, tenantFilter, StringComparison.OrdinalIgnoreCase));

                if (parsed.PageSize == null)
                {
                    var events = await _repository.GetOpsEventsAsync(category, parsed.DateFrom, parsed.DateTo);
                    var filtered = ApplyTenantFilter(events, filterTenantId).ToList();
                    return await req.OkAsync(new { success = true, count = filtered.Count, events = filtered });
                }

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!DateWindowPagination.TryAcceptContinuation(
                            parsed.Continuation,
                            scope: "ops-events",
                            callerTenantId: callerTenantId,
                            dateFrom: parsed.DateFrom,
                            dateTo: parsed.DateTo,
                            extras: extras,
                            out azureToken,
                            out var rejectReason))
                    {
                        _logger.LogWarning("GetOpsEvents: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = await _repository.GetOpsEventsPageAsync(
                    category, parsed.DateFrom, parsed.DateTo, parsed.PageSize.Value, azureToken);

                var pageItems = ApplyTenantFilter(page.Items, filterTenantId).ToList();

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = DateWindowPagination.Fingerprint(
                        scope: "ops-events",
                        callerTenantId: callerTenantId,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo,
                        extras: extras);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                    nextLink = DateWindowPagination.BuildNextLink(
                        basePath: "/api/global/ops-events",
                        pageSize: parsed.PageSize.Value,
                        wireContinuation: wireToken,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo,
                        extras: extras);
                }

                return await req.OkAsync(new
                {
                    success = true,
                    count = pageItems.Count,
                    events = pageItems,
                    nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get ops events");
            }
        }
    }
}
