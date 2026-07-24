using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// Global Admin endpoints for viewing AND mutating rules across tenants.
    /// Reads tenantId from the ?tenantId= query string (not JWT) so a Global Admin can inspect
    /// or edit any tenant. The write routes exist because the JWT-scoped GatherRulesFunction /
    /// AnalyzeRulesFunction resolve the tenant from the caller's own token: a GA editing a foreign
    /// tenant via those routes would silently upsert into its HOME tenant (200 → false "saved"),
    /// while the reload re-reads the foreign tenant and shows the unchanged rule. These routes are
    /// GlobalAdminOnly + TenantScoping.QueryParam so cross-tenant write stays platform-admin-only
    /// (no tenant-admin / delegated rescue — the delegated grant applies to READ tiers only).
    /// </summary>
    public class GlobalRulesFunction
    {
        private readonly ILogger<GlobalRulesFunction> _logger;
        private readonly GatherRuleService _gatherRuleService;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public GlobalRulesFunction(
            ILogger<GlobalRulesFunction> logger,
            GatherRuleService gatherRuleService,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _gatherRuleService = gatherRuleService;
            _analyzeRuleService = analyzeRuleService;
        }

        /// <summary>
        /// GET /api/global/rules/gather?tenantId=X - Get gather rules for any tenant (Global Admin only)
        /// </summary>
        [Function("GetGlobalGatherRules")]
        public async Task<HttpResponseData> GetGatherRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/rules/gather")] HttpRequestData req)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "").Get("tenantId");

            if (string.IsNullOrEmpty(tenantId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId query parameter is required" });
                return badRequest;
            }

            _logger.LogInformation("Global admin requesting gather rules for tenant {TenantId}", tenantId);

            var rules = await _gatherRuleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }

        /// <summary>
        /// GET /api/global/rules/analyze?tenantId=X - Get analyze rules for any tenant (Global Admin only)
        /// </summary>
        [Function("GetGlobalAnalyzeRules")]
        public async Task<HttpResponseData> GetAnalyzeRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/rules/analyze")] HttpRequestData req)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "").Get("tenantId");

            if (string.IsNullOrEmpty(tenantId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId query parameter is required" });
                return badRequest;
            }

            _logger.LogInformation("Global admin requesting analyze rules for tenant {TenantId}", tenantId);

            var rules = await _analyzeRuleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }

        /// <summary>
        /// PUT /api/global/rules/gather/{ruleId}?tenantId=X - Update a gather rule in any tenant (Global Admin only).
        /// Cross-tenant counterpart of GatherRulesFunction.UpdateRule; the tenant comes from the query, not the JWT.
        /// </summary>
        [Function("UpdateGlobalGatherRule")]
        public async Task<HttpResponseData> UpdateGatherRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "global/rules/gather/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
            var tenantId = ResolveTenantId(req, out var tenantError);
            if (tenantError != null)
                return await BadRequestAsync(req, tenantError);

            var (rule, bodyError) = await ReadRuleBodyAsync<GatherRule>(req);
            if (bodyError != null)
                return await BadRequestAsync(req, bodyError);

            rule!.RuleId = ruleId;

            var scopeError = GatherRulesFunction.ValidateScopeAndEmitMode(rule);
            if (scopeError != null)
                return await BadRequestAsync(req, scopeError);

            _logger.LogInformation("Global admin updating gather rule {RuleId} for tenant {TenantId}", ruleId, tenantId);

            var success = await _gatherRuleService.UpdateRuleAsync(tenantId, rule);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule updated" : "Failed to update rule" });
            return response;
        }

        /// <summary>
        /// DELETE /api/global/rules/gather/{ruleId}?tenantId=X - Delete a gather rule in any tenant (Global Admin only).
        /// </summary>
        [Function("DeleteGlobalGatherRule")]
        public async Task<HttpResponseData> DeleteGatherRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/rules/gather/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
            var tenantId = ResolveTenantId(req, out var tenantError);
            if (tenantError != null)
                return await BadRequestAsync(req, tenantError);

            // Load the rule to determine its type (built-in/community vs. custom).
            var rules = await _gatherRuleService.GetAllRulesForTenantAsync(tenantId);
            var rule = rules.FirstOrDefault(r => r.RuleId == ruleId);
            if (rule == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = "Rule not found" });
                return notFound;
            }

            _logger.LogInformation("Global admin deleting gather rule {RuleId} for tenant {TenantId}", ruleId, tenantId);

            var success = await _gatherRuleService.DeleteRuleAsync(tenantId, rule);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule deleted" : "Failed to delete rule" });
            return response;
        }

        /// <summary>
        /// PUT /api/global/rules/analyze/{ruleId}?tenantId=X - Update an analyze rule in any tenant (Global Admin only).
        /// Cross-tenant counterpart of AnalyzeRulesFunction.UpdateRule.
        /// </summary>
        [Function("UpdateGlobalAnalyzeRule")]
        public async Task<HttpResponseData> UpdateAnalyzeRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "global/rules/analyze/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
            var tenantId = ResolveTenantId(req, out var tenantError);
            if (tenantError != null)
                return await BadRequestAsync(req, tenantError);

            var (rule, bodyError) = await ReadRuleBodyAsync<AnalyzeRule>(req);
            if (bodyError != null)
                return await BadRequestAsync(req, bodyError);

            rule!.RuleId = ruleId;

            _logger.LogInformation("Global admin updating analyze rule {RuleId} for tenant {TenantId}", ruleId, tenantId);

            var success = await _analyzeRuleService.UpdateRuleAsync(tenantId, rule);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule updated" : "Failed to update rule" });
            return response;
        }

        /// <summary>
        /// DELETE /api/global/rules/analyze/{ruleId}?tenantId=X - Delete an analyze rule in any tenant (Global Admin only).
        /// </summary>
        [Function("DeleteGlobalAnalyzeRule")]
        public async Task<HttpResponseData> DeleteAnalyzeRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/rules/analyze/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
            var tenantId = ResolveTenantId(req, out var tenantError);
            if (tenantError != null)
                return await BadRequestAsync(req, tenantError);

            // Load the rule to determine its type (built-in/community vs. custom).
            var rules = await _analyzeRuleService.GetAllRulesForTenantAsync(tenantId);
            var rule = rules.FirstOrDefault(r => r.RuleId == ruleId);
            if (rule == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = "Rule not found" });
                return notFound;
            }

            _logger.LogInformation("Global admin deleting analyze rule {RuleId} for tenant {TenantId}", ruleId, tenantId);

            var success = await _analyzeRuleService.DeleteRuleAsync(tenantId, rule);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule deleted" : "Failed to delete rule" });
            return response;
        }

        /// <summary>
        /// Resolves the target tenant from the ?tenantId= query string (the write routes are
        /// TenantScoping.QueryParam). Sets <paramref name="error"/> when the parameter is missing.
        /// </summary>
        private static string ResolveTenantId(HttpRequestData req, out string? error)
        {
            var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "").Get("tenantId");
            error = string.IsNullOrEmpty(tenantId) ? "tenantId query parameter is required" : null;
            return tenantId ?? string.Empty;
        }

        /// <summary>
        /// Reads and deserializes a rule body with the same 1 MB guard the JWT-scoped functions apply.
        /// Returns (rule, null) on success or (null, errorMessage) for a 400.
        /// </summary>
        private static async Task<(T? rule, string? error)> ReadRuleBodyAsync<T>(HttpRequestData req) where T : class
        {
            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576) // 1 MB limit
            {
                return (null, "Request body too large");
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<T>(body);
            return rule == null ? (null, "Invalid rule data") : (rule, null);
        }

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, string message)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { success = false, message });
            return badRequest;
        }
    }
}
