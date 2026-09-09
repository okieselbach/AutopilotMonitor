using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins ANALYZE-ESP-004 ("ESP Gave Up on a Blocking App with 'Continue Anyway' — Soft Failure").
///
/// Background (tenant c9787ba2, session cb7036a6): a slow blocking app (Encompass
/// Hybrid Installer) does not finish inside the 30-min Device-ESP window, so the ESP
/// fails terminally in DeviceSetup. The profile allows "Continue anyway", so the user
/// most likely dismissed the failure screen and reached the desktop — but the agent
/// only ever sees the terminal failure and the session is recorded as Failed.
///
/// The rule needs THREE facts: the terminal enrollment_failed, the DecisionEngine's
/// <c>mayHaveContinuedAnyway=true</c> stamp (profile allows the button), and the agent's own
/// timeout verdict — an app_install_failed promoted with <c>failureType=esp_apps_timeout</c>
/// (ESP gave up while the app was still installing, no per-app HRESULT). Session 683f1eff:
/// an ESP failure with a real HRESULT (0x80070652, installer collision) on a Continue-anyway
/// profile fired the rule and rendered the configured 90-min limit as if it had elapsed —
/// the continue-anyway flag alone proves nothing about timing.
/// </summary>
public class RuleEngineEspSoftFailureTests
{
    private const string TenantId  = "c9787ba2-29de-4944-91f0-73594c12f85d";
    private const string SessionId = "cb7036a6-2c7c-470f-851b-24e5e537991c";
    private const string FailedAppName = "Encompass Hybrid Installer";

    [Fact]
    public async Task ANALYZE_ESP_004_fires_on_continue_anyway_soft_failure()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");
        Assert.True(rule.Enabled, "ANALYZE-ESP-004 should be enabled by default");
        Assert.Equal("warning", rule.Severity);
        Assert.Empty(rule.TemplateVariables ?? new List<TemplateVariable>());

