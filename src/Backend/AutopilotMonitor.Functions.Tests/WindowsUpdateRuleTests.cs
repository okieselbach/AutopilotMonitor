using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Windows Update during OOBE — pins the firing behaviour of the built-in analyze rules
/// that surface update activity captured by the agent's WindowsUpdateTracker:
///   - ANALYZE-DEV-004 (high): a quality/cumulative update FAILED during enrollment.
///   - ANALYZE-DEV-005 (info): a quality/cumulative update INSTALLED during enrollment.
///   - ANALYZE-DEV-006 (info): the OS build changed across a mid-enrollment reboot —
///     deterministic corroboration that works even when the WU channel showed nothing
///     (session 7443317c blind spot).
/// Also verifies the matched-condition evidence carries updateTitle / hresult / hresultSymbol /
/// previousBuild / currentBuild so the web interpolator can render the explanation tokens.
/// </summary>
public class WindowsUpdateRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task ANALYZE_DEV_004_fires_high_on_failed_update_with_decoded_hresult()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-004");
        Assert.True(rule.Enabled);
        Assert.Equal("high", rule.Severity);
        Assert.False(rule.MarkSessionAsFailedDefault); // failed OOBE update is high-insight, not KO

        var events = new List<EnrollmentEvent>
        {
            WindowsUpdateFailed("2026-07 Cumulative Update (KB5099999)", "0x80240022", "WU_E_ALL_UPDATES_FAILED"),
            WindowsUpdateRebootPending(),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-004", result.RuleId);
        Assert.Equal("high", result.Severity);

        // Interpolation material for {{updateTitle}} / {{hresult}} / {{hresultSymbol}}
        var title = AsDict(result.MatchedConditions["wu_update_title"]);
        Assert.Equal("updateTitle", AsString(title["field"]));
        Assert.Equal("2026-07 Cumulative Update (KB5099999)", AsString(title["value"]));

        var symbol = AsDict(result.MatchedConditions["wu_hresult_symbol"]);
        Assert.Equal("WU_E_ALL_UPDATES_FAILED", AsString(symbol["value"]));
    }

    [Fact]
    public async Task ANALYZE_DEV_004_does_not_fire_without_a_failed_update()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-004");

        var events = new List<EnrollmentEvent>
        {
            WindowsUpdateSucceeded("2026-07 Cumulative Update (KB5099999)"),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ANALYZE_DEV_005_fires_info_on_installed_update()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-005");
        Assert.True(rule.Enabled);
        Assert.Equal("info", rule.Severity);

        var events = new List<EnrollmentEvent>
        {
            WindowsUpdateSucceeded("2026-07 Cumulative Update (KB5099999)"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-005", result.RuleId);
        Assert.Equal("info", result.Severity);
        var title = AsDict(result.MatchedConditions["wu_update_title"]);
        Assert.Equal("2026-07 Cumulative Update (KB5099999)", AsString(title["value"]));
    }

    [Fact]
    public async Task RebootPending_exists_false_is_NOT_counted_as_corroboration()
    {
        // Codex round 3: a windows_update_reboot_pending event with exists=false (no reboot actually
        // pending) must NOT count as the reboot_pending corroborator — the condition matches on
        // event_data exists==true, not mere event existence.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-005");

        var events = new List<EnrollmentEvent>
        {
            WindowsUpdateSucceeded("2026-07 Cumulative Update (KB5099999)"),
            WindowsUpdateRebootPending(exists: false),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.False(result.MatchedConditions.ContainsKey("reboot_pending"),
            "exists=false must not satisfy the reboot_pending condition.");
    }

    [Fact]
    public async Task RebootPending_exists_true_IS_counted_as_corroboration()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-005");

        var events = new List<EnrollmentEvent>
        {
            WindowsUpdateSucceeded("2026-07 Cumulative Update (KB5099999)"),
            WindowsUpdateRebootPending(exists: true),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.MatchedConditions.ContainsKey("reboot_pending"),
            "exists=true must satisfy the reboot_pending condition.");
    }

    [Fact]
    public async Task ANALYZE_DEV_006_fires_info_on_os_build_change_with_interpolation_material()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-006");
        Assert.True(rule.Enabled);
        Assert.Equal("info", rule.Severity);
        Assert.False(rule.MarkSessionAsFailedDefault);

        var events = new List<EnrollmentEvent>
        {
            OsBuildChanged("26200.8037", "26200.8655"),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-006", result.RuleId);
        Assert.Equal("info", result.Severity);

        // Interpolation material for {{previousBuild}} / {{currentBuild}}
        var previous = AsDict(result.MatchedConditions["previous_build"]);
        Assert.Equal("26200.8037", AsString(previous["value"]));
        var current = AsDict(result.MatchedConditions["current_build"]);
        Assert.Equal("26200.8655", AsString(current["value"]));
    }

    [Fact]
    public async Task ANALYZE_DEV_006_counts_channel_census_as_corroboration()
    {
        // Session 7443317c shape: build change + census = the update came through a path the
        // WU watcher is blind to; the census signal should be part of the matched evidence.
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-006");

        var events = new List<EnrollmentEvent>
        {
            OsBuildChanged("26200.8037", "26200.8655"),
            WindowsUpdateChannelCensus(),
        };

        var outcome = await RunAsync(rule, events);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.MatchedConditions.ContainsKey("wu_channel_blind"),
            "the census event must be counted as corroboration evidence.");
    }

    [Fact]
    public async Task ANALYZE_DEV_006_does_not_fire_without_a_build_change()
    {
        var rule = BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == "ANALYZE-DEV-006");

        var events = new List<EnrollmentEvent>
        {
            WindowsUpdateSucceeded("2026-07 Cumulative Update (KB5099999)"),
            WindowsUpdateChannelCensus(),
        };

        var outcome = await RunAsync(rule, events);
        Assert.Empty(outcome.Results);
    }

    // ===== Event builders — mirror the WindowsUpdateTracker emit shape =====

    private static EnrollmentEvent OsBuildChanged(string previousBuild, string currentBuild) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "os_build_changed",
        Timestamp = DateTime.UtcNow,
        Sequence = 45,
        Data = new Dictionary<string, object>
        {
            ["previousBuild"] = previousBuild,
            ["currentBuild"] = currentBuild,
            ["previousCapturedUtc"] = DateTime.UtcNow.AddMinutes(-30).ToString("o"),
        }
    };

    private static EnrollmentEvent WindowsUpdateChannelCensus() => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "windows_update_channel_census",
        Timestamp = DateTime.UtcNow,
        Sequence = 46,
        Data = new Dictionary<string, object>
        {
            ["wuClientCensus"] = "21=1,25=3",
            ["updateOrchestratorCensus"] = "200=2",
            ["lookbackMinutes"] = 60,
            ["targetedEventIds"] = "19,20,43,44",
        }
    };

    private static EnrollmentEvent WindowsUpdateFailed(string title, string hresult, string hresultSymbol) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "windows_update_failed",
        Timestamp = DateTime.UtcNow,
        Sequence = 42,
        Data = new Dictionary<string, object>
        {
            ["wuEventId"] = 20,
            ["updateTitle"] = title,
            ["updateGuid"] = "{8b1c8726-1111-2222-3333-444455556666}",
            ["hresult"] = hresult,
            ["hresultSymbol"] = hresultSymbol,
            ["backfilled"] = false,
        }
    };

    private static EnrollmentEvent WindowsUpdateSucceeded(string title) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "windows_update_succeeded",
        Timestamp = DateTime.UtcNow,
        Sequence = 43,
        Data = new Dictionary<string, object>
        {
            ["wuEventId"] = 19,
            ["updateTitle"] = title,
            ["updateGuid"] = "{8b1c8726-1111-2222-3333-444455556666}",
            ["backfilled"] = false,
        }
    };

    private static EnrollmentEvent WindowsUpdateRebootPending(bool exists = true) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        TenantId = TenantId,
        SessionId = SessionId,
        EventType = "windows_update_reboot_pending",
        Timestamp = DateTime.UtcNow,
        Sequence = 44,
        Data = new Dictionary<string, object>
        {
            // Bool, matching the RegistryCollector's `data["exists"]`. In production emitOnlyIfExists
            // suppresses the exists=false case at the source; the analyze rule only counts exists==true.
            ["exists"] = exists,
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
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<WindowsUpdateRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
