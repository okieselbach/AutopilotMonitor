using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// evaluateOn interim-trigger semantics (docs/rules/analyze-rule-triggers.md):
/// trigger grammar + matching helpers, the engine's interim rule filter, the
/// RuleResult update/finalize/resolve lifecycle replacing the permanent dedupe
/// freeze, KO suppression on interim runs, and the ingest-side trigger registry.
/// </summary>
public class RuleEngineEvaluateOnTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string ProbeEventType = "custom_probe_signal";

    // ── Trigger grammar helpers ─────────────────────────────────────────────

    [Fact]
    public void EffectiveTriggers_absent_or_empty_defaults_to_enrollment_end()
    {
        Assert.Equal(new[] { "enrollment_end" }, AnalyzeRuleTriggers.EffectiveTriggers(new AnalyzeRule()));
        Assert.Equal(new[] { "enrollment_end" },
            AnalyzeRuleTriggers.EffectiveTriggers(new AnalyzeRule { EvaluateOn = new List<string>() }));

        var rule = new AnalyzeRule();
        Assert.True(AnalyzeRuleTriggers.RunsAtEnrollmentEnd(rule));
        Assert.False(AnalyzeRuleTriggers.RunsAtWhitegloveSealed(rule));
        Assert.Empty(AnalyzeRuleTriggers.OnEventTypes(rule));
    }

    [Fact]
    public void OnEventTypes_parses_prefix_and_matching_is_case_insensitive()
    {
        var rule = new AnalyzeRule
        {
            EvaluateOn = new List<string> { "enrollment_end", "on_event:Hybrid_Login_Pending", "whiteglove_sealed" },
        };

        Assert.Equal(new[] { "hybrid_login_pending" }, AnalyzeRuleTriggers.OnEventTypes(rule));
        Assert.True(AnalyzeRuleTriggers.RunsAtWhitegloveSealed(rule));
        Assert.True(AnalyzeRuleTriggers.MatchesOnEvent(rule, new[] { "HYBRID_LOGIN_PENDING" }));
        Assert.False(AnalyzeRuleTriggers.MatchesOnEvent(rule, new[] { "desktop_arrived" }));
        Assert.False(AnalyzeRuleTriggers.MatchesOnEvent(rule, Array.Empty<string>()));
    }

    [Theory]
    [InlineData("enrollment_end", true)]
    [InlineData("whiteglove_sealed", true)]
    [InlineData("on_event:hybrid_login_pending", true)]
    [InlineData("on_event:x1_2", true)]
    [InlineData("on_event:", false)]
    [InlineData("on_event:Not-Snake", false)]
    [InlineData("phase_exit:AccountSetup", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidTrigger_enforces_the_grammar(string? trigger, bool expected)
    {
        Assert.Equal(expected, AnalyzeRuleTriggers.IsValidTrigger(trigger));
    }

    // ── Engine: interim rule filter ─────────────────────────────────────────

    [Fact]
    public async Task Interim_run_evaluates_only_rules_matching_the_trigger()
    {
        var matching = ProbeRule("ANALYZE-TEST-9101", evaluateOn: new List<string> { "enrollment_end", $"on_event:{ProbeEventType}" });
        var terminalOnly = ProbeRule("ANALYZE-TEST-9102", evaluateOn: null);

        var fixture = CreateFixture(new List<AnalyzeRule> { matching, terminalOnly }, ProbeEvents());
        var outcome = await fixture.Engine.AnalyzeSessionAsync(
            TenantId, SessionId, AnalyzeRunContext.InterimTrigger(new[] { ProbeEventType }));

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-TEST-9101", result.RuleId);
        Assert.True(result.IsInterim);
        Assert.NotNull(result.FirstDetectedAt);
        Assert.NotNull(result.LastEvaluatedAt);
        Assert.Equal("ANALYZE-TEST-9101", Assert.Single(outcome.EvaluatedRules).RuleId);
    }

    [Fact]
    public async Task Interim_run_with_no_matching_rules_skips_the_event_read()
    {
        var terminalOnly = ProbeRule("ANALYZE-TEST-9103", evaluateOn: null);
        var fixture = CreateFixture(new List<AnalyzeRule> { terminalOnly }, ProbeEvents());

        var outcome = await fixture.Engine.AnalyzeSessionAsync(
            TenantId, SessionId, AnalyzeRunContext.InterimTrigger(new[] { ProbeEventType }));

        Assert.Empty(outcome.Results);
        Assert.Empty(outcome.EvaluatedRules);
        fixture.SessionRepo.Verify(
            s => s.GetSessionEventsStrictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task Whiteglove_sealed_run_matches_only_whiteglove_rules()
    {
        var wgRule = ProbeRule("ANALYZE-TEST-9104", evaluateOn: new List<string> { "enrollment_end", "whiteglove_sealed" });
        var onEventRule = ProbeRule("ANALYZE-TEST-9105", evaluateOn: new List<string> { $"on_event:{ProbeEventType}" });

        var fixture = CreateFixture(new List<AnalyzeRule> { wgRule, onEventRule }, ProbeEvents());
        var outcome = await fixture.Engine.AnalyzeSessionAsync(
            TenantId, SessionId, AnalyzeRunContext.WhitegloveSealed());

        Assert.Equal("ANALYZE-TEST-9104", Assert.Single(outcome.Results).RuleId);
    }

    // ── Engine: KO suppression ──────────────────────────────────────────────

    [Fact]
    public async Task Interim_run_suppresses_the_KO_escalation()
    {
        var koRule = ProbeRule("ANALYZE-TEST-9106", evaluateOn: new List<string> { $"on_event:{ProbeEventType}" });
        koRule.MarkSessionAsFailedDefault = true;

        var fixture = CreateFixture(new List<AnalyzeRule> { koRule }, ProbeEvents());
        var outcome = await fixture.Engine.AnalyzeSessionAsync(
            TenantId, SessionId, AnalyzeRunContext.InterimTrigger(new[] { ProbeEventType }));

        Assert.Single(outcome.Results);
        // TryMarkSessionFailedFromRuleAsync starts with GetSessionAsync — never reached on interim.
        fixture.SessionRepo.Verify(s => s.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Terminal_run_still_applies_the_KO_escalation()
    {
        var koRule = ProbeRule("ANALYZE-TEST-9107", evaluateOn: null);
        koRule.MarkSessionAsFailedDefault = true;

        var fixture = CreateFixture(new List<AnalyzeRule> { koRule }, ProbeEvents());
        await fixture.Engine.AnalyzeSessionAsync(TenantId, SessionId, AnalyzeRunContext.Terminal());

        fixture.SessionRepo.Verify(s => s.GetSessionAsync(TenantId, SessionId), Times.Once);
    }

    // ── Engine: result lifecycle (update semantics) ─────────────────────────

    [Fact]
    public async Task Terminal_run_skips_settled_final_rows_but_finalizes_interim_rows()
    {
        var settled = ProbeRule("ANALYZE-TEST-9108", evaluateOn: null);
        var interim = ProbeRule("ANALYZE-TEST-9109", evaluateOn: new List<string> { "enrollment_end", $"on_event:{ProbeEventType}" });

        var firstDetected = new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc);
        var notified = new DateTime(2026, 8, 13, 10, 5, 0, DateTimeKind.Utc);
        var existing = new List<RuleResult>
        {
            StoredResult("ANALYZE-TEST-9108", isInterim: false),
            StoredResult("ANALYZE-TEST-9109", isInterim: true, firstDetectedAt: firstDetected, notifiedAt: notified, resultId: "stable-result-id"),
        };

        var fixture = CreateFixture(new List<AnalyzeRule> { settled, interim }, ProbeEvents(), existing);
        var outcome = await fixture.Engine.AnalyzeSessionAsync(TenantId, SessionId, AnalyzeRunContext.Terminal());

        // Settled final row: classic dedupe — not re-evaluated, not in EvaluatedRules.
        Assert.DoesNotContain(outcome.EvaluatedRules, r => r.RuleId == "ANALYZE-TEST-9108");

        // Interim row: finalized with preserved identity + notification marker.
        var finalized = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-TEST-9109", finalized.RuleId);
        Assert.False(finalized.IsInterim);
        Assert.Null(finalized.ResolvedAt);
        Assert.Equal(firstDetected, finalized.FirstDetectedAt);
        Assert.Equal(firstDetected, finalized.DetectedAt);
        Assert.Equal(notified, finalized.NotifiedAt);
        Assert.Equal("stable-result-id", finalized.ResultId);
    }

    [Fact]
    public async Task Terminal_run_resolves_interim_rows_that_no_longer_fire()
    {
        // The rule requires an event type the session does NOT contain → no fire.
        var interim = ProbeRule("ANALYZE-TEST-9110",
            evaluateOn: new List<string> { "enrollment_end", "on_event:some_other_signal" },
            conditionEventType: "some_other_signal");

        var existing = new List<RuleResult> { StoredResult("ANALYZE-TEST-9110", isInterim: true) };
        var fixture = CreateFixture(new List<AnalyzeRule> { interim }, ProbeEvents(), existing);

        var outcome = await fixture.Engine.AnalyzeSessionAsync(TenantId, SessionId, AnalyzeRunContext.Terminal());

        Assert.Empty(outcome.Results);
        var resolved = Assert.Single(outcome.ResolvedResults);
        Assert.Equal("ANALYZE-TEST-9110", resolved.RuleId);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.False(resolved.IsInterim); // terminal settles the row
    }

    [Fact]
    public async Task Terminal_run_settles_a_row_an_interim_pass_already_resolved()
    {
        // Interim resolve keeps IsInterim=true (a later interim run may re-fire it);
        // the terminal pass must settle it to a final resolved row.
        var interim = ProbeRule("ANALYZE-TEST-9118",
            evaluateOn: new List<string> { "enrollment_end", "on_event:some_other_signal" },
            conditionEventType: "some_other_signal");

        var alreadyResolved = StoredResult("ANALYZE-TEST-9118", isInterim: true);
        alreadyResolved.ResolvedAt = new DateTime(2026, 8, 13, 13, 0, 0, DateTimeKind.Utc);

        var fixture = CreateFixture(new List<AnalyzeRule> { interim }, ProbeEvents(),
            new List<RuleResult> { alreadyResolved });
        var outcome = await fixture.Engine.AnalyzeSessionAsync(TenantId, SessionId, AnalyzeRunContext.Terminal());

        var settled = Assert.Single(outcome.ResolvedResults);
        Assert.False(settled.IsInterim);
        Assert.Equal(alreadyResolved.ResolvedAt, settled.ResolvedAt); // original resolve time kept
    }

    [Fact]
    public async Task Interim_refresh_keeps_interim_flag_and_preserves_notification_marker()
    {
        var rule = ProbeRule("ANALYZE-TEST-9111", evaluateOn: new List<string> { $"on_event:{ProbeEventType}" });
        var notified = new DateTime(2026, 8, 13, 11, 0, 0, DateTimeKind.Utc);
        var existing = new List<RuleResult>
        {
            StoredResult("ANALYZE-TEST-9111", isInterim: true, notifiedAt: notified),
        };

        var fixture = CreateFixture(new List<AnalyzeRule> { rule }, ProbeEvents(), existing);
        var outcome = await fixture.Engine.AnalyzeSessionAsync(
            TenantId, SessionId, AnalyzeRunContext.InterimTrigger(new[] { ProbeEventType }));

        var refreshed = Assert.Single(outcome.Results);
        Assert.True(refreshed.IsInterim);
        Assert.Equal(notified, refreshed.NotifiedAt);
    }

    [Fact]
    public async Task Interim_run_never_reopens_a_settled_final_row()
    {
        var rule = ProbeRule("ANALYZE-TEST-9112", evaluateOn: new List<string> { $"on_event:{ProbeEventType}" });
        var existing = new List<RuleResult> { StoredResult("ANALYZE-TEST-9112", isInterim: false) };

        var fixture = CreateFixture(new List<AnalyzeRule> { rule }, ProbeEvents(), existing);
        var outcome = await fixture.Engine.AnalyzeSessionAsync(
            TenantId, SessionId, AnalyzeRunContext.InterimTrigger(new[] { ProbeEventType }));

        Assert.Empty(outcome.Results);
        Assert.Empty(outcome.ResolvedResults);
    }

    [Fact]
    public async Task Reanalyze_preserves_notification_marker_and_resolves_stale_rows()
    {
        // fires: probe rule matches; stale: rule over an absent event type with a stored FINAL row.
        var firing = ProbeRule("ANALYZE-TEST-9113", evaluateOn: null);
        var stale = ProbeRule("ANALYZE-TEST-9114", evaluateOn: null, conditionEventType: "some_other_signal");

        var notified = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var existing = new List<RuleResult>
        {
            StoredResult("ANALYZE-TEST-9113", isInterim: false, notifiedAt: notified),
            StoredResult("ANALYZE-TEST-9114", isInterim: false),
        };

        var fixture = CreateFixture(new List<AnalyzeRule> { firing, stale }, ProbeEvents(), existing);
        var outcome = await fixture.Engine.AnalyzeSessionAsync(TenantId, SessionId, reanalyze: true);

        var refired = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-TEST-9113", refired.RuleId);
        Assert.Equal(notified, refired.NotifiedAt);

        var resolved = Assert.Single(outcome.ResolvedResults);
        Assert.Equal("ANALYZE-TEST-9114", resolved.RuleId);
        Assert.NotNull(resolved.ResolvedAt);
    }

    // ── Ingest trigger registry ─────────────────────────────────────────────

    [Fact]
    public async Task Registry_aggregates_on_event_types_and_whiteglove_flag()
    {
        var rules = new List<AnalyzeRule>
        {
            ProbeRule("ANALYZE-TEST-9115", evaluateOn: new List<string> { "enrollment_end", $"on_event:{ProbeEventType}" }),
            ProbeRule("ANALYZE-TEST-9116", evaluateOn: new List<string> { "whiteglove_sealed" }),
            ProbeRule("ANALYZE-TEST-9117", evaluateOn: null),
        };
        var fixture = CreateFixture(rules, ProbeEvents());

        var registry = new AutopilotMonitor.Functions.Services.Analyze.InterimTriggerRegistry(
            fixture.RuleService, NullLogger<AutopilotMonitor.Functions.Services.Analyze.InterimTriggerRegistry>.Instance);
        var triggers = await registry.GetAsync(TenantId);

        Assert.Contains(ProbeEventType, triggers.OnEventTypes);
        Assert.Single(triggers.OnEventTypes);
        Assert.True(triggers.HasWhitegloveSealedRules);
    }

    [Fact]
    public async Task Registry_is_fail_soft_on_rule_load_errors()
    {
        var repo = new Mock<IRuleRepository>();
        repo.Setup(r => r.GetAnalyzeRulesAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));

        var ruleService = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var registry = new AutopilotMonitor.Functions.Services.Analyze.InterimTriggerRegistry(
            ruleService, NullLogger<AutopilotMonitor.Functions.Services.Analyze.InterimTriggerRegistry>.Instance);

        var triggers = await registry.GetAsync(TenantId);
        Assert.Empty(triggers.OnEventTypes);
        Assert.False(triggers.HasWhitegloveSealedRules);
    }

    // ── Built-in catalog wiring ─────────────────────────────────────────────

    [Fact]
    public void BuiltIn_ID004_carries_the_hybrid_interim_trigger()
    {
        // Pins the JSON → model deserialization of evaluateOn end-to-end through the
        // embedded catalog (rules/dist/analyze-rules.json).
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ID-004");
        Assert.Equal(new List<string> { "enrollment_end", "on_event:hybrid_login_pending" }, rule.EvaluateOn);
        // Interim safety: the rule must not fire on a single overdue-login signal — the
        // threshold has to require the repetition factors (base 60 + count>=2 → 80).
        Assert.True(rule.ConfidenceThreshold > rule.BaseConfidence,
            "ANALYZE-ID-004 must be repetition-gated to stay interim-safe");
    }

    [Fact]
    public void BuiltIn_catalog_evaluateOn_triggers_are_all_grammatical()
    {
        foreach (var rule in BuiltInAnalyzeRules.GetAll())
        {
            if (rule.EvaluateOn is not { Count: > 0 })
                continue;
            foreach (var trigger in rule.EvaluateOn)
            {
                Assert.True(AnalyzeRuleTriggers.IsValidTrigger(trigger),
                    $"Rule {rule.RuleId} ships invalid evaluateOn trigger '{trigger}'");
            }
        }
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private sealed record Fixture(
        RuleEngine Engine,
        AnalyzeRuleService RuleService,
        Mock<IRuleRepository> RuleRepo,
        Mock<ISessionRepository> SessionRepo);

    /// <summary>
    /// Global partition mocked EMPTY (seed writes go to a black-hole mock), rules under test
    /// live in the TENANT partition as custom rules — that keeps the embedded-catalog sunset
    /// filter and the 43 shipped rules out of the picture entirely.
    /// </summary>
    private static Fixture CreateFixture(
        List<AnalyzeRule> tenantRules, List<EnrollmentEvent> events, List<RuleResult>? existingResults = null)
    {
        foreach (var rule in tenantRules)
        {
            rule.IsBuiltIn = false;
            rule.IsCommunity = false;
        }

        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(tenantRules);
        ruleRepo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(TenantId, SessionId))
            .ReturnsAsync(existingResults ?? new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>()))
            .ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object,
            NullLogger<RuleEngineEvaluateOnTests>.Instance);

        return new Fixture(engine, ruleService, ruleRepo, sessionRepo);
    }

    /// <summary>Minimal rule that fires when an event of <paramref name="conditionEventType"/> exists.</summary>
    private static AnalyzeRule ProbeRule(
        string ruleId, List<string>? evaluateOn, string conditionEventType = ProbeEventType)
    {
        return new AnalyzeRule
        {
            RuleId = ruleId,
            Title = $"Probe {ruleId}",
            Severity = "warning",
            Category = "device",
            Enabled = true,
            EvaluateOn = evaluateOn,
            BaseConfidence = 60,
            ConfidenceThreshold = 50,
            Explanation = "probe",
            Conditions = new List<RuleCondition>
            {
                new()
                {
                    Signal = "probe",
                    Source = "event_type",
                    EventType = conditionEventType,
                    Operator = "exists",
                    Value = string.Empty,
                    Required = true,
                },
            },
        };
    }

    private static List<EnrollmentEvent> ProbeEvents() => new()
    {
        new EnrollmentEvent
        {
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = ProbeEventType,
            Timestamp = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
            Message = "probe signal",
        },
    };

    private static RuleResult StoredResult(
        string ruleId, bool isInterim, DateTime? firstDetectedAt = null, DateTime? notifiedAt = null,
        string? resultId = null)
    {
        return new RuleResult
        {
            ResultId = resultId ?? Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            RuleId = ruleId,
            RuleTitle = $"Probe {ruleId}",
            Severity = "warning",
            Category = "device",
            ConfidenceScore = 60,
            Explanation = "probe",
            DetectedAt = firstDetectedAt ?? new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc),
            FirstDetectedAt = firstDetectedAt,
            IsInterim = isInterim,
            NotifiedAt = notifiedAt,
        };
    }
}
