using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the precondition gate semantics on <see cref="RuleEngine.EvaluateRule"/>:
/// preconditions evaluate BEFORE conditions, are AND-combined, and a failing precondition
/// causes the rule to be silently skipped (no <see cref="RuleResult"/> emitted, no UI card,
/// no telemetry-visible "rule fired"). Distinct from conditions which decide whether the
/// rule fires given that it applies.
/// </summary>
public class RuleEnginePreconditionTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    /// <summary>
    /// Baseline: rule with no preconditions and a matching condition fires as expected.
    /// Confirms the test harness is wired correctly before we exercise gating.
    /// </summary>
    [Fact]
    public async Task NoPreconditions_RuleFires_WhenConditionMatches()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>());
        var events = new List<EnrollmentEvent>
        {
            HardwareSpecEvent(isVirtualMachine: "false"),
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
        Assert.Equal(rule.RuleId, outcome.Results[0].RuleId);
    }

    /// <summary>
    /// Precondition passes (device is NOT a VM) → rule evaluates conditions and fires.
    /// </summary>
    [Fact]
    public async Task PreconditionPasses_RuleFires()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "hardware_spec", DataField = "isVirtualMachine", Operator = "equals", Value = "false" }
        });
        var events = new List<EnrollmentEvent>
        {
            HardwareSpecEvent(isVirtualMachine: "false"),
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
    }

    /// <summary>
    /// Precondition fails (device IS a VM) → rule is silently skipped. No result row, even
    /// though the underlying condition would have matched.
    /// </summary>
    [Fact]
    public async Task PreconditionFails_RuleSilentlySkipped()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "hardware_spec", DataField = "isVirtualMachine", Operator = "equals", Value = "false" }
        });
        var events = new List<EnrollmentEvent>
        {
            HardwareSpecEvent(isVirtualMachine: "true"),
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Empty(outcome.Results);
        // The rule is still in EvaluatedRules (we attempted it) but no result was produced.
        Assert.Single(outcome.EvaluatedRules);
    }

    /// <summary>
    /// AND-semantics: two preconditions, one passes one fails → rule skipped.
    /// </summary>
    [Fact]
    public async Task TwoPreconditions_AnyFailure_SkipsRule()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "hardware_spec", DataField = "isVirtualMachine", Operator = "equals", Value = "false" },
            new() { Source = "event_data", EventType = "os_info", DataField = "edition", Operator = "equals", Value = "Enterprise" }
        });
        var events = new List<EnrollmentEvent>
        {
            HardwareSpecEvent(isVirtualMachine: "false"), // first precondition passes
            OsInfoEvent(edition: "Pro"),                  // second precondition fails
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Empty(outcome.Results);
    }

    /// <summary>
    /// Missing event type with non-existence operator: a precondition like
    /// "not_exists hardware_spec.isVirtualMachine" passes when the event isn't there at all.
    /// This is the "old agent without the new field" path.
    /// </summary>
    [Fact]
    public async Task NotExistsOperator_PassesWhenEventMissing()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "hardware_spec", DataField = "isVirtualMachine", Operator = "not_exists", Value = "" }
        });
        var events = new List<EnrollmentEvent>
        {
            // No hardware_spec event at all
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
    }

    /// <summary>
    /// Pure event-type presence gate (no dataField): not_exists passes when NO event of that type
    /// occurs in the session. This is the session-level gate ANALYZE-ENRL-002 uses to suppress a
    /// timeout when a later enrollment_complete/enrollment_failed exists (no shared join field).
    /// </summary>
    [Fact]
    public async Task NotExistsNoDataField_PassesWhenEventTypeAbsent()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "os_info", Operator = "not_exists" }
        });
        var events = new List<EnrollmentEvent>
        {
            // No os_info event at all → pure-type not_exists passes.
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
    }

    /// <summary>
    /// Pure event-type presence gate: not_exists fails (rule skipped) when an event of that type
    /// IS present — regardless of its data fields.
    /// </summary>
    [Fact]
    public async Task NotExistsNoDataField_SkipsWhenEventTypePresent()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "os_info", Operator = "not_exists" }
        });
        var events = new List<EnrollmentEvent>
        {
            OsInfoEvent(edition: "Pro"), // os_info present → gate closes
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Empty(outcome.Results);
    }

    /// <summary>
    /// Pure event-type presence gate: exists (no dataField) passes when an event of that type occurs.
    /// </summary>
    [Fact]
    public async Task ExistsNoDataField_PassesWhenEventTypePresent()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "os_info", Operator = "exists" }
        });
        var events = new List<EnrollmentEvent>
        {
            OsInfoEvent(edition: "Pro"),
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
    }

    /// <summary>
    /// Missing event with comparison operator (equals, contains, …) → fail-closed (rule skipped).
    /// We refuse to fire a rule when its precondition can't be assessed; better silent than wrong.
    /// </summary>
    [Fact]
    public async Task EqualsOperator_FailsClosed_WhenEventMissing()
    {
        var rule = MakeSecureBootRule(preconditions: new List<RulePrecondition>
        {
            new() { Source = "event_data", EventType = "hardware_spec", DataField = "isVirtualMachine", Operator = "equals", Value = "false" }
        });
        var events = new List<EnrollmentEvent>
        {
            // No hardware_spec event
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Empty(outcome.Results);
    }

    /// <summary>
    /// Empty preconditions array (or null) is the legacy default — must behave identically
    /// to today: condition match → rule fires.
    /// </summary>
    [Fact]
    public async Task EmptyPreconditions_BehavesLikeBefore()
    {
        var rule = MakeSecureBootRule(preconditions: null);
        var events = new List<EnrollmentEvent>
        {
            SecureBootStatusEvent(uefiCA2023Status: "unknown", secureBootEnabled: "true")
        };

        var outcome = await RunAsync(rule, events);

        Assert.Single(outcome.Results);
    }

    // ===== Helpers =====

    private static AnalyzeRule MakeSecureBootRule(List<RulePrecondition>? preconditions)
    {
        return new AnalyzeRule
        {
            RuleId = "ANALYZE-SEC-001",
            Title = "Secure Boot UEFI CA 2023 certificate not deployed",
            Severity = "warning",
            Category = "security",
            Enabled = true,
            BaseConfidence = 70,
            ConfidenceThreshold = 40,
            Preconditions = preconditions ?? new List<RulePrecondition>(),
            Conditions = new List<RuleCondition>
            {
                new() { Signal = "ca2023_not_updated", Source = "event_data", EventType = "secureboot_status", DataField = "uefiCA2023Status", Operator = "not_equals", Value = "Updated", Required = true },
                new() { Signal = "secureboot_enabled", Source = "event_data", EventType = "secureboot_status", DataField = "uefiSecureBootEnabled", Operator = "equals", Value = "true", Required = true }
            },
            Explanation = "test"
        };
    }

    private static EnrollmentEvent HardwareSpecEvent(string isVirtualMachine) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "hardware_spec",
        Timestamp = DateTime.UtcNow,
        Sequence = 1,
        Data = new Dictionary<string, object> { ["isVirtualMachine"] = isVirtualMachine }
    };

    private static EnrollmentEvent OsInfoEvent(string edition) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "os_info",
        Timestamp = DateTime.UtcNow,
        Sequence = 2,
        Data = new Dictionary<string, object> { ["edition"] = edition }
    };

    private static EnrollmentEvent SecureBootStatusEvent(string uefiCA2023Status, string secureBootEnabled) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "secureboot_status",
        Timestamp = DateTime.UtcNow,
        Sequence = 3,
        Data = new Dictionary<string, object>
        {
            ["uefiCA2023Status"] = uefiCA2023Status,
            ["uefiSecureBootEnabled"] = secureBootEnabled
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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEnginePreconditionTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
