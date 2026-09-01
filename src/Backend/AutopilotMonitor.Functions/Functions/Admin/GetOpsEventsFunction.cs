using System.Collections.Generic;
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

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Cross-tenant READ endpoint for PLATFORM-OPERATIONAL events (Consent/Maintenance/Security/Tenant/Agent/
    /// SLA). Supports optional category + eventType/severity/minSeverity + dateFrom/dateTo filters (all
    /// server-side except the tenant drill) and opt-in pagination. Authentication +
    /// GlobalReadOrAdmin + TenantScoping.None enforced by PolicyEnforcementMiddleware — a Global Admin and the
    /// read-only Global Reader reach it, but a delegated ("MSP") admin does NOT: ops-events is the platform
    /// operator's view, not customer telemetry, so it stays GA/Reader-only (None ⇒ no scoped-route rescue for
    /// delegated; see EndpointAccessPolicyCatalog + PolicyEnforcementMiddlewareTests.Delegated_OpsEvents_*).
    /// The optional ?tenantId= is a DRILL for a GA/Reader (who already see all tenants); OpsEvents is
    /// partitioned by CATEGORY (not tenant), so <see cref="FilterByTenant"/> applies the drill in-memory.
    /// That filter is drill CORRECTNESS, not a tenant-isolation boundary — every caller here already has full
    /// cross-tenant scope.
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

                // Optional exact-match field filters (eventType / severity / minSeverity). Unlike
                // the tenantId drill below these are pushed into the STORAGE query, so an operator
                // asking for one event type no longer pulls a whole category over the wire.
                var fieldFilters = OpsEventFilterRequest.Parse(query);
                if (fieldFilters.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = fieldFilters.Error });
                    return bad;
                }
                var filters = fieldFilters.Filters;

                // OpsEvents are partitioned by category, not tenant — tenantId filter is applied
                // post-fetch. Pages may report fewer items than pageSize when the filter is
                // narrow (Azure scan returns pageSize raw rows; we filter and emit what passes).
                // The field filters are appended AFTER the pre-existing discriminators so a token
                // minted before they existed still fingerprints identically when none are named.
                var extrasList = new List<KeyValuePair<string, string?>>();
                if (!string.IsNullOrEmpty(category))
                    extrasList.Add(new KeyValuePair<string, string?>("category", category));
                if (!string.IsNullOrEmpty(filterTenantId))
                    extrasList.Add(new KeyValuePair<string, string?>("tenantId", filterTenantId));
                extrasList.AddRange(OpsEventFilterRequest.ToExtras(filters));
                var extras = extrasList.Count > 0 ? extrasList.ToArray() : null;

                if (parsed.PageSize == null)
                {
                    var events = await _repository.GetOpsEventsAsync(category, parsed.DateFrom, parsed.DateTo, filters);
                    // Drill correctness: apply the optional ?tenantId= GA/Reader drill in-memory (OpsEvents PK
                    // = category, so the storage query can't). Not a tenant-isolation boundary — see class doc.
                    var filtered = FilterByTenant(events, filterTenantId).ToList();
                    return await req.OkAsync(new OpsEventListResponse { Success = true, Count = filtered.Count, Events = filtered });
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
                    category, parsed.DateFrom, parsed.DateTo, parsed.PageSize.Value, azureToken, filters);

                // Drill correctness: same optional ?tenantId= GA/Reader drill on the paged path — see FilterByTenant.
                var pageItems = FilterByTenant(page.Items, filterTenantId).ToList();

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

                return await req.OkAsync(new OpsEventListResponse
                {
                    Success = true,
                    Count = pageItems.Count,
                    Events = pageItems,
                    NextLink = nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get ops events");
            }
        }

        /// <summary>
        /// Applies the optional ?tenantId= DRILL to an OpsEvents result set: OpsEvents rows are partitioned by
        /// CATEGORY, not tenant, so the storage query can't filter by tenant — this in-memory pass does. A
        /// null/empty filter (the default GA/Reader cross-tenant view) passes everything through. This is
        /// DRILL CORRECTNESS, not a tenant-isolation boundary: ops-events is GA/Reader-only (catalog
        /// TenantScoping.None — a delegated caller cannot reach the route), and a GA/Reader already has full
        /// cross-tenant scope. Internal so the drill has direct unit coverage (GetOpsEventsFunctionTests).
        /// </summary>
        internal static IEnumerable<OpsEventEntry> FilterByTenant(
            IEnumerable<OpsEventEntry> source, string? tenantFilter) =>
            string.IsNullOrEmpty(tenantFilter)
                ? source
                : source.Where(e => string.Equals(e.TenantId, tenantFilter, StringComparison.OrdinalIgnoreCase));
    }
}
