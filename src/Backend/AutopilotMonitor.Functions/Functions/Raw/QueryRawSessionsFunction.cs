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

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class QueryRawSessionsFunction
    {
        private readonly ILogger<QueryRawSessionsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public QueryRawSessionsFunction(ILogger<QueryRawSessionsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// GET /api/raw/sessions — Tenant-scoped raw session query
        /// </summary>
        [Function("QueryRawSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw/sessions")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);
                return await QuerySessions(req, tenantId, scope: "raw-sessions:tenant",
                    basePath: "/api/raw/sessions", filterTenantId: null);
            }
            catch (UnauthorizedAccessException)
            {
                var err = req.CreateResponse(HttpStatusCode.Unauthorized);
                await err.WriteAsJsonAsync(new { error = "Unauthorized" });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query raw sessions");
            }
        }

        /// <summary>
        /// GET /api/global/raw/sessions — Cross-tenant raw session query (GlobalAdminOnly)
        /// </summary>
        [Function("QueryRawSessionsGlobal")]
        public async Task<HttpResponseData> RunGlobal(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/sessions")] HttpRequestData req)
        {
            try
            {
                var filterTenantId = req.Query["tenantId"];
                var effectiveTenantId = string.IsNullOrEmpty(filterTenantId) ? null : filterTenantId;
                return await QuerySessions(req, effectiveTenantId, scope: "raw-sessions:global",
                    basePath: "/api/global/raw/sessions", filterTenantId: effectiveTenantId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query global raw sessions");
            }
        }

        private async Task<HttpResponseData> QuerySessions(
            HttpRequestData req, string? tenantId, string scope, string basePath, string? filterTenantId)
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

            var status = query["status"];
            var startedAfter = query["startedAfter"];
            var startedBefore = query["startedBefore"];
            var serialNumber = query["serialNumber"];
            var agentVersion = query["agentVersion"];
            var agentVersionPrefix = query["agentVersionPrefix"];
            var imeAgentVersion = query["imeAgentVersion"];
            var imeAgentVersionPrefix = query["imeAgentVersionPrefix"];
            // Device/hardware scalar filters — parity with search_sessions so the raw tool is a true
            // superset, not a subset. All are already supported by SessionSearchFilter +
            // BuildSearchScanFilter / MatchesScanClientFilters; we only plumb the query params through.
            var manufacturer = query["manufacturer"];
            var model = query["model"];
            var enrollmentType = query["enrollmentType"];
            var deviceName = query["deviceName"];
            var osBuild = query["osBuild"];
            var geoCountry = query["geoCountry"];
            var isPreProvisioned = query["isPreProvisioned"];
            var isHybridJoin = query["isHybridJoin"];
            var fieldsParam = query["fields"];

            var pagination = SearchSessionsPagination.ParsePagination(query);
            if (pagination.Error != null)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { error = pagination.Error });
                return bad;
            }

            // Build search filter — Limit is ignored, pagination owns size.
            var filter = new SessionSearchFilter
            {
                Status = string.IsNullOrEmpty(status) ? null : status,
                SerialNumber = string.IsNullOrEmpty(serialNumber) ? null : serialNumber,
                AgentVersion = string.IsNullOrEmpty(agentVersion) ? null : agentVersion,
                AgentVersionPrefix = string.IsNullOrEmpty(agentVersionPrefix) ? null : agentVersionPrefix,
                ImeAgentVersion = string.IsNullOrEmpty(imeAgentVersion) ? null : imeAgentVersion,
                ImeAgentVersionPrefix = string.IsNullOrEmpty(imeAgentVersionPrefix) ? null : imeAgentVersionPrefix,
                Manufacturer = string.IsNullOrEmpty(manufacturer) ? null : manufacturer,
                Model = string.IsNullOrEmpty(model) ? null : model,
                EnrollmentType = string.IsNullOrEmpty(enrollmentType) ? null : enrollmentType,
                DeviceName = string.IsNullOrEmpty(deviceName) ? null : deviceName,
                OsBuild = string.IsNullOrEmpty(osBuild) ? null : osBuild,
                GeoCountry = string.IsNullOrEmpty(geoCountry) ? null : geoCountry,
            };
            if (!string.IsNullOrEmpty(startedAfter) && DateTime.TryParse(startedAfter, out var after))
                filter.StartedAfter = after;
            if (!string.IsNullOrEmpty(startedBefore) && DateTime.TryParse(startedBefore, out var before))
                filter.StartedBefore = before;
            if (!string.IsNullOrEmpty(isPreProvisioned) && bool.TryParse(isPreProvisioned, out var pp))
                filter.IsPreProvisioned = pp;
            if (!string.IsNullOrEmpty(isHybridJoin) && bool.TryParse(isHybridJoin, out var hj))
                filter.IsHybridJoin = hj;
            if (int.TryParse(query["rebootCountMin"], out var rcMin)) filter.RebootCountMin = rcMin;
            if (int.TryParse(query["rebootCountMax"], out var rcMax)) filter.RebootCountMax = rcMax;

            var callerTenantId = TenantHelper.GetTenantId(req);

            string? azureToken = null;
            if (pagination.Continuation != null)
            {
                if (!SearchSessionsPagination.TryAcceptContinuation(
                        pagination.Continuation, scope, callerTenantId, filterTenantId, filter,
                        out azureToken, out var rejectReason))
                {
                    _logger.LogWarning("QueryRawSessions: continuation rejected ({Reason})", rejectReason);
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new
                    {
                        error = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                    });
                    return bad;
                }
            }

            var page = await _sessionRepo.SearchSessionsRawPageAsync(tenantId, filter, pagination.PageSize, azureToken);

            // Raw projection: the rows are the literal SessionsIndex columns. Optional fields= is a
            // pure pass-through (narrow, never silently drop a real column); empty fields= returns
            // every stored column. It's presentation-only and doesn't touch cursor mechanics.
            var sessionsPayload = RawEntityProjection.Project(page.Items, fieldsParam);

            string? nextLink = null;
            if (!string.IsNullOrEmpty(page.NextRawToken))
            {
                var fp = SearchSessionsPagination.Fingerprint(scope, callerTenantId, filterTenantId, filter);
                var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                nextLink = SearchSessionsPagination.BuildNextLink(basePath, pagination.PageSize, wireToken, query);
            }

            return await req.OkAsync(new
            {
                tenantId,
                count = page.Items.Count,
                sessions = sessionsPayload,
                nextLink,
            });
        }
    }
}
