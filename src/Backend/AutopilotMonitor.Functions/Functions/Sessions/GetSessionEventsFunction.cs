using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetSessionEventsFunction
    {
        private readonly ILogger<GetSessionEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionEventsFunction(
            ILogger<GetSessionEventsFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/events")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
                return badRequestResponse;
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
            var pagination = SessionEventsPagination.ParseQuery(query);
            if (pagination.Error != null)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { success = false, message = pagination.Error });
                return bad;
            }

            _logger.LogInformation(
                "{Prefix} GetSessionEvents: Fetching events (pageSize={PageSize}, hasContinuation={HasContinuation})",
                sessionPrefix, pagination.PageSize?.ToString() ?? "all", pagination.Continuation != null);

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                // Cross-tenant access check handled by middleware (TargetTenantId)
                var requestCtx = req.GetRequestContext();
                var tenantIdQueryParam = query["tenantId"];

                // Optional server-side filters (post-fetch). Previously these lived in the MCP
                // tool layer, which made count and nextLink semantics inconsistent (filter →
                // count: 0 + nextLink "more ahead" was confusing). Centralizing here keeps
                // count and nextLink coherent for every consumer.
                var filterEventType = query["eventType"];
                var filterSeverity = query["severity"];
                var filterSource = query["source"];
                var hasFilters = !string.IsNullOrEmpty(filterEventType)
                    || !string.IsNullOrEmpty(filterSeverity)
                    || !string.IsNullOrEmpty(filterSource);

                static IEnumerable<EnrollmentEvent> ApplyFilters(
                    IEnumerable<EnrollmentEvent> source,
                    string? eventType, string? severity, string? sourceFilter)
                {
                    EventSeverity? wantSeverity = null;
                    if (!string.IsNullOrEmpty(severity)
                        && Enum.TryParse<EventSeverity>(severity, ignoreCase: true, out var parsed))
                    {
                        wantSeverity = parsed;
                    }

                    foreach (var e in source)
                    {
                        if (!string.IsNullOrEmpty(eventType) &&
                            !string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (wantSeverity.HasValue && e.Severity != wantSeverity.Value)
                            continue;
                        if (!string.IsNullOrEmpty(sourceFilter) &&
                            (e.Source == null ||
                             e.Source.IndexOf(sourceFilter, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        yield return e;
                    }
                }

                if (pagination.PageSize == null)
                {
                    // Legacy unpaginated path — full list, no nextLink.
                    var events = await _sessionRepo.GetSessionEventsAsync(requestCtx.TargetTenantId, sessionId);

                    if (events.Count == 0 && requestCtx.IsGlobalAdmin)
                    {
                        var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                        if (resolvedTenantId != null && !string.Equals(resolvedTenantId, requestCtx.TargetTenantId, StringComparison.OrdinalIgnoreCase))
                        {
                            events = await _sessionRepo.GetSessionEventsAsync(resolvedTenantId, sessionId);
                        }
                    }

                    var filtered = hasFilters
                        ? ApplyFilters(events, filterEventType, filterSeverity, filterSource).ToList()
                        : events;

                    ErrorCodeEnricher.EnrichEvents(filtered);

                    return await req.OkAsync(new
                    {
                        success = true,
                        sessionId,
                        count = filtered.Count,
                        events = filtered,
                    });
                }

                // Paginated path. Continuation token binds (tenantId, sessionId), so the
                // tenantId on every page must match the tenant the token was issued for.
                //
                // For Global Admin cross-tenant, the nextLink we emit echoes that tenant
                // back as ?tenantId=, so follow-up pages can re-bind to it. On page 1, if
                // GA hasn't passed tenantId yet, we resolve it from the session lookup.
                //
                // For non-GA, ?tenantId= in the URL is ignored — middleware-bound JWT
                // tenant is authoritative; we must never let a query param override it.
                var effectiveTenantId = requestCtx.TargetTenantId;

                if (requestCtx.IsGlobalAdmin)
                {
                    if (!string.IsNullOrEmpty(tenantIdQueryParam))
                    {
                        // Caller passed an explicit tenantId — including via the
                        // nextLink we emitted on a prior page. Cheap-path: trust it,
                        // skip the storage probe.
                        effectiveTenantId = tenantIdQueryParam;
                    }
                    else
                    {
                        // No tenantId in URL. Resolve it from the session lookup
                        // *on every page*, not just page 1, so external callers
                        // that strip nextLink down to the bare continuation token
                        // (older MCP clients, deployed agents) still validate.
                        // The lookup is one indexed point-read on SessionsIndex —
                        // negligible compared to the events page fetch itself.
                        var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                        if (resolvedTenantId != null && !string.Equals(resolvedTenantId, effectiveTenantId, StringComparison.OrdinalIgnoreCase))
                        {
                            effectiveTenantId = resolvedTenantId;
                        }
                    }
                }

                string? azureToken = null;
                if (pagination.Continuation != null)
                {
                    if (!SessionEventsPagination.TryAcceptContinuation(
                            pagination.Continuation, effectiveTenantId, sessionId, out azureToken, out var reason))
                    {
                        _logger.LogWarning(
                            "{Prefix} GetSessionEvents: continuation rejected ({Reason})",
                            sessionPrefix, reason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({reason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = await _sessionRepo.GetSessionEventsPageAsync(
                    effectiveTenantId, sessionId, pagination.PageSize.Value, azureToken);

                var pageItems = hasFilters
                    ? ApplyFilters(page.Items, filterEventType, filterSeverity, filterSource).ToList()
                    : page.Items;

                ErrorCodeEnricher.EnrichEvents(pageItems);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    // Filters are echoed onto the nextLink so the client doesn't have to
                    // re-supply them when continuing pagination. Both BuildNextLink and the
                    // client (MCP, UI) round-trip these via the continuation URL verbatim.
                    var extras = new List<KeyValuePair<string, string?>>(3);
                    if (!string.IsNullOrEmpty(filterEventType))
                        extras.Add(new KeyValuePair<string, string?>("eventType", filterEventType));
                    if (!string.IsNullOrEmpty(filterSeverity))
                        extras.Add(new KeyValuePair<string, string?>("severity", filterSeverity));
                    if (!string.IsNullOrEmpty(filterSource))
                        extras.Add(new KeyValuePair<string, string?>("source", filterSource));

                    var fp = SessionEventsPagination.Fingerprint(effectiveTenantId, sessionId);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, effectiveTenantId, fp);
                    nextLink = SessionEventsPagination.BuildNextLink(
                        sessionId, pagination.PageSize.Value, wireToken, effectiveTenantId,
                        extras: extras.Count > 0 ? extras : null);
                }

                return await req.OkAsync(new
                {
                    success = true,
                    sessionId,
                    count = pageItems.Count,
                    events = pageItems,
                    nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Get events for session '{sessionId}'");
            }
        }
    }
}
