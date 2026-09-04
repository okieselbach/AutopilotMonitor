using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// ANALYZE-ID-004 v2.0.0 — pins the built-in that consumes the agent's user-affinity signal:
/// the real user reached the desktop (<c>desktop_arrived</c>), IME acquired no Entra user token
/// within the wait window (<c>entra_user_affinity_pending</c>, at most once per agent process),
/// and no <c>ime_user_token_acquired</c> disproves it. Repetition across reboots is the
/// confidence lever (base 60, +20 at two, threshold 75), the terminal precondition on
/// <c>enrollment_complete</c> stays the suppression at session end, and the v1.x evidence
/// (<c>hybrid_login_pending</c>, the JoinInfo placeholder) must no longer fire the rule.
/// </summary>
public class HybridUserAffinityRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "77777777-8888-9999-aaaa-bbbbbbbbbbbb";

    private static AnalyzeRule Rule() => BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ID-004");

    [Fact]
    public void ANALYZE_ID_004_v2_shape_is_pinned()
    {
        var rule = Rule();
        Assert.Equal("2.0.0", rule.Version);
        Assert.True(rule.Enabled);
        Assert.Equal("high", rule.Severity);
        Assert.False(rule.MarkSessionAsFailedDefault);
        Assert.Equal(75, rule.ConfidenceThreshold);
        Assert.Contains("on_event:entra_user_affinity_pending", rule.EvaluateOn!);
        Assert.DoesNotContain("on_event:hybrid_login_pending", rule.EvaluateOn!);

        // The old evidence is gone from every rule surface — trigger, conditions and factors.
        Assert.DoesNotContain(rule.Conditions, c => c.EventType is "hybrid_login_pending" or "aad_placeholder_user_detected");
        Assert.DoesNotContain(rule.ConfidenceFactors, f => f.Signal is "hybrid_login_pending" or "aad_placeholder_user_detected");

        var noToken = Assert.Single(rule.Conditions, c => c.Signal == "no_user_token");
        Assert.Equal("event_type", noToken.Source);
        Assert.Equal("ime_user_token_acquired", noToken.EventType);
        Assert.Equal("not_exists", noToken.Operator);
        Assert.True(noToken.Required);
    }

    [Fact]
    public async Task Fires_when_affinity_is_pending_twice_and_no_token_was_acquired()
    {
        var events = new List<EnrollmentEvent>
        {
            DesktopArrived(sequence: 10),
            AffinityPending(sequence: 20),
            AffinityPending(sequence: 30), // second agent process after a reboot
        };

        var outcome = await RunAsync(Rule(), events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-ID-004", result.RuleId);
        Assert.True(result.ConfidenceScore >= 75, $"confidence {result.ConfidenceScore} below threshold");
        Assert.Equal(80, result.ConfidenceScore); // base 60 + count >= 2

        // The absence condition carries its own evidence so the matched-condition map is
        // complete for the UI, not just the two presence signals.
        Assert.True(result.MatchedConditions.ContainsKey("user_affinity_pending"));
        Assert.True(result.MatchedConditions.ContainsKey("user_desktop_reached"));
        Assert.True(result.MatchedConditions.ContainsKey("no_user_token"));
    }

    [Fact]
    public async Task Three_pending_warnings_reach_confidence_90()
    {
        var events = new List<EnrollmentEvent>
        {
            DesktopArrived(sequence: 10),
            AffinityPending(sequence: 20),
            AffinityPending(sequence: 30),
            AffinityPending(sequence: 40),
        };

        var outcome = await RunAsync(Rule(), events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(90, result.ConfidenceScore); // base 60 + 20 + 10
    }

    [Fact]
    public async Task Does_not_fire_on_a_single_pending_warning()
    {
        // One agent process without a token is hybrid latency until it repeats: 60 < 75.
        var events = new List<EnrollmentEvent>
        {
            DesktopArrived(sequence: 10),
            AffinityPending(sequence: 20),
        };

        var outcome = await RunAsync(Rule(), events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Does_not_fire_when_a_user_token_was_acquired()
    {
        // A token success after the desktop disproves the diagnosis even when the warning
        // repeated (e.g. the first two processes stalled, the third one got the PRT).
        var events = new List<EnrollmentEvent>
        {
            DesktopArrived(sequence: 10),
            AffinityPending(sequence: 20),
            AffinityPending(sequence: 30),
            Event("ime_user_token_acquired", sequence: 40, new Dictionary<string, object>
            {
                ["minutesAfterDesktop"] = 3,
                ["tokenFailuresBeforeSuccess"] = 4,
                ["isHybridJoin"] = true,
            }),
        };

        var outcome = await RunAsync(Rule(), events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Is_skipped_by_the_precondition_when_the_enrollment_completed()
    {
        var events = new List<EnrollmentEvent>
        {
            DesktopArrived(sequence: 10),
            AffinityPending(sequence: 20),
            AffinityPending(sequence: 30),
            Event("enrollment_complete", sequence: 40, new Dictionary<string, object>()),
        };

        var outcome = await RunAsync(Rule(), events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Does_not_fire_on_the_v1_evidence_alone()
    {
        // hybrid_login_pending now means "no real-user desktop yet"; together with the placeholder
        // it was the v1.x evidence and must not satisfy v2 — even at the old confidence-raising count.
        var events = new List<EnrollmentEvent>
        {
            DesktopArrived(sequence: 10),
            Event("hybrid_login_pending", sequence: 20, HybridLoginPendingData()),
            Event("hybrid_login_pending", sequence: 30, HybridLoginPendingData()),
            Event("hybrid_login_pending", sequence: 40, HybridLoginPendingData()),
            Event("aad_placeholder_user_detected", sequence: 50, new Dictionary<string, object>()),
            Event("aad_placeholder_user_detected", sequence: 60, new Dictionary<string, object>()),
            Event("aad_placeholder_user_detected", sequence: 70, new Dictionary<string, object>()),
        };

        var outcome = await RunAsync(Rule(), events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Does_not_fire_without_a_real_user_desktop()
    {
        // The agent only arms the detector after the desktop, but the rule must not rely on that:
        // two pending warnings without desktop_arrived are not this diagnosis.
        var events = new List<EnrollmentEvent>
        {
            AffinityPending(sequence: 20),
            AffinityPending(sequence: 30),
        };

        var outcome = await RunAsync(Rule(), events);
        Assert.Empty(outcome.Results);
    }

    // ===== Event builders — mirror the agent emit shapes =====

    private static EnrollmentEvent DesktopArrived(int sequence) =>
        Event("desktop_arrived", sequence, new Dictionary<string, object>());

    private static EnrollmentEvent AffinityPending(int sequence) =>
        Event("entra_user_affinity_pending", sequence, new Dictionary<string, object>
        {
            ["delayMinutes"] = 10,
            ["reason"] = "no_user_token_after_desktop",
            ["minutesSinceDesktop"] = 10,
            ["tokenFailureCount"] = 4,
            ["tokenFailureCodes"] = "0xCAA2000C",
            ["placeholderActive"] = true,
            ["isHybridJoin"] = true,
        });

    private static Dictionary<string, object> HybridLoginPendingData() => new()
    {
        ["delayMinutes"] = 10,
        ["reason"] = "no_real_user_desktop",
        ["isHybridJoin"] = true,
        ["realUserDesktopSeen"] = false,
        ["placeholderActive"] = true,
    };

    private static EnrollmentEvent Event(string eventType, int sequence, Dictionary<string, object> data) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = eventType,
        Timestamp = DateTime.UtcNow.AddMinutes(sequence),
        Sequence = sequence,
        Data = data,
    };

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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<HybridUserAffinityRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
