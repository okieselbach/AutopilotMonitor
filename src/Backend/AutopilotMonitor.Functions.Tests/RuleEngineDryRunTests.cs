using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the dry-run (rule-authoring) evaluation path:
/// 1. The trace evaluates EVERY condition (no early break) so authors see the full picture.
/// 2. The verdict/confidence is outcome-equivalent to the production EvaluateRule path
///    (parity tests via AnalyzeSessionAsync).
/// 3. The dry-run is side-effect free: only the strict event read touches storage — never
///    UpdateSessionStatusAsync, never a rule-result write.
/// Plus the draft validation surface of <see cref="DryRunAnalyzeRuleFunction"/>.
/// </summary>
public class RuleEngineDryRunTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // ===== Trace semantics =====

    [Fact]
    public async Task Fired_FullTrace_WithFactorAndInterpolationEvidence()
    {
        var rule = MakeRule();
        rule.ConfidenceFactors = new List<ConfidenceFactor>
        {
            new() { Signal = "app_install_failed", Condition = "count >= 2", Weight = 15 },
        };
        var events = new List<EnrollmentEvent>
        {
            AppFailedEvent("app-1", "0x80070005"),
            AppFailedEvent("app-1", "0x80070005"),
        };

        var (dry, sessionRepo, ruleRepo) = await DryRunAsync(rule, events);

        Assert.Equal(RuleDryRunVerdict.Fired, dry.Verdict);
        Assert.Equal(2, dry.EventCount);
        Assert.Single(dry.Conditions);
        Assert.True(dry.Conditions[0].Matched);
        Assert.Equal(2, dry.Conditions[0].MatchingEventCount);
        Assert.IsType<Dictionary<string, object>>(dry.Conditions[0].Evidence);

        Assert.Single(dry.ConfidenceFactors);
        Assert.True(dry.ConfidenceFactors[0].Matched);
        Assert.Equal(50 + 15, dry.FinalConfidence);

        // Evidence map matches what production would persist — including the factor marker —
        // so clients can preview {{token}} interpolation against it.
        Assert.NotNull(dry.MatchedConditions);
        Assert.True(dry.MatchedConditions!.ContainsKey("app_failure"));
        Assert.True(dry.MatchedConditions.ContainsKey("factor_app_install_failed"));

        // Side-effect freedom: exactly one strict event read, nothing else.
        sessionRepo.Verify(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>()), Times.Once);
        sessionRepo.VerifyNoOtherCalls();
        ruleRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RequiredConditionNotMet_StillEvaluatesAllConditions()
    {
        var rule = MakeRule();
        rule.Conditions.Add(new RuleCondition
        {
            Signal = "os_edition", Source = "event_data", EventType = "os_info",
            DataField = "edition", Operator = "equals", Value = "Enterprise", Required = false,
        });
        var events = new List<EnrollmentEvent> { OsInfoEvent("Enterprise") }; // no app_install_failed

        var (dry, _, _) = await DryRunAsync(rule, events);

        Assert.Equal(RuleDryRunVerdict.RequiredConditionNotMet, dry.Verdict);
        // Production breaks on the first failed required condition; the dry-run keeps going —
        // the second (optional) condition must appear in the trace, matched.
        Assert.Equal(2, dry.Conditions.Count);
        Assert.False(dry.Conditions[0].Matched);
        Assert.Equal("no matching events", dry.Conditions[0].Evidence);
        Assert.Equal(0, dry.Conditions[0].MatchingEventCount);
        Assert.True(dry.Conditions[1].Matched);

        Assert.Null(dry.FinalConfidence);
        Assert.Empty(dry.ConfidenceFactors); // factor stage never reached, mirrors production
        Assert.False(dry.WouldMarkSessionAsFailed);
    }

    [Fact]
    public async Task PreconditionFails_VerdictSkipped_ConditionsStillTraced()
    {
        var rule = MakeRule();
        rule.Preconditions = new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "hardware_spec", DataField = "isVirtualMachine", Operator = "equals", Value = "false" },
        };
        var events = new List<EnrollmentEvent>
        {
            HardwareSpecEvent(isVirtualMachine: "true"),
            AppFailedEvent("app-1", "0x1"),
        };

        var (dry, _, _) = await DryRunAsync(rule, events);

        Assert.Equal(RuleDryRunVerdict.SkippedByPrecondition, dry.Verdict);
        Assert.Single(dry.Preconditions);
        Assert.False(dry.Preconditions[0].Passed);
        // Author still sees that the condition WOULD have matched.
        Assert.Single(dry.Conditions);
        Assert.True(dry.Conditions[0].Matched);
        Assert.Null(dry.FinalConfidence);
    }

    [Fact]
    public async Task BelowThreshold_ReportsFinalConfidence()
    {
        var rule = MakeRule();
        rule.BaseConfidence = 30;
        rule.ConfidenceThreshold = 60;
        var events = new List<EnrollmentEvent> { AppFailedEvent("app-1", "0x1") };

        var (dry, _, _) = await DryRunAsync(rule, events);

        Assert.Equal(RuleDryRunVerdict.BelowConfidenceThreshold, dry.Verdict);
        Assert.Equal(30, dry.FinalConfidence);
        Assert.Equal(60, dry.ConfidenceThreshold);
        Assert.False(dry.WouldMarkSessionAsFailed);
    }

    [Fact]
    public async Task NoEvents_ShortCircuits()
    {
        var (dry, _, _) = await DryRunAsync(MakeRule(), new List<EnrollmentEvent>());

        Assert.Equal(RuleDryRunVerdict.NoEvents, dry.Verdict);
        Assert.Equal(0, dry.EventCount);
        Assert.Empty(dry.Conditions);
    }

    [Fact]
    public async Task AllOptionalNoneMatch_VerdictNoConditionsMatched()
    {
        var rule = MakeRule();
        rule.Conditions[0].Required = false;
        var events = new List<EnrollmentEvent> { OsInfoEvent("Pro") };

        var (dry, _, _) = await DryRunAsync(rule, events);

        Assert.Equal(RuleDryRunVerdict.NoConditionsMatched, dry.Verdict);
        Assert.Null(dry.FinalConfidence);
    }

    [Fact]
    public async Task Fired_WithMarkSessionAsFailed_OnlyReportsFlag()
    {
        var rule = MakeRule();
        rule.MarkSessionAsFailedDefault = true;
        var events = new List<EnrollmentEvent> { AppFailedEvent("app-1", "0x1") };

        var (dry, sessionRepo, _) = await DryRunAsync(rule, events);

        Assert.Equal(RuleDryRunVerdict.Fired, dry.Verdict);
        Assert.True(dry.WouldMarkSessionAsFailed);
        // The flag is REPORTED, never acted upon: no session read, no status write.
        sessionRepo.Verify(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>()), Times.Once);
        sessionRepo.VerifyNoOtherCalls();
    }

    // ===== Parity with the production path =====

    public static TheoryData<string> ParityScenarios => new() { "fires", "required_miss", "below_threshold", "precondition_skip" };

    /// <summary>
    /// The dry-run verdict must be outcome-equivalent to EvaluateRule (via AnalyzeSessionAsync):
    /// fired ⇔ a RuleResult is produced, and on fire the confidence and evidence keys are identical.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParityScenarios))]
    public async Task DryRunVerdict_MatchesProductionOutcome(string scenario)
    {
        var rule = MakeRule();
        rule.ConfidenceFactors = new List<ConfidenceFactor>
        {
            new() { Signal = "app_install_failed", Condition = "count >= 2", Weight = 15 },
        };
        List<EnrollmentEvent> events = scenario switch
        {
            "fires" => new() { AppFailedEvent("a", "0x1"), AppFailedEvent("a", "0x1") },
            "required_miss" => new() { OsInfoEvent("Pro") },
            "below_threshold" => new() { AppFailedEvent("a", "0x1") },
            "precondition_skip" => new() { HardwareSpecEvent("true"), AppFailedEvent("a", "0x1") },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (scenario == "below_threshold") { rule.BaseConfidence = 30; rule.ConfidenceThreshold = 60; }
        if (scenario == "precondition_skip")
        {
            rule.Preconditions = new List<RulePrecondition>
            {
                new() { Source = "event_data", EventType = "hardware_spec", DataField = "isVirtualMachine", Operator = "equals", Value = "false" },
            };
        }

        var production = await RunProductionAsync(rule, events);
        var (dry, _, _) = await DryRunAsync(rule, events);

        var productionFired = production.Results.Count == 1;
        Assert.Equal(productionFired, dry.Verdict == RuleDryRunVerdict.Fired);

        if (productionFired)
        {
            var result = production.Results[0];
            Assert.Equal(result.ConfidenceScore, dry.FinalConfidence);
            Assert.Equal(
                result.MatchedConditions.Keys.OrderBy(k => k),
                dry.MatchedConditions!.Keys.OrderBy(k => k));
        }
    }

    // ===== Draft validation (DryRunAnalyzeRuleFunction.ValidateDraftRule) =====

    [Fact]
    public void Validate_ValidRule_NoErrors()
    {
        Assert.Empty(DryRunAnalyzeRuleFunction.ValidateDraftRule(MakeRule()));
    }

    [Fact]
    public void Validate_NullRule_Error()
    {
        Assert.Contains("rule is required", DryRunAnalyzeRuleFunction.ValidateDraftRule(null));
    }

    [Fact]
    public void Validate_NoConditions_Error()
    {
        var rule = MakeRule();
        rule.Conditions.Clear();
        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("at least one condition"));
    }

    [Theory]
    [InlineData("Event_Type")] // wrong case — the evaluator switch is case-sensitive
    [InlineData("eventtype")]
    [InlineData("")]
    public void Validate_UnknownOrMissingSource_Error(string source)
    {
        var rule = MakeRule();
        rule.Conditions[0].Source = source;
        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("source"));
    }

    [Fact]
    public void Validate_UnknownOperator_Error()
    {
        var rule = MakeRule();
        rule.Conditions[0].Operator = "starts_with";
        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("unknown operator 'starts_with'"));
    }

    [Fact]
    public void Validate_CorrelationWithoutJoinField_Error()
    {
        var rule = MakeRule();
        rule.Conditions[0].Source = "event_correlation";
        rule.Conditions[0].CorrelateEventType = "app_install_completed";
        rule.Conditions[0].JoinField = "";
        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("joinField"));
    }

    [Fact]
    public void Validate_InvalidRegex_Error()
    {
        var rule = MakeRule();
        rule.Conditions[0].Operator = "regex";
        rule.Conditions[0].Value = "([unclosed";
        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("not a valid regex"));
    }

    [Theory]
    [InlineData("count > 3")]           // wrong comparator
    [InlineData("Count >= 3")]          // wrong case
    [InlineData("phase_duration >= 3")] // wrong comparator
    [InlineData("always")]
    public void Validate_UnsupportedFactorCondition_Error(string condition)
    {
        var rule = MakeRule();
        rule.ConfidenceFactors = new List<ConfidenceFactor> { new() { Signal = "x", Condition = condition, Weight = 10 } };
        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("not evaluable"));
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("count >= 3")]
    [InlineData("count >=3")]
    [InlineData("phase_duration > 300")]
    public void Validate_SupportedFactorCondition_NoError(string condition)
    {
        var rule = MakeRule();
        rule.ConfidenceFactors = new List<ConfidenceFactor> { new() { Signal = "x", Condition = condition, Weight = 10 } };
        Assert.Empty(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule));
    }

    [Fact]
    public void Validate_DuplicateSignals_Error()
    {
        var rule = MakeRule();
        rule.Conditions.Add(new RuleCondition
        {
            Signal = "app_failure", Source = "event_type", EventType = "os_info", Operator = "exists", Required = false,
        });
        Assert.Contains(DryRunAnalyzeRuleFunction.ValidateDraftRule(rule), e => e.Contains("duplicate signal"));
    }

    [Fact]
    public void Validate_ConfidenceOutOfRange_Error()
    {
        var rule = MakeRule();
        rule.BaseConfidence = 120;
        rule.ConfidenceThreshold = -5;
        var errors = DryRunAnalyzeRuleFunction.ValidateDraftRule(rule);
        Assert.Contains(errors, e => e.Contains("baseConfidence"));
        Assert.Contains(errors, e => e.Contains("confidenceThreshold"));
    }

    // ===== Helpers =====

    /// <summary>Simple single-condition rule: required event_type match on app_install_failed,
    /// base 50 / threshold 40 — fires whenever the event exists.</summary>
    private static AnalyzeRule MakeRule() => new()
    {
        RuleId = "ANALYZE-TEST-001",
        Title = "Test rule",
        Severity = "warning",
        Category = "apps",
        // Drafts behave like tenant custom rules; also keeps the parity path clear of the
        // live-catalog sunset filter in GetAllRulesForTenantAsync (built-in ids only).
        IsBuiltIn = false,
        BaseConfidence = 50,
        ConfidenceThreshold = 40,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal = "app_failure", Source = "event_type", EventType = "app_install_failed",
                Operator = "exists", Value = "", Required = true,
            },
        },
        Explanation = "App {{appId}} failed with {{errorCode}}.",
    };

    private static EnrollmentEvent AppFailedEvent(string appId, string errorCode) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "app_install_failed",
        Timestamp = DateTime.UtcNow,
        Sequence = 1,
        Data = new Dictionary<string, object> { ["appId"] = appId, ["errorCode"] = errorCode },
    };

    private static EnrollmentEvent OsInfoEvent(string edition) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "os_info",
        Timestamp = DateTime.UtcNow,
        Sequence = 2,
        Data = new Dictionary<string, object> { ["edition"] = edition },
    };

    private static EnrollmentEvent HardwareSpecEvent(string isVirtualMachine) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "hardware_spec",
        Timestamp = DateTime.UtcNow,
        Sequence = 3,
        Data = new Dictionary<string, object> { ["isVirtualMachine"] = isVirtualMachine },
    };

    private static (RuleEngine engine, Mock<ISessionRepository> sessionRepo, Mock<IRuleRepository> ruleRepo) MakeEngine(List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);
        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineDryRunTests>.Instance);
        return (engine, sessionRepo, ruleRepo);
    }

    private static async Task<(RuleDryRun dry, Mock<ISessionRepository> sessionRepo, Mock<IRuleRepository> ruleRepo)> DryRunAsync(
        AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var (engine, sessionRepo, ruleRepo) = MakeEngine(events);
        var dry = await engine.DryRunRuleAsync(TenantId, SessionId, rule);
        return (dry, sessionRepo, ruleRepo);
    }

    private static async Task<AnalysisOutcome> RunProductionAsync(AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);
        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineDryRunTests>.Instance);
        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
