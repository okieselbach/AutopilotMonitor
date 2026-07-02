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

namespace AutopilotMonitor.Functions.Functions.Sessions;

public class SearchSessionsFunction
{
    private readonly ILogger<SearchSessionsFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    public SearchSessionsFunction(ILogger<SearchSessionsFunction> logger, ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    [Function("SearchSessions")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search/sessions")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/search/sessions")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: false);

    private async Task<HttpResponseData> HandleAsync(HttpRequestData req, bool isTenantScoped)
    {
        try
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

            // The endpoint binding tenantId — for tenant-scoped routes the JWT
            // is authoritative. For global routes the JWT just identifies the
            // caller; tenantId in the query is a filter, not authorization.
            string? tenantId;
            string callerTenantId = TenantHelper.GetTenantId(req);
            string? filterTenantId = null;
            string scope;
            string basePath;

            if (isTenantScoped)
            {
                tenantId = callerTenantId;
                scope = "search:tenant";
                basePath = "/api/search/sessions";
            }
            else
            {
                filterTenantId = query["tenantId"];
                tenantId = filterTenantId;
                scope = "search:global";
                basePath = "/api/global/search/sessions";
            }

            var filter = BuildFilter(query);

            var pagination = SearchSessionsPagination.ParsePagination(query);
            if (pagination.Error != null)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { success = false, message = pagination.Error });
                return bad;
            }

            string? azureToken = null;
            if (pagination.Continuation != null)
            {
                if (!SearchSessionsPagination.TryAcceptContinuation(
                        pagination.Continuation, scope, callerTenantId, filterTenantId, filter,
                        out azureToken, out var rejectReason))
                {
                    _logger.LogWarning("SearchSessions: continuation rejected ({Reason})", rejectReason);
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                    });
                    return bad;
                }
            }

            var page = await _sessionRepo.SearchSessionsPageAsync(
                tenantId, filter, pagination.PageSize, azureToken);

            // Optional field projection — when the LLM only needs a few columns
            // (e.g. for counting / aggregation), this prunes the wire payload by
            // 5-20x and avoids the client-side response cap that fat
            // SessionSummary objects routinely trip.
            var fieldsParam = query["fields"];
            object sessionsPayload = !string.IsNullOrEmpty(fieldsParam)
                ? ProjectSessionFields(page.Items, fieldsParam!)
                : page.Items;

            string? nextLink = null;
            if (!string.IsNullOrEmpty(page.NextRawToken))
            {
                var fp = SearchSessionsPagination.Fingerprint(scope, callerTenantId, filterTenantId, filter);
                var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                nextLink = SearchSessionsPagination.BuildNextLink(
                    basePath, pagination.PageSize, wireToken, query);
            }

            return await req.OkAsync(new
            {
                success = true,
                count = page.Items.Count,
                sessions = sessionsPayload,
                nextLink,
            });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "Search sessions");
        }
    }

    private static SessionSearchFilter BuildFilter(System.Collections.Specialized.NameValueCollection query)
    {
        var filter = new SessionSearchFilter
        {
            Status = query["status"],
            SerialNumber = query["serialNumber"],
            DeviceName = query["deviceName"],
            Manufacturer = query["manufacturer"],
            Model = query["model"],
            OsBuild = query["osBuild"],
            EnrollmentType = query["enrollmentType"],
            GeoCountry = query["geoCountry"],
            AgentVersion = query["agentVersion"],
            ImeAgentVersion = query["imeAgentVersion"],
            AgentVersionPrefix = query["agentVersionPrefix"],
            ImeAgentVersionPrefix = query["imeAgentVersionPrefix"],
            // Limit field is a no-op in the paged path — pagination drives count.
        };

        if (bool.TryParse(query["isPreProvisioned"], out var ipp)) filter.IsPreProvisioned = ipp;
        if (bool.TryParse(query["isHybridJoin"], out var ihj)) filter.IsHybridJoin = ihj;
        if (bool.TryParse(query["isSelfDeployingProfile"], out var isdp)) filter.IsSelfDeployingProfile = isdp;
        if (DateTime.TryParse(query["startedAfter"], out var sa)) filter.StartedAfter = sa;
        if (DateTime.TryParse(query["startedBefore"], out var sb)) filter.StartedBefore = sb;
        if (int.TryParse(query["rebootCountMin"], out var rcMin)) filter.RebootCountMin = rcMin;
        if (int.TryParse(query["rebootCountMax"], out var rcMax)) filter.RebootCountMax = rcMax;

        // Dynamic device property filters: any query param starting with "prop."
        var deviceProperties = new Dictionary<string, string>();
        foreach (string? key in query.AllKeys)
        {
            if (key != null && key.StartsWith("prop.", StringComparison.OrdinalIgnoreCase))
            {
                var propName = key.Substring(5);
                var value = query[key];
                if (!string.IsNullOrEmpty(value))
                    deviceProperties[propName] = value;
            }
        }
        if (deviceProperties.Count > 0)
            filter.DeviceProperties = deviceProperties;

        return filter;
    }

    /// <summary>
    /// Projects SessionSummary objects down to the comma-separated field set.
    /// Mirrors the projection table from QueryRawSessionsFunction — kept in sync
    /// so the same field names work on both endpoints. If no field name matches,
    /// returns a sensible default subset so callers passing typos still get
    /// useful data instead of empty objects.
    /// </summary>
    private static List<Dictionary<string, object?>> ProjectSessionFields(
        IReadOnlyList<SessionSummary> sessions, string fieldsParam)
    {
        var fields = new HashSet<string>(
            fieldsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        var projected = new List<Dictionary<string, object?>>(sessions.Count);
        foreach (var s in sessions)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (fields.Contains("sessionId")) dict["sessionId"] = s.SessionId;
            if (fields.Contains("tenantId")) dict["tenantId"] = s.TenantId;
            if (fields.Contains("status")) dict["status"] = s.Status.ToString();
            if (fields.Contains("serialNumber")) dict["serialNumber"] = s.SerialNumber;
            if (fields.Contains("manufacturer")) dict["manufacturer"] = s.Manufacturer;
            if (fields.Contains("model")) dict["model"] = s.Model;
            if (fields.Contains("deviceName")) dict["deviceName"] = s.DeviceName;
            if (fields.Contains("osBuild")) dict["osBuild"] = s.OsBuild;
            if (fields.Contains("osName")) dict["osName"] = s.OsName;
            if (fields.Contains("startedAt")) dict["startedAt"] = s.StartedAt;
            if (fields.Contains("completedAt")) dict["completedAt"] = s.CompletedAt;
            if (fields.Contains("durationSeconds")) dict["durationSeconds"] = s.DurationSeconds;
            if (fields.Contains("currentPhase")) dict["currentPhase"] = s.CurrentPhase;
            if (fields.Contains("failureReason")) dict["failureReason"] = s.FailureReason;
            if (fields.Contains("eventCount")) dict["eventCount"] = s.EventCount;
            if (fields.Contains("enrollmentType")) dict["enrollmentType"] = s.EnrollmentType;
            if (fields.Contains("isPreProvisioned")) dict["isPreProvisioned"] = s.IsPreProvisioned;
            if (fields.Contains("isUserDriven")) dict["isUserDriven"] = s.IsUserDriven;
            if (fields.Contains("isHybridJoin")) dict["isHybridJoin"] = s.IsHybridJoin;
            if (fields.Contains("isSelfDeployingProfile")) dict["isSelfDeployingProfile"] = s.IsSelfDeployingProfile;
            if (fields.Contains("agentVersion")) dict["agentVersion"] = s.AgentVersion;
            if (fields.Contains("imeAgentVersion")) dict["imeAgentVersion"] = s.ImeAgentVersion;
            if (fields.Contains("geoCountry")) dict["geoCountry"] = s.GeoCountry;
            if (fields.Contains("rebootCount")) dict["rebootCount"] = s.RebootCount;

            if (dict.Count == 0)
            {
                // No field name matched — fall back to a lean default set.
                dict["sessionId"] = s.SessionId;
                dict["tenantId"] = s.TenantId;
                dict["status"] = s.Status.ToString();
                dict["startedAt"] = s.StartedAt;
            }
            projected.Add(dict);
        }
        return projected;
    }
}
