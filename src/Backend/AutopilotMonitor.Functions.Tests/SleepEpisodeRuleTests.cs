using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// ANALYZE-DEV-012 — pins the firing behaviour of the built-in that consumes the agent's
/// <c>system_sleep_episode</c> events (SystemTimelineWatcherHost): informational, fires once
/// (trigger single) when a completed sleep/hibernate/Modern-Standby episode of at least
/// 5 minutes is observed. Single-required-condition on durationSeconds so the match is pinned
/// to one event instance and the interpolated duration is the matched value. Also pins that the
/// agent's numeric payload (long) satisfies the rule's string "300" via numeric gte.
/// </summary>
public class SleepEpisodeRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "66666666-7777-8888-9999-aaaaaaaaaaaa";

    [Fact]
    public async Task ANALYZE_DEV_012_fires_info_on_a_five_minute_episode()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-012");
        Assert.True(rule.Enabled);
        Assert.Equal("info", rule.Severity);
        Assert.False(rule.MarkSessionAsFailedDefault); // environment observation, never a KO verdict

        var events = new List<EnrollmentEvent>
        {
            SleepEpisode(durationSeconds: 300, kind: "modern_standby"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-012", result.RuleId);
        Assert.Equal("info", result.Severity);

        // durationSeconds is the matched condition's dataField, so {{durationSeconds}} in the
        // explanation interpolates the matched value (the DEV-009 whitelist lesson).
        var matched = AsDict(result.MatchedConditions["slept_during_enrollment"]);
        Assert.Equal("durationSeconds", AsString(matched["field"]));
    }

    [Theory]
    [InlineData(60)]   // real but short episode — below the rule's 5-minute floor
    [InlineData(299)]  // boundary: gte 300 must not fire at 299
    public async Task ANALYZE_DEV_012_does_not_fire_below_the_five_minute_floor(long durationSeconds)
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-012");

        var events = new List<EnrollmentEvent>
        {
            SleepEpisode(durationSeconds, kind: "sleep"),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_DEV_012_does_not_fire_on_a_clock_change_event()
    {
        // The sibling watcher event carries a large timeDeltaMs but is a different event type —
        // the rule must key on system_sleep_episode only.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-012");

        var events = new List<EnrollmentEvent>
        {
            new()
            {
                EventId = Guid.NewGuid().ToString(),
                TenantId = TenantId,
                SessionId = SessionId,
                EventType = "system_clock_changed",
                Timestamp = DateTime.UtcNow,
                Sequence = 7,
                Data = new Dictionary<string, object>
                {
                    ["timeDeltaMs"] = 7_200_000L,
                    ["durationSeconds"] = 900L, // adversarial: same field name on the wrong type
                    ["reason"] = 1,
                },
            },
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_DEV_012_does_not_fire_on_a_backfilled_pre_session_episode()
    {
        // Backlog q8n: the watcher backfills pre-agent episodes for timeline context. A 91 h
        // hibernate that ended before the enrollment started (session a2256107) satisfied the
        // duration test and fired the rule although the device never slept while enrolling.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-012");

        var events = new List<EnrollmentEvent>
        {
            SleepEpisode(durationSeconds: 329_140, kind: "hibernate", backfilled: true),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_DEV_012_pins_duration_and_backfilled_to_the_same_episode()
    {
        // The adversarial mix from a2256107: a long backfilled episode plus a short live one.
        // Two independent conditions (duration >= 300, backfilled = false) would each find a
        // match on a different event and fire; the same-event filter must not.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-012");

        var events = new List<EnrollmentEvent>
        {
            SleepEpisode(durationSeconds: 329_140, kind: "hibernate", backfilled: true),
            SleepEpisode(durationSeconds: 89, kind: "modern_standby", backfilled: false),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_DEV_012_fires_on_a_live_episode_next_to_a_backfilled_one()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-012");

        var events = new List<EnrollmentEvent>
        {
            SleepEpisode(durationSeconds: 329_140, kind: "hibernate", backfilled: true),
            SleepEpisode(durationSeconds: 600, kind: "modern_standby", backfilled: false),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        var matched = AsDict(result.MatchedConditions["slept_during_enrollment"]);
        Assert.Equal("600", AsString(matched["value"])); // the live episode, not the backfilled one
    }

    // ===== Event builder — mirrors the SystemTimelineTracker emit shape =====

    private static EnrollmentEvent SleepEpisode(long durationSeconds, string kind, bool backfilled = false) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "system_sleep_episode",
        Timestamp = DateTime.UtcNow,
        Sequence = 42,
        Data = new Dictionary<string, object>
        {
            ["kind"] = kind,
            ["enteredAt"] = DateTime.UtcNow.AddSeconds(-durationSeconds).ToString("o"),
            ["exitedAt"] = DateTime.UtcNow.ToString("o"),
            ["durationSeconds"] = durationSeconds,
            ["backfilled"] = backfilled,
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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<SleepEpisodeRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
