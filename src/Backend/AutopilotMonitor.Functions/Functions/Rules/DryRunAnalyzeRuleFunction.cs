using System.Net;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// Dry-runs a DRAFT analyze rule against one session's events and returns a full diagnostic
    /// trace (per-condition matched/evidence, confidence breakdown, verdict) WITHOUT persisting
    /// anything. Powers the rule-authoring loop in the portal and the MCP `test_analyze_rule` tool.
    ///
    /// The rule arrives in the request body and never touches storage; the session status is never
    /// modified (the engine's dry-run path has no MarkSessionAsFailed side effect by construction).
    /// Global-scope callers may dry-run against any tenant's session — the tenant is resolved from
    /// the session, same as GetRuleResultsFunction.
    /// </summary>
    public class DryRunAnalyzeRuleFunction
    {
        private readonly ILogger<DryRunAnalyzeRuleFunction> _logger;
        private readonly IRuleRepository _ruleRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public DryRunAnalyzeRuleFunction(
            ILogger<DryRunAnalyzeRuleFunction> logger,
            IRuleRepository ruleRepo,
            ISessionRepository sessionRepo,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _ruleRepo = ruleRepo;
            _sessionRepo = sessionRepo;
            _analyzeRuleService = analyzeRuleService;
        }

        public sealed class DryRunRequest
        {
            public string? SessionId { get; set; }
            public AnalyzeRule? Rule { get; set; }
        }

        [Function("DryRunAnalyzeRule")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/analyze/dryrun")] HttpRequestData req)
        {
            // Authentication + TenantAdminOrGlobalReader authorization enforced by PolicyEnforcementMiddleware.
            // Read-only diagnostic, so the read-only Global Reader is admitted alongside Tenant Admins and GAs.
            var requestCtx = req.GetRequestContext();
            var effectiveTenantId = requestCtx.TargetTenantId;

            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576) // 1 MB limit
            {
                return await BadRequestAsync(req, new[] { "Request body too large" });
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            DryRunRequest? request;
            try
            {
                request = JsonConvert.DeserializeObject<DryRunRequest>(body);
            }
            catch (JsonException ex)
            {
                return await BadRequestAsync(req, new[] { $"Request body is not valid JSON: {ex.Message}" });
            }

            if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
            {
                return await BadRequestAsync(req, new[] { "sessionId is required" });
            }

            var errors = ValidateDraftRule(request.Rule);
            if (errors.Count > 0)
            {
                return await BadRequestAsync(req, errors);
            }

            var sessionId = request.SessionId!.Trim();
            var rule = request.Rule!;

            // Global-scope cross-tenant fallback: resolve the session's actual tenant so a GA /
            // Global Reader can dry-run against any tenant's session (mirrors GetRuleResultsFunction).
            effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

            var session = await _sessionRepo.GetSessionAsync(effectiveTenantId, sessionId);
            if (session == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = "Session not found", sessionId });
                return notFound;
            }

            var ruleEngine = new RuleEngine(_analyzeRuleService, _ruleRepo, _sessionRepo, _logger);
            var result = await ruleEngine.DryRunRuleAsync(effectiveTenantId, sessionId, rule);

            _logger.LogInformation(
                "Dry-run of draft rule {RuleId} against session {SessionId}: verdict={Verdict}, confidence={Confidence}",
                string.IsNullOrEmpty(rule.RuleId) ? "(no id)" : rule.RuleId, sessionId, result.Verdict, result.FinalConfidence);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, sessionId, result });
            return response;
        }

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, IReadOnlyList<string> errors)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { success = false, message = errors[0], errors });
            return badRequest;
        }

        // ===== Draft validation =====

        // Exact-match sets mirroring what the RuleEngine actually dispatches on. The evaluator
        // switch is case-sensitive ("event_type", not "Event_Type"), and an unknown source/operator
        // silently evaluates to false in production — for a dry-run that silence is exactly the
        // confusion we want to catch up front.
        // internal: SharedManifestParityTests exports these into the cross-language manifest.
        internal static readonly HashSet<string> KnownSources = new(StringComparer.Ordinal)
        {
            "event_type", "event_data", "event_data_array", "event_count",
            "phase_duration", "app_install_duration", "event_correlation",
        };

        internal static readonly HashSet<string> KnownOperators = new(StringComparer.OrdinalIgnoreCase)
        {
            "equals", "not_equals", "contains", "not_contains", "regex", "not_regex",
            "gt", "lt", "gte", "lte", "exists", "not_exists",
            "count_gte", "count_per_group_gte", "in", "not_in",
        };

        // The three shapes EvaluateConfidenceFactor actually parses. Anything else is silently
        // false in production ("count > 3", "Count >= 3", "phase_duration>=300", …).
        private static readonly Regex FactorConditionShape = new(
            @"^(exists|count >= ?\d+|phase_duration > ?\d+)$", RegexOptions.Compiled);

        /// <summary>
        /// Structural validation of a draft rule before evaluation. Returns human-readable errors
        /// (empty list = valid). Deliberately limited to what would make the dry-run silently
        /// meaningless or throw — full authoring lint (schema, guardrails, event-type catalog)
        /// lives client-side in the MCP `validate_rule` tool.
        /// </summary>
        internal static List<string> ValidateDraftRule(AnalyzeRule? rule)
        {
            var errors = new List<string>();
            if (rule == null)
            {
                errors.Add("rule is required");
                return errors;
            }

            if (rule.Conditions == null || rule.Conditions.Count == 0)
            {
                errors.Add("rule must contain at least one condition");
                return errors;
            }

            if (rule.BaseConfidence < 0 || rule.BaseConfidence > 100)
                errors.Add($"baseConfidence must be 0-100 (was {rule.BaseConfidence})");
            if (rule.ConfidenceThreshold < 0 || rule.ConfidenceThreshold > 100)
                errors.Add($"confidenceThreshold must be 0-100 (was {rule.ConfidenceThreshold})");

            var seenSignals = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rule.Conditions.Count; i++)
            {
                var c = rule.Conditions[i];
                var label = $"conditions[{i}]";

                if (string.IsNullOrWhiteSpace(c.Signal))
                    errors.Add($"{label}: signal is required");
                else if (!seenSignals.Add(c.Signal))
                    errors.Add($"{label}: duplicate signal '{c.Signal}' — signals must be unique (evidence is keyed by signal)");

                if (string.IsNullOrWhiteSpace(c.Source))
                    errors.Add($"{label}: source is required");
                else if (!KnownSources.Contains(c.Source))
                    errors.Add($"{label}: unknown source '{c.Source}' (must be one of: {string.Join(", ", KnownSources)}; exact lowercase)");

                if (!string.IsNullOrEmpty(c.Operator) && !KnownOperators.Contains(c.Operator))
                    errors.Add($"{label}: unknown operator '{c.Operator}'");

                if (string.Equals(c.Source, "event_correlation", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(c.CorrelateEventType))
                        errors.Add($"{label}: event_correlation requires correlateEventType");
                    if (string.IsNullOrWhiteSpace(c.JoinField))
                        errors.Add($"{label}: event_correlation requires joinField");
                }

                if ((string.Equals(c.Operator, "regex", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Operator, "not_regex", StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrEmpty(c.Value))
                {
                    try
                    {
                        _ = new Regex(c.Value, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    }
                    catch (ArgumentException ex)
                    {
                        errors.Add($"{label}: value is not a valid regex: {ex.Message}");
                    }
                }
            }

            for (var i = 0; i < (rule.ConfidenceFactors?.Count ?? 0); i++)
            {
                var f = rule.ConfidenceFactors![i];
                var label = $"confidenceFactors[{i}]";
                if (string.IsNullOrWhiteSpace(f.Signal))
                    errors.Add($"{label}: signal is required");
                if (string.IsNullOrEmpty(f.Condition) || !FactorConditionShape.IsMatch(f.Condition))
                    errors.Add($"{label}: condition '{f.Condition}' is not evaluable — supported shapes: \"exists\", \"count >= N\", \"phase_duration > N\" (exact spacing)");
            }

            return errors;
        }
    }
}
