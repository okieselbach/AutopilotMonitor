using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the slim-evidence contract for <c>event_correlation</c> rules. Before this fix the
/// per-pair evidence carried <c>eventA_message</c> / <c>eventB_message</c> plus a free-text
/// <c>errorDetail</c> field, and <c>allMatches</c> was uncapped — long-running stuck sessions
/// could push <c>RuleResult.MatchedConditionsJson</c> past Table Storage's 64KB property limit
/// and the persist would silently return false. The queue worker (post-fix) caught the false
/// and triggered a retry, but the underlying problem was still oversized evidence.
/// </summary>
public class RuleEngineCorrelationEvidenceSlimTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public async Task Correlation_evidence_omits_message_and_errorDetail_to_stay_slim()
    {
        var rule = BuildCorrelationRule();
        var baseTime = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>
        {
            BuildEvent("evt-A", "app_install_failed",    appId: "abc", sequence: 1, timestamp: baseTime,
                       errorDetail: new string('x', 8000), message: "long error string " + new string('y', 8000)),
            BuildEvent("evt-B", "app_install_completed", appId: "abc", sequence: 2, timestamp: baseTime.AddSeconds(5),
                       errorDetail: null, message: "completion details " + new string('z', 8000)),
        };

        var (engine, _) = SutWithRule(rule, events);
        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId);

        Assert.Single(outcome.Results);
        var primary = (Dictionary<string, object>)outcome.Results[0].MatchedConditions["enforcement_resolved"];

        Assert.False(primary.ContainsKey("eventA_message"), "free-text message must not leak into evidence");
        Assert.False(primary.ContainsKey("eventB_message"), "free-text message must not leak into evidence");
        Assert.False(primary.ContainsKey("eventA_errorDetail"), "errorDetail (often stack trace) must not leak into evidence");
        Assert.False(primary.ContainsKey("eventB_errorDetail"), "errorDetail (often stack trace) must not leak into evidence");

        // Identifying fields stay — they're short and necessary for the explanation.
        Assert.Equal("evt-A", primary["eventA_eventId"]);
        Assert.Equal("evt-B", primary["eventB_eventId"]);
        Assert.Equal("abc",   primary["eventA_appId"]);
        Assert.Equal(1,       primary["totalMatches"]);
    }

    [Fact]
    public async Task Correlation_with_many_pairs_caps_allMatches_at_ten_and_flags_truncated()
    {
        var rule = BuildCorrelationRule();
        var baseTime = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>();
        // 25 distinct app failures + 25 matching completions — uncapped this would be 25 pairs.
        for (var i = 0; i < 25; i++)
        {
            events.Add(BuildEvent($"evt-A-{i}", "app_install_failed",    appId: $"app-{i}", sequence: i * 2,     timestamp: baseTime.AddSeconds(i * 10)));
            events.Add(BuildEvent($"evt-B-{i}", "app_install_completed", appId: $"app-{i}", sequence: i * 2 + 1, timestamp: baseTime.AddSeconds(i * 10 + 5)));
        }

        var (engine, _) = SutWithRule(rule, events);
        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId);

        Assert.Single(outcome.Results);
        var primary = (Dictionary<string, object>)outcome.Results[0].MatchedConditions["enforcement_resolved"];

        Assert.Equal(25, primary["totalMatches"]);
        Assert.True(primary.ContainsKey("allMatches"));
        var allMatches = (List<Dictionary<string, object>>)primary["allMatches"];
        Assert.Equal(10, allMatches.Count);
        Assert.True((bool)primary["matchesTruncated"]);
    }

    [Fact]
    public async Task Correlation_with_multiple_pairs_serializes_without_self_referencing_loop()
    {
        // Regression pin: production session d434a3ba (2026-04-28) hit
        //   "Self referencing loop detected with type 'Dictionary<String,Object>'.
        //    Path 'enforcement_error_resolved.allMatches'."
        // because matchedPairs[0] WAS primary by reference, and primary["allMatches"] = matchedPairs
        // looped primary -> allMatches[0] -> primary. Cloning the dicts in allMatches breaks the
        // cycle. This test fails fast if a future refactor re-introduces the shared reference.
        var rule = BuildCorrelationRule();
        var baseTime = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>();
        for (var i = 0; i < 5; i++)
        {
            events.Add(BuildEvent($"evt-A-{i}", "app_install_failed",    appId: $"app-{i}", sequence: i * 2,     timestamp: baseTime.AddSeconds(i * 10)));
            events.Add(BuildEvent($"evt-B-{i}", "app_install_completed", appId: $"app-{i}", sequence: i * 2 + 1, timestamp: baseTime.AddSeconds(i * 10 + 5)));
        }

        var (engine, _) = SutWithRule(rule, events);
        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId);

        Assert.Single(outcome.Results);

        // The actual production failure was JsonConvert.SerializeObject throwing
        // JsonSerializationException at the StoreRuleResultAsync persist step. Reproduce that
        // exact serialize call here — if the loop sneaks back, this throws.
        var json = JsonConvert.SerializeObject(outcome.Results[0].MatchedConditions);
        Assert.False(string.IsNullOrEmpty(json));
        Assert.Contains("allMatches", json);
        Assert.Contains("totalMatches", json);
    }

    [Fact]
    public async Task Correlation_with_few_pairs_does_not_truncate()
    {
        var rule = BuildCorrelationRule();
        var baseTime = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>();
        for (var i = 0; i < 3; i++)
        {
            events.Add(BuildEvent($"evt-A-{i}", "app_install_failed",    appId: $"app-{i}", sequence: i * 2,     timestamp: baseTime.AddSeconds(i * 10)));
            events.Add(BuildEvent($"evt-B-{i}", "app_install_completed", appId: $"app-{i}", sequence: i * 2 + 1, timestamp: baseTime.AddSeconds(i * 10 + 5)));
        }

        var (engine, _) = SutWithRule(rule, events);
        var outcome = await engine.AnalyzeSessionAsync(TenantId, SessionId);

        Assert.Single(outcome.Results);
        var primary = (Dictionary<string, object>)outcome.Results[0].MatchedConditions["enforcement_resolved"];

        Assert.Equal(3, primary["totalMatches"]);
        var allMatches = (List<Dictionary<string, object>>)primary["allMatches"];
        Assert.Equal(3, allMatches.Count);
        Assert.False((bool)primary["matchesTruncated"]);
    }

    // ============================================================ helpers

    private static (RuleEngine engine, Mock<IRuleRepository> ruleRepo) SutWithRule(AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        // The "global" partition holds built-in/community rules; the tenant-specific partition
        // holds custom rules. AnalyzeRuleService merges both. We park the rule under "global"
        // and return empty for the tenant partition so it doesn't show up twice.
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(It.Is<string>(s => s != "global"))).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineCorrelationEvidenceSlimTests>.Instance);

        return (engine, ruleRepo);
    }

    private static AnalyzeRule BuildCorrelationRule() => new()
    {
        RuleId          = "TEST-CORR-001",
        Title           = "Test correlation rule",
        Severity        = "warning",
        Category        = "apps",
        Trigger         = "correlation",
        Enabled         = true,
        BaseConfidence  = 80,
        IsBuiltIn       = false,
        IsCommunity     = false,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal             = "enforcement_resolved",
                Source             = "event_correlation",
                EventType          = "app_install_failed",
                CorrelateEventType = "app_install_completed",
                JoinField          = "appId",
                Required           = true,
            }
        },
        ConfidenceFactors = new List<ConfidenceFactor>(),
    };

    private static EnrollmentEvent BuildEvent(string eventId, string eventType, string appId, int sequence, DateTime timestamp, string? errorDetail = null, string? message = null)
    {
        var data = new Dictionary<string, object>
        {
            ["appId"] = appId,
            ["appName"] = $"App-{appId}",
            ["errorPatternId"] = "IME-ERROR-ENFORCEMENT",
        };
        if (errorDetail != null)
            data["errorDetail"] = errorDetail;

        return new EnrollmentEvent
        {
            EventId   = eventId,
            TenantId  = TenantId,
            SessionId = SessionId,
            EventType = eventType,
            Sequence  = sequence,
            Timestamp = timestamp,
            Message   = message ?? string.Empty,
            Data      = data,
        };
    }
}
