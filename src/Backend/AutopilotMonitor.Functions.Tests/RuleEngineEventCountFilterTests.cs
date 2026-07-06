using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the optional value filter on the <c>event_count</c> condition source
/// (FilterField/FilterOperator/FilterValue): only events whose filter field satisfies the
/// operator are counted. Backs "sustained" rules like ANALYZE-DEV-002, which requires
/// memory_used_percent &gt; 90 across at least 3 performance snapshots instead of firing
/// on a single transient spike.
/// </summary>
public class RuleEngineEventCountFilterTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public async Task SustainedHighMemory_ThreeSnapshotsAboveThreshold_Fires()
    {
        var events = Snapshots(95, 93, 40, 96, 50);

        var outcome = await RunAsync(MakeSustainedMemoryRule(), events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-002", result.RuleId);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["sustained_high_memory"]);
        Assert.Equal(3, Convert.ToInt32(evidence["count"]));
        Assert.Equal(3, Convert.ToInt32(evidence["threshold"]));
    }

    [Fact]
    public async Task TransientSpike_OnlyTwoSnapshotsAboveThreshold_DoesNotFire()
    {
        // The pre-filter must drop the below-threshold snapshots — an unfiltered count
        // (5 events >= 3) would fire here.
        var events = Snapshots(95, 40, 50, 60, 93);

        var outcome = await RunAsync(MakeSustainedMemoryRule(), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task NoFilterConfigured_CountsAllEventsOfType()
    {
        // Backward compatibility: without FilterField/FilterOperator the classic
        // count_gte semantics (count every event of the type) must be unchanged.
        var rule = MakeSustainedMemoryRule();
        rule.Conditions[0].FilterField = null!;
        rule.Conditions[0].FilterOperator = null!;
        rule.Conditions[0].FilterValue = null!;

        var events = Snapshots(10, 20, 30);

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task FilterAppliesToPerGroupCount()
    {
        // The filter runs before grouping: only failed installs of the same app count.
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-TST-001",
            Title = "Grouped count with filter",
            Severity = "warning",
            Category = "apps",
            Enabled = true,
            // Synthetic rule ID not in the embedded built-in catalog: without this flag the
            // AnalyzeRuleService live-catalog filter would hide it as sunset-pending
            // (AnalyzeRule.IsBuiltIn defaults to true).
            IsBuiltIn = false,
            BaseConfidence = 80,
            ConfidenceThreshold = 40,
            Conditions = new List<RuleCondition>
            {
                new()
                {
                    Signal = "repeated_not_detected",
                    Source = "event_count",
                    EventType = "app_install_failed",
                    DataField = "appId",
                    Operator = "count_per_group_gte",
                    Value = "2",
                    FilterField = "detectionResult",
                    FilterOperator = "equals",
                    FilterValue = "NotDetected",
                    Required = true
                }
            },
            Explanation = "test"
        };

        // app-1: two NotDetected → fires. app-2: two events but only one NotDetected.
        var events = new List<EnrollmentEvent>
        {
            AppFailedEvent("app-1", "NotDetected", 1),
            AppFailedEvent("app-1", "NotDetected", 2),
            AppFailedEvent("app-2", "NotDetected", 3),
            AppFailedEvent("app-2", "Error", 4)
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["repeated_not_detected"]);
        Assert.Equal("app-1", evidence["groupKey"]);
        Assert.Equal(2, Convert.ToInt32(evidence["count"]));
    }

    // ===== Helpers =====

    /// <summary>Mirrors the shipped ANALYZE-DEV-002 v2 condition shape.</summary>
    private static AnalyzeRule MakeSustainedMemoryRule() => new()
    {
        RuleId = "ANALYZE-DEV-002",
        Title = "Sustained High Memory Usage During Enrollment",
        Severity = "warning",
        Category = "device",
        Enabled = true,
        BaseConfidence = 55,
        ConfidenceThreshold = 40,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal = "sustained_high_memory",
                Source = "event_count",
                EventType = "performance_snapshot",
                Operator = "count_gte",
                Value = "3",
                FilterField = "memory_used_percent",
                FilterOperator = "gt",
                FilterValue = "90",
                Required = true
            }
        },
        Explanation = "test"
    };

    private static List<EnrollmentEvent> Snapshots(params int[] memoryUsedPercents)
    {
        var events = new List<EnrollmentEvent>();
        for (int i = 0; i < memoryUsedPercents.Length; i++)
        {
            events.Add(new EnrollmentEvent
            {
                EventId = Guid.NewGuid().ToString(),
                TenantId = TenantId,
                SessionId = SessionId,
                EventType = "performance_snapshot",
                Timestamp = DateTime.UtcNow.AddMinutes(i),
                Sequence = i + 1,
                Data = new Dictionary<string, object>
                {
                    ["memory_used_percent"] = memoryUsedPercents[i],
                    ["disk_free_gb"] = 100
                }
            });
        }
        return events;
    }

    private static EnrollmentEvent AppFailedEvent(string appId, string detectionResult, int sequence) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "app_install_failed",
        Timestamp = DateTime.UtcNow.AddMinutes(sequence),
        Sequence = sequence,
        Data = new Dictionary<string, object>
        {
            ["appId"] = appId,
            ["detectionResult"] = detectionResult
        }
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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineEventCountFilterTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
