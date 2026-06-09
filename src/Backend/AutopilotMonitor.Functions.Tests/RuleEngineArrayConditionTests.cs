using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the <c>event_data_array</c> condition source: it iterates an array field on a single
/// event (e.g. provisioning_package_scan's <c>artifacts</c>) and matches when ANY element's
/// <c>itemField</c> satisfies the operator. Backs the PPKG allow-list rules (ANALYZE-SEC-005/006):
/// one aggregate event + an allow-list regex, instead of one event per package.
/// </summary>
public class RuleEngineArrayConditionTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // Mirrors the ANALYZE-SEC-005 default allow-list (OS-inbox families) — ANCHORED at start.
    private const string AllowList = @"^(?:Microsoft\.Windows\.Cosa|Power\.EnergyEstimationEngine|Power\.Settings|SecureStart\.Settings)\b";

    [Fact]
    public async Task AllArtifactsAllowListed_RuleDoesNotFire()
    {
        // Clean Win11 VM: only OS-inbox .ppkg → every identity matches the allow-list → no fire.
        var events = new List<EnrollmentEvent>
        {
            ScanEvent(
                "Power.Settings.Sleep.ppkg",
                "Power.EnergyEstimationEngine.CPU.ppkg",
                "Microsoft.Windows.Cosa.Desktop.Client.ppkg",
                "SecureStart.Settings.ppkg")
        };

        var outcome = await RunAsync(MakeAllowListRule(), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task OneArtifactNotAllowListed_RuleFires_WithThatIdentityAsEvidence()
    {
        var events = new List<EnrollmentEvent>
        {
            ScanEvent(
                "Power.Settings.Sleep.ppkg",          // allow-listed
                "Contoso.BulkEnroll.ppkg",            // NOT allow-listed → should fire
                "Microsoft.Windows.Cosa.Desktop.ppkg")
        };

        var outcome = await RunAsync(MakeAllowListRule(), events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-SEC-005", result.RuleId);
        var evidence = Assert.IsAssignableFrom<IDictionary<string, object>>(result.MatchedConditions["ppkg_not_allowlisted"]);
        Assert.Equal("identity", evidence["field"]);
        Assert.Equal("Contoso.BulkEnroll.ppkg", evidence["value"]);
        Assert.Equal(1, Convert.ToInt32(evidence["matchCount"]));
    }

    [Fact]
    public async Task MultipleNotAllowListed_MatchCountReflectsAll()
    {
        var events = new List<EnrollmentEvent>
        {
            ScanEvent("Power.Settings.Sleep.ppkg", "Contoso.A.ppkg", "Contoso.B.ppkg")
        };

        var outcome = await RunAsync(MakeAllowListRule(), events);

        var result = Assert.Single(outcome.Results);
        var evidence = (IDictionary<string, object>)result.MatchedConditions["ppkg_not_allowlisted"];
        Assert.Equal(2, Convert.ToInt32(evidence["matchCount"]));
    }

    [Fact]
    public async Task EmptyArtifacts_RuleDoesNotFire()
    {
        var events = new List<EnrollmentEvent> { ScanEvent(/* none */) };

        var outcome = await RunAsync(MakeAllowListRule(), events);

        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task AllowListIsAnchored_PrefixedImpostorStillFires()
    {
        // Security: an unanchored substring allow-list would let "Contoso.Power.Settings.Backdoor"
        // slip through because it contains "Power.Settings". The anchored (^) pattern must reject it.
        var events = new List<EnrollmentEvent>
        {
            ScanEvent("Contoso.Power.Settings.Backdoor.ppkg")
        };

        var outcome = await RunAsync(MakeAllowListRule(), events);

        var result = Assert.Single(outcome.Results);
        var evidence = (IDictionary<string, object>)result.MatchedConditions["ppkg_not_allowlisted"];
        Assert.Equal("Contoso.Power.Settings.Backdoor.ppkg", evidence["value"]);
    }

    [Fact]
    public async Task DotPathArrayField_IsResolved()
    {
        // event_data_array dataField supports dot-path into a nested object (Low-finding fix).
        var nested = new Dictionary<string, object>
        {
            ["scan"] = new Dictionary<string, object>
            {
                ["artifacts"] = new List<object>
                {
                    new Dictionary<string, object> { ["identity"] = "Contoso.BulkEnroll.ppkg" }
                }
            }
        };
        var evt = new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "provisioning_package_scan",
            Timestamp = DateTime.UtcNow,
            Sequence = 1,
            Data = nested
        };

        var rule = MakeAllowListRule();
        rule.Conditions[0].DataField = "scan.artifacts";

        var outcome = await RunAsync(rule, new List<EnrollmentEvent> { evt });

        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task ScanTruncated_FiresTruncationAlarmRule()
    {
        // ANALYZE-SEC-007 shape: a per-source cap hit (scanTruncated=true) must alarm, because the
        // allow-list rules could not evaluate every package (a tail artifact may be unseen).
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-SEC-007",
            Title = "Provisioning Package Scan Incomplete",
            Severity = "warning",
            Category = "security",
            Enabled = true,
            BaseConfidence = 80,
            ConfidenceThreshold = 40,
            Conditions = new List<RuleCondition>
            {
                new() { Signal = "ppkg_scan_truncated", Source = "event_data", EventType = "provisioning_package_scan", DataField = "scanTruncated", Operator = "equals", Value = "true", Required = true }
            },
            Explanation = "test"
        };
        var evt = new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "provisioning_package_scan",
            Timestamp = DateTime.UtcNow,
            Sequence = 1,
            Data = new Dictionary<string, object> { ["scanTruncated"] = true, ["artifacts"] = new List<object>() }
        };

        var outcome = await RunAsync(rule, new List<EnrollmentEvent> { evt });

        Assert.Single(outcome.Results);
    }

    // ===== Helpers =====

    private static AnalyzeRule MakeAllowListRule() => new()
    {
        RuleId = "ANALYZE-SEC-005",
        Title = "Unexpected Provisioning Package",
        Severity = "warning",
        Category = "security",
        Enabled = true,
        BaseConfidence = 80,
        ConfidenceThreshold = 40,
        Conditions = new List<RuleCondition>
        {
            new()
            {
                Signal = "ppkg_not_allowlisted",
                Source = "event_data_array",
                EventType = "provisioning_package_scan",
                DataField = "artifacts",
                ItemField = "identity",
                Operator = "not_regex",
                Value = AllowList,
                Required = true
            }
        },
        Explanation = "test"
    };

    private static EnrollmentEvent ScanEvent(params string[] identities)
    {
        // Mirror the storage-read shape: artifacts is a List<object> of Dictionary<string,object>.
        var artifacts = new List<object>();
        foreach (var id in identities)
            artifacts.Add(new Dictionary<string, object> { ["source"] = "file", ["identity"] = id });

        return new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = "provisioning_package_scan",
            Timestamp = DateTime.UtcNow,
            Sequence = 1,
            Data = new Dictionary<string, object>
            {
                ["anyPpkgFound"] = identities.Length > 0,
                ["artifacts"] = artifacts
            }
        };
    }

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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<RuleEngineArrayConditionTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
