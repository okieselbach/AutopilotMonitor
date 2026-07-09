using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Rule-level channel notifications (Notify / NotifyChannelIds): two-tier override semantics
/// mirroring the KO criterion, RuleState persistence contract, service merge, and the
/// rule-fired alert shape. The dispatch itself (AnalyzeOnEnrollmentEndHandler →
/// SendToChannelsAsync) is fail-soft plumbing verified during manual QA.
/// </summary>
public class AnalyzeRuleNotifyTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    private static Mock<IRuleRepository> CreateRepo()
    {
        var repo = new Mock<IRuleRepository>();
        // Global partition mirrors the shipped catalog exactly → seed diff is a no-op.
        repo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(BuiltInAnalyzeRules.GetAll().ToList());
        repo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        repo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>());
        return repo;
    }

    [Fact]
    public void BuiltInAnalyzeRules_NotifyDefault_IsFalseForAllBuiltIns()
    {
        // Opt-in only: notification targets are tenant-specific channel ids, so a shipped
        // NotifyDefault=true could never be actionable — and would surprise every tenant
        // the moment they select channels. Mirrors the MarkSessionAsFailedDefault guard.
        foreach (var rule in BuiltInAnalyzeRules.GetAll())
        {
            Assert.False(rule.NotifyDefault,
                $"Rule {rule.RuleId} ships with NotifyDefault=true — rule notifications must be tenant opt-in.");
        }
    }

    [Fact]
    public void EffectiveNotify_NullOverride_InheritsDefault()
    {
        var rule = new AnalyzeRule { RuleId = "ANALYZE-TEST-001", NotifyDefault = false, Notify = null };
        Assert.False(rule.Notify ?? rule.NotifyDefault);

        rule.Notify = true;
        Assert.True(rule.Notify ?? rule.NotifyDefault);
    }

    [Fact]
    public async Task GetAllRules_MergesNotifyStateOntoBuiltInRule()
    {
        var repo = CreateRepo();
        var builtInId = BuiltInAnalyzeRules.GetAll().First().RuleId;
        repo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>
        {
            [builtInId] = new RuleState
            {
                Enabled = true,
                Notify = true,
                NotifyChannelIdsJson = "[\"ch-1\",\"ch-2\"]"
            }
        });

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        var rule = merged.Single(r => r.RuleId == builtInId);
        Assert.True(rule.Notify);
        Assert.Equal(new[] { "ch-1", "ch-2" }, rule.NotifyChannelIds);
    }

    [Fact]
    public async Task GetAllRules_MalformedChannelIdsJson_YieldsNullTargets()
    {
        // Fail-soft: a hand-corrupted RuleState row must not throw the whole rule load —
        // and no targets means no notification (safe direction).
        var repo = CreateRepo();
        var builtInId = BuiltInAnalyzeRules.GetAll().First().RuleId;
        repo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>
        {
            [builtInId] = new RuleState { Enabled = true, Notify = true, NotifyChannelIdsJson = "not-json" }
        });

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Null(merged.Single(r => r.RuleId == builtInId).NotifyChannelIds);
    }

    [Fact]
    public async Task UpdateRule_BuiltIn_PersistsNotifyIntoRuleState()
    {
        var repo = CreateRepo();
        RuleState? captured = null;
        repo.Setup(r => r.StoreRuleStateAsync(TenantId, It.IsAny<string>(), It.IsAny<RuleState>()))
            .Callback<string, string, RuleState>((_, _, s) => captured = s)
            .ReturnsAsync(true);

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-002",
            IsBuiltIn = true,
            Enabled = true,
            Notify = true,
            NotifyChannelIds = new List<string> { "ch-1" }
        };

        Assert.True(await service.UpdateRuleAsync(TenantId, rule));
        Assert.NotNull(captured);
        Assert.True(captured!.Notify);
        Assert.Equal(new List<string> { "ch-1" }, JsonConvert.DeserializeObject<List<string>>(captured.NotifyChannelIdsJson!));
    }

    [Fact]
    public async Task UpdateRule_BuiltIn_EmptyChannelList_ClearsStateColumn()
    {
        var repo = CreateRepo();
        RuleState? captured = null;
        repo.Setup(r => r.StoreRuleStateAsync(TenantId, It.IsAny<string>(), It.IsAny<RuleState>()))
            .Callback<string, string, RuleState>((_, _, s) => captured = s)
            .ReturnsAsync(true);

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var rule = new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-002",
            IsBuiltIn = true,
            Enabled = true,
            Notify = null,
            NotifyChannelIds = new List<string>()
        };

        Assert.True(await service.UpdateRuleAsync(TenantId, rule));
        Assert.NotNull(captured);
        Assert.Null(captured!.Notify);              // inherit default
        Assert.Null(captured.NotifyChannelIdsJson); // Replace-mode upsert wipes the column
    }

    [Fact]
    public async Task UpdateRule_CustomRule_FoldsNotifyOverrideIntoRowDefault()
    {
        // Custom rules have no RuleState — the override must be folded into the tenant-owned
        // row (NotifyDefault) so a subsequent load computes the same effective value.
        var repo = CreateRepo();
        AnalyzeRule? stored = null;
        repo.Setup(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), TenantId))
            .Callback<AnalyzeRule, string>((r, _) => stored = r)
            .ReturnsAsync(true);

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var rule = new AnalyzeRule
        {
            RuleId = "TENANT-CUSTOM-001",
            IsBuiltIn = false,
            IsCommunity = false,
            Enabled = true,
            NotifyDefault = false,
            Notify = true,
            NotifyChannelIds = new List<string> { "ch-9" }
        };

        Assert.True(await service.UpdateRuleAsync(TenantId, rule));
        Assert.NotNull(stored);
        Assert.True(stored!.NotifyDefault);
        Assert.Null(stored.Notify);
        Assert.Equal(new List<string> { "ch-9" }, stored.NotifyChannelIds);
    }

    // ── Alert shape ───────────────────────────────────────────────────────

    [Fact]
    public void BuildRuleFiredAlert_CarriesRuleFactsAndSessionLink()
    {
        var result = new RuleResult
        {
            RuleId = "ANALYZE-NET-001",
            RuleTitle = "Proxy blocks enrollment endpoints",
            Severity = "high",
            Category = "network",
            ConfidenceScore = 85,
            Explanation = "The device could not reach the enrollment endpoints.",
        };

        var alert = NotificationAlertBuilder.BuildRuleFiredAlert(
            result, "DESKTOP-CONTOSO1", "SN-1234",
            "https://portal.autopilotmonitor.com/sessions/abc");

        Assert.Equal("analyze_rule_fired", alert.EventType);
        Assert.Contains("Proxy blocks enrollment endpoints", alert.Title);
        Assert.Contains(alert.Facts, f => f.Name == "Device" && f.Value == "DESKTOP-CONTOSO1");
        Assert.Contains(alert.Facts, f => f.Name == "Serial" && f.Value == "SN-1234");
        Assert.Contains(alert.Facts, f => f.Name == "Rule" && f.Value.Contains("ANALYZE-NET-001"));
        Assert.Contains(alert.Facts, f => f.Name == "Confidence" && f.Value == "85%");
        Assert.Contains(alert.Actions, a => a.Type == "openUrl" && a.Url == "https://portal.autopilotmonitor.com/sessions/abc");
        Assert.Contains(alert.Sections, s => s.Text.Contains("could not reach"));
    }

    [Theory]
    [InlineData("critical", NotificationSeverityExpectation.Error)]
    [InlineData("high", NotificationSeverityExpectation.Error)]
    [InlineData("warning", NotificationSeverityExpectation.Warning)]
    [InlineData("info", NotificationSeverityExpectation.Info)]
    public void BuildRuleFiredAlert_MapsRuleSeverity(string ruleSeverity, NotificationSeverityExpectation expected)
    {
        var alert = NotificationAlertBuilder.BuildRuleFiredAlert(
            new RuleResult { RuleId = "R", RuleTitle = "T", Severity = ruleSeverity }, null, null);

        var actual = alert.Severity.ToString();
        Assert.Equal(expected.ToString(), actual);
    }

    public enum NotificationSeverityExpectation { Info, Success, Warning, Error }
}