        var events = new List<EnrollmentEvent>
        {
            SettleStartedEvent(errorCode: null),
            EnrollmentFailedEvent(mayHaveContinuedAnyway: "true"),
            AppInstallFailed(failureType: "esp_apps_timeout"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-ESP-004", result.RuleId);
        Assert.Equal("warning", result.Severity);

        // Required gates: the continue-anyway flag AND the agent's timeout verdict.
        var gate = AsDict(result.MatchedConditions["may_have_continued"]);
        Assert.Equal("true", AsString(gate["value"]));
        var verdict = AsDict(result.MatchedConditions["esp_gave_up_on_app"]);
        Assert.Equal("esp_apps_timeout", AsString(verdict["value"]));

        // Interpolation material: {{espSyncFailureTimeoutMinutes}} (enrollment_failed),
        // {{failedSubcategory}} (esp_failure_settle_started — the terminal event drops it when
        // the Shell-Core failure wins the race) and {{appName}} (the promoted app).
        var timeout = AsDict(result.MatchedConditions["espSyncFailureTimeoutMinutes"]);
        Assert.Equal("30", AsString(timeout["value"]));

        var subcategory = AsDict(result.MatchedConditions["failedSubcategory"]);
        Assert.Equal("Certificates", AsString(subcategory["value"]));

        var failedApp = AsDict(result.MatchedConditions["appName"]);
        Assert.Equal("appName", AsString(failedApp["field"]));
        Assert.Equal(FailedAppName, AsString(failedApp["value"]));
    }

    [Fact]
    public async Task ANALYZE_ESP_004_stays_silent_on_a_hard_failure_with_continue_anyway()
    {
        // Session 683f1eff: continue-anyway profile, but the ESP failed on a real installer
        // error (0x80070652 / MSI 1618) after 19 minutes. The failed app carries no
        // esp_apps_timeout verdict — the rule must not turn an installer collision into
        // "the ESP reached its 90-min timeout".
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");

        var events = new List<EnrollmentEvent>
        {
            SettleStartedEvent(errorCode: "0x80070652"),
            EnrollmentFailedEvent(mayHaveContinuedAnyway: "true"),
            AppInstallFailed(failureType: null, exitCodeClass: "Retry"),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_ESP_004_names_the_timed_out_app_not_another_failed_one()
    {
        // Two failed apps: one hard installer failure earlier in the run, one promoted
        // esp_apps_timeout at the terminal. {{appName}} must be the timed-out app.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");

        var events = new List<EnrollmentEvent>
        {
            AppInstallFailed(failureType: null, exitCodeClass: "Failed", appName: "Contoso Agent", sequence: 100),
            SettleStartedEvent(errorCode: null),
            EnrollmentFailedEvent(mayHaveContinuedAnyway: "true"),
            AppInstallFailed(failureType: "esp_apps_timeout"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        var failedApp = AsDict(result.MatchedConditions["appName"]);
        Assert.Equal(FailedAppName, AsString(failedApp["value"]));
    }

    [Fact]
    public async Task ANALYZE_ESP_004_does_not_fire_when_continue_anyway_is_false()
    {
        // Hard failure (profile blocks "Continue anyway"): the device is genuinely stuck,
        // so the soft-failure advisory must stay silent — ANALYZE-ENRL-001 owns that case.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");

        var events = new List<EnrollmentEvent>
        {
            EnrollmentFailedEvent(mayHaveContinuedAnyway: "false"),
            AppInstallFailed(failureType: "esp_apps_timeout"),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_ESP_004_does_not_fire_when_flag_absent()
    {
        // Older agents (or non-ESP failure paths) emit enrollment_failed without the
        // mayHaveContinuedAnyway flag — the required gate must fail closed.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ESP-004");

        var events = new List<EnrollmentEvent>
        {
            EnrollmentFailedEvent(mayHaveContinuedAnyway: null),
            AppInstallFailed(failureType: "esp_apps_timeout"),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    // ===== Event builders (faithful to session cb7036a6's actual payloads) =====

    private static EnrollmentEvent EnrollmentFailedEvent(string? mayHaveContinuedAnyway) =>
        new()
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "enrollment_failed",
            Timestamp = DateTime.UtcNow,
            Sequence = 224,
            Data = BuildFailureData(mayHaveContinuedAnyway),
        };

    private static Dictionary<string, object> BuildFailureData(string? mayHaveContinuedAnyway)
    {
        var data = new Dictionary<string, object>
        {
            ["reason"] = "esp_terminal_failure",
            ["decisionSource"] = "DecisionEngine",
            ["trigger"] = "EspTerminalFailure",
            ["sessionStage"] = "Failed",
            ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
            ["failedSubcategory"] = "Certificates",
            ["category"] = "DeviceSetup",
            ["espSyncFailureTimeoutMinutes"] = "30",
            ["espAllowContinueAnyway"] = "true",
        };
        if (mayHaveContinuedAnyway != null)
            data["mayHaveContinuedAnyway"] = mayHaveContinuedAnyway;
        return data;
    }

    private static EnrollmentEvent SettleStartedEvent(string? errorCode)
    {
        var data = new Dictionary<string, object>
        {
            ["category"] = "DeviceSetup",
            ["failedSubcategory"] = "Certificates",
            ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
            ["settleSeconds"] = 30,
            ["reason"] = "wait_for_late_ime_signals",
        };
        if (errorCode != null)
            data["errorCode"] = errorCode;
        return new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "esp_failure_settle_started",
            Timestamp = DateTime.UtcNow,
            Sequence = 212,
            Data = data,
        };
    }

    private static EnrollmentEvent AppInstallFailed(
        string? failureType,
        string? exitCodeClass = null,
        string appName = FailedAppName,
        int sequence = 220)
    {
        var data = new Dictionary<string, object>
        {
            ["appName"] = appName,
            ["state"] = "Error",
            ["targeted"] = "Device",
        };
        if (failureType != null)
        {
            data["failureType"] = failureType;
            data["confidence"] = "presumed";
            data["errorDetail"] = "Install status unconfirmed — ESP gave up while this app was still installing.";
        }
        if (exitCodeClass != null)
            data["exitCodeClass"] = exitCodeClass;
        return new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "app_install_failed",
            Timestamp = DateTime.UtcNow,
            Sequence = sequence,
            Data = data,
        };
    }

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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineEspSoftFailureTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
