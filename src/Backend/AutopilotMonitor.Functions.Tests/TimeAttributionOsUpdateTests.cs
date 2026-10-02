using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Golden fixtures for the OOBE quality update in the time attribution (AttributionVersion 4,
/// D-314): <c>os_update</c> from the update page start whose visit holds update evidence to the
/// last update evidence before the user is back, then — after a restart — <c>awaiting_sign_in</c>
/// until the user is back. Sequences follow the field sessions of 2026-10-02 (synthetic times, no
/// customer data). EVERY fixture asserts the exact-partition invariant.
/// </summary>
public class TimeAttributionOsUpdateTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);
    private const string Lcu = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.6899.1.7";
    private const string LcuKb = "Package_for_KB5129195~31bf3856ad364e35~amd64~~26100.6899.1.7";

    private static long _seq;

    private static EnrollmentEvent Evt(
        DateTime ts, string eventType,
        EnrollmentPhase phase = EnrollmentPhase.Unknown,
        Dictionary<string, object>? data = null)
        => new()
        {
            EventType = eventType,
            Timestamp = ts,
            Sequence = ++_seq,
            Phase = phase,
            Data = data ?? new Dictionary<string, object>(),
        };

    private static EnrollmentEvent PageStart(DateTime ts, string page) =>
        Evt(ts, "oobe_update_page", data: new() { ["cxhEvent"] = "page_started", ["page"] = page });

    private static EnrollmentEvent PageStop(DateTime ts, string page, string result) =>
        Evt(ts, "oobe_update_page", data: new() { ["cxhEvent"] = "page_stopped", ["page"] = page, ["result"] = result });

    private static EnrollmentEvent PageName(DateTime ts, string name, string? value = null)
    {
        var data = new Dictionary<string, object> { ["cxhEvent"] = "event_name", ["name"] = name };
        if (value != null) data["value"] = value;
        return Evt(ts, "oobe_update_page", data: data);
    }

    private static EnrollmentEvent Servicing(DateTime ts, string step, string package = LcuKb, string targetState = "Installed") =>
        Evt(ts, "windows_update_servicing", data: new()
        {
            ["step"] = step,
            ["package"] = package,
            ["client"] = "UpdateAgentLCU",
            ["targetState"] = targetState,
        });

    /// <summary>A restart: the agent's next run detects it; the gap runs from the last event before the boot to the first after.</summary>
    private static EnrollmentEvent RebootDetected(DateTime detectedAt, DateTime lastBoot) =>
        Evt(detectedAt, "system_reboot_detected", data: new() { ["lastBootUtc"] = lastBoot.ToString("o") });

    private static TimeAttributionInput Input(
        List<EnrollmentEvent> events, DateTime completedAt, int durationSeconds,
        string status = "Succeeded", bool wg = false, DateTime? resumedAt = null)
        => new()
        {
            TenantId = "00000000-0000-0000-0000-0000000000t1",
            SessionId = "00000000-0000-0000-0000-0000000000s1",
            Status = status,
            StartedAt = T0,
            CompletedAt = completedAt,
            DurationSeconds = durationSeconds,
            IsPreProvisioned = wg,
            ResumedAt = resumedAt,
            Events = events,
        };

    private static void AssertExactPartition(SessionTimeBreakdown b)
    {
        Assert.Equal(b.WallClockSeconds, b.Segments.Sum(s => s.Seconds) + b.UnattributedSeconds);
        Assert.True(b.UnattributedSeconds >= 0, "unattributed must never be negative");
    }

    private static int Total(SessionTimeBreakdown b, string segment) =>
        b.GetSegmentTotals().TryGetValue(segment, out var seconds) ? seconds : 0;

    private static DateTime At(int minutes, int seconds = 0) => T0.AddMinutes(minutes).AddSeconds(seconds);

    // ── the device ESP and the update page, shared by the fixtures ──────────

    private static List<EnrollmentEvent> DeviceEsp() => new()
    {
        Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
        Evt(At(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
        Evt(At(20), "esp_exiting"),
    };

    // ── golden: long update, three restarts, second page visit (field: 83 min) ──

    private static List<EnrollmentEvent> LongUpdateWithSecondVisit()
    {
        var events = DeviceEsp();
        events.AddRange(new[]
        {
            PageStart(At(20, 5), "OobeNDUP"),
            PageName(At(20, 10), "ExpeditedUpdate_isNDUPAllowedByCSPSucceeded", "true"),
            // The agent declares AccountSetup while the page still runs — no evidence of the user.
            Evt(At(22, 36), "esp_phase_changed", EnrollmentPhase.AccountSetup),
            Servicing(At(43, 35), "initiating", targetState: "Staged"),
            Servicing(At(63), "state_reached", targetState: "Staged"),
            Servicing(At(63, 10), "initiating"),
            Servicing(At(86, 46), "reboot_required"),
            PageName(At(102, 39), "ExpeditedUpdate_downloadInstallFailureHelper"),
            PageStart(At(103, 11), "RebootNDUP"),
            RebootDetected(At(104, 43), lastBoot: At(104)),
            Evt(At(106), "agent_shutting_down"),
            RebootDetected(At(108), lastBoot: At(107)),
            Evt(At(110), "agent_shutting_down"),
            RebootDetected(At(112), lastBoot: At(111)),
            Servicing(At(119, 54), "state_reached"),
            PageStart(At(120, 6), "OobeNDUP"),
            PageStop(At(120, 36), "OobeNDUP", "success"),
            Evt(At(122, 8), "desktop_arrived"),
            Evt(At(125), "enrollment_complete"),
        });
        return events;
    }

    [Fact]
    public void LongUpdate_IsOneUpdateSegment_FromThePageStartToTheSecondVisit_ThenTheWait()
    {
        var b = TimeAttributionCalculator.Compute(Input(LongUpdateWithSecondVisit(), At(125), 7500))!;

        AssertExactPartition(b);
        Assert.Equal(4, b.AttributionVersion);
        Assert.Equal(120, Total(b, TimeAttributionSegments.DevicePrep));
        Assert.Equal(18 * 60 + 5, Total(b, TimeAttributionSegments.EspApps));             // +2m → page start
        Assert.Equal(100 * 60 + 31, Total(b, TimeAttributionSegments.OsUpdate));          // page start → second visit ends
        Assert.Equal(92, Total(b, TimeAttributionSegments.AwaitingSignIn));               // → desktop
        Assert.Equal(0, Total(b, TimeAttributionSegments.IdentityHello));                 // the early AccountSetup lay inside the update
        Assert.Equal(172, Total(b, TimeAttributionSegments.DesktopHandoff));

        var update = Assert.Single(b.OsUpdates);
        Assert.Equal(At(20, 5), update.StartUtc);
        Assert.Equal(At(120, 36), update.EndUtc);
        Assert.Equal(100 * 60 + 31, update.Seconds);
        Assert.Equal(new[] { "KB5129195" }, update.Kbs);
        Assert.Equal(3, update.RebootCount);

        // The update's restarts sit in its own segment.
        Assert.Equal(3, b.RebootSpans.Count);
        Assert.All(b.RebootSpans, r => Assert.Equal(TimeAttributionSegments.OsUpdate, r.SegmentKey));
    }

    [Fact]
    public void LongUpdate_WasIdentityHelloBeforeV4()
    {
        // Same stream without the update signals = what v3 saw: the AccountSetup declaration
        // took the whole update.
        var withoutUpdate = LongUpdateWithSecondVisit()
            .Where(e => e.EventType != "oobe_update_page" && e.EventType != "windows_update_servicing")
            .ToList();

        var b = TimeAttributionCalculator.Compute(Input(withoutUpdate, At(125), 7500))!;

        AssertExactPartition(b);
        Assert.Equal(0, Total(b, TimeAttributionSegments.OsUpdate));
        Assert.Equal(0, Total(b, TimeAttributionSegments.AwaitingSignIn));
        Assert.Equal((122 * 60 + 8) - (22 * 60 + 36), Total(b, TimeAttributionSegments.IdentityHello));
        Assert.Empty(b.OsUpdates);
    }

    // ── golden: update inside the apps phase, long wait with standby (field: 48 min) ──

    [Fact]
    public void UpdateBeforeAccountSetup_TakesItsTimeFromTheAppsPhase_AndTheWaitHoldsTheStandby()
    {
        var events = DeviceEsp();
        events.AddRange(new[]
        {
            PageStart(At(20, 4), "OobeNDUP"),
            PageName(At(21), "ExpeditedUpdate_startWUScanStarted"),
            Servicing(At(30), "initiating", package: Lcu, targetState: "Staged"),
            Servicing(At(38, 50), "reboot_required", package: Lcu),
            PageStart(At(38, 55), "RebootNDUP"),
            RebootDetected(At(40, 10), lastBoot: At(39, 30)),
            Evt(At(41), "agent_shutting_down"),
            RebootDetected(At(42, 30), lastBoot: At(42)),
            Servicing(At(43), "state_reached", package: Lcu),
            // The user is away: the device sleeps 45 minutes at the lock screen.
            Evt(At(89), "system_sleep_episode", data: new()
            {
                ["enteredAt"] = At(44).ToString("o"),
                ["exitedAt"] = At(89).ToString("o"),
                ["kind"] = "modern_standby",
            }),
            Evt(At(90, 50), "esp_phase_changed", EnrollmentPhase.AccountSetup),
            Evt(At(91), "hello_wizard_started"),
            Evt(At(91), "esp_phase_changed", EnrollmentPhase.FinalizingSetup),
            Evt(At(93), "desktop_arrived"),
            Evt(At(95), "enrollment_complete"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(95), 5700))!;

        AssertExactPartition(b);
        Assert.Equal(18 * 60 + 4, Total(b, TimeAttributionSegments.EspApps));            // +2m → page start only
        Assert.Equal(22 * 60 + 56, Total(b, TimeAttributionSegments.OsUpdate));          // → servicing reports Installed
        // The wait ends at the first evidence of the user: the Hello wizard, not the AccountSetup row.
        Assert.Equal((91 - 43) * 60, Total(b, TimeAttributionSegments.AwaitingSignIn));
        Assert.Equal(120, Total(b, TimeAttributionSegments.IdentityHello));              // Hello → desktop

        var update = Assert.Single(b.OsUpdates);
        Assert.Empty(update.Kbs);                                                        // RollupFix names no KB
        Assert.Equal(2, update.RebootCount);
        var sleep = Assert.Single(b.SleepSpans);
        Assert.Equal(TimeAttributionSegments.AwaitingSignIn, sleep.SegmentKey);
    }

    // ── no update, no segment ───────────────────────────────────────────────

    private static List<EnrollmentEvent> WithAccountPhase(IEnumerable<EnrollmentEvent> updatePart)
    {
        var events = DeviceEsp();
        events.AddRange(updatePart);
        events.AddRange(new[]
        {
            Evt(At(21), "esp_phase_changed", EnrollmentPhase.AccountSetup),
            Evt(At(26), "hello_wizard_started"),
            Evt(At(26), "esp_phase_changed", EnrollmentPhase.FinalizingSetup),
            Evt(At(28), "desktop_arrived"),
            Evt(At(30), "enrollment_complete"),
        });
        // Canonical order = event time here: the update part was created first.
        var ordered = events.OrderBy(e => e.Timestamp).ToList();
        foreach (var evt in ordered) evt.Sequence = ++_seq;
        return ordered;
    }

    [Fact]
    public void CancelledPage_LeavesThePhasePartitionUnchanged()
    {
        // AllowOOBEUpdates=0: the page checks the CSP gate and cancels within seconds.
        var cancelled = WithAccountPhase(new[]
        {
            PageStart(At(20, 2), "OobeNDUP"),
            PageName(At(20, 3), "ExpeditedUpdate_isNDUPAllowedByCSPSucceeded", "false"),
            PageStop(At(20, 5), "OobeNDUP", "cancel"),
            PageStart(At(20, 6), "RebootNDUP"),
        });
        var plain = WithAccountPhase(Array.Empty<EnrollmentEvent>());

        var b = TimeAttributionCalculator.Compute(Input(cancelled, At(30), 1800))!;
        var v3 = TimeAttributionCalculator.Compute(Input(plain, At(30), 1800))!;

        AssertExactPartition(b);
        Assert.Empty(b.OsUpdates);
        Assert.Equal(v3.Segments.Select(s => (s.SegmentKey, s.Seconds)), b.Segments.Select(s => (s.SegmentKey, s.Seconds)));
    }

    [Fact]
    public void ScanThatFoundNothing_IsNoUpdate()
    {
        // Every visit logs the same init names — with "Install" and "Download" in them.
        var events = WithAccountPhase(new[]
        {
            PageStart(At(20, 2), "OobeNDUP"),
            PageName(At(20, 3), "ExpeditedUpdate_isNDUPAllowedByCSPSucceeded", "true"),
            PageName(At(20, 4), "SdxWebAppCloudNDUP_initialize_NDUPInstallCanceledInOptOut", "false"),
            PageName(At(20, 4), "SdxWebAppCloudNDUP_initialize_NDUPDownloadInstallPreviousFailureCount", "0"),
            PageName(At(20, 9), "ExpeditedUpdate_startWUScanStarted"),
            PageName(At(20, 58), "SdxWebAppCloudNDUP_updateManager_processUpdatesMetadata_osUpdatesWithNDUPPI", "false"),
            PageStop(At(20, 59), "OobeNDUP", "cancel"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(30), 1800))!;

        AssertExactPartition(b);
        Assert.Empty(b.OsUpdates);
        Assert.Equal(0, Total(b, TimeAttributionSegments.OsUpdate));
    }

    [Fact]
    public void ServicingBesideACancelledPage_IsSomeOtherUpdate()
    {
        // The page was seen and did not update; a servicing step during the account phase is
        // not the OOBE update.
        var events = WithAccountPhase(new[]
        {
            PageStart(At(20, 2), "OobeNDUP"),
            PageStop(At(20, 5), "OobeNDUP", "cancel"),
            Servicing(At(23), "initiating", targetState: "Staged"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(30), 1800))!;

        AssertExactPartition(b);
        Assert.Empty(b.OsUpdates);
    }

    [Fact]
    public void UpdateAfterTheDesktop_StaysATimelineEvent()
    {
        var events = WithAccountPhase(new[]
        {
            Servicing(At(28, 30), "initiating", targetState: "Staged"),
            Servicing(At(29, 30), "state_reached", targetState: "Staged"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(30), 1800))!;

        AssertExactPartition(b);
        Assert.Empty(b.OsUpdates);
        Assert.Equal(120, Total(b, TimeAttributionSegments.DesktopHandoff));
    }

    // ── update without a restart: no wait ──────────────────────────────────

    [Fact]
    public void UpdateWithoutRestart_HasNoWaitForSignIn()
    {
        // Without a restart the user who started the enrollment is still signed in.
        var events = WithAccountPhase(new[]
        {
            PageStart(At(20, 2), "OobeNDUP"),
            PageName(At(20, 40), "ExpeditedUpdate_commitExpeditionDownloadInstallAsyncSucceeded"),
            PageStop(At(20, 50), "OobeNDUP", "success"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(30), 1800))!;

        AssertExactPartition(b);
        var update = Assert.Single(b.OsUpdates);
        Assert.Equal(0, update.RebootCount);
        Assert.Equal(48, Total(b, TimeAttributionSegments.OsUpdate));                    // page start → page stop
        Assert.Equal(0, Total(b, TimeAttributionSegments.AwaitingSignIn));
    }

    // ── the page start was not observed ─────────────────────────────────────

    [Fact]
    public void WithoutAnObservedPageStart_TheFirstServicingStepAfterTheEspExitBeginsTheUpdate()
    {
        var events = DeviceEsp();
        events.AddRange(new[]
        {
            Servicing(At(10), "initiating", targetState: "Staged"),                      // during the ESP: not the OOBE update
            Servicing(At(25), "initiating", targetState: "Staged"),
            Servicing(At(35), "reboot_required"),
            PageStart(At(35, 10), "RebootNDUP"),
            RebootDetected(At(37), lastBoot: At(36)),
            Servicing(At(38), "state_reached"),
            Evt(At(40), "desktop_arrived"),
            Evt(At(42), "enrollment_complete"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(42), 2520))!;

        AssertExactPartition(b);
        var update = Assert.Single(b.OsUpdates);
        Assert.Equal(At(25), update.StartUtc);
        Assert.Equal(At(38), update.EndUtc);
        Assert.Equal(120, Total(b, TimeAttributionSegments.AwaitingSignIn));
        Assert.Equal(23 * 60, Total(b, TimeAttributionSegments.EspApps));                // the hole until the first step stays the phase's
    }

    [Theory]
    [InlineData("ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted", true)]
    [InlineData("SdxWebAppCloudNDUP_processStatusChangeFromHandler_downloadSucceeded", true)]
    [InlineData("SdxWebAppCloudNDUP_processStatusChangeFromHandler_installSucceededRebootRequired_lcu", true)]
    [InlineData("SdxWebAppCloudNDUP_updateUSOProgressBar_DownloadPhase_progress100", true)]
    [InlineData("SdxWebAppCloudNDUP_downloadInstallFailureHelper", true)]
    [InlineData("SdxWebAppCloudNDUP_rebootCountdown_starting", true)]
    [InlineData("SdxWebAppCloudNDUP_initialize_NDUPInstallCanceledInOptOut", false)]
    [InlineData("SdxWebAppCloudNDUP_initialize_NDUPDownloadInstallPreviousFailureCount", false)]
    [InlineData("SdxWebAppCloudNDUP_processStatusChangeFromHandler_installSucceededNoReboot", false)]
    [InlineData("SdxWebAppCloudNDUP_handleInstallHelper", false)]
    [InlineData("ExpeditedUpdate_getUpdateResultsSucceeded", false)]
    [InlineData(null, false)]
    public void UpdateActivityNames_AreTheOnesOnlyAnUpdateWrites(string? name, bool expected)
    {
        Assert.Equal(expected, TimeAttributionCalculator.IsUpdateActivityName(name));
    }

    // ── restarts ────────────────────────────────────────────────────────────

    [Fact]
    public void ClockSyncAtBoot_DoesNotShortenTheRestart()
    {
        // The hardware clock sync is logged 0.1 s before lastBootUtc; the restart began with
        // the last real event before it.
        var events = DeviceEsp();
        events.AddRange(new[]
        {
            PageStart(At(20, 2), "OobeNDUP"),
            Servicing(At(25), "initiating", targetState: "Staged"),
            Servicing(At(30), "reboot_required"),
            PageStart(At(30, 5), "RebootNDUP"),
            Evt(At(31).AddMilliseconds(-140), "system_clock_changed"),
            RebootDetected(At(31, 20), lastBoot: At(31)),
            Servicing(At(32), "state_reached"),
            Evt(At(34), "desktop_arrived"),
            Evt(At(35), "enrollment_complete"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(35), 2100))!;

        AssertExactPartition(b);
        var restart = Assert.Single(b.RebootSpans);
        Assert.Equal(At(30, 5), restart.StartUtc);                                       // not the clock sync
        Assert.Equal(At(31, 20), restart.EndUtc);
        Assert.Equal(75, restart.Seconds);
        Assert.Equal(TimeAttributionSegments.OsUpdate, restart.SegmentKey);
    }

    // ── robustness ──────────────────────────────────────────────────────────

    [Fact]
    public void BackfillDuplicates_ChangeNothing()
    {
        var once = LongUpdateWithSecondVisit();
        var twice = LongUpdateWithSecondVisit();
        // An agent restart re-sends the page records it read before, with their event times.
        twice.AddRange(twice.Where(e => e.EventType == "oobe_update_page").Select(e => Evt(e.Timestamp, e.EventType, data: e.Data)).ToList());

        var a = TimeAttributionCalculator.Compute(Input(once, At(125), 7500))!;
        var b = TimeAttributionCalculator.Compute(Input(twice, At(125), 7500))!;

        AssertExactPartition(b);
        Assert.Equal(a.Segments.Select(s => (s.SegmentKey, s.Seconds)), b.Segments.Select(s => (s.SegmentKey, s.Seconds)));
        Assert.Equal(a.OsUpdates.Single().Seconds, b.OsUpdates.Single().Seconds);
    }

    [Fact]
    public void FailedDeclaration_EndsTheUpdateToo()
    {
        var events = DeviceEsp();
        events.AddRange(new[]
        {
            PageStart(At(20, 2), "OobeNDUP"),
            Servicing(At(25), "initiating", targetState: "Staged"),
            Evt(At(50), "enrollment_failed", EnrollmentPhase.Failed),
            Servicing(At(55), "state_reached", targetState: "Staged"),
        });

        var b = TimeAttributionCalculator.Compute(Input(events, At(60), 3600, status: "Failed"))!;

        AssertExactPartition(b);
        var update = Assert.Single(b.OsUpdates);
        Assert.Equal(At(25), update.EndUtc);                                             // the step after the failure is not attributed
        Assert.Equal(4 * 60 + 58, Total(b, TimeAttributionSegments.OsUpdate));
        Assert.Equal(0, Total(b, TimeAttributionSegments.AwaitingSignIn));
        Assert.True(b.UnattributedSeconds >= 10 * 60);                                   // the post-failure tail stays unattributed
    }

    [Fact]
    public void WhiteGlove_UpdateInTheUserPart_StaysInsideItsWindow()
    {
        var part1End = At(30);
        var resumedAt = T0.AddDays(2);
        var completedAt = resumedAt.AddMinutes(40);
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(At(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(part1End, "whiteglove_part1_complete"),
            Evt(resumedAt.AddSeconds(30), "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(resumedAt.AddMinutes(3), "esp_exiting"),
            PageStart(resumedAt.AddMinutes(3).AddSeconds(4), "OobeNDUP"),
            Servicing(resumedAt.AddMinutes(10), "initiating", targetState: "Staged"),
            Servicing(resumedAt.AddMinutes(20), "reboot_required"),
            PageStart(resumedAt.AddMinutes(20).AddSeconds(5), "RebootNDUP"),
            RebootDetected(resumedAt.AddMinutes(23), lastBoot: resumedAt.AddMinutes(22)),
            Servicing(resumedAt.AddMinutes(24), "state_reached"),
            Evt(resumedAt.AddMinutes(30), "esp_phase_changed", EnrollmentPhase.AccountSetup),
            Evt(resumedAt.AddMinutes(32), "hello_wizard_started"),
            Evt(resumedAt.AddMinutes(38), "desktop_arrived"),
            Evt(completedAt, "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt, durationSeconds: 30 * 60 + 40 * 60, wg: true, resumedAt: resumedAt))!;

        AssertExactPartition(b);
        var update = Assert.Single(b.OsUpdates);
        Assert.Equal(resumedAt.AddMinutes(3).AddSeconds(4), update.StartUtc);
        Assert.Equal(resumedAt.AddMinutes(24), update.EndUtc);
        Assert.Equal(8 * 60, Total(b, TimeAttributionSegments.AwaitingSignIn));          // → Hello wizard
        Assert.All(b.Segments, s => Assert.True(s.EndUtc <= part1End || s.StartUtc >= resumedAt, "no span in the pause"));
    }
}
