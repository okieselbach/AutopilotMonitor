using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// End-to-end behavior of the shipped <c>ANALYZE-ENRL-002</c> ("Session Timed Out") rule against the
/// real RuleEngine. Drives the actual embedded rule JSON (via <see cref="BuiltInAnalyzeRules"/>) so the
/// preconditions + condition shape is validated, not a hand-rebuilt copy.
///
/// Contract:
///   - fires when a server-authored <c>session_timeout</c> event exists and the session neither
///     completed nor explicitly failed;
///   - is suppressed (silent skip) when a later <c>enrollment_complete</c> exists — the 5h server
///     timeout can fire before the agent's 6h max-lifetime, so a slow session may complete afterwards;
///   - is suppressed when an <c>enrollment_failed</c> exists — already covered by ANALYZE-ENRL-001,
///     so we never double-report.
/// </summary>
public class SessionTimeoutRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public void ShippedRule_IsPresentAndShaped()
    {
        var rule = GetRule();
        Assert.Equal("enrollment", rule.Category);
        Assert.True(rule.Enabled);
        Assert.Contains(rule.Conditions, c => c.EventType == "session_timeout" && c.Operator == "exists");
        // Two pure-type-presence suppression gates (no dataField).
        Assert.NotNull(rule.Preconditions);
        Assert.Contains(rule.Preconditions, p => p.EventType == "enrollment_complete" && p.Operator == "not_exists" && string.IsNullOrEmpty(p.DataField));
        Assert.Contains(rule.Preconditions, p => p.EventType == "enrollment_failed" && p.Operator == "not_exists" && string.IsNullOrEmpty(p.DataField));
    }

    [Fact]
    public async Task Fires_WhenOnlySessionTimeout()
    {
        var outcome = await RunAsync(new List<EnrollmentEvent> { SessionTimeoutEvent() });

        Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-ENRL-002", outcome.Results[0].RuleId);
    }

    [Fact]
    public async Task Suppressed_WhenEnrollmentCompleteAlsoPresent()
    {
        var outcome = await RunAsync(new List<EnrollmentEvent>
        {
            SessionTimeoutEvent(),
            EnrollmentCompleteEvent() // late completion (5h-6h race) → suppress the timeout flag
        });

        Assert.DoesNotContain(outcome.Results, r => r.RuleId == "ANALYZE-ENRL-002");
    }

    [Fact]
    public async Task Suppressed_WhenEnrollmentFailedAlsoPresent()
    {
        var outcome = await RunAsync(new List<EnrollmentEvent>
        {
            SessionTimeoutEvent(),
            EnrollmentFailedEvent() // explicit failure → ANALYZE-ENRL-001 owns it, don't double-report
        });

        Assert.DoesNotContain(outcome.Results, r => r.RuleId == "ANALYZE-ENRL-002");
    }

    // ===== Helpers =====

    private static AnalyzeRule GetRule()
    {
        var rule = BuiltInAnalyzeRules.GetAll().FirstOrDefault(r => r.RuleId == "ANALYZE-ENRL-002");
        Assert.NotNull(rule);
        return rule!;
    }

    private static EnrollmentEvent SessionTimeoutEvent() => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "session_timeout",
        Source = "System.Maintenance",
        Severity = EventSeverity.Error,
        Timestamp = DateTime.UtcNow,
        Sequence = 100,
        Data = new Dictionary<string, object> { ["timeoutHours"] = 5, ["source"] = "maintenance_sweep" }
    };

    private static EnrollmentEvent EnrollmentCompleteEvent() => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "enrollment_complete",
        Timestamp = DateTime.UtcNow,
        Sequence = 101,
        Data = new Dictionary<string, object> { ["signalsSeen"] = "esp_final_exit,hello" }
    };

    private static EnrollmentEvent EnrollmentFailedEvent() => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "enrollment_failed",
        Timestamp = DateTime.UtcNow,
        Sequence = 101,
        Data = new Dictionary<string, object> { ["reason"] = "esp_terminal_failure" }
    };

    private static async Task<AnalysisOutcome> RunAsync(List<EnrollmentEvent> events)
    {
        var rule = GetRule();

        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<SessionTimeoutRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
