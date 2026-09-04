using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Timestamp provenance in the duration evaluators (phase_duration, app_install_duration).
/// IME-log-derived events carry <c>data.sourceOffsetOrigin</c>; <c>reader-zone-fallback</c>
/// means the agent assumed its own timezone for the log writer, so the Timestamp can be hours
/// off. A span with MIXED provenance — one fallback endpoint, the other anchored or "now" — is
/// not measurable: the condition is false with a provenance reason. Two fallback endpoints share
/// the writer's error and stay measurable, as do other origins and agent-native events (no tag).
/// Field case: a 20-minute DeviceSetup rendered as 3 h 22 min fired ANALYZE-ESP-001 (high).
/// </summary>
public class RuleEngineDurationProvenanceTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "77777777-8888-9999-aaaa-bbbbbbbbbbbb";
    private const string Fallback = "reader-zone-fallback";
    private const string LineAnchored = "line-anchored";

    private static readonly DateTime T0 = new(2026, 9, 1, 13, 30, 0, DateTimeKind.Utc);

    // ===== phase_duration =====

    [Theory]
    [InlineData(Fallback)]
    [InlineData("Reader-Zone-Fallback")] // origin compare is case-insensitive
    public async Task PhaseDuration_StartWithFallbackProvenance_IsNotMeasurable(string origin)
    {
        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, origin),
            PhaseEvent("AccountSetup", T0.AddHours(3), 20, LineAnchored),
        };

        var (dry, _, _) = await DryRunAsync(PhaseRule("DeviceSetup"), events);

        Assert.Equal(RuleDryRunVerdict.RequiredConditionNotMet, dry.Verdict);
        var condition = Assert.Single(dry.Conditions);
        Assert.False(condition.Matched);
        var evidence = AsDict(condition.Evidence!);
        Assert.Contains("not measurable", AsString(evidence["reason"]));
        Assert.Contains("start timestamp derived from reader-zone fallback", AsString(evidence["reason"]));
        Assert.Equal(Fallback, AsString(evidence["timestampProvenance"]));
        Assert.Equal("start", AsString(evidence["provenanceEndpoint"]));
        Assert.Equal(events[0].EventId, AsString(evidence["eventId"]));
        Assert.Equal(10L, Convert.ToInt64(evidence["sequence"]));
    }

    [Fact]
    public async Task PhaseDuration_EndWithFallbackProvenance_IsNotMeasurable()
    {
        // The end of a phase is the NEXT esp_phase_changed of any phase — its provenance counts too.
        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, LineAnchored),
            PhaseEvent("AccountSetup", T0.AddHours(3), 20, Fallback),
        };

        var (dry, _, _) = await DryRunAsync(PhaseRule("DeviceSetup"), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.False(condition.Matched);
        var evidence = AsDict(condition.Evidence!);
        Assert.Contains("end timestamp derived from reader-zone fallback", AsString(evidence["reason"]));
        Assert.Equal(Fallback, AsString(evidence["timestampProvenance"]));
        Assert.Equal("end", AsString(evidence["provenanceEndpoint"]));
        Assert.Equal(events[1].EventId, AsString(evidence["provenanceEventId"]));
        Assert.Equal(events[0].EventId, AsString(evidence["eventId"])); // the phase event stays the anchor
    }

    [Theory]
    [InlineData(LineAnchored)]
    [InlineData("bias")]
    [InlineData("calibrated")]
    [InlineData("era-anchored")] // backlog line resolved through its anchored writer era (agent 2026-09-04)
    public async Task PhaseDuration_TrustedProvenanceOnBothEnds_IsComputed(string origin)
    {
        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, origin),
            PhaseEvent("AccountSetup", T0.AddHours(3), 20, origin),
        };

        var (dry, _, _) = await DryRunAsync(PhaseRule("DeviceSetup"), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.True(condition.Matched);
        var evidence = AsDict(condition.Evidence!);
        Assert.Equal(3 * 3600d, Convert.ToDouble(evidence["durationSeconds"]));
        Assert.Equal(events[1].EventId, AsString(evidence["phaseEndEventId"]));
        Assert.False(evidence.ContainsKey("timestampProvenance"));
    }

    [Fact]
    public async Task PhaseDuration_NoProvenanceAtAll_IsComputed()
    {
        // Agent-native events carry no sourceOffsetOrigin — nothing to distrust.
        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, origin: null),
            PhaseEvent("AccountSetup", T0.AddMinutes(20), 20, origin: null),
        };

        var (dry, _, _) = await DryRunAsync(PhaseRule("DeviceSetup"), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.True(condition.Matched);
        Assert.Equal(20 * 60d, Convert.ToDouble(AsDict(condition.Evidence!)["durationSeconds"]));
    }

    [Fact]
    public async Task PhaseDuration_OpenPhase_OnlyTheStartProvenanceMatters()
    {
        // No next phase event: the end is "now" and has no provenance. A trusted start measures,
        // a fallback start does not.
        var trusted = new List<EnrollmentEvent> { PhaseEvent("DeviceSetup", DateTime.UtcNow.AddMinutes(-5), 10, LineAnchored) };
        var (dryTrusted, _, _) = await DryRunAsync(PhaseRule("DeviceSetup"), trusted);
        var trustedCondition = Assert.Single(dryTrusted.Conditions);
        Assert.True(trustedCondition.Matched);
        Assert.Equal("(still active)", AsString(AsDict(trustedCondition.Evidence!)["phaseEndEventId"]));

        var fallback = new List<EnrollmentEvent> { PhaseEvent("DeviceSetup", DateTime.UtcNow.AddMinutes(-5), 10, Fallback) };
        var (dryFallback, _, _) = await DryRunAsync(PhaseRule("DeviceSetup"), fallback);
        var fallbackCondition = Assert.Single(dryFallback.Conditions);
        Assert.False(fallbackCondition.Matched);
        Assert.Equal("start", AsString(AsDict(fallbackCondition.Evidence!)["provenanceEndpoint"]));
    }

    [Fact]
    public async Task PhaseDuration_BothEndsFallback_IsComputed()
    {
        // Two fallback endpoints carry the same writer-zone error, so the span is right — and
        // this is the shape of every session from an agent without an era anchor, because the
        // DeviceSetup line is written before the agent starts.
        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, Fallback),
            PhaseEvent("AccountSetup", T0.AddHours(3), 20, Fallback),
        };

        var (dry, _, _) = await DryRunAsync(PhaseRule("DeviceSetup"), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.True(condition.Matched);
        Assert.Equal(10800d, Convert.ToDouble(AsDict(condition.Evidence!)["durationSeconds"]));
    }

    // ===== app_install_duration =====

    [Fact]
    public async Task AppInstallDuration_BothEndsFallback_IsComputed()
    {
        var events = new List<EnrollmentEvent>
        {
            AppEvent("app_install_started", "app-a", T0, 10, Fallback),
            AppEvent("app_install_completed", "app-a", T0.AddHours(1), 20, Fallback),
        };

        var (dry, _, _) = await DryRunAsync(AppDurationRule(minSeconds: 600), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.True(condition.Matched);
    }

    [Fact]
    public async Task AppInstallDuration_StartWithFallbackProvenance_IsNotMeasurable()
    {
        var events = new List<EnrollmentEvent>
        {
            AppEvent("app_install_started", "app-a", T0, 10, Fallback),
            AppEvent("app_install_completed", "app-a", T0.AddHours(1), 20, LineAnchored),
        };

        var (dry, _, _) = await DryRunAsync(AppDurationRule(minSeconds: 600), events);

        Assert.Equal(RuleDryRunVerdict.RequiredConditionNotMet, dry.Verdict);
        var condition = Assert.Single(dry.Conditions);
        Assert.False(condition.Matched);
        var evidence = AsDict(condition.Evidence!);
        Assert.Contains("app install duration not measurable: start timestamp derived from reader-zone fallback", AsString(evidence["reason"]));
        Assert.Equal(Fallback, AsString(evidence["timestampProvenance"]));
        Assert.Equal("start", AsString(evidence["provenanceEndpoint"]));
        Assert.Equal(events[0].EventId, AsString(evidence["provenanceEventId"]));
        Assert.Equal(events[1].EventId, AsString(evidence["eventId"])); // completion stays the anchor
        Assert.Equal(20L, Convert.ToInt64(evidence["sequence"]));
    }

    [Fact]
    public async Task AppInstallDuration_EndWithFallbackProvenance_IsNotMeasurable()
    {
        var events = new List<EnrollmentEvent>
        {
            AppEvent("app_install_started", "app-a", T0, 10, LineAnchored),
            AppEvent("app_install_completed", "app-a", T0.AddHours(1), 20, Fallback),
        };

        var (dry, _, _) = await DryRunAsync(AppDurationRule(minSeconds: 600), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.False(condition.Matched);
        var evidence = AsDict(condition.Evidence!);
        Assert.Equal("end", AsString(evidence["provenanceEndpoint"]));
        Assert.Equal(Fallback, AsString(evidence["timestampProvenance"]));
    }

    [Fact]
    public async Task AppInstallDuration_TrustedProvenance_IsComputed()
    {
        var events = new List<EnrollmentEvent>
        {
            AppEvent("app_install_started", "app-a", T0, 10, LineAnchored),
            AppEvent("app_install_completed", "app-a", T0.AddHours(1), 20, LineAnchored),
        };

        var (dry, _, _) = await DryRunAsync(AppDurationRule(minSeconds: 600), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.True(condition.Matched);
        Assert.Equal(3600d, Convert.ToDouble(AsDict(condition.Evidence!)["durationSeconds"]));
    }

    [Fact]
    public async Task AppInstallDuration_NoProvenance_IsComputed()
    {
        var events = new List<EnrollmentEvent>
        {
            AppEvent("app_install_started", "app-a", T0, 10, origin: null),
            AppEvent("app_install_completed", "app-a", T0.AddHours(1), 20, origin: null),
        };

        var (dry, _, _) = await DryRunAsync(AppDurationRule(minSeconds: 600), events);

        Assert.True(Assert.Single(dry.Conditions).Matched);
    }

    [Fact]
    public async Task AppInstallDuration_UnmeasurablePairDoesNotHideAMeasurableOne()
    {
        // The fallback pair is skipped, not fatal: another app with trusted endpoints still matches.
        var events = new List<EnrollmentEvent>
        {
            AppEvent("app_install_started", "app-a", T0, 10, Fallback),
            AppEvent("app_install_completed", "app-a", T0.AddHours(1), 20, LineAnchored),
            AppEvent("app_install_started", "app-b", T0.AddHours(1), 30, LineAnchored),
            AppEvent("app_install_completed", "app-b", T0.AddHours(2), 40, LineAnchored),
        };

        var (dry, _, _) = await DryRunAsync(AppDurationRule(minSeconds: 600), events);

        var condition = Assert.Single(dry.Conditions);
        Assert.True(condition.Matched);
        Assert.Equal("app-b", AsString(AsDict(condition.Evidence!)["appId"]));
    }

    // ===== Rule level: ANALYZE-ESP-001 from rules/dist =====

    [Fact]
    public async Task ANALYZE_ESP_001_does_not_fire_on_a_reader_zone_fallback_DeviceSetup()
    {
        // The field case: DeviceSetup start read under the wrong writer zone sits 3 h before the
        // next (correctly anchored) phase event although the real phase lasted ~20 minutes.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-001");
        Assert.True(rule.Enabled);

        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, Fallback),
            PhaseEvent("AccountSetup", T0.AddHours(3), 20, LineAnchored),
        };

        var outcome = await RunBuiltInAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_ESP_001_still_fires_when_both_endpoints_are_fallback()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-001");

        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, Fallback),
            PhaseEvent("AccountSetup", T0.AddHours(3), 20, Fallback),
        };

        var outcome = await RunBuiltInAsync(rule, events);
        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-ESP-001", result.RuleId);
    }

    [Fact]
    public async Task ANALYZE_ESP_001_fires_on_the_same_span_when_line_anchored()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-001");

        var events = new List<EnrollmentEvent>
        {
            PhaseEvent("DeviceSetup", T0, 10, LineAnchored),
            PhaseEvent("AccountSetup", T0.AddHours(3), 20, LineAnchored),
        };

        var outcome = await RunBuiltInAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-ESP-001", result.RuleId);
        Assert.True(result.ConfidenceScore >= rule.ConfidenceThreshold);
        var matched = AsDict(result.MatchedConditions["esp_stalled"]);
        Assert.Equal(3 * 3600d, Convert.ToDouble(matched["durationSeconds"]));
    }

    // ===== Event builders — mirror the ImeLogTrackerAdapter emit shape =====

    private static EnrollmentEvent PhaseEvent(string phase, DateTime timestamp, long sequence, string? origin)
        => ImeEvent("esp_phase_changed", timestamp, sequence, origin, new Dictionary<string, object> { ["espPhase"] = phase });

    private static EnrollmentEvent AppEvent(string eventType, string appId, DateTime timestamp, long sequence, string? origin)
        => ImeEvent(eventType, timestamp, sequence, origin, new Dictionary<string, object> { ["appId"] = appId, ["appName"] = appId });

    private static EnrollmentEvent ImeEvent(string eventType, DateTime timestamp, long sequence, string? origin, Dictionary<string, object> data)
    {
        if (origin != null)
        {
            data["sourceLocalTs"] = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
            data["sourceOffsetOrigin"] = origin;
            data["sourceOffsetMinutes"] = "-240";
        }
        return new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = eventType,
            Timestamp = timestamp,
            Sequence = sequence,
            Source = origin != null ? "ImeLogTracker" : "Agent",
            Data = data,
        };
    }

    // ===== Rules =====

    private static AnalyzeRule PhaseRule(string phase) => new()
    {
        RuleId = "ANALYZE-TEST-PHASE",
        Title = "Phase duration draft",
        Severity = "warning",
        Category = "esp",
        IsBuiltIn = false,
        BaseConfidence = 50,
        ConfidenceThreshold = 40,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal = "esp_stalled", Source = "phase_duration", EventType = "esp_phase_changed",
                DataField = "espPhase", Operator = "equals", Value = phase, Required = true,
            },
        },
        Explanation = "Phase {{phase}} lasted {{durationFormatted}}.",
    };

    private static AnalyzeRule AppDurationRule(int minSeconds) => new()
    {
        RuleId = "ANALYZE-TEST-APPDUR",
        Title = "App install duration draft",
        Severity = "warning",
        Category = "apps",
        IsBuiltIn = false,
        BaseConfidence = 50,
        ConfidenceThreshold = 40,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal = "slow_install", Source = "app_install_duration",
                Operator = "gt", Value = minSeconds.ToString(), Required = true,
            },
        },
        Explanation = "App {{appName}} took {{durationFormatted}}.",
    };

    // ===== Harness =====

    private static Dictionary<string, object> AsDict(object o)
    {
        if (o is Dictionary<string, object> d) return d;
        throw new InvalidOperationException($"Expected Dictionary<string,object>, got {o?.GetType().Name ?? "null"}");
    }

    private static string AsString(object o) => o?.ToString() ?? string.Empty;

    private static async Task<(RuleDryRun dry, Mock<ISessionRepository> sessionRepo, Mock<IRuleRepository> ruleRepo)> DryRunAsync(
        AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);
        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineDurationProvenanceTests>.Instance);
        var dry = await engine.DryRunRuleAsync(TenantId, SessionId, rule);
        return (dry, sessionRepo, ruleRepo);
    }

    private static async Task<AnalysisOutcome> RunBuiltInAsync(AnalyzeRule rule, List<EnrollmentEvent> events)
    {
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineDurationProvenanceTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
