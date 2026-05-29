using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class QueryRawEventsFunction
    {
        private readonly ILogger<QueryRawEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public QueryRawEventsFunction(ILogger<QueryRawEventsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// GET /api/raw/events — Tenant-scoped raw event query (cross-session)
        /// </summary>
        [Function("QueryRawEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw/events")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);
                return await QueryEvents(req, tenantId, scope: "raw-events:tenant", basePath: "/api/raw/events", filterTenantId: null);
            }
            catch (UnauthorizedAccessException)
            {
                var err = req.CreateResponse(HttpStatusCode.Unauthorized);
                await err.WriteAsJsonAsync(new { error = "Unauthorized" });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query raw events");
            }
        }

        /// <summary>
        /// GET /api/global/raw/events — Cross-tenant raw event query (GlobalAdminOnly)
        /// Omit tenantId to query across all tenants.
        /// </summary>
        [Function("QueryRawEventsGlobal")]
        public async Task<HttpResponseData> RunGlobal(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/events")] HttpRequestData req)
        {
            try
            {
                var filterTenantId = req.Query["tenantId"];
                var effectiveTenantId = string.IsNullOrEmpty(filterTenantId) ? null : filterTenantId;
                return await QueryEvents(req, effectiveTenantId, scope: "raw-events:global",
                    basePath: "/api/global/raw/events", filterTenantId: effectiveTenantId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query global raw events");
            }
        }

        private async Task<HttpResponseData> QueryEvents(
            HttpRequestData req, string? tenantId, string scope, string basePath, string? filterTenantId)
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
            var sessionId = query["sessionId"];
            var eventType = query["eventType"];
            var severity = query["severity"];
            var source = query["source"];
            var startedAfter = query["startedAfter"];
            var startedBefore = query["startedBefore"];
            var fields = query["fields"];

            var pagination = QueryRawEventsPagination.ParsePagination(query);
            if (pagination.Error != null)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { error = pagination.Error });
                return bad;
            }

            var callerTenantId = TenantHelper.GetTenantId(req);

            // Single-session path — paginated session-events walk so sessions
            // with more events than pageSize remain fully reachable across
            // multiple calls. Continuation token binds caller + sessionId so
            // a cursor from session A cannot be replayed for session B.
            if (!string.IsNullOrEmpty(sessionId))
            {
                // GA cross-tenant convenience: a sessionId query may omit tenantId; resolve it from
                // the session so the contract matches GetSessionEventsFunction. Only on the global
                // scope — the tenant-scoped path always has a JWT-bound tenantId (TenantHelper
                // throws when unauthenticated), so it never reaches this branch with an empty value.
                if (string.IsNullOrEmpty(tenantId) && scope == "raw-events:global")
                {
                    tenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                }
                if (string.IsNullOrEmpty(tenantId))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = "tenantId is required when querying by sessionId (or the session was not found)" });
                    return bad;
                }

                string? singleAzureToken = null;
                if (pagination.Continuation != null)
                {
                    if (!QueryRawEventsPagination.TryAcceptContinuation(
                            pagination.Continuation, scope, callerTenantId, filterTenantId,
                            sessionId, eventType, source, severity, startedAfter, startedBefore,
                            out singleAzureToken, out var rejectReason))
                    {
                        _logger.LogWarning("QueryRawEvents: single-session continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            error = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var sessionPage = await _sessionRepo.GetSessionEventsPageAsync(
                    tenantId, sessionId, pagination.PageSize, singleAzureToken);

                var filtered = ApplyClientFilters(
                    sessionPage.Items.ToList(), eventType, severity, source, startedAfter, startedBefore);
                filtered = filtered.OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).ToList();
                // Skip error-code enrichment when the projection drops Data (enrichment only writes
                // into Data) — pure work avoidance for lean fields= queries.
                if (EventFieldProjection.WantsData(fields))
                    ErrorCodeEnricher.EnrichEvents(filtered);

                string? singleNextLink = null;
                if (!string.IsNullOrEmpty(sessionPage.NextRawToken))
                {
                    var fp = QueryRawEventsPagination.Fingerprint(
                        scope, callerTenantId, filterTenantId,
                        sessionId, eventType, source, severity, startedAfter, startedBefore);
                    var wireToken = ContinuationToken.Encode(sessionPage.NextRawToken!, callerTenantId, fp);
                    singleNextLink = QueryRawEventsPagination.BuildNextLink(
                        basePath, pagination.PageSize, wireToken, query);
                }

                return await req.OkAsync(new
                {
                    tenantId,
                    count = filtered.Count,
                    events = EventFieldProjection.Project(filtered, fields),
                    nextLink = singleNextLink,
                });
            }

            if (string.IsNullOrEmpty(eventType))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { error = "Either sessionId or eventType is required for raw event queries" });
                return bad;
            }

            // Cross-session path — paginated EventTypeIndex walk replaces the
            // legacy hard-coded limit:20 (recall-loss bug from the audit).
            string? azureToken = null;
            if (pagination.Continuation != null)
            {
                if (!QueryRawEventsPagination.TryAcceptContinuation(
                        pagination.Continuation, scope, callerTenantId, filterTenantId,
                        sessionId: null, eventType, source, severity, startedAfter, startedBefore,
                        out azureToken, out var rejectReason))
                {
                    _logger.LogWarning("QueryRawEvents: continuation rejected ({Reason})", rejectReason);
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new
                    {
                        error = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                    });
                    return bad;
                }
            }

            var sessionsPage = await _sessionRepo.SearchSessionsByEventPageAsync(
                tenantId, eventType, source, severity, phase: null,
                pageSize: pagination.PageSize, continuation: azureToken);

            var events = new List<EnrollmentEvent>();
            foreach (var session in sessionsPage.Items)
            {
                var sessionEvents = await _sessionRepo.GetSessionEventsByTypeAsync(
                    session.TenantId, session.SessionId, eventType, maxResults: 200);
                events.AddRange(sessionEvents.Where(e =>
                    (string.IsNullOrEmpty(severity) || e.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(source) || (e.Source ?? "").Contains(source, StringComparison.OrdinalIgnoreCase))));
            }

            events = ApplyDateFilters(events, startedAfter, startedBefore)
                .OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).ToList();
            if (EventFieldProjection.WantsData(fields))
                ErrorCodeEnricher.EnrichEvents(events);

            string? nextLink = null;
            if (!string.IsNullOrEmpty(sessionsPage.NextRawToken))
            {
                var fp = QueryRawEventsPagination.Fingerprint(
                    scope, callerTenantId, filterTenantId,
                    sessionId: null, eventType, source, severity, startedAfter, startedBefore);
                var wireToken = ContinuationToken.Encode(sessionsPage.NextRawToken!, callerTenantId, fp);
                nextLink = QueryRawEventsPagination.BuildNextLink(
                    basePath, pagination.PageSize, wireToken, query);
            }

            return await req.OkAsync(new
            {
                tenantId,
                count = events.Count,
                events = EventFieldProjection.Project(events, fields),
                nextLink,
            });
        }

        private static List<EnrollmentEvent> ApplyClientFilters(
            List<EnrollmentEvent> events, string? eventType, string? severity, string? source,
            string? startedAfter, string? startedBefore)
        {
            if (!string.IsNullOrEmpty(eventType))
                events = events.Where(e => e.EventType == eventType).ToList();
            if (!string.IsNullOrEmpty(severity))
                events = events.Where(e => e.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(source))
                events = events.Where(e => (e.Source ?? "").Contains(source, StringComparison.OrdinalIgnoreCase)).ToList();
            return ApplyDateFilters(events, startedAfter, startedBefore);
        }

        private static List<EnrollmentEvent> ApplyDateFilters(
            List<EnrollmentEvent> events, string? startedAfter, string? startedBefore)
        {
            if (!string.IsNullOrEmpty(startedAfter) && DateTime.TryParse(startedAfter, out var after))
                events = events.Where(e => e.Timestamp >= after).ToList();
            if (!string.IsNullOrEmpty(startedBefore) && DateTime.TryParse(startedBefore, out var before))
                events = events.Where(e => e.Timestamp <= before).ToList();
            return events;
        }
    }
}
