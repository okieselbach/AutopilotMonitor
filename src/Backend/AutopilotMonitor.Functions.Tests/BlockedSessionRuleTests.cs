using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The shipped rules on a session the platform blocked, driven through the real RuleEngine with
/// the embedded rule JSON: ANALYZE-ENRL-006 explains the block — right after it, through the
/// interim run the block recorder enqueues — and ANALYZE-ENRL-002 no longer blames a dead agent
/// or an offline device for the silence the block caused.
/// </summary>
public class BlockedSessionRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public void BlockRule_RunsRightAfterTheBlock()
    {
        var rule = Rule("ANALYZE-ENRL-006");
        Assert.True(rule.Enabled);
        Assert.Contains("on_event:device_blocked", rule.EvaluateOn!);
        Assert.Contains("enrollment_end", rule.EvaluateOn!);
    }

    [Fact]
    public async Task BlockRule_Fires_OnTheBlockEvent()
    {
        var outcome = await RunAsync("ANALYZE-ENRL-006", new List<EnrollmentEvent> { DeviceBlocked(eventLimit: true) });

        Assert.Contains(outcome.Results, r => r.RuleId == "ANALYZE-ENRL-006");
    }

    [Fact]
    public async Task BlockRule_Fires_ForAnOperatorBlockWithoutCounts()
    {
        var outcome = await RunAsync("ANALYZE-ENRL-006", new List<EnrollmentEvent> { DeviceBlocked(eventLimit: false) });

        Assert.Contains(outcome.Results, r => r.RuleId == "ANALYZE-ENRL-006");
    }

    [Fact]
    public async Task BlockRule_Silent_WithoutABlock()
    {
        var outcome = await RunAsync("ANALYZE-ENRL-006", new List<EnrollmentEvent> { SessionTimeout() });

        Assert.DoesNotContain(outcome.Results, r => r.RuleId == "ANALYZE-ENRL-006");
    }

    [Fact]
    public async Task TimeoutRule_Suppressed_WhenThePlatformBlockedTheSession()
    {
        var outcome = await RunAsync("ANALYZE-ENRL-002", new List<EnrollmentEvent> { DeviceBlocked(eventLimit: true), SessionTimeout() });

        Assert.DoesNotContain(outcome.Results, r => r.RuleId == "ANALYZE-ENRL-002");
    }

    [Fact]
    public async Task TimeoutRule_StillFires_WithoutABlock()
    {
        var outcome = await RunAsync("ANALYZE-ENRL-002", new List<EnrollmentEvent> { SessionTimeout() });

        Assert.Contains(outcome.Results, r => r.RuleId == "ANALYZE-ENRL-002");
    }

    // ===== Helpers =====

    private static AnalyzeRule Rule(string ruleId)
    {
        var rule = BuiltInAnalyzeRules.GetAll().FirstOrDefault(r => r.RuleId == ruleId);
        Assert.NotNull(rule);
        return rule!;
    }

    private static EnrollmentEvent DeviceBlocked(bool eventLimit)
    {
        var record = new SessionBlockRecord
        {
            BlockedAt = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc),
            Action = DeviceBlockActions.Block,
            BlockedUntil = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc),
            Cause = eventLimit ? SessionBlockCauses.EventLimit : SessionBlockCauses.Operator,
            EventCount = eventLimit ? 2553 : null,
            EventThreshold = eventLimit ? 2500 : null,
        };
        var session = new SessionSummary { TenantId = TenantId, SessionId = SessionId };
        return SessionBlockRecorder.BuildDeviceBlockedEvent(session, record, new List<EnrollmentEvent>());
    }

    private static EnrollmentEvent SessionTimeout() => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "session_timeout",
        Source = "System.Maintenance",
        Severity = EventSeverity.Error,
        Timestamp = new DateTime(2026, 10, 14, 17, 0, 0, DateTimeKind.Utc),
        Sequence = 100,
        Data = new Dictionary<string, object> { ["timeoutHours"] = 5, ["source"] = "maintenance_sweep" }
    };

    private static async Task<AnalysisOutcome> RunAsync(string ruleId, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { Rule(ruleId) });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<BlockedSessionRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
