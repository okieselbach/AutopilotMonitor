using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The OOBE quality update page (oobe_update_page, D-310) — pins the built-in analyze rules for
/// the update that did not happen:
///   - ANALYZE-DEV-013: the page reported a failed download or install and no install success
///     (field shapes e7551b82, cb1992b8). Only the explicit failure names count: an update that
///     installed logs downloadInstallFailureHelper and a start timeout too (e4ecd6f8).
///   - ANALYZE-DEV-014: nothing offered to an out-of-date device whose page counts an earlier
///     failure, without a failure in this session (b2d9f12c).
///   - ANALYZE-DEV-015: the scan started and the page ended without a scan result (c5b20dfa).
/// Every absence condition is vetoed by the agent's names_capped marker. Sequences follow the
/// field sessions of 2026-10-03..05 (synthetic times, no customer data); a session never fires
/// two of the three.
/// </summary>
public class OobeUpdateRuleTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    private static readonly DateTime T0 = new(2026, 10, 3, 16, 56, 59, DateTimeKind.Utc);

    private const string DownloadFailed = "SdxWebAppCloudNDUP_processStatusChangeFromHandler_downloadFailedError";
    private const string InstallFailed = "SdxWebAppCloudNDUP_processStatusChangeFromHandler_installFailedError";
    private const string CommitFailed = "ExpeditedUpdate_commitExpeditionDownloadInstallAsyncFailure";

    private static readonly string[] RuleIds = { "ANALYZE-DEV-013", "ANALYZE-DEV-014", "ANALYZE-DEV-015" };

    // ── the rules ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ANALYZE-DEV-013")]
    [InlineData("ANALYZE-DEV-014")]
    [InlineData("ANALYZE-DEV-015")]
    public void Rules_are_enabled_warnings_that_never_fail_the_session(string ruleId)
    {
        var rule = Rule(ruleId);
        Assert.True(rule.Enabled);
        Assert.Equal("warning", rule.Severity);
        Assert.Equal("device", rule.Category);
        Assert.False(rule.MarkSessionAsFailedDefault); // the enrollment continues without the update
    }

    // ── ANALYZE-DEV-013: failed ─────────────────────────────────────────────

    [Fact]
    public async Task DEV_013_fires_on_a_failed_download_and_names_the_failure()
    {
        var outcome = await RunAsync(FailedDownload());

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-013", result.RuleId);
        Assert.Equal(DownloadFailed, Value(result, "update_failure"));   // {{update_failure}}
        Assert.Equal("fail", Value(result, "update_page_failed"));
        Assert.Equal(85, result.ConfidenceScore);
    }

    [Fact]
    public async Task DEV_013_fires_when_the_update_is_skipped_after_the_failure()
    {
        var outcome = await RunAsync(FailureThenSkip());

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-013", result.RuleId);
        Assert.Equal(DownloadFailed, Value(result, "update_failure"));
        Assert.Equal("CloudNDUPSkipDownloadInstallButtonClicked", Value(result, "update_skipped"));
    }

    [Theory]
    [InlineData(InstallFailed)]
    [InlineData(CommitFailed)]
    public async Task DEV_013_fires_on_every_explicit_failure_name(string failureName)
    {
        var events = Visit(days: 120, previousFailures: 0);
        events.AddRange(new[] { Name("ExpeditedUpdate_getUpdateResultsSucceeded"), Name(failureName), PageStop("OobeNDUP", "fail") });

        var result = Assert.Single((await RunAsync(events)).Results);
        Assert.Equal(failureName, Value(result, "update_failure"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DEV_013_stays_silent_on_an_update_that_installed_despite_the_failure_helper(bool buildChangeObserved)
    {
        // Field shape e4ecd6f8: the failure helper and a start timeout after "restart required",
        // then the update installed — with or without the build change on record.
        var events = InstalledDespiteFailureHelper();
        if (!buildChangeObserved) events.RemoveAll(e => e.EventType == "os_build_changed");

        Assert.Empty((await RunAsync(events)).Results);
    }

    [Fact]
    public async Task DEV_013_stays_silent_when_a_retry_installed_the_update()
    {
        var events = FailedDownload();
        events.AddRange(new[]
        {
            PageStart("OobeNDUP"),
            Name("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted"),
            Name("SdxWebAppCloudNDUP_processStatusChangeFromHandler_installSucceededRebootRequired_lcu"),
        });

        Assert.Empty((await RunAsync(events)).Results);
    }

    [Fact]
    public async Task DEV_013_is_vetoed_by_an_os_build_change()
    {
        var events = FailedDownload();
        events.Add(OsBuildChanged());

        Assert.Empty((await RunAsync(events)).Results);
    }

    // ── ANALYZE-DEV-014: not offered after an earlier failure ───────────────

    [Fact]
    public async Task DEV_014_fires_when_nothing_is_offered_after_an_earlier_failure()
    {
        var outcome = await RunAsync(NothingOfferedAfterFailure());

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-014", result.RuleId);
        Assert.Equal("137", Value(result, "days_out_of_date"));   // {{days_out_of_date}}
        Assert.Equal("1", Value(result, "previous_failures"));    // {{previous_failures}}
    }

    [Theory]
    [InlineData(0, 1)]   // the device is current: nothing to offer
    [InlineData(137, 0)] // no earlier failure on record
    public async Task DEV_014_stays_silent_without_both_the_backlog_and_the_earlier_failure(int days, int previousFailures)
    {
        var events = Visit(days, previousFailures);
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("SdxWebAppCloudNDUP_updateManager_processUpdatesMetadata_osUpdatesWithNDUPPI", "false"),
            PageStop("OobeNDUP", "cancel"),
        });

        Assert.Empty((await RunAsync(events)).Results);
    }

    // ── ANALYZE-DEV-015: scan without a result ──────────────────────────────

    [Fact]
    public async Task DEV_015_fires_when_the_page_ends_without_a_scan_result()
    {
        var outcome = await RunAsync(ScanWithoutResult());

        var result = Assert.Single(outcome.Results);
        Assert.Equal("ANALYZE-DEV-015", result.RuleId);
        Assert.Equal("438", Value(result, "days_out_of_date"));
        Assert.Equal("page_stopped", Value(result, "update_page_ended"));
    }

    [Fact]
    public async Task DEV_015_stays_silent_while_the_page_still_runs()
    {
        // The session ended during the scan: no page end, so no claim that the page gave up.
        var events = ScanWithoutResult();
        events.RemoveAll(e => e.EventType == "oobe_update_page" && (string)e.Data["cxhEvent"] == "page_stopped");

        Assert.Empty((await RunAsync(events)).Results);
    }

    [Fact]
    public async Task DEV_015_stays_silent_when_the_agent_missed_the_scan_start()
    {
        // A late agent sees the page end but not the scan: the missing result proves nothing.
        var events = ScanWithoutResult();
        events.RemoveAll(e => e.EventType == "oobe_update_page" && (e.Data.TryGetValue("name", out var n) ? (string)n : "").StartsWith("ExpeditedUpdate_startWUScan"));

        Assert.Empty((await RunAsync(events)).Results);
    }

    // ── shared vetoes and the sessions that fire nothing ────────────────────

    [Theory]
    [MemberData(nameof(FiringShapes))]
    public async Task The_names_capped_marker_vetoes_every_rule(string shape)
    {
        // Once the agent capped the page's names, a missing success or scan result proves nothing.
        var events = Shapes[shape]();
        events.Add(NamesCapped());

        Assert.Empty((await RunAsync(events)).Results);
    }

    [Fact]
    public async Task A_page_the_policy_switched_off_fires_nothing()
    {
        var events = new List<EnrollmentEvent>
        {
            PageStart("OobeNDUP"),
            Name("ExpeditedUpdate_isNDUPAllowedByCSPSucceeded", "false"),
            PageStop("OobeNDUP", "cancel"),
            PageStart("RebootNDUP"),
            PageStop("RebootNDUP", "success"),
        };

        Assert.Empty((await RunAsync(events)).Results);
    }

    [Fact]
    public async Task An_installed_update_fires_nothing()
    {
        Assert.Empty((await RunAsync(Installed())).Results);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public async Task No_session_fires_more_than_one_of_the_three(string shape)
    {
        var outcome = await RunAsync(Shapes[shape]());

        Assert.True(outcome.Results.Count <= 1, $"{shape}: {string.Join(", ", outcome.Results.Select(r => r.RuleId))}");
    }

    // ── session shapes (field sessions, synthetic times) ────────────────────

    private static readonly Dictionary<string, Func<List<EnrollmentEvent>>> Shapes = new()
    {
        ["failed_download"] = FailedDownload,
        ["failure_then_skip"] = FailureThenSkip,
        ["installed_despite_failure_helper"] = InstalledDespiteFailureHelper,
        ["nothing_offered_after_failure"] = NothingOfferedAfterFailure,
        ["scan_without_result"] = ScanWithoutResult,
        ["installed"] = Installed,
    };

    public static IEnumerable<object[]> AllShapes() => Shapes.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> FiringShapes() =>
        new[] { "failed_download", "failure_then_skip", "nothing_offered_after_failure", "scan_without_result" }
            .Select(k => new object[] { k });

    /// <summary>The names every visit logs before its scan.</summary>
    private static List<EnrollmentEvent> Visit(int days, int previousFailures) => new()
    {
        PageStart("OobeNDUP"),
        Name("ExpeditedUpdate_isNDUPAllowedByCSPSucceeded", "true"),
        Name("ExpeditedUpdate_getDaysOutOfDateSucceeded", days.ToString()),
        Name("SdxWebAppCloudNDUP_initialize_NDUPInstallCanceledInOptOut", "false"),
        Name("SdxWebAppCloudNDUP_initialize_NDUPDownloadInstallPreviousFailureCount", previousFailures.ToString()),
        Name("ExpeditedUpdate_startWUScanStarted"),
        Name("ExpeditedUpdate_startWUScanStartedSuccedded"),
    };

    /// <summary>e7551b82: only the servicing stack staged, the download failed after 25 min; the second visit found nothing to offer.</summary>
    private static List<EnrollmentEvent> FailedDownload()
    {
        var events = Visit(days: 136, previousFailures: 0);
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsStarted"),
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("SdxWebAppCloudNDUP_updateManager_processUpdatesMetadata_osUpdatesWithNDUPPI"), // a list: no safe value
            Name("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted"),
            Name(DownloadFailed),
            Name(CommitFailed),
            Name("SdxWebAppCloudNDUP_downloadInstallFailureHelper"),
            PageStop("OobeNDUP", "fail"),
            PageStart("RebootNDUP"),
        });
        events.AddRange(Visit(days: 137, previousFailures: 1));
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("SdxWebAppCloudNDUP_updateManager_processUpdatesMetadata_osUpdatesWithNDUPPI", "false"),
            PageStop("OobeNDUP", "cancel"),
            PageStart("RebootNDUP"),
            PageStop("RebootNDUP", "success"),
        });
        return events;
    }

    /// <summary>cb1992b8: visit 1 failed; visit 2 downloaded again and someone selected Skip before the install.</summary>
    private static List<EnrollmentEvent> FailureThenSkip()
    {
        var events = Visit(days: 438, previousFailures: 0);
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted"),
            Name(DownloadFailed),
            Name(CommitFailed),
            Name(InstallFailed),
            PageStop("OobeNDUP", "fail"),
            PageStart("RebootNDUP"),
        });
        events.AddRange(Visit(days: 438, previousFailures: 1));
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("SdxWebAppCloudNDUP_updateManager_processUpdatesMetadata_osUpdatesWithNDUPPI"),
            Name("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted"),
            Name("CloudNDUPOptoutLinkClicked", "0"),
            Name("SdxWebAppCloudNDUP_processStatusChangeFromHandler_downloadSucceeded"),
            Name("SdxWebAppCloudNDUP_updateUSOProgressBar_DownloadPhase_progress100"),
            Name("SdxWebAppCloudNDUP_manageProgressBars_waitingForInstallStartError"),
            Name("CloudNDUPSkipDownloadInstallButtonClicked", "0"),
            PageStop("OobeNDUP", "cancel"),
            PageStart("RebootNDUP"),
            PageStop("RebootNDUP", "success"),
        });
        return events;
    }

    /// <summary>e4ecd6f8: the failure helper and a start timeout after "restart required" — the update installed after three restarts.</summary>
    private static List<EnrollmentEvent> InstalledDespiteFailureHelper()
    {
        var events = Visit(days: 99, previousFailures: 0);
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted"),
            Name("SdxWebAppCloudNDUP_updateUSOProgressBar_installPhase_progress100"),
            Name("SdxWebAppCloudNDUP_downloadInstallFailureHelper"),
            Name("SdxWebAppCloudNDUP_startEventArrivalTimeout_TimerExpired"),
            PageStart("RebootNDUP"),
            OsBuildChanged(),
            PageStart("OobeNDUP"),
            PageStop("OobeNDUP", "success"),
        });
        return events;
    }

    /// <summary>b2d9f12c: the same device the next day — out of date, an earlier failure on record, nothing offered.</summary>
    private static List<EnrollmentEvent> NothingOfferedAfterFailure()
    {
        var events = Visit(days: 137, previousFailures: 1);
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsStarted"),
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("SdxWebAppCloudNDUP_updateManager_processUpdatesMetadata_osUpdatesWithNDUPPI", "false"),
            Name("SdxWebAppCloudNDUP_exit_appResult"),
            PageStop("OobeNDUP", "cancel"),
            PageStart("RebootNDUP"),
            PageStop("RebootNDUP", "success"),
        });
        return events;
    }

    /// <summary>c5b20dfa: 438 days behind; the page ended two minutes after the scan started, without a result.</summary>
    private static List<EnrollmentEvent> ScanWithoutResult()
    {
        var events = Visit(days: 438, previousFailures: 0);
        events.AddRange(new[]
        {
            Name("SdxWebAppCloudNDUP_processStatusChangeFromHandler_installSucceededNoReboot"), // the language check, every visit
            Name("SdxWebAppCloudNDUP_exit_appResult"),
            PageStop("OobeNDUP", "cancel"),
            PageStart("RebootNDUP"),
            PageStop("RebootNDUP", "success"),
        });
        return events;
    }

    /// <summary>6a7d241d: downloaded, installed, restarted.</summary>
    private static List<EnrollmentEvent> Installed()
    {
        var events = Visit(days: 120, previousFailures: 0);
        events.AddRange(new[]
        {
            Name("ExpeditedUpdate_getUpdateResultsSucceeded"),
            Name("SdxWebAppCloudNDUP_updateManager_processUpdatesMetadata_osUpdatesWithNDUPPI"),
            Name("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted"),
            Name("SdxWebAppCloudNDUP_processStatusChangeFromHandler_downloadSucceeded"),
            Name("SdxWebAppCloudNDUP_processStatusChangeFromHandler_installSucceededRebootRequired"),
            Name("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncSucceeded"),
            Name("SdxWebAppCloudNDUP_rebootCountdown_starting"),
            PageStart("RebootNDUP"),
            OsBuildChanged(),
        });
        return events;
    }

    // ── event builders — mirror the agent's OobeUpdateTelemetry payload ─────

    private static long _seq;

    private static EnrollmentEvent Evt(string eventType, Dictionary<string, object> data)
    {
        var seq = Interlocked.Increment(ref _seq);
        return new EnrollmentEvent
        {
            EventId = Guid.NewGuid().ToString(),
            TenantId = TenantId,
            SessionId = SessionId,
            EventType = eventType,
            Timestamp = T0.AddSeconds(seq),
            Sequence = seq,
            Data = data,
        };
    }

    private static Dictionary<string, object> PageData(string cxhEvent, int windowsEventId) => new()
    {
        ["cxhEvent"] = cxhEvent,
        ["windowsEventId"] = windowsEventId,
        ["backfill"] = false,
        ["occurrence"] = 1,
    };

    private static EnrollmentEvent PageStart(string page)
    {
        var data = PageData("page_started", 62404);
        data["page"] = page;
        return Evt("oobe_update_page", data);
    }

    private static EnrollmentEvent PageStop(string page, string result)
    {
        var data = PageData("page_stopped", 62405);
        data["page"] = page;
        data["result"] = result;
        return Evt("oobe_update_page", data);
    }

    private static EnrollmentEvent Name(string name, string? value = null)
    {
        var data = PageData("event_name", value == null ? 62406 : 62407);
        data["name"] = name;
        if (value != null) data["value"] = value;
        return Evt("oobe_update_page", data);
    }

    private static EnrollmentEvent NamesCapped()
    {
        var data = PageData("names_capped", 0);
        data.Remove("windowsEventId");
        data.Remove("occurrence");
        data["limit"] = 150;
        return Evt("oobe_update_page", data);
    }

    private static EnrollmentEvent OsBuildChanged() => Evt("os_build_changed", new Dictionary<string, object>
    {
        ["previousBuild"] = "26200.8037",
        ["currentBuild"] = "26200.8655",
    });

    // ── harness (as WindowsUpdateRuleTests) ─────────────────────────────────

    private static AnalyzeRule Rule(string ruleId) => BuiltInAnalyzeRules.GetAll().First(r => r.RuleId == ruleId);

    private static string Value(RuleResult result, string signal)
    {
        var evidence = Assert.IsType<Dictionary<string, object>>(result.MatchedConditions[signal]);
        return evidence["value"]?.ToString() ?? string.Empty;
    }

    private static async Task<AnalysisOutcome> RunAsync(List<EnrollmentEvent> events)
    {
        var rules = RuleIds.Select(Rule).ToList();

        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(rules);
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.GetRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(new List<RuleResult>());

        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(s => s.GetSessionEventsStrictAsync(TenantId, SessionId, It.IsAny<int>())).ReturnsAsync(events);

        var ruleService = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var engine = new RuleEngine(ruleService, ruleRepo.Object, sessionRepo.Object, NullLogger<OobeUpdateRuleTests>.Instance);

        return await engine.AnalyzeSessionAsync(TenantId, SessionId);
    }
}
