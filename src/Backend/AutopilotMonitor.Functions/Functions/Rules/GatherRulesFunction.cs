using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// CRUD API for managing gather rules (portal-facing, JWT auth)
    /// </summary>
    public class GatherRulesFunction
    {
        private readonly ILogger<GatherRulesFunction> _logger;
        private readonly GatherRuleService _ruleService;

        public GatherRulesFunction(
            ILogger<GatherRulesFunction> logger,
            GatherRuleService ruleService)
        {
            _logger = logger;
            _ruleService = ruleService;
        }

        [Function("GetGatherRules")]
        public async Task<HttpResponseData> GetRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/gather")] HttpRequestData req)
        {
            // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            var rules = await _ruleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new GatherRuleListResponse { Success = true, Rules = rules });
            return response;
        }

        [Function("CreateGatherRule")]
        public async Task<HttpResponseData> CreateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/gather")] HttpRequestData req)
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
            var rule = JsonConvert.DeserializeObject<GatherRule>(body);

            if (rule == null || string.IsNullOrEmpty(rule.RuleId))
            {
                return await req.BadRequestAsync("Invalid rule data");
            }

            var scopeError = ValidateScopeAndEmitMode(rule);
            if (scopeError != null)
            {
                return await req.BadRequestAsync(scopeError);
            }

            // Author is stamped from the creator's token, never from the payload
            // (anti-spoof), and stays immutable through every later update.
            rule.Author = TenantHelper.GetUserDisplayName(req) ?? "Autopilot Monitor";

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
        }

        [Function("UpdateGatherRule")]
        public async Task<HttpResponseData> UpdateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "rules/gather/{ruleId}")] HttpRequestData req,
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
            var rule = JsonConvert.DeserializeObject<GatherRule>(body);

            if (rule == null)
            {
                return await req.BadRequestAsync("Invalid rule data");
            }

            rule.RuleId = ruleId;

            var scopeError = ValidateScopeAndEmitMode(rule);
            if (scopeError != null)
            {
                return await req.BadRequestAsync(scopeError);
            }

            // Same anti-spoof stamp as CreateRule: on a true update the service replaces
            // this with the original author (immutable attribution), but a full-payload PUT
            // that upserts a rule with no existing row must not store the payload's author.
            rule.Author = TenantHelper.GetUserDisplayName(req) ?? "Autopilot Monitor";

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
        }

        /// <summary>
        /// Selectable phase-scope tokens: the <see cref="EnrollmentPhase"/> enum NAMES from
        /// Start(0) through Complete(7). Explicit name list (not Enum.TryParse) so numeric
        /// tokens ("4") and the non-selectable Unknown/Failed members are rejected — the
        /// canonical vocabulary the agent, schema, and portal share.
        /// </summary>
        private static readonly HashSet<string> ValidScopePhases = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(EnrollmentPhase.Start),
            nameof(EnrollmentPhase.DevicePreparation),
            nameof(EnrollmentPhase.DeviceSetup),
            nameof(EnrollmentPhase.AppsDevice),
            nameof(EnrollmentPhase.AccountSetup),
            nameof(EnrollmentPhase.AppsUser),
            nameof(EnrollmentPhase.FinalizingSetup),
            nameof(EnrollmentPhase.Complete),
        };

        /// <summary>
        /// Validates the phase-scope + emit-mode fields and the on_event trigger wiring on
        /// create/update. Returns an error message for a 400 response, or null when valid.
        /// Toggle-style partial payloads carry none of these fields and pass through.
        /// <para>
        /// on_event: the agent never dispatches on_event rules for events the gather executor
        /// itself emitted (feedback-loop guard, <c>GatherRuleExecutorHost</c>), so a rule that
        /// triggers on its own output — or on the <c>gather_result</c> default output type —
        /// could never fire and is rejected here with an explanatory message instead of being
        /// stored as a dead rule.
        /// </para>
        /// </summary>
        internal static string? ValidateScopeAndEmitMode(GatherRule rule)
        {
            if (string.Equals(rule.Trigger, "on_event", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(rule.TriggerEventType))
            {
                var outputEventType = string.IsNullOrEmpty(rule.OutputEventType)
                    ? Constants.EventTypes.GatherResult
                    : rule.OutputEventType;

                if (string.Equals(rule.TriggerEventType, outputEventType, StringComparison.OrdinalIgnoreCase))
                    return "triggerEventType must differ from the rule's own outputEventType — gather rules never trigger on their own output.";

                if (string.Equals(rule.TriggerEventType, Constants.EventTypes.GatherResult, StringComparison.OrdinalIgnoreCase))
                    return $"triggerEventType '{Constants.EventTypes.GatherResult}' is the gather-rule output type — gather rules never trigger on gather output.";
            }

            var hasActivePhases = rule.ActivePhases != null && rule.ActivePhases.Count > 0;
            var hasFromPhase = !string.IsNullOrEmpty(rule.ActiveFromPhase);

            if (hasActivePhases && hasFromPhase)
                return "activePhases and activeFromPhase are mutually exclusive — set only one.";

            if (hasActivePhases)
            {
                foreach (var phase in rule.ActivePhases!)
                {
                    if (string.IsNullOrEmpty(phase) || !ValidScopePhases.Contains(phase))
                        return $"Invalid phase '{phase}' in activePhases. Valid phases: {string.Join(", ", ValidScopePhases)}.";
                }
            }

            if (hasFromPhase && !ValidScopePhases.Contains(rule.ActiveFromPhase!))
                return $"Invalid activeFromPhase '{rule.ActiveFromPhase}'. Valid phases: {string.Join(", ", ValidScopePhases)}.";

            if (!string.IsNullOrEmpty(rule.EmitMode)
                && !string.Equals(rule.EmitMode, "always", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rule.EmitMode, "on_change", StringComparison.OrdinalIgnoreCase))
                return "emitMode must be \"always\" or \"on_change\".";

            return null;
        }

        [Function("DeleteGatherRule")]
        public async Task<HttpResponseData> DeleteRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rules/gather/{ruleId}")] HttpRequestData req,
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
