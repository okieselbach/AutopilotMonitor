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

public class SearchSessionsByCveFunction
{
    private readonly ILogger<SearchSessionsByCveFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    public SearchSessionsByCveFunction(ILogger<SearchSessionsByCveFunction> logger, ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    [Function("SearchSessionsByCve")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search/sessions-by-cve")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsByCveGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/search/sessions-by-cve")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: false);

    private async Task<HttpResponseData> HandleAsync(HttpRequestData req, bool isTenantScoped)
    {
        try
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query);

            string? tenantId;
            string? filterTenantId;
            string scope;
            string basePath;
            if (isTenantScoped)
            {
                tenantId = TenantHelper.GetTenantId(req);
                filterTenantId = null;
                scope = "search-by-cve:tenant";
                basePath = "/api/search/sessions-by-cve";
            }
            else
            {
                filterTenantId = query["tenantId"];
                tenantId = string.IsNullOrEmpty(filterTenantId) ? null : filterTenantId;
                scope = "search-by-cve:global";
                basePath = "/api/global/search/sessions-by-cve";
            }

            var cveId = query["cveId"];
            if (string.IsNullOrEmpty(cveId))
            {
                return await req.BadRequestAsync("cveId is required");
            }

            double? minCvssScore = double.TryParse(query["minCvssScore"], out var mcs) ? mcs : null;
            var overallRisk = query["overallRisk"];

            var pagination = SearchSessionsByCvePagination.ParsePagination(query);
            if (pagination.Error != null)
            {
                return await req.BadRequestAsync(pagination.Error);
            }

            var callerTenantId = TenantHelper.GetTenantId(req);

            string? azureToken = null;
            if (pagination.Continuation != null)
            {
                if (!SearchSessionsByCvePagination.TryAcceptContinuation(
                        pagination.Continuation, scope, callerTenantId, filterTenantId,
                        cveId, minCvssScore, overallRisk,
                        out azureToken, out var rejectReason))
                {
                    _logger.LogWarning("SearchSessionsByCve: continuation rejected ({Reason})", rejectReason);
                    return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                }
            }

            var page = await _sessionRepo.SearchSessionsByCvePageAsync(
                tenantId, cveId, minCvssScore, overallRisk, pagination.PageSize, azureToken);

            string? nextLink = null;
            if (!string.IsNullOrEmpty(page.NextRawToken))
            {
                var fp = SearchSessionsByCvePagination.Fingerprint(
                    scope, callerTenantId, filterTenantId, cveId, minCvssScore, overallRisk);
                var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                nextLink = SearchSessionsByCvePagination.BuildNextLink(basePath, pagination.PageSize, wireToken, query);
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
            return await req.InternalServerErrorAsync(_logger, ex, "Search sessions by CVE");
        }
    }
}
