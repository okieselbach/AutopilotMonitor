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

public class SearchSessionsByEventFunction
{
    private readonly ILogger<SearchSessionsByEventFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    public SearchSessionsByEventFunction(ILogger<SearchSessionsByEventFunction> logger, ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    [Function("SearchSessionsByEvent")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search/sessions-by-event")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsByEventGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/search/sessions-by-event")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: false);

    private async Task<HttpResponseData> HandleAsync(HttpRequestData req, bool isTenantScoped)
    {
        try
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

            string? tenantId;
            string? filterTenantId;
            string scope;
            string basePath;
            var callerTenantId = TenantHelper.GetTenantId(req);

            if (isTenantScoped)
            {
                tenantId = callerTenantId;
                filterTenantId = null;
                scope = "search-by-event:tenant";
                basePath = "/api/search/sessions-by-event";
            }
            else
            {
                filterTenantId = query["tenantId"];
                tenantId = string.IsNullOrEmpty(filterTenantId) ? null : filterTenantId;
                scope = "search-by-event:global";
                basePath = "/api/global/search/sessions-by-event";
            }

            var eventType = query["eventType"];
            if (string.IsNullOrEmpty(eventType))
            {
                return await req.BadRequestAsync("eventType is required");
            }

            var pagination = SearchSessionsByEventPagination.ParsePagination(query);
            if (pagination.Error != null)
            {
                return await req.BadRequestAsync(pagination.Error);
            }

            string? azureToken = null;
            if (pagination.Continuation != null)
            {
                if (!SearchSessionsByEventPagination.TryAcceptContinuation(
                        pagination.Continuation, scope, callerTenantId, filterTenantId, eventType,
                        out azureToken, out var rejectReason))
                {
                    _logger.LogWarning("SearchSessionsByEvent: continuation rejected ({Reason})", rejectReason);
                    return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                }
            }

            var page = await _sessionRepo.SearchSessionsByEventPageAsync(
                tenantId, eventType, source: null, severity: null, phase: null,
                pageSize: pagination.PageSize, continuation: azureToken);

            string? nextLink = null;
            if (!string.IsNullOrEmpty(page.NextRawToken))
            {
                var fp = SearchSessionsByEventPagination.Fingerprint(scope, callerTenantId, filterTenantId, eventType);
                var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                nextLink = SearchSessionsByEventPagination.BuildNextLink(basePath, pagination.PageSize, wireToken, query);
            }

            return await req.OkAsync(new SessionListResponse
            {
                Success = true,
                Count = page.Items.Count,
                Sessions = page.Items,
                NextLink = nextLink,
            });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "Search sessions by event");
        }
    }
}
