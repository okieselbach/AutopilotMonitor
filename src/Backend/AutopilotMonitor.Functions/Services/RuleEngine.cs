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
        /// Evaluates analyze rules against the full session event stream. Fetches events internally.
        /// Legacy overload — maps to <see cref="AnalyzeRunKind.Terminal"/> (or
        /// <see cref="AnalyzeRunKind.Reanalyze"/> when <paramref name="reanalyze"/> is true).
        /// </summary>
        public Task<AnalysisOutcome> AnalyzeSessionAsync(string tenantId, string sessionId, bool reanalyze = false)
        {
            var context = reanalyze ? AnalyzeRunContext.Reanalyze() : AnalyzeRunContext.Terminal();
            return AnalyzeSessionAsync(tenantId, sessionId, context);
        }

        /// <summary>
        /// Evaluates analyze rules against the full session event stream under an explicit run
        /// context (see <see cref="AnalyzeRunContext"/> and docs/rules/analyze-rule-triggers.md).
        /// <para>
        /// <b>Rule scope:</b> Terminal/Reanalyze runs evaluate every active rule; Interim runs
        /// evaluate only rules whose <c>evaluateOn</c> matches the run's trigger.
        /// </para>
        /// <para>
        /// <b>Result lifecycle (update semantics):</b> a rule with an existing FINAL row is skipped
        /// on Terminal and Interim runs (classic dedupe). A rule with an existing INTERIM row is
        /// always re-evaluated — Interim runs refresh it, the Terminal run finalizes it
        /// (IsInterim=false) or resolves it (<see cref="RuleResult.ResolvedAt"/>) when it no longer
        /// fires. FirstDetectedAt/DetectedAt and NotifiedAt are preserved across refreshes so the
        /// finding's identity and its notification dedupe survive re-evaluation. Reanalyze
        /// re-evaluates everything but merges the same lifecycle markers.
        /// </para>
        /// <para>
        /// <b>KO suppression:</b> the MarkSessionAsFailed escalation only runs on Terminal and
        /// Reanalyze — an interim pass must never flip a Pending/InProgress session to Failed.
        /// </para>
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
        public async Task<AnalysisOutcome> AnalyzeSessionAsync(string tenantId, string sessionId, AnalyzeRunContext context)
        {
            var outcome = new AnalysisOutcome();

            var activeRules = await _ruleService.GetActiveRulesForTenantAsync(tenantId);

            // Interim runs only look at rules that asked for this trigger — bail before the
            // (expensive) event read when the tenant has none. The ingest-side trigger registry
            // already gates the enqueue, so this is a cheap second net against registry staleness.
            if (context.Kind == AnalyzeRunKind.Interim)
            {
                activeRules = activeRules.Where(r => MatchesInterimRun(r, context)).ToList();
                if (activeRules.Count == 0)
                {
                    _logger.LogInformation(
                        $"Interim analyze ({context.Reason}) for session {sessionId}: no matching evaluateOn rules, skipping");
                    return outcome;
                }
            }

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

            // Always load existing results: even the Reanalyze path (which re-evaluates every
            // rule) needs them to preserve the lifecycle markers (FirstDetectedAt, NotifiedAt).
            var existingResults = await _ruleRepo.GetRuleResultsAsync(tenantId, sessionId);
            var existingByRuleId = existingResults
                .GroupBy(r => r.RuleId)
                .ToDictionary(g => g.Key, g => g.First());

            _logger.LogInformation(
                $"Analyzing session {sessionId} ({context.Kind}/{context.Reason}): {allEvents.Count} events, {activeRules.Count} rules ({existingByRuleId.Count} with stored results)");

            var now = DateTime.UtcNow;

            foreach (var rule in activeRules)
            {
                try
                {
                    existingByRuleId.TryGetValue(rule.RuleId, out var existing);

                    // Classic dedupe: a FINAL row is settled — neither Terminal nor Interim
                    // touches it again. Only Reanalyze (explicit user action) re-opens it.
                    // Interim rows are always re-evaluated (refresh / finalize / resolve).
                    if (context.Kind != AnalyzeRunKind.Reanalyze && existing != null && !existing.IsInterim)
                        continue;

                    // Track that this rule was evaluated (for telemetry)
                    outcome.EvaluatedRules.Add(rule);

                    var result = EvaluateRule(rule, allEvents);
                    if (result != null)
                    {
                        result.SessionId = sessionId;
                        result.TenantId = tenantId;
                        ApplyLifecycle(result, existing, context.Kind, now);
                        outcome.Results.Add(result);
                        _logger.LogInformation($"Rule {rule.RuleId} ({rule.Trigger}) fired for session {sessionId} with confidence {result.ConfidenceScore}% ({context.Kind})");

                        // KO-criterion: if the (effective) MarkSessionAsFailed flag is on,
                        // escalate the rule finding to a terminal session status. Tenant override
                        // wins; otherwise we honor the rule-definition default. Suppressed on
                        // interim runs — a Pending/InProgress session must never be failed early.
                        var shouldFailSession = rule.MarkSessionAsFailed ?? rule.MarkSessionAsFailedDefault;
                        if (shouldFailSession && context.Kind != AnalyzeRunKind.Interim)
                        {
                            await TryMarkSessionFailedFromRuleAsync(tenantId, sessionId, rule);
                        }
                    }
                    else if (existing != null && (existing.IsInterim || context.Kind == AnalyzeRunKind.Reanalyze))
                    {
                        // The finding no longer fires (the session healed, or a terminal-context
                        // precondition now suppresses it). Keep the row for audit, mark resolved.
                        var changed = false;
                        if (existing.ResolvedAt == null)
                        {
                            existing.ResolvedAt = now;
                            changed = true;
                            _logger.LogInformation($"Rule {rule.RuleId} resolved for session {sessionId} ({context.Kind}) — no longer fires");
                        }
                        // Terminal + Reanalyze settle the row; an interim resolve stays interim
                        // (a later interim run may legitimately re-fire it). Also settles rows
                        // that an earlier interim pass already resolved.
                        if (context.Kind != AnalyzeRunKind.Interim && existing.IsInterim)
                        {
                            existing.IsInterim = false;
                            changed = true;
                        }
                        if (changed)
                        {
                            existing.LastEvaluatedAt = now;
                            outcome.ResolvedResults.Add(existing);
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

        /// <summary>True when the rule's evaluateOn matches this interim run's trigger.</summary>
        private static bool MatchesInterimRun(AnalyzeRule rule, AnalyzeRunContext context)
        {
            if (string.Equals(context.Reason, AnalyzeRunContext.ReasonWhitegloveSealed, StringComparison.OrdinalIgnoreCase))
                return AnalyzeRuleTriggers.RunsAtWhitegloveSealed(rule);
            return AnalyzeRuleTriggers.MatchesOnEvent(rule, context.TriggerEventTypes);
        }

        /// <summary>
        /// Merges the evaluation-lifecycle markers from an existing row into a freshly built
        /// result so a refresh/finalization keeps the finding's identity: DetectedAt and
        /// FirstDetectedAt stay anchored on the FIRST fire (stable UI ordering, no radar
        /// double-hits) and NotifiedAt survives so the notification dedupe can never re-arm.
        /// </summary>
        private static void ApplyLifecycle(RuleResult result, RuleResult? existing, AnalyzeRunKind kind, DateTime now)
        {
            result.IsInterim = kind == AnalyzeRunKind.Interim;
            result.LastEvaluatedAt = now;
            result.ResolvedAt = null;

            if (existing != null)
            {
                result.FirstDetectedAt = existing.FirstDetectedAt ?? existing.DetectedAt;
                result.DetectedAt = existing.FirstDetectedAt ?? existing.DetectedAt;
                result.NotifiedAt = existing.NotifiedAt;
                // Keep the row's stable identity across refreshes.
                if (!string.IsNullOrEmpty(existing.ResultId))
                    result.ResultId = existing.ResultId;
            }
            else
            {
                result.FirstDetectedAt = result.DetectedAt;
            }
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
        /// <summary>Rules that fired (produced a result) — new findings and refreshed/finalized interim rows.</summary>
        public List<RuleResult> Results { get; set; } = new List<RuleResult>();

        /// <summary>
        /// Previously stored findings that no longer fire (session healed / terminal precondition
        /// now suppresses). Marked with <see cref="RuleResult.ResolvedAt"/>; callers persist them
        /// like Results but never notify or count them as issues.
        /// </summary>
        public List<RuleResult> ResolvedResults { get; set; } = new List<RuleResult>();

        /// <summary>All rules that were evaluated in this pass (includes rules that didn't fire)</summary>
        public List<AnalyzeRule> EvaluatedRules { get; set; } = new List<AnalyzeRule>();
    }

    /// <summary>Which flavor of analyze run is executing — decides rule scope, dedupe and KO behavior.</summary>
    public enum AnalyzeRunKind
    {
        /// <summary>Enrollment-end run (complete / failed / sweep-terminalized / vuln rerun): all rules, KO applies, interim rows finalized.</summary>
        Terminal,
        /// <summary>evaluateOn-triggered run before the session is terminal: filtered rules, KO + stats suppressed.</summary>
        Interim,
        /// <summary>Manual "Analyze Now": every rule re-evaluated, lifecycle markers preserved, never notifies.</summary>
        Reanalyze,
    }

    /// <summary>
    /// Run context for <see cref="RuleEngine.AnalyzeSessionAsync(string, string, AnalyzeRunContext)"/>.
    /// Built from the queue envelope's Reason (see docs/rules/analyze-rule-triggers.md).
    /// </summary>
    public sealed class AnalyzeRunContext
    {
        public const string ReasonWhitegloveSealed = "whiteglove_sealed";
        public const string ReasonInterimTrigger = "interim_trigger";

        public AnalyzeRunKind Kind { get; private init; }

        /// <summary>The envelope reason verbatim (diagnostics + whiteglove_sealed matching).</summary>
        public string Reason { get; private init; } = string.Empty;

        /// <summary>For interim_trigger runs: the batch's matched trigger event types.</summary>
        public IReadOnlyCollection<string> TriggerEventTypes { get; private init; } = Array.Empty<string>();

        public static AnalyzeRunContext Terminal(string reason = "enrollment_end")
            => new() { Kind = AnalyzeRunKind.Terminal, Reason = reason };

        public static AnalyzeRunContext Reanalyze()
            => new() { Kind = AnalyzeRunKind.Reanalyze, Reason = "reanalyze" };

        public static AnalyzeRunContext WhitegloveSealed()
            => new() { Kind = AnalyzeRunKind.Interim, Reason = ReasonWhitegloveSealed };

        public static AnalyzeRunContext InterimTrigger(IReadOnlyCollection<string>? triggerEventTypes)
            => new()
            {
                Kind = AnalyzeRunKind.Interim,
                Reason = ReasonInterimTrigger,
                TriggerEventTypes = triggerEventTypes ?? Array.Empty<string>(),
            };
    }
}
