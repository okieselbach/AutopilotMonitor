using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// API for managing IME log patterns (portal-facing, JWT auth).
    /// Phase 1: Read-only for tenants, edit/reseed for Global Admins only.
    /// </summary>
    public class ImeLogPatternsFunction
    {
        private readonly ILogger<ImeLogPatternsFunction> _logger;
        private readonly ImeLogPatternService _patternService;

        public ImeLogPatternsFunction(ILogger<ImeLogPatternsFunction> logger, ImeLogPatternService patternService)
        {
            _logger = logger;
            _patternService = patternService;
        }

        [Function("GetImeLogPatterns")]
        public async Task<HttpResponseData> GetPatterns(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/ime-log-patterns")] HttpRequestData req)
        {
            // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            var patterns = await _patternService.GetAllPatternsForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new ImeLogPatternListResponse { Success = true, Patterns = patterns });
            return response;
        }

        [Function("UpdateImeLogPattern")]
        public async Task<HttpResponseData> UpdatePattern(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "rules/ime-log-patterns/{patternId}")] HttpRequestData req,
            string patternId)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

            // Phase 1: Only global edits
            var globalEdit = req.Url.Query.Contains("global=true", StringComparison.OrdinalIgnoreCase);
            if (!globalEdit)
            {
                return await req.ForbiddenAsync("IME log pattern edits require ?global=true");
            }

            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576)
            {
                return await req.BadRequestAsync("Request body too large");
            }
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var pattern = JsonConvert.DeserializeObject<ImeLogPattern>(body);

            if (pattern == null)
            {
                return await req.BadRequestAsync("Invalid pattern data");
            }

            pattern.PatternId = patternId;

            var success = await _patternService.UpdateGlobalPatternAsync(pattern);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new SuccessMessageResponse { Success = success, Message = success ? "Global pattern updated" : "Failed to update global pattern" });
            return response;
        }

        [Function("ReseedImeLogPatterns")]
        public async Task<HttpResponseData> ReseedPatterns(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/ime-log-patterns/reseed")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var upn = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"Reseed IME log patterns triggered by Global Admin {upn}");

                var (deleted, written) = await _patternService.ReseedBuiltInPatternsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new ReseedImeLogPatternsResponse
                {
                    Success = true,
                    Message = $"Reseed complete: {deleted} old patterns removed, {written} patterns written from code.",
                    Deleted = deleted,
                    Written = written
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "ImeLogPatterns");
            }
        }
    }
}
