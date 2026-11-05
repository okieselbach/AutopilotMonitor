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
        /// analysis pass would see. The evaluation runs on <see cref="RuleEvaluationBudget.DryRun"/>
        /// and ends early when <paramref name="cancellationToken"/> is signalled (D-410).
        /// </summary>
        public async Task<RuleDryRun> DryRunRuleAsync(string tenantId, string sessionId, AnalyzeRule rule, CancellationToken cancellationToken = default)
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
            return DryRunEvaluate(rule, allEvents, new RuleEvaluationBudget(RuleEvaluationBudget.DryRun, cancellationToken));
        }

        /// <summary>
        /// Trace twin of EvaluateRule. Keep the confidence arithmetic and the factor evaluation
        /// ORDER in lock-step with EvaluateRule — factors are evaluated sequentially against the
        /// accumulating matchedConditions dictionary (a factor's "exists" check can observe
        /// earlier factor_&lt;signal&gt; entries), so reordering would change results.
        /// A stop by the budget ends the trace with verdict "stopped", a stop reason and a note
        /// naming the precondition or condition it stopped at. Confidence factors need no budget
        /// check: they never run an operator and scan the events at most once each.
        /// </summary>
        internal RuleDryRun DryRunEvaluate(AnalyzeRule rule, List<EnrollmentEvent> events, RuleEvaluationBudget budget)
        {
            var dry = new RuleDryRun
            {
                EventCount = events.Count,
                BaseConfidence = rule.BaseConfidence,
                ConfidenceThreshold = rule.ConfidenceThreshold,
            };

            // The precondition or condition being evaluated — a stop note names it.
            var stage = string.Empty;
            var preconditionsPassed = true;
            var matchedConditions = new Dictionary<string, object>();
            var allRequiredMet = true;
            try
            {
                if (rule.Preconditions != null)
                {
                    for (var i = 0; i < rule.Preconditions.Count; i++)
                    {
                        var pre = rule.Preconditions[i];
                        stage = $"preconditions[{i}] ({pre.EventType})";
                        var passed = EvaluatePrecondition(pre, events, budget);
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
                for (var i = 0; i < rule.Conditions.Count; i++)
                {
                    var condition = rule.Conditions[i];
                    stage = $"conditions[{i}] ('{condition.Signal}')";
                    var (matched, evidence) = EvaluateCondition(condition, events, budget);
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
            }
            catch (RuleEvaluationStoppedException stop)
            {
                // The trace keeps what finished before the stop; the evidence map stays unset,
                // so no client previews a half-evaluated finding.
                dry.Verdict = RuleDryRunVerdict.Stopped;
                dry.StopReason = stop.Reason;
                dry.StopNote = StopNote(stage, stop.Reason, budget, events.Count);
                return dry;
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

        /// <summary>What the author reads when the dry-run stopped: where, why, and whether trying again helps.</summary>
        private static string StopNote(string stage, string reason, RuleEvaluationBudget budget, int eventCount) => reason switch
        {
            EvaluationStopReasons.RegexTimeout =>
                $"{stage}: a regex hit the {RegexMatchTimeout.TotalSeconds:0.#} s match timeout on this session's data — the pattern backtracks catastrophically; remove nested or overlapping quantifiers. Running the test again gives the same result, and the analysis would stop this rule the same way.",
            EvaluationStopReasons.TimeBudget =>
                $"{stage}: the dry-run used up its {budget.Limit.TotalSeconds:0.#} s time budget on this session's {eventCount} events — the rule is too expensive here; narrow its event types or simplify its conditions.",
            _ => $"{stage}: the request was cancelled before the evaluation finished.",
        };
    }

    // RuleDryRun, RuleDryRunVerdict and the per-stage trace classes are wire contract and live in
    // AutopilotMonitor.Shared.Models (RulesApiModels.cs).
}
