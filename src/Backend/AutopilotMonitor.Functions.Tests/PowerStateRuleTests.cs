using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Power watcher analyze rules — pins the firing behaviour of the built-ins that consume the
/// agent's live <c>power_state_change</c> events (PowerStateWatcherHost):
///   - ANALYZE-DEV-009 (high): battery crossed the 15% threshold while enrolling on battery.
///   - ANALYZE-DEV-010 (warning): the device switched from AC to battery mid-enrollment.
/// Both rules are deliberately single-required-condition: event_data conditions scan events
/// INDEPENDENTLY (no same-instance join), so a two-condition rule could false-positive across
/// two different power events. DEV-009 matches the agent-stamped thresholdPercent (only present
/// on threshold events, value pins the ladder level); DEV-010 matches transition=ac_to_battery.
/// Also pins that the rule's string "15" compares equal against the agent's numeric payload.
/// </summary>
public class PowerStateRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task ANALYZE_DEV_009_fires_high_on_the_15_percent_threshold_event()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-009");
        Assert.True(rule.Enabled);
        Assert.Equal("high", rule.Severity);
        Assert.False(rule.MarkSessionAsFailedDefault); // environment risk, not a KO verdict

        var events = new List<EnrollmentEvent>
        {
            ThresholdCrossed(thresholdPercent: 15, batteryPercent: 13),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-009", result.RuleId);
        Assert.Equal("high", result.Severity);

        // The numeric payload (int 15) must satisfy the rule's string value "15"; field/value on
        // the matched condition feed the {{thresholdPercent}} interpolation token.
        var matched = AsDict(result.MatchedConditions["battery_threshold_15"]);
        Assert.Equal("thresholdPercent", AsString(matched["field"]));
        Assert.Equal("15", AsString(matched["value"]));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(30)]
    public async Task ANALYZE_DEV_009_does_not_fire_on_higher_threshold_levels(int level)
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-009");

        var events = new List<EnrollmentEvent>
        {
            ThresholdCrossed(thresholdPercent: level, batteryPercent: level - 2),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_DEV_009_does_not_fire_on_a_low_battery_transition_event()
    {
        // An ac_to_battery event at 13% carries batteryPercent but NO thresholdPercent — the
        // agent emits the separate threshold event for that; the rule must not fire early here.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-009");

        var events = new List<EnrollmentEvent>
        {
            Transition("ac_to_battery", onAcPower: false, batteryPercent: 13),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_DEV_010_fires_warning_on_ac_to_battery()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-010");
        Assert.True(rule.Enabled);
        Assert.Equal("warning", rule.Severity);
        Assert.False(rule.MarkSessionAsFailedDefault);

        var events = new List<EnrollmentEvent>
        {
            Transition("ac_to_battery", onAcPower: false, batteryPercent: 63),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-010", result.RuleId);
        Assert.Equal("warning", result.Severity);

        var matched = AsDict(result.MatchedConditions["switched_to_battery"]);
        Assert.Equal("transition", AsString(matched["field"]));
        Assert.Equal("ac_to_battery", AsString(matched["value"]));
    }

    [Theory]
    [InlineData("battery_to_ac")]
    [InlineData("threshold_crossed")]
    public async Task ANALYZE_DEV_010_does_not_fire_on_other_transitions(string transition)
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-010");

        var events = new List<EnrollmentEvent>
        {
            Transition(transition, onAcPower: transition == "battery_to_ac", batteryPercent: 40),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Neither_rule_fires_on_the_startup_power_state_check()
    {
        // The one-shot startup probe event (power_state_check) is a different event type and must
        // not satisfy either rule, even when it reports a low battery on DC power.
        var rules = BuiltInAnalyzeRules.GetAll()
            .Where(r => r.RuleId is "ANALYZE-DEV-009" or "ANALYZE-DEV-010").ToList();
        Assert.Equal(2, rules.Count);

        var events = new List<EnrollmentEvent>
        {
            new()
            {
                EventId = Guid.NewGuid().ToString(),
                TenantId = TenantId,
                SessionId = SessionId,
                EventType = "power_state_check",
                Timestamp = DateTime.UtcNow,
                Sequence = 5,
                Data = new Dictionary<string, object>
                {
                    ["onAcPower"] = false,
                    ["hasBattery"] = true,
                    ["batteryPercent"] = 12,
                    ["isCharging"] = false,
                },
            },
        };

        foreach (var rule in rules)
        {
            var outcome = await RunAsync(rule, events);
            Assert.Empty(outcome.Results);
        }
    }

    // ===== Event builders — mirror the PowerStateWatcherHost emit shape =====

    private static EnrollmentEvent ThresholdCrossed(int thresholdPercent, int batteryPercent) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "power_state_change",
        Timestamp = DateTime.UtcNow,
        Sequence = 42,
        Data = new Dictionary<string, object>
        {
            ["transition"] = "threshold_crossed",
            ["thresholdPercent"] = thresholdPercent,
            ["onAcPower"] = false,
            ["batteryPercent"] = batteryPercent,
            ["isCharging"] = false,
            ["batteryLifeMinutes"] = 25,
        }
    };

    private static EnrollmentEvent Transition(string transition, bool onAcPower, int batteryPercent) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "power_state_change",
        Timestamp = DateTime.UtcNow,
        Sequence = 43,
        Data = new Dictionary<string, object>
        {
            ["transition"] = transition,
            ["onAcPower"] = onAcPower,
            ["batteryPercent"] = batteryPercent,
            ["isCharging"] = onAcPower,
            ["batteryLifeMinutes"] = 120,
        }
    };

    private static Dictionary<string, object> AsDict(object o)
    {
        if (o is Dictionary<string, object> d) return d;
        throw new InvalidOperationException($"Expected Dictionary<string,object>, got {o?.GetType().Name ?? "null"}");
    }

    private static string AsString(object o) => o?.ToString() ?? string.Empty;

    private static async Task<AnalysisOutcome> RunAsync(AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<PowerStateRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
