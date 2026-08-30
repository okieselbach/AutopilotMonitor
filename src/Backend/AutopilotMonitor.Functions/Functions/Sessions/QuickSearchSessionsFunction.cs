using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions;

public class QuickSearchSessionsFunction
{
    private readonly ILogger<QuickSearchSessionsFunction> _logger;
    private readonly ISessionRepository _sessionRepo;
    private readonly RateLimitService _rateLimitService;

    public QuickSearchSessionsFunction(
        ILogger<QuickSearchSessionsFunction> logger,
        ISessionRepository sessionRepo,
        RateLimitService rateLimitService)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
        _rateLimitService = rateLimitService;
    }

    [Function("QuickSearchSessions")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search/quick")] HttpRequestData req)
    {
        try
        {
            var requestCtx = req.GetRequestContext();

            // Rate limit by user identity (30 req/min — generous for typeahead with 250ms debounce)
            var rateLimitKey = $"quick-search:{requestCtx.UserPrincipalName}";
            var rateLimitResult = _rateLimitService.CheckRateLimit(rateLimitKey, 30);
            if (!rateLimitResult.IsAllowed)
            {
                var tooMany = req.CreateResponse(HttpStatusCode.TooManyRequests);
                if (rateLimitResult.RetryAfter.HasValue)
                    tooMany.Headers.Add("Retry-After", ((int)rateLimitResult.RetryAfter.Value.TotalSeconds).ToString());
                await tooMany.WriteAsJsonAsync(new { success = false, message = "Rate limit exceeded. Try again later." });
                return tooMany;
            }

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var q = query["q"]?.Trim();

            if (string.IsNullOrEmpty(q) || q.Length < 2)
            {
                return await req.BadRequestAsync("Query parameter 'q' must be at least 2 characters.");
            }

            // Global-scope callers (GA or read-only GlobalReader) search across all tenants;
            // regular users are scoped to their own tenant.
            string? tenantId = requestCtx.HasGlobalScope ? null : requestCtx.TenantId;

            var results = await _sessionRepo.QuickSearchSessionsAsync(tenantId, q, limit: 10);

            return await req.OkAsync(new QuickSearchSessionsResponse
            {
                Success = true,
                Count = results.Count,
                Results = results
            });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "Error in quick search");
        }
    }
}
