using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// ANALYZE-APP-018 — pins the firing behaviour of the reboot-return-code rule on the agent's
/// <c>exitCodeClass</c> payload (IME-EXITCODE-CLASS: the admin-defined return-code class of the
/// installer exit code, one of Success/SoftReboot/HardReboot/Retry/Failed):
///   - SoftReboot or HardReboot on an app_install_completed event fires at base confidence,
///   - HardReboot and an observed system_reboot_detected add confidence (ESP-005's twin),
///   - Success, Retry and Failed never fire, and neither does a class on app_install_failed alone.
/// The required condition uses the comma-separated <c>in</c> operator; the matched event's
/// data fields feed the {{appName}} / {{exitCode}} / {{exitCodeClass}} interpolation.
/// </summary>
public class ExitCodeClassRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private const string RuleId    = "ANALYZE-APP-018";

    private static AnalyzeRule Rule() => BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == RuleId);

    [Fact]
    public void Rule_is_a_warning_that_never_fails_the_session()
    {
        var rule = Rule();
        Assert.True(rule.Enabled);
        Assert.Equal("warning", rule.Severity);
        Assert.Equal("apps", rule.Category);
        Assert.False(rule.MarkSessionAsFailedDefault);
        Assert.Contains("whiteglove_sealed", rule.EvaluateOn ?? new List<string>());
    }

    [Fact]
    public async Task SoftReboot_on_a_completed_app_fires_at_base_confidence_without_an_observed_reboot()
    {
        var events = new List<EnrollmentEvent>
        {
            AppCompleted("Contoso VPN Client", exitCode: "3010", exitCodeClass: "SoftReboot"),
        };

        var outcome = await RunAsync(events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(RuleId, result.RuleId);
        Assert.Equal("warning", result.Severity);
        Assert.Equal(60, result.ConfidenceScore);

        // The evidence is persisted and interpolated at render time (portal, MCP
        // interpolate-rule-template.ts): {{exitCodeClass}} resolves through the condition's
        // field/value pair, {{appName}} and {{exitCode}} through the whitelisted payload keys
        // (AddDataFieldsToEvidence) — every token the rule text uses must be reachable here.
        var matched = AsDict(result.MatchedConditions["reboot_return_code"]);
        Assert.Equal("exitCodeClass", AsString(matched["field"]));
        Assert.Equal("SoftReboot", AsString(matched["value"]));
        Assert.Equal("Contoso VPN Client", AsString(matched["appName"]));
        Assert.Equal("3010", AsString(matched["exitCode"]));
    }

    [Fact]
    public async Task HardReboot_with_an_observed_reboot_reaches_full_confidence()
    {
        var events = new List<EnrollmentEvent>
        {
            AppCompleted("Contoso Security Agent", exitCode: "1641", exitCodeClass: "HardReboot", state: "Postponed"),
            RebootDetected(),
        };

        var outcome = await RunAsync(events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(100, result.ConfidenceScore);
        Assert.True(result.MatchedConditions.ContainsKey("hard_reboot_return_code"));
        Assert.True(result.MatchedConditions.ContainsKey("reboot_observed"));
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("Retry")]
    [InlineData("Failed")]
    public async Task Other_return_code_classes_do_not_fire(string exitCodeClass)
    {
        var events = new List<EnrollmentEvent>
        {
            AppCompleted("Contoso Reader", exitCode: "0", exitCodeClass: exitCodeClass),
            RebootDetected(),
        };

        var outcome = await RunAsync(events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task A_completed_app_without_the_class_does_not_fire_even_with_a_reboot_exit_code()
    {
        // Older agents emit exitCode only on failures and never the class; 3010 alone is not
        // evidence of the mapping the admin chose.
        var evt = AppCompleted("Legacy App", exitCode: "3010", exitCodeClass: null);
        var outcome = await RunAsync(new List<EnrollmentEvent> { evt, RebootDetected() });
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task A_reboot_class_on_a_failed_app_alone_does_not_fire()
    {
        // The rule reads the terminal completed/postponed event; a failure carrying the class is
        // covered by the app-failure rules and must not double-report here.
        var failed = AppCompleted("Contoso Agent", exitCode: "1641", exitCodeClass: "HardReboot");
        failed.EventType = "app_install_failed";
        failed.Data!["state"] = "Error";
        failed.Data["isError"] = "true";

        var outcome = await RunAsync(new List<EnrollmentEvent> { failed, RebootDetected() });
        Assert.Empty(outcome.Results);
    }

    // ===== Event builders — mirror ImeLogTrackerAdapter.BuildAppStatePayload =====

    private static int s_sequence = 10;

    private static EnrollmentEvent AppCompleted(string appName, string exitCode, string? exitCodeClass, string state = "Installed")
    {
        var data = new Dictionary<string, object>
        {
            ["appId"] = Guid.NewGuid().ToString(),
            ["appName"] = appName,
            ["state"] = state,
            ["intent"] = "Install",
            ["targeted"] = "Device",
            ["runAs"] = "System",
            ["progressPercent"] = "100",
            ["bytesDownloaded"] = "0",
            ["bytesTotal"] = "0",
            ["isError"] = "false",
            ["isCompleted"] = "true",
            ["exitCode"] = exitCode,
        };
        if (exitCodeClass != null) data["exitCodeClass"] = exitCodeClass;

        return new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "app_install_completed",
            Timestamp = DateTime.UtcNow,
            Sequence = s_sequence++,
            Data = data,
        };
    }

    private static EnrollmentEvent RebootDetected() => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "system_reboot_detected",
        Timestamp = DateTime.UtcNow.AddMinutes(2),
        Sequence = s_sequence++,
        Data = new Dictionary<string, object>
        {
            ["bootTime"] = DateTime.UtcNow.AddMinutes(1).ToString("o"),
        },
    };

    private static Dictionary<string, object> AsDict(object o)
    {
        if (o is Dictionary<string, object> d) return d;
        throw new InvalidOperationException($"Expected Dictionary<string,object>, got {o?.GetType().Name ?? "null"}");
    }

    private static string AsString(object o) => o?.ToString() ?? string.Empty;

    private static async Task<AnalysisOutcome> RunAsync(List<EnrollmentEvent> events)
    {
        var rule = Rule();
        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule> { rule });
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<ExitCodeClassRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
