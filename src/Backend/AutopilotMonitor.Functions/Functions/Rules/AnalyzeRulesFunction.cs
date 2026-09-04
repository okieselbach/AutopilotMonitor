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
    /// CRUD API for managing analyze rules (portal-facing, JWT auth)
    /// </summary>
    public class AnalyzeRulesFunction
    {
        private readonly ILogger<AnalyzeRulesFunction> _logger;
        private readonly AnalyzeRuleService _ruleService;

        public AnalyzeRulesFunction(
            ILogger<AnalyzeRulesFunction> logger,
            AnalyzeRuleService ruleService)
        {
            _logger = logger;
            _ruleService = ruleService;
        }

        [Function("GetAnalyzeRules")]
        public async Task<HttpResponseData> GetRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/analyze")] HttpRequestData req)
        {
            // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            var rules = await _ruleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new AnalyzeRuleListResponse { Success = true, Rules = rules });
            return response;
        }

        [Function("CreateAnalyzeRule")]
        public async Task<HttpResponseData> CreateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/analyze")] HttpRequestData req)
        {
            // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576) // 1 MB limit
            {
                return await req.BadRequestAsync("Request body too large");
            }
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<AnalyzeRule>(body);

            if (rule == null || string.IsNullOrEmpty(rule.RuleId))
            {
                return await req.BadRequestAsync("Invalid rule data");
            }

            try
            {
                var success = await _ruleService.CreateRuleAsync(tenantId, rule);

                var response = req.CreateResponse(success ? HttpStatusCode.Created : HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new SuccessMessageResponse { Success = success, Message = success ? "Rule created" : "Failed to create rule" });
                return response;
            }
            catch (InvalidOperationException ex)
            {
                return await req.ConflictAsync(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return await req.BadRequestAsync(ex.Message);
            }
        }

        [Function("UpdateAnalyzeRule")]
        public async Task<HttpResponseData> UpdateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "rules/analyze/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            if (req.Headers.TryGetValues("Content-Length", out var clValues2)
                && long.TryParse(clValues2.FirstOrDefault(), out var contentLength2)
                && contentLength2 > 1_048_576) // 1 MB limit
            {
                return await req.BadRequestAsync("Request body too large");
            }
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<AnalyzeRule>(body);

            if (rule == null)
            {
                return await req.BadRequestAsync("Invalid rule data");
            }

            rule.RuleId = ruleId;

            try
            {
                var success = await _ruleService.UpdateRuleAsync(tenantId, rule);
                var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new SuccessMessageResponse { Success = success, Message = success ? "Rule updated" : "Failed to update rule" });
                return response;
            }
            catch (InvalidOperationException ex)
            {
                return await req.ConflictAsync(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return await req.BadRequestAsync(ex.Message);
            }
        }

        [Function("CreateAnalyzeRuleFromTemplate")]
        public async Task<HttpResponseData> CreateFromTemplate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/analyze/{ruleId}/create-from-template")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var variables = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

            if (variables == null || variables.Count == 0)
            {
                return await req.BadRequestAsync("Template variable values are required");
            }

            try
            {
                var newRule = await _ruleService.CreateFromTemplateAsync(tenantId, ruleId, variables);

                var response = req.CreateResponse(HttpStatusCode.Created);
                await response.WriteAsJsonAsync(new CreateAnalyzeRuleFromTemplateResponse { Success = true, Rule = newRule, Message = "Custom rule created from template" });
                return response;
            }
            catch (InvalidOperationException ex)
            {
                return await req.ConflictAsync(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return await req.BadRequestAsync(ex.Message);
            }
        }

        [Function("DeleteAnalyzeRule")]
        public async Task<HttpResponseData> DeleteRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rules/analyze/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            // Load the rule to determine its type (built-in/community vs. custom)
            var rules = await _ruleService.GetAllRulesForTenantAsync(tenantId);
            var rule = rules.FirstOrDefault(r => r.RuleId == ruleId);

            if (rule == null)
            {
                return await req.NotFoundAsync("Rule not found");
            }

            var success = await _ruleService.DeleteRuleAsync(tenantId, rule);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new SuccessMessageResponse { Success = success, Message = success ? "Rule deleted" : "Failed to delete rule" });
            return response;
        }
    }
}
