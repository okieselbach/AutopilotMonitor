using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the absence gate on <c>event_type</c> conditions: <c>not_exists</c> without a
/// <c>dataField</c> matches when no event of that type occurs in the session and is disproved by
/// a single such event. Before this branch an absent type always evaluated false, so a required
/// "must not have happened" condition could never be expressed (ANALYZE-ID-004 v2 relies on it).
/// The dataField variant keeps its historical meaning (some event of the type has an empty field)
/// and is not touched here.
/// </summary>
public class RuleEngineEventTypeNotExistsTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "12121212-3434-5656-7878-909090909090";

    [Fact]
    public async Task Not_exists_matches_when_the_event_type_is_absent()
    {
        var events = new List<EnrollmentEvent> { Event("marker_seen", 1) };

        var outcome = await RunAsync(Rule(), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsType<Dictionary<string, object>>(result.MatchedConditions["disproof_absent"]);
        Assert.Equal("disproof", evidence["eventType"]);
        Assert.Equal(0, evidence["count"]);
    }

    [Fact]
    public async Task A_single_event_of_the_type_vetoes_a_required_not_exists()
    {
        var events = new List<EnrollmentEvent> { Event("marker_seen", 1), Event("disproof", 2) };

        var outcome = await RunAsync(Rule(), events);

        Assert.Empty(outcome.Results);
    }

    private static AnalyzeRule Rule() => new()
    {
        // Custom-namespace rule: a built-in ID outside the live catalog is hidden by the
        // sunset filter in AnalyzeRuleService (IsBuiltIn defaults to true).
        RuleId = "ANALYZE-CUSTOM-001",
        IsBuiltIn = false,
        Title = "not_exists absence gate",
        Severity = "info",
        Category = "device",
        Enabled = true,
        Trigger = "correlation",
        BaseConfidence = 80,
        ConfidenceThreshold = 50,
        Conditions = new List<RuleCondition>
        {
            new() { Signal = "marker", Source = "event_type", EventType = "marker_seen", Operator = "exists", Value = "", Required = true },
            new() { Signal = "disproof_absent", Source = "event_type", EventType = "disproof", Operator = "not_exists", Value = "", Required = true },
        },
    };

    private static EnrollmentEvent Event(string eventType, int sequence) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = eventType,
        Timestamp = DateTime.UtcNow.AddMinutes(sequence),
        Sequence = sequence,
        Data = new Dictionary<string, object>(),
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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineEventTypeNotExistsTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
