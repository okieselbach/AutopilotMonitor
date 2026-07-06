using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Session 080edee9 follow-up — pins the analyze-rule shape for the canonical detection-
/// failure scenario (Microsoft Apps for Enterprise + HRESULT 0x87D1041C).
///
/// The customer-visible expectation:
///   - ANALYZE-APP-013 fires CRITICAL with the failed app name + HRESULT.
///   - ANALYZE-ENRL-001 fires CRITICAL referencing the same app + HRESULT.
///   - ANALYZE-CORR-001 ("Slow Network") does NOT fire — there was no slow network.
///   - ANALYZE-APP-009 ("App Download Stalled") does NOT fire — the synthetic
///     `download_progress: failed` emitted by the V2 termination handler is a
///     post-mortem signal, not a real download stall.
///
/// Both APP-013 and ENRL-001 must work for HISTORICAL events emitted by agents
/// shipped BEFORE the AppFailureTypes classifier landed (the legacy shape:
/// `detectionResult: NotDetected` + co-located `esp_failure_settle_started`
/// errorCode=0x87d1041c, with the old `failureType: esp_apps_timeout` tag).
/// </summary>
public class RuleEngineDetectionFailureTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "080edee9-9f2e-47b9-b8f0-59fe18b8a8ad";
    private const string FailedAppId = "f9026516-dbec-47cd-ae4e-ec2312a1303c";
    private const string FailedAppName = "Microsoft Apps for Enterprise - 64Bit";

    [Fact]
    public async Task ANALYZE_APP_013_fires_on_legacy_detectionResult_plus_errorCode_shape()
    {
        // Legacy agent shape (pre-classifier): app_install_failed carries
        // failureType=esp_apps_timeout (the old default) but ALSO detectionResult=NotDetected,
        // and there's a co-located esp_failure_settle_started with errorCode=0x87d1041c.
        // The rule must fire and the explanation must interpolate to the Office app.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-013");
        Assert.Equal("1.1.0", rule.Version);
        Assert.True(rule.Enabled);
        Assert.Equal("critical", rule.Severity);

        var events = new List<EnrollmentEvent>
        {
            AppInstallFailedLegacyShape(detectionResult: "NotDetected"),
            EspFailureSettleStarted(errorCode: "0x87d1041c", failedSubcategory: "Apps"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-APP-013", result.RuleId);
        Assert.Equal("critical", result.Severity);

        // Backend stores the raw `{{...}}` template — the web interpolator
        // (`interpolateRuleTemplate.ts`) resolves them at render time. So we pin the
        // MATERIAL the interpolator needs: the matched-condition evidence dicts must
        // carry appName (via auto-field injection on the required condition's match)
        // AND errorCode (via the optional byField condition).
        Assert.True(result.MatchedConditions.ContainsKey("app_with_detection_failure"));
        var detectionEvidence = AsDict(result.MatchedConditions["app_with_detection_failure"]);
        Assert.Equal(FailedAppName, AsString(detectionEvidence["appName"]));
        Assert.Equal("NotDetected", AsString(detectionEvidence["value"]));

        Assert.True(result.MatchedConditions.ContainsKey("esp_failure_errorcode"));
        var errorCodeEvidence = AsDict(result.MatchedConditions["esp_failure_errorcode"]);
        Assert.Equal("errorCode", AsString(errorCodeEvidence["field"]));
        Assert.Equal("0x87d1041c", AsString(errorCodeEvidence["value"]));
    }

    [Fact]
    public async Task ANALYZE_APP_013_does_not_fire_when_errorCode_is_not_0x87d1041c()
    {
        // Defense: even with a detection-failure event, if the ESP HRESULT is
        // something else (e.g. a SecurityPolicies failure with 0x8007064a), the
        // detection-rule story doesn't apply — the precondition must gate that out.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-013");

        var events = new List<EnrollmentEvent>
        {
            AppInstallFailedLegacyShape(detectionResult: "NotDetected"),
            EspFailureSettleStarted(errorCode: "0x8007064a", failedSubcategory: "SecurityPolicies"),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_APP_013_does_not_fire_without_esp_failure_settle_started()
    {
        // Defense: a Win32 app with detectionResult=NotDetected but NO ESP terminal
        // failure (e.g. a retry that later succeeded, or a non-blocking app) must not
        // pull in the critical detection-failure-during-ESP narrative — that rule is
        // specifically about ESP-terminating detection failures.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-013");

        var events = new List<EnrollmentEvent>
        {
            AppInstallFailedLegacyShape(detectionResult: "NotDetected"),
            // No esp_failure_settle_started event — precondition fails.
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_ENRL_001_explanation_references_failed_app_and_errorCode()
    {
        // ENRL-001 carries the high-level "Enrollment Failed" verdict. The user's complaint
        // was that this card never mentioned which app caused the failure. The rule must
        // pull appName from app_install_failed (optional condition with dataField=appName)
        // and resolve {{appName}} via the byField path.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-ENRL-001");
        Assert.Equal("1.1.2", rule.Version);
        Assert.True(rule.Enabled);

        var events = new List<EnrollmentEvent>
        {
            EnrollmentFailedEvent(reason: "esp_terminal_failure"),
            EspFailureSettleStarted(errorCode: "0x87d1041c", failedSubcategory: "Apps"),
            AppInstallFailedLegacyShape(detectionResult: "NotDetected"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        // ENRL-001 must collect the material for `{{appName}}`, `{{errorCode}}`,
        // `{{failedSubcategory}}`, and `{{reason}}` from across three distinct event
        // types so the web interpolator can render the full picture. Cross-link to
        // ANALYZE-APP-013 is hard-coded into the explanation template.
        var failedApp = AsDict(result.MatchedConditions["failed_app"]);
        Assert.Equal("appName", AsString(failedApp["field"]));
        Assert.Equal(FailedAppName, AsString(failedApp["value"]));

        var subcategory = AsDict(result.MatchedConditions["esp_failure_subcategory"]);
        Assert.Equal("Apps", AsString(subcategory["value"]));

        var errorCode = AsDict(result.MatchedConditions["esp_failure_errorcode"]);
        Assert.Equal("0x87d1041c", AsString(errorCode["value"]));

        var reason = AsDict(result.MatchedConditions["enrollment_failed_reason"]);
        Assert.Equal("esp_terminal_failure", AsString(reason["value"]));

        // Cross-link to the more actionable rule is embedded in the explanation
        // template (no interpolation needed):
        Assert.Contains("ANALYZE-APP-013", result.Explanation);
    }

    [Fact]
    public void ANALYZE_CORR_001_is_removed_from_builtin_catalog()
    {
        // "Slow Network Causing App Installation Failure" was sunset because its
        // conditions never verified a slow download — it fired on the simple combo
        // of any app-failure + any download event + sufficient disk. The first attempt
        // to ship it as enabled:false was a half-measure that left orphan per-tenant
        // RuleState{Enabled=true} overrides in place. Sunset = removed from source +
        // EnsureBuiltInRulesSeededAsync GCs the orphan state on the next backend boot.
        Assert.DoesNotContain(BuiltInAnalyzeRules.GetAll(), r => r.RuleId == "ANALYZE-CORR-001");
    }

    [Fact]
    public void ANALYZE_APP_009_is_removed_from_builtin_catalog()
    {
        // "App Download Stalled" was sunset for the same reason CORR-001 was: the rule
        // fired on the V2 termination handler's synthetic `download_progress: failed`
        // (emitted AFTER the download already completed), and the engine's
        // suppressByEvent can't filter by a third event's `status` field. Re-introduce
        // only with a real stall-detection mechanism (e.g. a dedicated `download_stalled`
        // event type that isn't post-hoc-synthesized).
        Assert.DoesNotContain(BuiltInAnalyzeRules.GetAll(), r => r.RuleId == "ANALYZE-APP-009");
    }

    // ===== Event builders (faithful to session 080edee9's actual payloads) =====

    /// <summary>
    /// Legacy app_install_failed shape — the one currently sitting in Table Storage
    /// for session 080edee9. Note: failureType is the OLD `esp_apps_timeout` tag (the
    /// new classifier hadn't shipped yet), errorCode is ABSENT, but detectionResult
    /// is populated by ImeLogTracker from the IME log.
    /// </summary>
    private static EnrollmentEvent AppInstallFailedLegacyShape(string detectionResult) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "app_install_failed",
        Timestamp = DateTime.UtcNow,
        Sequence = 168,
        Data = new Dictionary<string, object>
        {
            ["appId"] = FailedAppId,
            ["appName"] = FailedAppName,
            ["state"] = "Error",
            ["intent"] = "Install",
            ["targeted"] = "Device",
            ["runAs"] = "System",
            ["progressPercent"] = "0",
            ["bytesDownloaded"] = "10580224",
            ["bytesTotal"] = "10580224",
            ["isError"] = "true",
            ["isCompleted"] = "true",
            ["attemptNumber"] = "1",
            ["detectionResult"] = detectionResult,
            ["errorPatternId"] = "esp_apps_timeout",
            ["failureType"] = "esp_apps_timeout",
            ["confidence"] = "presumed",
            ["errorDetail"] = "Install status unconfirmed — ESP timed out (180 min) while still installing.",
            ["terminationTrigger"] = "EspTerminalFailure",
        }
    };

    private static EnrollmentEvent EspFailureSettleStarted(string errorCode, string failedSubcategory) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "esp_failure_settle_started",
        Timestamp = DateTime.UtcNow,
        Sequence = 161,
        Data = new Dictionary<string, object>
        {
            ["category"] = "DeviceSetup",
            ["failedSubcategory"] = failedSubcategory,
            ["failureType"] = $"Provisioning_DeviceSetup_{failedSubcategory}_Failed",
            ["settleSeconds"] = "30",
            ["reason"] = "wait_for_late_ime_signals",
            ["errorCode"] = errorCode,
        }
    };

    private static EnrollmentEvent EnrollmentFailedEvent(string reason) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "enrollment_failed",
        Timestamp = DateTime.UtcNow,
        Sequence = 163,
        Data = new Dictionary<string, object>
        {
            ["reason"] = reason,
            ["decisionSource"] = "DecisionEngine",
            ["trigger"] = "EspTerminalFailure",
            ["sessionStage"] = "Failed",
        }
    };

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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineDetectionFailureTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
