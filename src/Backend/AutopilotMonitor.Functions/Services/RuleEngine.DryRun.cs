using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Dry-run (diagnostic) evaluation of a single draft analyze rule against a session's events.
    /// Powers the rule-authoring loop (portal / MCP `test_analyze_rule`): the author gets a full
    /// per-condition trace instead of the production path's fire-or-silent-null.
    ///
    /// Deliberately side-effect free: nothing is persisted, the session status is never touched,
    /// no notifications are emitted. The trace loop mirrors <see cref="RuleEngine"/>.EvaluateRule
    /// but does NOT short-circuit on the first failed required condition — evaluating every
    /// condition is the whole point of a dry run. Both paths share the same evaluators
    /// (EvaluatePrecondition / EvaluateCondition / EvaluateConfidenceFactor), so a verdict of
    /// "fired" here is equivalent to EvaluateRule returning a result; the parity test
    /// (RuleEngineDryRunTests) pins that equivalence.
    /// </summary>
    public partial class RuleEngine
    {
        /// <summary>
        /// Loads the session's events (strict read — storage failures propagate) and dry-runs the
        /// given draft rule against them. Mirrors the event handling of AnalyzeSessionAsync
        /// (strict load + BackfillDerivedEventFields) so the dry-run sees exactly what a real
        /// analysis pass would see.
        /// </summary>
        public async Task<RuleDryRun> DryRunRuleAsync(string tenantId, string sessionId, AnalyzeRule rule)
        {
            var allEvents = await _sessionRepo.GetSessionEventsStrictAsync(tenantId, sessionId);
            if (allEvents.Count == 0)
            {
                return new RuleDryRun
                {
                    Verdict = RuleDryRunVerdict.NoEvents,
                    EventCount = 0,
                    BaseConfidence = rule.BaseConfidence,
                    ConfidenceThreshold = rule.ConfidenceThreshold,
                };
            }

            BackfillDerivedEventFields(allEvents);
            return DryRunEvaluate(rule, allEvents);
        }

        /// <summary>
        /// Trace twin of EvaluateRule. Keep the confidence arithmetic and the factor evaluation
        /// ORDER in lock-step with EvaluateRule — factors are evaluated sequentially against the
        /// accumulating matchedConditions dictionary (a factor's "exists" check can observe
        /// earlier factor_&lt;signal&gt; entries), so reordering would change results.
        /// </summary>
        internal RuleDryRun DryRunEvaluate(AnalyzeRule rule, List<EnrollmentEvent> events)
        {
            var dry = new RuleDryRun
            {
                EventCount = events.Count,
                BaseConfidence = rule.BaseConfidence,
                ConfidenceThreshold = rule.ConfidenceThreshold,
            };

            var preconditionsPassed = true;
            if (rule.Preconditions != null)
            {
                foreach (var pre in rule.Preconditions)
                {
                    var passed = EvaluatePrecondition(pre, events);
                    if (!passed) preconditionsPassed = false;
                    dry.Preconditions.Add(new RuleDryRunPrecondition
                    {
                        Source = pre.Source,
                        EventType = pre.EventType,
                        DataField = string.IsNullOrEmpty(pre.DataField) ? null : pre.DataField,
                        Operator = pre.Operator,
                        Value = pre.Value,
                        Passed = passed,
                    });
                }
            }

            // Evaluate ALL conditions (no early break — the author wants the full picture).
            // Outcome-equivalent to EvaluateRule: a failed required condition makes the
            // production path return null before matchedConditions is ever used.
            var matchedConditions = new Dictionary<string, object>();
            var allRequiredMet = true;
            foreach (var condition in rule.Conditions)
            {
                var (matched, evidence) = EvaluateCondition(condition, events);
                if (condition.Required && !matched) allRequiredMet = false;
                if (matched) matchedConditions[condition.Signal] = evidence;

                dry.Conditions.Add(new RuleDryRunCondition
                {
                    Signal = condition.Signal,
                    Source = condition.Source,
                    EventType = string.IsNullOrEmpty(condition.EventType) ? null : condition.EventType,
                    Required = condition.Required,
                    Matched = matched,
                    // Matched: the evidence dictionary the production path stores. Not matched:
                    // the evaluator's reason string ("no matching events", …).
                    Evidence = evidence,
                    MatchingEventCount = string.IsNullOrEmpty(condition.EventType)
                        ? null
                        : events.Count(e => MatchesEventType(e, condition.EventType)),
                });
            }

            dry.MatchedConditions = matchedConditions;

            if (!preconditionsPassed)
            {
                dry.Verdict = RuleDryRunVerdict.SkippedByPrecondition;
                return dry;
            }

            if (!allRequiredMet)
            {
                dry.Verdict = RuleDryRunVerdict.RequiredConditionNotMet;
                return dry;
            }

            if (matchedConditions.Count == 0)
            {
                dry.Verdict = RuleDryRunVerdict.NoConditionsMatched;
                return dry;
            }

            var confidence = rule.BaseConfidence;
            foreach (var factor in rule.ConfidenceFactors)
            {
                var factorMatched = EvaluateConfidenceFactor(factor, events, matchedConditions);
                if (factorMatched)
                {
                    confidence += factor.Weight;
                    matchedConditions[$"factor_{factor.Signal}"] = true;
                }

                dry.ConfidenceFactors.Add(new RuleDryRunFactor
                {
                    Signal = factor.Signal,
                    Condition = factor.Condition,
                    Weight = factor.Weight,
                    Matched = factorMatched,
                });
            }

            confidence = Math.Min(confidence, 100);
            dry.FinalConfidence = confidence;

            if (confidence < rule.ConfidenceThreshold)
            {
                dry.Verdict = RuleDryRunVerdict.BelowConfidenceThreshold;
                return dry;
            }

            dry.Verdict = RuleDryRunVerdict.Fired;
            dry.WouldMarkSessionAsFailed = rule.MarkSessionAsFailed ?? rule.MarkSessionAsFailedDefault;
            return dry;
        }
    }

    // RuleDryRun, RuleDryRunVerdict and the per-stage trace classes are wire contract and live in
    // AutopilotMonitor.Shared.Models (RulesApiModels.cs).
}
