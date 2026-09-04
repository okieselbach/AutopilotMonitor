using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the same-event value filter (FilterField/FilterOperator/FilterValue) on the
/// <c>event_data</c> and <c>event_data_array</c> condition sources (backlog q8n). Every
/// condition scans all events of its type independently, so two conditions cannot express
/// "field A AND field B on the SAME event" — the filter can. Mirrors the event_count filter
/// semantics (<see cref="RuleEngineEventCountFilterTests"/>): no filter = unchanged behaviour,
/// a missing filter field stringifies to empty.
/// </summary>
public class RuleEngineEventDataFilterTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "c3d4e5f6-a7b8-9012-cdef-123456789012";

    [Fact]
    public async Task EventData_FilterAndMainTest_MustHoldOnTheSameEvent()
    {
        // Event 1 passes the main test but fails the filter; event 2 passes the filter but
        // fails the main test. Independent evaluation would fire — the same-event filter must not.
        var events = new List<EnrollmentEvent>
        {
            Episode(sequence: 1, durationSeconds: 5000, backfilled: true),
            Episode(sequence: 2, durationSeconds: 90, backfilled: false),
        };

        var outcome = await RunAsync(MakeEventDataRule(), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task EventData_MatchesTheEventThatSatisfiesBoth_AndReportsItAsEvidence()
    {
        var events = new List<EnrollmentEvent>
        {
            Episode(sequence: 1, durationSeconds: 5000, backfilled: true),
            Episode(sequence: 2, durationSeconds: 900, backfilled: false),
        };

        var outcome = await RunAsync(MakeEventDataRule(), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["long_live_episode"]);
        Assert.Equal(2, Convert.ToInt32(evidence["sequence"]));
        Assert.Equal("900", evidence["value"]?.ToString());
    }

    [Fact]
    public async Task EventData_NoFilterConfigured_KeepsIndependentBehaviour()
    {
        var rule = MakeEventDataRule();
        rule.Conditions[0].FilterField = null!;
        rule.Conditions[0].FilterOperator = null!;
        rule.Conditions[0].FilterValue = null!;

        var events = new List<EnrollmentEvent>
        {
            Episode(sequence: 1, durationSeconds: 5000, backfilled: true),
        };

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task EventData_MissingFilterField_IsExcludedByEquals_AndKeptByNotEquals()
    {
        var evt = Episode(sequence: 1, durationSeconds: 5000, backfilled: false);
        evt.Data!.Remove("backfilled");
        var events = new List<EnrollmentEvent> { evt };

        // equals "false" on a missing field → empty string ≠ "false" → excluded.
        Assert.Empty((await RunAsync(MakeEventDataRule(), events)).Results);

        // not_equals "true" on a missing field → kept.
        var lenient = MakeEventDataRule();
        lenient.Conditions[0].FilterOperator = "not_equals";
        lenient.Conditions[0].FilterValue = "true";
        Assert.Single((await RunAsync(lenient, events)).Results);
    }

    [Fact]
    public async Task EventDataArray_FilterSelectsWhichEventsArraysAreIterated()
    {
        // The array element would match in both events; only the event passing the filter counts.
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-TST-011",
            Title = "Array with same-event filter",
            Severity = "warning",
            Category = "security",
            Enabled = true,
            IsBuiltIn = false,
            BaseConfidence = 80,
            ConfidenceThreshold = 40,
            Conditions = new List<RuleCondition>
            {
                new()
                {
                    Signal = "unexpected_artifact",
                    Source = "event_data_array",
                    EventType = "provisioning_package_scan",
                    DataField = "artifacts",
                    ItemField = "identity",
                    Operator = "regex",
                    Value = "^Evil",
                    FilterField = "source",
                    FilterOperator = "equals",
                    FilterValue = "registry",
                    Required = true
                }
            },
            Explanation = "test"
        };

        var fileScan = ScanEvent(sequence: 1, source: "file", "Evil.ppkg");
        Assert.Empty((await RunAsync(rule, new List<EnrollmentEvent> { fileScan })).Results);

        var registryScan = ScanEvent(sequence: 2, source: "registry", "Evil.ppkg");
        var outcome = await RunAsync(rule, new List<EnrollmentEvent> { fileScan, registryScan });
        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["unexpected_artifact"]);
        Assert.Equal(2, Convert.ToInt32(evidence["sequence"]));
    }

    // ===== Builders =====

    private static AnalyzeRule MakeEventDataRule() => new()
    {
        RuleId = "ANALYZE-TST-010",
        Title = "Long live sleep episode",
        Severity = "info",
        Category = "device",
        Enabled = true,
        // Synthetic rule ID outside the embedded catalog — see RuleEngineEventCountFilterTests.
        IsBuiltIn = false,
        BaseConfidence = 90,
        ConfidenceThreshold = 40,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal = "long_live_episode",
                Source = "event_data",
                EventType = "system_sleep_episode",
                DataField = "durationSeconds",
                Operator = "gte",
                Value = "300",
                FilterField = "backfilled",
                FilterOperator = "equals",
                FilterValue = "false",
                Required = true
            }
        },
        Explanation = "test"
    };

    private static EnrollmentEvent Episode(int sequence, long durationSeconds, bool backfilled) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "system_sleep_episode",
        Timestamp = DateTime.UtcNow.AddMinutes(sequence),
        Sequence = sequence,
        Data = new Dictionary<string, object>
        {
            ["kind"] = "modern_standby",
            ["durationSeconds"] = durationSeconds,
            ["backfilled"] = backfilled,
        }
    };

    private static EnrollmentEvent ScanEvent(int sequence, string source, params string[] identities) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "provisioning_package_scan",
        Timestamp = DateTime.UtcNow.AddMinutes(sequence),
        Sequence = sequence,
        Data = new Dictionary<string, object>
        {
            ["source"] = source,
            ["artifacts"] = identities
                .Select(id => (object)new Dictionary<string, object> { ["identity"] = id })
                .ToList(),
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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineEventDataFilterTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
