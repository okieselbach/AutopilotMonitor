using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    /// <summary>
    /// Index-based event search across sessions.
    /// Uses EventTypeIndex for pre-filtering (severity, source), then fetches only matching
    /// event types per session via server-side OData filter — eliminates the N+1 pattern.
    /// Designed for MCP search tools (search_events_semantic, deep_search_events).
    /// </summary>
    public class SearchEventsFunction
    {
        private readonly ILogger<SearchEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public SearchEventsFunction(ILogger<SearchEventsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// GET /api/raw/events/search — Tenant-scoped index-based event search
        /// </summary>
        [Function("SearchEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw/events/search")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);
                return await SearchEvents(req, tenantId);
            }
            catch (UnauthorizedAccessException)
            {
                var err = req.CreateResponse(HttpStatusCode.Unauthorized);
                await err.WriteAsJsonAsync(new { error = "Unauthorized" });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Search events");
            }
        }

        /// <summary>
        /// GET /api/global/raw/events/search — Cross-tenant index-based event search (GlobalAdminOnly)
        /// </summary>
        [Function("SearchEventsGlobal")]
        public async Task<HttpResponseData> RunGlobal(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/events/search")] HttpRequestData req)
        {
            try
            {
                var tenantId = req.Query["tenantId"];
                return await SearchEvents(req, string.IsNullOrEmpty(tenantId) ? null : tenantId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Search events global");
            }
        }

        private async Task<HttpResponseData> SearchEvents(HttpRequestData req, string? tenantId)
        {
            var eventTypesStr = req.Query["eventTypes"];
            var severity = req.Query["severity"];
            var source = req.Query["source"];
            var sessionLimitStr = req.Query["sessionLimit"];
            var limitStr = req.Query["limit"];

            if (string.IsNullOrEmpty(eventTypesStr))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { error = "eventTypes parameter is required (comma-separated list)" });
                return bad;
            }

            var eventTypes = eventTypesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sessionLimit = int.TryParse(sessionLimitStr, out var sl) ? Math.Clamp(sl, 1, 50) : 10;
            var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 500) : 50;

            var events = await _sessionRepo.SearchEventsByTypesAsync(tenantId, eventTypes, source, severity, sessionLimit, limit);
            ErrorCodeEnricher.EnrichEvents(events);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                tenantId,
                eventTypes,
                count = events.Count,
                events
            });
            return response;
        }
    }
}
