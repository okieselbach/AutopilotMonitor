using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the analyze rules for an app that IME did not detect after its installer finished
/// (HRESULT 0x87D1041C). The verdict is IME's own error code, never <c>detectionResult</c>:
/// that field is IME's check BEFORE the install, and every installed app starts out
/// NotDetected, so it names no detection failure (D-392).
///
/// The code reaches <c>app_install_failed</c> in two shapes:
///   - IME's report delta: the signed decimal the IME log writes, -2016345060, after the app
///     had already been reported installed (IME flips Success → Error after its check).
///   - The ESP promotion of an app still installing when the ESP failed: hex 0x87d1041c plus
///     failureType esp_apps_detection_failure.
///
/// The customer-visible expectation:
///   - ANALYZE-APP-001 names the app in any enrollment outcome.
///   - ANALYZE-APP-013 fires CRITICAL with the app when the ESP terminated on 0x87D1041C.
///   - ANALYZE-ENRL-001 references the same app + HRESULT.
///   - An app that failed for another reason (download, installer) is never a detection failure.
/// </summary>
public class RuleEngineDetectionFailureTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "080edee9-9f2e-47b9-b8f0-59fe18b8a8ad";
    private const string FailedAppId = "f9026516-dbec-47cd-ae4e-ec2312a1303c";
    private const string FailedAppName = "Microsoft Apps for Enterprise - 64Bit";
    private const string OtherAppId = "6c1f0e52-3b8d-4f4e-9a6e-2d7c5b9e1a40";
    private const string OtherAppName = "Contoso VPN Client";

    [Fact]
    public async Task ANALYZE_APP_001_fires_on_IME_report_after_the_app_was_reported_installed()
    {
        // The real IME sequence: "Execution state … Success" (app_install_completed), then IME's
        // check after the install, then Success → Error with -2016345060. The completed event
        // must not silence the finding.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-001");
        Assert.Equal("2.0.0", rule.Version);
        Assert.True(rule.Enabled);

        var events = new List<EnrollmentEvent>
        {
            AppInstallCompleted(sequence: 134),
            AppInstallFailedImeReport(errorCode: "-2016345060", sequence: 139),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        var evidence = AsDict(result.MatchedConditions["detection_failure"]);
        Assert.Equal(FailedAppName, AsString(evidence["appName"]));
        Assert.Equal("-2016345060", AsString(evidence["errorCode"]));
    }

    [Fact]
    public async Task ANALYZE_APP_001_fires_on_the_ESP_promotion_shape()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-001");

        var outcome = await RunAsync(rule, new List<EnrollmentEvent> { AppInstallFailedEspPromotion() });

        var result = Assert.Single(outcome.Results);
        var evidence = AsDict(result.MatchedConditions["detection_failure"]);
        Assert.Equal("0x87d1041c", AsString(evidence["errorCode"]));
    }

    [Fact]
    public async Task ANALYZE_APP_001_does_not_fire_on_an_install_failure_with_another_code()
    {
        // A download error carries detectionResult NotDetected like every app that was not
        // installed yet; the old rule read that as "not detected after the install".
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-001");

        var outcome = await RunAsync(rule, new List<EnrollmentEvent>
        {
            AppInstallFailedImeReport(errorCode: "-2145091577", sequence: 120),
        });

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_APP_013_names_the_app_IME_reported_with_0x87D1041C()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-013");
        Assert.Equal("1.4.0", rule.Version);
        Assert.True(rule.Enabled);
        Assert.Equal("critical", rule.Severity);

        var events = new List<EnrollmentEvent>
        {
            AppInstallFailedImeReport(errorCode: "-2145091577", sequence: 120, appId: OtherAppId, appName: OtherAppName),
            AppInstallFailedImeReport(errorCode: "-2016345060", sequence: 139),
            EspFailureSettleStarted(errorCode: "0x87d1041c", failedSubcategory: "Apps"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-APP-013", result.RuleId);
        Assert.Equal("critical", result.Severity);

        // Backend stores the raw `{{...}}` template — the web interpolator resolves it at render
        // time. Pin the material it needs: the app from the detection-failure match (not the app
        // that failed its download) and the ESP code from the optional byField condition.
        var detectionEvidence = AsDict(result.MatchedConditions["app_with_detection_failure"]);
        Assert.Equal(FailedAppName, AsString(detectionEvidence["appName"]));
        Assert.Equal("-2016345060", AsString(detectionEvidence["value"]));

        var errorCodeEvidence = AsDict(result.MatchedConditions["esp_failure_errorcode"]);
        Assert.Equal("errorCode", AsString(errorCodeEvidence["field"]));
        Assert.Equal("0x87d1041c", AsString(errorCodeEvidence["value"]));
    }

    [Fact]
    public async Task ANALYZE_APP_013_names_the_app_the_ESP_promotion_tagged()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-013");

        var outcome = await RunAsync(rule, new List<EnrollmentEvent>
        {
            AppInstallFailedEspPromotion(),
            EspFailureSettleStarted(errorCode: "0x87d1041c", failedSubcategory: "Apps"),
        });

        var result = Assert.Single(outcome.Results);
        var detectionEvidence = AsDict(result.MatchedConditions["app_with_detection_failure"]);
        Assert.Equal(FailedAppName, AsString(detectionEvidence["appName"]));
    }

    [Fact]
    public async Task ANALYZE_APP_013_does_not_name_an_app_that_failed_for_another_reason()
    {
        // The ESP failed on 0x87D1041C, but the only failed app the agent saw had a download
        // error. Its detectionResult NotDetected is the check before the install, so the rule
        // must not blame it.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-013");

        var outcome = await RunAsync(rule, new List<EnrollmentEvent>
        {
            AppInstallFailedImeReport(errorCode: "-2145091577", sequence: 120, appId: OtherAppId, appName: OtherAppName),
            EspFailureSettleStarted(errorCode: "0x87d1041c", failedSubcategory: "Apps"),
        });

        Assert.Empty(outcome.Results);
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
            AppInstallFailedImeReport(errorCode: "-2016345060", sequence: 139),
            EspFailureSettleStarted(errorCode: "0x8007064a", failedSubcategory: "SecurityPolicies"),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_APP_013_does_not_fire_without_esp_failure_settle_started()
    {
        // Defense: a detection failure without an ESP terminal failure (a non-blocking app)
        // is ANALYZE-APP-001's finding, not the critical ESP narrative.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-APP-013");

        var events = new List<EnrollmentEvent>
        {
            AppInstallFailedImeReport(errorCode: "-2016345060", sequence: 139),
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
        Assert.Equal("1.2.0", rule.Version);
        Assert.True(rule.Enabled);

        var events = new List<EnrollmentEvent>
        {
            EnrollmentFailedEvent(reason: "esp_terminal_failure"),
            EspFailureSettleStarted(errorCode: "0x87d1041c", failedSubcategory: "Apps"),
            AppInstallFailedEspPromotion(),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        // ENRL-001 must collect the material for `{{appName}}`, `{{errorCode}}`,
        // `{{failedSubcategory}}`, and `{{reason}}` from across three distinct event
        // types so the web interpolator can render the full picture.
        var failedApp = AsDict(result.MatchedConditions["failed_app"]);
        Assert.Equal("appName", AsString(failedApp["field"]));
        Assert.Equal(FailedAppName, AsString(failedApp["value"]));

        var subcategory = AsDict(result.MatchedConditions["esp_failure_subcategory"]);
        Assert.Equal("Apps", AsString(subcategory["value"]));

        var errorCode = AsDict(result.MatchedConditions["esp_failure_errorcode"]);
        Assert.Equal("0x87d1041c", AsString(errorCode["value"]));

        var reason = AsDict(result.MatchedConditions["enrollment_failed_reason"]);
        Assert.Equal("esp_terminal_failure", AsString(reason["value"]));

        // ENRL-001 repeats the outcome and names no cause of its own: the cause findings
        // (here ANALYZE-APP-013) carry it, so the explanation no longer tells one code's story.
        Assert.DoesNotContain("ANALYZE-APP-013", result.Explanation);
        Assert.Equal(RulePresentationRoles.Outcome, result.Presentation?.Role);
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

    [Fact]
    public void ANALYZE_APP_010_is_removed_from_builtin_catalog()
    {
        // "App Reverted to Error After Successful Install" matched completed → IME error report.
        // That flip is IME's check after the install failing (every one carries 0x87D1041C), so
        // since ANALYZE-APP-001 reads the code it would report the same event twice (D-392).
        Assert.DoesNotContain(BuiltInAnalyzeRules.GetAll(), r => r.RuleId == "ANALYZE-APP-010");
    }

    // ===== Event builders =====

    private static Dictionary<string, object> AppData(string appId, string appName) => new()
    {
        ["appId"] = appId,
        ["appName"] = appName,
        ["intent"] = "Install",
        ["targeted"] = "Device",
        ["runAs"] = "System",
        ["attemptNumber"] = "1",
        // The check before the install: what every app carries until it is installed.
        ["detectionResult"] = "NotDetected",
    };

    private static EnrollmentEvent AppInstallCompleted(long sequence)
    {
        var data = AppData(FailedAppId, FailedAppName);
        data["state"] = "Installed";
        data["isCompleted"] = "true";
        data["exitCode"] = "0";
        return AppEvent("app_install_completed", sequence, data);
    }

    /// <summary>The failed event IME-ERROR-REPORT produces once its errorCode group is captured.</summary>
    private static EnrollmentEvent AppInstallFailedImeReport(
        string errorCode, long sequence, string appId = FailedAppId, string appName = FailedAppName)
    {
        var data = AppData(appId, appName);
        data["state"] = "Error";
        data["isError"] = "true";
        data["isCompleted"] = "true";
        data["errorPatternId"] = "IME-ERROR-REPORT";
        data["errorCode"] = errorCode;
        data["errorDetail"] = "[Win32App][ReportingManager] Execution state for app with id: " + appId +
            " has been updated. Report delta: {\"EnforcementState\":{\"OldValue\":\"Success\",\"NewValue\":\"Error\"}," +
            "\"EnforcementErrorCode\":{\"OldValue\":null,\"NewValue\":" + errorCode + "}}";
        return AppEvent("app_install_failed", sequence, data);
    }

    /// <summary>The termination handler's promotion of the app still installing at the ESP failure.</summary>
    private static EnrollmentEvent AppInstallFailedEspPromotion()
    {
        var data = AppData(FailedAppId, FailedAppName);
        data["state"] = "Error";
        data["isError"] = "true";
        data["isCompleted"] = "true";
        data["errorPatternId"] = "esp_apps_detection_failure";
        data["failureType"] = "esp_apps_detection_failure";
        data["errorCode"] = "0x87d1041c";
        data["confidence"] = "presumed";
        data["terminationTrigger"] = "EspTerminalFailure";
        return AppEvent("app_install_failed", 168, data);
    }

    private static EnrollmentEvent AppEvent(string eventType, long sequence, Dictionary<string, object> data) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = eventType,
        Timestamp = DateTime.UtcNow,
        Sequence = sequence,
        Data = data,
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
