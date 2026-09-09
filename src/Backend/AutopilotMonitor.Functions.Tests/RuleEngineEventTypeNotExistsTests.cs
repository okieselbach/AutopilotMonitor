using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the absence gates on conditions.
///
/// Without a <c>dataField</c>, <c>not_exists</c> matches when no event of that type occurs in
/// the session and is disproved by a single such event (ANALYZE-ID-004 v2 relies on it).
///
/// With a <c>dataField</c>, <c>not_exists</c> matches when no event of the type carries a
/// non-empty value at that field — including when the type is absent — and is disproved by a
/// single non-empty value; on <c>event_data</c> the same-event filter narrows the inspected set
/// first. Before this the per-event loop's null short-circuit rejected a missing field before
/// the operator ever ran, so "the ESP failure carried no HRESULT" (session 683f1eff) could not be
/// expressed at all, while the precondition gate already had the absence semantics.
/// </summary>
public class RuleEngineEventTypeNotExistsTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "12121212-3434-5656-7878-909090909090";

    [Fact]
    public async Task Not_exists_matches_when_the_event_type_is_absent()
    {
        var events = new List<EnrollmentEvent> { Event("marker_seen", 1) };

        var outcome = await RunAsync(TypeAbsenceRule(), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsType<Dictionary<string, object>>(result.MatchedConditions["disproof_absent"]);
        Assert.Equal("disproof", evidence["eventType"]);
        Assert.Equal(0, evidence["count"]);
    }

    [Fact]
    public async Task A_single_event_of_the_type_vetoes_a_required_not_exists()
    {
        var events = new List<EnrollmentEvent> { Event("marker_seen", 1), Event("disproof", 2) };

        var outcome = await RunAsync(TypeAbsenceRule(), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Field_not_exists_matches_when_no_event_carries_the_field()
    {
        // The event type is present, the field is not — the shape of an ESP failure without an
        // HRESULT (esp_failure_settle_started without errorCode).
        var events = new List<EnrollmentEvent> { Event("marker_seen", 1), Event("disproof", 2) };

        var outcome = await RunAsync(FieldAbsenceRule("event_type"), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsType<Dictionary<string, object>>(result.MatchedConditions["code_absent"]);
        Assert.Equal("disproof", evidence["eventType"]);
        Assert.Equal("errorCode", evidence["field"]);
        Assert.Equal(1, evidence["count"]);
    }

    [Fact]
    public async Task Field_not_exists_matches_when_the_event_type_is_absent()
    {
        var events = new List<EnrollmentEvent> { Event("marker_seen", 1) };

        var outcome = await RunAsync(FieldAbsenceRule("event_type"), events);

        var result = Assert.Single(outcome.Results);
        var evidence = Assert.IsType<Dictionary<string, object>>(result.MatchedConditions["code_absent"]);
        Assert.Equal(0, evidence["count"]);
    }

    [Theory]
    [InlineData("event_type")]
    [InlineData("event_data")]
    public async Task A_non_empty_value_on_any_event_vetoes_a_required_field_not_exists(string source)
    {
        var events = new List<EnrollmentEvent>
        {
            Event("marker_seen", 1),
            Event("disproof", 2),
            Event("disproof", 3, ("errorCode", "0x80070652")),
        };

        var outcome = await RunAsync(FieldAbsenceRule(source), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task An_empty_string_does_not_count_as_a_carried_value()
    {
        var events = new List<EnrollmentEvent> { Event("marker_seen", 1), Event("disproof", 2, ("errorCode", "")) };

        var outcome = await RunAsync(FieldAbsenceRule("event_type"), events);

        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task Event_data_field_not_exists_inspects_only_the_filtered_events()
    {
        // The carrier of the field is filtered out (kind=a); among kind=b events nobody carries it.
        var events = new List<EnrollmentEvent>
        {
            Event("marker_seen", 1),
            Event("disproof", 2, ("kind", "a"), ("errorCode", "0x80070652")),
            Event("disproof", 3, ("kind", "b")),
        };

        var matched = await RunAsync(FieldAbsenceRule("event_data", filterKind: "b"), events);
        var result = Assert.Single(matched.Results);
        var evidence = Assert.IsType<Dictionary<string, object>>(result.MatchedConditions["code_absent"]);
        Assert.Equal(1, evidence["count"]);

        var vetoed = await RunAsync(FieldAbsenceRule("event_data", filterKind: "a"), events);
        Assert.Empty(vetoed.Results);
    }

    private static AnalyzeRule TypeAbsenceRule() => Rule(
        new RuleCondition { Signal = "disproof_absent", Source = "event_type", EventType = "disproof", Operator = "not_exists", Value = "", Required = true });

    private static AnalyzeRule FieldAbsenceRule(string source, string? filterKind = null)
    {
        var absence = new RuleCondition
        {
            Signal = "code_absent",
            Source = source,
            EventType = "disproof",
            DataField = "errorCode",
            Operator = "not_exists",
            Value = "",
            Required = true,
        };
        if (filterKind != null)
        {
            absence.FilterField = "kind";
            absence.FilterOperator = "equals";
            absence.FilterValue = filterKind;
        }
        return Rule(absence);
    }

    private static AnalyzeRule Rule(RuleCondition absence) => new()
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
            absence,
        },
    };

    private static EnrollmentEvent Event(string eventType, int sequence, params (string key, string value)[] data) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = eventType,
        Timestamp = DateTime.UtcNow.AddMinutes(sequence),
        Sequence = sequence,
        Data = data.ToDictionary(d => d.key, d => (object)d.value),
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
