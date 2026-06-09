using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Evaluates analyze rules against session events to detect issues.
    /// Runs once at enrollment end or on-demand via "Analyze Now" button.
    /// All rules (single + correlation) are evaluated in a single pass over all events.
    /// </summary>
    public partial class RuleEngine
    {
        private readonly AnalyzeRuleService _ruleService;
        private readonly IRuleRepository _ruleRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly ILogger _logger;

        public RuleEngine(AnalyzeRuleService ruleService, IRuleRepository ruleRepo, ISessionRepository sessionRepo, ILogger logger)
        {
            _ruleService = ruleService;
            _ruleRepo = ruleRepo;
            _sessionRepo = sessionRepo;
            _logger = logger;
        }

        /// <summary>
        /// Evaluates ALL active analyze rules against the full session event stream.
        /// Called once at enrollment end or on-demand. Fetches events internally.
        /// When reanalyze=true, all rules are re-evaluated regardless of existing results.
        /// Returns both the fired results and metadata about all rules that were evaluated (for telemetry).
        /// <para>
        /// <b>Failure semantics:</b> storage-layer exceptions from rule loading, event reading
        /// or existing-results lookup propagate to the caller. The queue worker relies on this
        /// to leave its message un-deleted so a transient Table Storage failure can be retried
        /// via visibility-timeout (<see cref="AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndQueueWorker"/>).
        /// The on-demand HTTP path (<see cref="AutopilotMonitor.Functions.Functions.Rules.GetRuleResultsFunction"/>)
        /// wraps the call in its own try/catch and logs failures as warnings — the user sees
        /// the previously-stored results until they re-trigger.
        /// </para>
        /// <para>
        /// Per-rule evaluation failures are caught locally and logged: a single buggy rule must
        /// not abort the whole session pass. Rules that throw are simply absent from
        /// <see cref="AnalysisOutcome.Results"/>; they remain in <see cref="AnalysisOutcome.EvaluatedRules"/>
        /// so telemetry counts the attempt.
        /// </para>
        /// </summary>
        public async Task<AnalysisOutcome> AnalyzeSessionAsync(string tenantId, string sessionId, bool reanalyze = false)
        {
            var outcome = new AnalysisOutcome();

            var activeRules = await _ruleService.GetActiveRulesForTenantAsync(tenantId);
            // Strict read: storage failures propagate (→ queue retry / poison) instead of
            // degrading to an empty list — an empty result here therefore really means a
            // session without events, never a swallowed transient fault.
            var allEvents = await _sessionRepo.GetSessionEventsStrictAsync(tenantId, sessionId);

            if (allEvents.Count == 0)
            {
                _logger.LogInformation($"No events found for session {sessionId}, skipping analysis");
                return outcome;
            }

            // Backfill derived fields for backward compatibility with events produced by older agents.
            // This is a pure read-time projection — we never persist the synthesized values.
            BackfillDerivedEventFields(allEvents);

            // On reanalyze: skip deduplication so all rules are re-evaluated from scratch
            // On normal run: skip rules already evaluated to avoid duplicate storage
            HashSet<string> existingRuleIds;
            if (reanalyze)
            {
                existingRuleIds = new HashSet<string>();
            }
            else
            {
                var existingResults = await _ruleRepo.GetRuleResultsAsync(tenantId, sessionId);
                existingRuleIds = new HashSet<string>(existingResults.Select(r => r.RuleId));
            }

            _logger.LogInformation($"Analyzing session {sessionId}: {allEvents.Count} events, {activeRules.Count} rules ({existingRuleIds.Count} already evaluated)");

            foreach (var rule in activeRules)
            {
                try
                {
                    // Skip if we already have a result for this rule
                    if (existingRuleIds.Contains(rule.RuleId))
                        continue;

                    // Track that this rule was evaluated (for telemetry)
                    outcome.EvaluatedRules.Add(rule);

                    var result = EvaluateRule(rule, allEvents);
                    if (result != null)
                    {
                        result.SessionId = sessionId;
                        result.TenantId = tenantId;
                        outcome.Results.Add(result);
                        _logger.LogInformation($"Rule {rule.RuleId} ({rule.Trigger}) fired for session {sessionId} with confidence {result.ConfidenceScore}%");

                        // KO-criterion: if the (effective) MarkSessionAsFailed flag is on,
                        // escalate the rule finding to a terminal session status. Tenant override
                        // wins; otherwise we honor the rule-definition default.
                        var shouldFailSession = rule.MarkSessionAsFailed ?? rule.MarkSessionAsFailedDefault;
                        if (shouldFailSession)
                        {
                            await TryMarkSessionFailedFromRuleAsync(tenantId, sessionId, rule);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Per-rule evaluation failures are isolated: a buggy rule must not kill the
                    // whole pass. Storage-layer exceptions from the surrounding code (rule loading,
                    // event reading, results lookup, KO-criterion side-effects) are NOT caught here
                    // and propagate to the caller — the queue worker depends on this for retry.
                    _logger.LogWarning(ex, $"Error evaluating rule {rule.RuleId}");
                }
            }

            return outcome;
        }

        /// <summary>
        /// Read-time backfill of derived Event.Data fields that older agent builds didn't emit.
        /// Keeps rules forward-compatible with historical sessions. Pure in-memory mutation — the
        /// original event records in Table Storage are not modified.
        ///
        /// Current projections:
        /// - esp_provisioning_status: synthesize `failedSubcategories` (comma-joined registry names)
        ///   from `transitions[]` entries where newState == "failed". Matches what ProvisioningStatusTracker
        ///   now emits natively, so ANALYZE-ESP-002 fires against pre-upgrade data too.
        /// </summary>
        private static void BackfillDerivedEventFields(List<EnrollmentEvent> events)
        {
            foreach (var evt in events)
            {
                if (evt.Data == null) continue;
                if (!string.Equals(evt.EventType, "esp_provisioning_status", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                // Don't overwrite what the agent already provided.
                if (evt.Data.ContainsKey("failedSubcategories"))
                    continue;

                if (!evt.Data.TryGetValue("transitions", out var transitionsObj) || transitionsObj == null)
                    continue;

                // TableStorageService.DeserializeEventData normalizes JArray → List<object>, so every
                // transition shows up as a Dictionary<string, object> after the JToken conversion.
                if (transitionsObj is not System.Collections.IEnumerable enumerable)
                    continue;

                var failed = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item is not System.Collections.Generic.IDictionary<string, object> dict)
                        continue;

                    if (!dict.TryGetValue("newState", out var newStateObj) || newStateObj == null)
                        continue;
                    if (!string.Equals(newStateObj.ToString(), "failed", System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (dict.TryGetValue("subcategory", out var nameObj) && nameObj != null)
                    {
                        var name = nameObj.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                            failed.Add(name!);
                    }
                }

                if (failed.Count > 0)
                    evt.Data["failedSubcategories"] = string.Join(",", failed);
            }
        }

        /// <summary>
        /// Promotes a fired rule to a terminal Failed status on the session, but only when the session
        /// is still in a non-terminal state (InProgress/Pending/Stalled). Terminal states
        /// (Succeeded/Failed) are left untouched — the agent's own terminal signal wins, and we never
        /// overwrite a prior rule-based failure.
        /// </summary>
        private async Task TryMarkSessionFailedFromRuleAsync(string tenantId, string sessionId, AnalyzeRule rule)
        {
            try
            {
                var session = await _sessionRepo.GetSessionAsync(tenantId, sessionId);
                if (session == null)
                    return;

                // Don't stomp on an already-terminal session. This also makes the call idempotent:
                // on re-analysis the rule may fire again, but we only flip the status once.
                if (session.Status == SessionStatus.Succeeded || session.Status == SessionStatus.Failed)
                {
                    _logger.LogDebug($"Rule {rule.RuleId} fired for session {sessionId} but status is already {session.Status} — skipping session failure");
                    return;
                }

                var failureSource = $"rule:{rule.RuleId}";
                var failureReason = $"Rule: {rule.Title}";

                // No completedAt: rule firing is decoupled from real-time session activity
                // (analysis can run minutes after the last event), so UtcNow would inflate
                // DurationSeconds. Letting UpdateSessionStatusAsync fall back to LastEventAt
                // anchors duration on when the session actually went silent.
                await _sessionRepo.UpdateSessionStatusAsync(
                    tenantId, sessionId, SessionStatus.Failed,
                    failureReason: failureReason,
                    failureSource: failureSource);

                _logger.LogWarning($"Session {sessionId} marked as failed by rule {rule.RuleId} ('{rule.Title}')");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to mark session {sessionId} as failed via rule {rule.RuleId}");
            }
        }

        /// <summary>
        /// Evaluates a single rule against the full session event stream
        /// </summary>
        private RuleResult? EvaluateRule(AnalyzeRule rule, List<EnrollmentEvent> events)
        {
            // Preconditions gate (AND-semantics, silent skip): if any precondition fails the
            // rule is not evaluated at all — no result row, no UI card. Distinct from
            // conditions, which decide whether the rule fires given that it applies.
            if (rule.Preconditions != null && rule.Preconditions.Count > 0)
            {
                foreach (var pre in rule.Preconditions)
                {
                    if (!EvaluatePrecondition(pre, events))
                    {
                        _logger.LogDebug(
                            "Rule {RuleId} skipped by precondition (eventType={EventType}, field={Field}, op={Op}, value={Value})",
                            rule.RuleId, pre.EventType, pre.DataField, pre.Operator, pre.Value);
                        return null;
                    }
                }
            }

            var matchedConditions = new Dictionary<string, object>();
            int confidence = rule.BaseConfidence;
            bool allRequiredMet = true;

            // Evaluate each condition
            foreach (var condition in rule.Conditions)
            {
                var (matched, evidence) = EvaluateCondition(condition, events);

                if (condition.Required && !matched)
                {
                    allRequiredMet = false;
                    break;
                }

                if (matched)
                {
                    matchedConditions[condition.Signal] = evidence;
                }
            }

            if (!allRequiredMet)
                return null;

            // Safety net: if no conditions matched at all, the rule should not fire.
            // This prevents rules with all-optional conditions from vacuously triggering.
            if (matchedConditions.Count == 0)
                return null;

            // Calculate confidence from factors
            foreach (var factor in rule.ConfidenceFactors)
            {
                if (EvaluateConfidenceFactor(factor, events, matchedConditions))
                {
                    confidence += factor.Weight;
                    matchedConditions[$"factor_{factor.Signal}"] = true;
                }
            }

            // Cap confidence at 100
            confidence = Math.Min(confidence, 100);

            // Check threshold
            if (confidence < rule.ConfidenceThreshold)
                return null;

            return new RuleResult
            {
                RuleId = rule.RuleId,
                RuleTitle = rule.Title,
                Severity = rule.Severity,
                Category = rule.Category,
                ConfidenceScore = confidence,
                Explanation = rule.Explanation,
                Remediation = rule.Remediation,
                RelatedDocs = rule.RelatedDocs,
                MatchedConditions = matchedConditions,
                DetectedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Evaluates a single condition against the event stream
        /// </summary>
    }

    /// <summary>
    /// Return type for AnalyzeSessionAsync — includes both fired results and evaluation metadata for telemetry.
    /// </summary>
    public class AnalysisOutcome
    {
        /// <summary>Rules that fired (produced a result)</summary>
        public List<RuleResult> Results { get; set; } = new List<RuleResult>();

        /// <summary>All rules that were evaluated in this pass (includes rules that didn't fire)</summary>
        public List<AnalyzeRule> EvaluatedRules { get; set; } = new List<AnalyzeRule>();
    }
}
