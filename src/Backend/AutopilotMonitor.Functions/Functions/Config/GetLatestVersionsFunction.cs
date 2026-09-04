using System;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Returns the latest published agent + bootstrap script versions.
    /// Cached in-memory for 12h; supports <c>?refresh=true</c> to bypass the cache.
    /// Authenticated users only (read-only metadata).
    /// </summary>
    public class GetLatestVersionsFunction
    {
        private readonly ILogger<GetLatestVersionsFunction> _logger;
        private readonly ILatestVersionsService _latestVersionsService;

        public GetLatestVersionsFunction(
            ILogger<GetLatestVersionsFunction> logger,
            ILatestVersionsService latestVersionsService)
        {
            _logger = logger;
            _latestVersionsService = latestVersionsService;
        }

        [Function("GetLatestVersions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/latest-versions")] HttpRequestData req)
        {
            try
            {
                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var refreshRaw = query["refresh"];
                var forceRefresh = !string.IsNullOrEmpty(refreshRaw)
                    && (refreshRaw.Equals("true", StringComparison.OrdinalIgnoreCase) || refreshRaw == "1");

                var versions = await _latestVersionsService.GetAsync(forceRefresh, req.FunctionContext.CancellationToken);

                var response = req.CreateResponse(HttpStatusCode.OK);

                // Force-refresh responses MUST NOT be browser-cached.
                // Normal cached responses may be stored for up to 1h (aligns with 12h backend cache).
                if (forceRefresh)
                {
                    response.Headers.Add("Cache-Control", "no-store");
                }
                else
                {
                    response.Headers.Add("Cache-Control", "public, max-age=3600");
                }

                await response.WriteAsJsonAsync(new GetLatestVersionsResponse
                {
                    LatestAgentVersion = versions?.AgentVersion,
                    LatestBootstrapScriptVersion = versions?.BootstrapVersion,
                    LatestAgentSha256 = versions?.AgentSha256,
                    FetchedAtUtc = versions?.FetchedAtUtc,
                    Source = versions?.FromCache == true ? "cache" : "blob"
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetLatestVersions");
            }
        }
    }
}
