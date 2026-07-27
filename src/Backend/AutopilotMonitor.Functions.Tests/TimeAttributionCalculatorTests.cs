using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Golden fixtures for <see cref="TimeAttributionCalculator"/> (F1 PR1, insights spec §F1) —
/// synthetic sessions only (no customer data), each pinning one semantic decision:
/// linear phase partition, clock skew, late start, WhiteGlove two-window split, missing /
/// truncated blocking lists, IME retry occupancy, and reboot gaps. EVERY fixture asserts the
/// exact-partition invariant: sum(span seconds) + unattributed == wall clock ==
/// the session's DurationSeconds (truthfulness rule 2 — never normalized, never interpolated).
/// </summary>
public class TimeAttributionCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);

    private const string AppA = "aaaaaaaa-1111-2222-3333-444444444444"; // in blocking list
    private const string AppB = "bbbbbbbb-1111-2222-3333-444444444444"; // in blocking list, never observed
    private const string AppC = "cccccccc-1111-2222-3333-444444444444"; // observed, NOT listed

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

    private static Dictionary<string, object> AppData(string appId, string appName, string? state = null)
    {
        var d = new Dictionary<string, object> { ["appId"] = appId, ["appName"] = appName };
        if (state != null) d["state"] = state;
        return d;
    }

    /// <summary>Storage-shape blocking lists (EventDataNormalizer yields List&lt;object&gt;).</summary>
    private static Dictionary<string, object> EspLists(long? win32Count = null, params string[] win32Ids)
        => new()
        {
            ["espTrackedWin32AppIds"] = new List<object>(win32Ids),
            ["espTrackedUserWin32AppIds"] = new List<object>(),
            ["espTrackedMsiProductCodes"] = new List<object>(),
            ["espTrackedModernAppPfns"] = new List<object>(),
            ["espTrackedWin32Count"] = win32Count ?? win32Ids.Length,
            ["espTrackedMsiCount"] = 0L,
            ["espTrackedModernCount"] = 0L,
        };

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

    /// <summary>The invariant every fixture must satisfy exactly (spec §0 rule 2).</summary>
    private static void AssertExactPartition(SessionTimeBreakdown b)
    {
        Assert.Equal(b.WallClockSeconds, b.Segments.Sum(s => s.Seconds) + b.UnattributedSeconds);
        Assert.True(b.UnattributedSeconds >= 0, "unattributed must never be negative");
    }

    // ── golden: linear UDE session ──────────────────────────────────────────

    private static List<EnrollmentEvent> LinearSession()
        => new()
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(2), "esp_config_detected", data: EspLists(win32Ids: new[] { AppA, AppB })),
            Evt(T0.AddMinutes(5), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(T0.AddMinutes(6), "app_install_started", data: AppData(AppA, "App A")),
            Evt(T0.AddMinutes(10), "app_install_completed", data: AppData(AppA, "App A", "Installed")),
            Evt(T0.AddMinutes(11), "app_install_started", data: AppData(AppC, "Untracked App")),
            Evt(T0.AddMinutes(13), "app_install_completed", data: AppData(AppC, "Untracked App", "Installed")),
            Evt(T0.AddMinutes(15), "esp_phase_changed", EnrollmentPhase.AccountSetup),
            Evt(T0.AddMinutes(20), "esp_phase_changed", EnrollmentPhase.AppsUser),
            Evt(T0.AddMinutes(25), "esp_phase_changed", EnrollmentPhase.FinalizingSetup),
            Evt(T0.AddMinutes(28), "desktop_arrived"),
            Evt(T0.AddMinutes(30), "enrollment_complete"),
        };

    [Fact]
    public void Linear_Session_PartitionsIntoAllFiveSegments_Exactly()
    {
        var b = TimeAttributionCalculator.Compute(
            Input(LinearSession(), completedAt: T0.AddMinutes(30), durationSeconds: 1800))!;

        AssertExactPartition(b);
        Assert.Equal(1800, b.WallClockSeconds);
        Assert.Equal(0, b.UnattributedSeconds);
        Assert.Equal(TimeAttributionFlags.None, b.QualityFlags);
        Assert.Equal(TimeAttributionCalculator.CurrentVersion, b.AttributionVersion);

        var totals = b.GetSegmentTotals();
        Assert.Equal(300, totals[TimeAttributionSegments.DevicePrep]);      // T0 → apps entry (+5m)
        Assert.Equal(600, totals[TimeAttributionSegments.EspApps]);         // +5m → +15m
        Assert.Equal(480, totals[TimeAttributionSegments.IdentityHello]);   // AccountSetup 300 + Finalizing 180
        Assert.Equal(300, totals[TimeAttributionSegments.UserEsp]);         // +20m → +25m
        Assert.Equal(120, totals[TimeAttributionSegments.DesktopHandoff]);  // desktop_arrived → terminal
    }

    [Fact]
    public void Linear_Session_BlockingJoin_IsPositiveEvidenceOnly()
    {
        var b = TimeAttributionCalculator.Compute(
            Input(LinearSession(), completedAt: T0.AddMinutes(30), durationSeconds: 1800))!;

        // App A is listed → blocking, interval from EVENT timestamps (started → completed).
        var interval = Assert.Single(b.BlockingApps);
        Assert.Equal(AppA, interval.AppId);
        Assert.Equal(240, interval.Seconds); // +6m → +10m
        // App C installed fine but is NOT in the lists → unknown, NEVER "not blocking":
        // it appears nowhere (no negative claim), and only App A counts as matched.
        Assert.Equal(1, b.BlockingAppCount);
        // Occupancy = App A's interval clipped to the esp_apps span.
        Assert.Equal(240, b.EspAppsOccupancySeconds);
    }

    // ── skew: backward anchor is dropped, dust stays honest ─────────────────

    [Fact]
    public void BackwardPhaseAnchor_IsDropped_WithClockSkewFlag()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(10), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            // Replayed declaration whose timestamp runs BACKWARD in canonical order.
            Evt(T0.AddMinutes(3), "esp_phase_changed", EnrollmentPhase.AccountSetup),
            Evt(T0.AddMinutes(30), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(30), durationSeconds: 1800))!;

        AssertExactPartition(b);
        Assert.True(b.QualityFlags.HasFlag(TimeAttributionFlags.ClockSkewDropped));
        var totals = b.GetSegmentTotals();
        Assert.Equal(600, totals[TimeAttributionSegments.DevicePrep]);
        Assert.Equal(1200, totals[TimeAttributionSegments.EspApps]); // runs to window end — no phantom AccountSetup
        Assert.False(totals.ContainsKey(TimeAttributionSegments.IdentityHello));
    }

    [Fact]
    public void SubSecondBoundaries_FloorIntoUnattributedDust_NeverInvented()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddSeconds(90.5), "esp_phase_changed", EnrollmentPhase.AppsDevice),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddSeconds(300), durationSeconds: 300))!;

        AssertExactPartition(b);
        var totals = b.GetSegmentTotals();
        Assert.Equal(90, totals[TimeAttributionSegments.DevicePrep]);   // floor(90.5)
        Assert.Equal(209, totals[TimeAttributionSegments.EspApps]);     // floor(209.5)
        Assert.Equal(1, b.UnattributedSeconds);                          // the dust, disclosed
    }

    // ── late start ──────────────────────────────────────────────────────────

    [Fact]
    public void AgentLateStart_SetsPartialObservation()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddSeconds(1), "agent_late_start"),
            Evt(T0.AddMinutes(10), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(10), durationSeconds: 600))!;

        AssertExactPartition(b);
        Assert.True(b.QualityFlags.HasFlag(TimeAttributionFlags.PartialObservation));
    }

    // ── WhiteGlove: two windows in ONE session, pause never attributed ──────

    [Fact]
    public void WhiteGlove_SplitsIntoTwoWindows_PauseExcluded_WallClockIsDurationSeconds()
    {
        var part1End = T0.AddMinutes(20);
        var resumedAt = T0.AddDays(2);                 // 2-day pause
        var completedAt = resumedAt.AddMinutes(10);    // part 2 = 600 s
        // Stored combined duration: part1 (1200 s) + part2 (600 s) — pause excluded by design.
        const int duration = 1800;

        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(1), "esp_config_detected", data: EspLists(win32Ids: new[] { AppA })),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(part1End, "whiteglove_part1_complete"),
            // — pause —
            Evt(resumedAt.AddSeconds(30), "agent_started", EnrollmentPhase.AccountSetup),
            Evt(resumedAt.AddMinutes(5), "esp_phase_changed", EnrollmentPhase.FinalizingSetup),
            Evt(resumedAt.AddMinutes(8), "desktop_arrived"),
            Evt(completedAt, "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt, duration, wg: true, resumedAt: resumedAt))!;

        AssertExactPartition(b);
        // Wall clock is the stored DurationSeconds — NOT CompletedAt − StartedAt (≈ 2 days).
        Assert.Equal(1800, b.WallClockSeconds);
        Assert.Equal(TimeAttributionFlags.None, b.QualityFlags); // part1 anchored on the event

        var totals = b.GetSegmentTotals();
        Assert.Equal(120, totals[TimeAttributionSegments.DevicePrep]);     // W1: T0 → +2m
        Assert.Equal(1080, totals[TimeAttributionSegments.EspApps]);       // W1: +2m → part1End
        Assert.Equal(450, totals[TimeAttributionSegments.IdentityHello]);  // W2: +30s → +8m
        Assert.Equal(120, totals[TimeAttributionSegments.DesktopHandoff]); // W2: +8m → +10m
        // W2's 30 s resume gap has no phase context — unattributed, not guessed.
        Assert.Equal(30, b.UnattributedSeconds);
        // No span may live inside the pause.
        Assert.DoesNotContain(b.Segments, s => s.StartUtc > part1End && s.EndUtc < resumedAt);
    }

    [Fact]
    public void WhiteGlove_WithoutPart1CompleteEvent_FallsBackAndFlags()
    {
        var resumedAt = T0.AddDays(1);
        var completedAt = resumedAt.AddMinutes(10);
        var lastPart1Event = T0.AddMinutes(20);

        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(lastPart1Event, "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(resumedAt.AddSeconds(10), "agent_started", EnrollmentPhase.AccountSetup),
            Evt(completedAt, "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt, durationSeconds: 1800, wg: true, resumedAt: resumedAt))!;

        AssertExactPartition(b);
        Assert.True(b.QualityFlags.HasFlag(TimeAttributionFlags.WhiteGloveAnchorsIncomplete));
        Assert.Equal(1800, b.WallClockSeconds);
    }

    [Fact]
    public void WhiteGlove_Part2LongerThanStoredDuration_DegradesToSingleWindow_Flagged()
    {
        // Stored duration (600 s) contradicts the resume stamp (part 2 alone spans 900 s):
        // no honest two-window split exists — degrade to the single derived window instead
        // of inventing a negative part 1.
        var resumedAt = T0.AddDays(1);
        var completedAt = resumedAt.AddMinutes(15);

        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(resumedAt.AddSeconds(5), "agent_started", EnrollmentPhase.AccountSetup),
            Evt(completedAt, "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt, durationSeconds: 600, wg: true, resumedAt: resumedAt))!;

        AssertExactPartition(b);
        Assert.Equal(600, b.WallClockSeconds);
        Assert.True(b.QualityFlags.HasFlag(TimeAttributionFlags.WhiteGloveAnchorsIncomplete));
    }

    // ── blocking list missing / truncated ───────────────────────────────────

    [Fact]
    public void NoBlockingLists_MeansUnknown_NotZero()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(T0.AddMinutes(3), "app_install_started", data: AppData(AppA, "App A")),
            Evt(T0.AddMinutes(8), "app_install_completed", data: AppData(AppA, "App A", "Installed")),
            Evt(T0.AddMinutes(10), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(10), durationSeconds: 600))!;

        AssertExactPartition(b);
        Assert.True(b.QualityFlags.HasFlag(TimeAttributionFlags.BlockingSetUnknown));
        Assert.Empty(b.BlockingApps);
        Assert.Equal(0, b.BlockingAppCount);
        Assert.Null(b.EspAppsOccupancySeconds); // unknown — NOT 0 (rule 1)
    }

    [Fact]
    public void EspConfigWithoutListKeys_IsNotEvidence_LaterEmissionNeverErasesEarlierLists()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(1), "esp_config_detected", data: EspLists(win32Ids: new[] { AppA })),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(T0.AddMinutes(3), "app_install_started", data: AppData(AppA, "App A")),
            Evt(T0.AddMinutes(8), "app_install_completed", data: AppData(AppA, "App A", "Installed")),
            // Later emission whose probe found no Diagnostics key — no espTracked* keys at all.
            Evt(T0.AddMinutes(9), "esp_config_detected",
                data: new Dictionary<string, object> { ["source"] = "registry_firstsync" }),
            Evt(T0.AddMinutes(10), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(10), durationSeconds: 600))!;

        Assert.False(b.QualityFlags.HasFlag(TimeAttributionFlags.BlockingSetUnknown));
        Assert.Equal(AppA, Assert.Single(b.BlockingApps).AppId);
    }

    [Fact]
    public void TruncatedBlockingList_SetsFlag_AbsentAppsStayUnknown()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            // Uncapped total (60) exceeds the emitted list (1 id) → cap dropped entries.
            Evt(T0.AddMinutes(1), "esp_config_detected", data: EspLists(win32Count: 60, AppA)),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(T0.AddMinutes(3), "app_install_started", data: AppData(AppC, "Maybe Capped App")),
            Evt(T0.AddMinutes(7), "app_install_completed", data: AppData(AppC, "Maybe Capped App", "Installed")),
            Evt(T0.AddMinutes(10), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(10), durationSeconds: 600))!;

        Assert.True(b.QualityFlags.HasFlag(TimeAttributionFlags.BlockingSetTruncated));
        // App C is absent from the (capped) list → it stays UNKNOWN: no interval, no claim.
        Assert.Empty(b.BlockingApps);
        Assert.Equal(0, b.BlockingAppCount);
    }

    // ── retry: occupancy spans to the LAST terminal event (audit Q1) ────────

    [Fact]
    public void Retry_IntervalRunsFromFirstStart_ToLastTerminalEvent()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(1), "esp_config_detected", data: EspLists(win32Ids: new[] { AppA })),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(T0.AddMinutes(6), "app_install_started", data: AppData(AppA, "App A")),
            Evt(T0.AddMinutes(8), "app_install_failed", data: AppData(AppA, "App A")),
            Evt(T0.AddMinutes(9), "app_install_started", data: AppData(AppA, "App A")),   // IME retry
            Evt(T0.AddMinutes(12), "app_install_completed", data: AppData(AppA, "App A", "Installed")),
            Evt(T0.AddMinutes(15), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(15), durationSeconds: 900))!;

        AssertExactPartition(b);
        var interval = Assert.Single(b.BlockingApps);
        // First started (+6m) → LAST terminal (+12m) = 360 s. The payload timing would have
        // frozen at the first failure (+8m) — event timestamps are the only valid source.
        Assert.Equal(360, interval.Seconds);
        Assert.Equal(360, b.EspAppsOccupancySeconds);
    }

    // ── reboots: spans from event gaps, never the detection event's stamp ───

    [Fact]
    public void RebootSpan_ComesFromEventGapAroundLastBootUtc_NotFromDetectionTimestamp()
    {
        var boot = T0.AddMinutes(12);
        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(5), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(T0.AddMinutes(10), "app_install_started", data: AppData(AppA, "App A")), // last pre-reboot
            Evt(T0.AddMinutes(14), "system_reboot_detected",                             // next run, detection-time
                data: new Dictionary<string, object> { ["lastBootUtc"] = boot.ToString("o") }),
            // Duplicate detection of the SAME boot (batch replay) — must not double-count.
            Evt(T0.AddMinutes(14.5), "system_reboot_detected",
                data: new Dictionary<string, object> { ["lastBootUtc"] = boot.ToString("o") }),
            Evt(T0.AddMinutes(20), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(20), durationSeconds: 1200))!;

        AssertExactPartition(b);
        var span = Assert.Single(b.RebootSpans);
        // Gap = last event before boot (+10m) → first observation at/after boot (+14m).
        Assert.Equal(T0.AddMinutes(10), span.StartUtc);
        Assert.Equal(T0.AddMinutes(14), span.EndUtc);
        Assert.Equal(240, span.Seconds);
        Assert.Equal(240, b.RebootSeconds);
        // The reboot began inside the apps phase — cross-cutting annotation, segment unchanged.
        Assert.Equal(TimeAttributionSegments.EspApps, span.SegmentKey);
        Assert.Equal(1200 - 300, b.GetSegmentTotals()[TimeAttributionSegments.EspApps]);
    }

    // ── WhiteGlove pause vs. intervals/reboots (Codex review P1): the pause lies INSIDE
    // the windows' hull, so hull-clamping alone would count it — a reboot bracketing the
    // pause must contribute only its in-window flanks, never the pause itself ─────────────

    [Fact]
    public void WhiteGlove_RebootGapAcrossPause_CountsOnlyInWindowFlanks()
    {
        var part1End = T0.AddMinutes(20);
        var resumedAt = T0.AddDays(2);                 // 2-day pause
        var completedAt = resumedAt.AddMinutes(10);    // part 2 = 600 s → part 1 = 1200 s
        var bootInsidePause = T0.AddDays(1);

        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(part1End, "whiteglove_part1_complete"),
            // — pause: device sealed, user boots it a day later —
            Evt(resumedAt.AddSeconds(30), "agent_started", EnrollmentPhase.AccountSetup),
            Evt(resumedAt.AddSeconds(40), "system_reboot_detected",
                data: new Dictionary<string, object> { ["lastBootUtc"] = bootInsidePause.ToString("o") }),
            Evt(completedAt, "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt, durationSeconds: 1800, wg: true, resumedAt: resumedAt))!;

        AssertExactPartition(b);
        var span = Assert.Single(b.RebootSpans);
        // Gap = part1End → first post-boot event (+30 s into part 2). In-window seconds are
        // ONLY the part-2 flank (30 s) — the ~2-day pause never counts (pre-fix: ~172 830 s
        // of "reboot" in a 30-minute enrollment).
        Assert.Equal(part1End, span.StartUtc);
        Assert.Equal(resumedAt.AddSeconds(30), span.EndUtc);
        Assert.Equal(30, span.Seconds);
        Assert.Equal(30, b.RebootSeconds);
    }

    [Fact]
    public void WhiteGlove_BlockingIntervalAcrossPause_CountsOnlyInWindowSeconds()
    {
        var part1End = T0.AddMinutes(20);
        var resumedAt = T0.AddDays(2);
        var completedAt = resumedAt.AddMinutes(10);

        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(1), "esp_config_detected", data: EspLists(win32Ids: new[] { AppA })),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(T0.AddMinutes(10), "app_install_started", data: AppData(AppA, "App A")),
            Evt(part1End, "whiteglove_part1_complete"),
            // — pause —
            Evt(resumedAt.AddSeconds(30), "agent_started", EnrollmentPhase.AccountSetup),
            // The app's final terminal lands in part 2 (user-scope finish after resume).
            Evt(resumedAt.AddMinutes(2), "app_install_completed", data: AppData(AppA, "App A", "Installed")),
            Evt(completedAt, "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt, durationSeconds: 1800, wg: true, resumedAt: resumedAt))!;

        AssertExactPartition(b);
        var interval = Assert.Single(b.BlockingApps);
        // Chronological endpoints stay the real event times…
        Assert.Equal(T0.AddMinutes(10), interval.StartUtc);
        Assert.Equal(resumedAt.AddMinutes(2), interval.EndUtc);
        // …but Seconds is the in-window sum: part 1 tail (10 min) + part 2 head (2 min).
        Assert.Equal(720, interval.Seconds);
        // Occupancy intersects with the esp_apps span (part 1 only) — pause never leaks in.
        Assert.Equal(600, b.EspAppsOccupancySeconds);
    }

    [Fact]
    public void WhiteGlove_IntervalWhollyInsidePause_MakesNoClaim_AndNoSkewFlag()
    {
        var part1End = T0.AddMinutes(20);
        var resumedAt = T0.AddDays(2);
        var completedAt = resumedAt.AddMinutes(10);

        var events = new List<EnrollmentEvent>
        {
            Evt(T0, "agent_started", EnrollmentPhase.DeviceSetup),
            Evt(T0.AddMinutes(1), "esp_config_detected", data: EspLists(win32Ids: new[] { AppA })),
            Evt(T0.AddMinutes(2), "esp_phase_changed", EnrollmentPhase.AppsDevice),
            Evt(part1End, "whiteglove_part1_complete"),
            // Both endpoints inside the pause (post-seal tail activity) — observed, but in no window.
            Evt(part1End.AddHours(1), "app_install_started", data: AppData(AppA, "App A")),
            Evt(part1End.AddHours(2), "app_install_completed", data: AppData(AppA, "App A", "Installed")),
            Evt(resumedAt.AddSeconds(30), "agent_started", EnrollmentPhase.AccountSetup),
            Evt(completedAt, "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt, durationSeconds: 1800, wg: true, resumedAt: resumedAt))!;

        AssertExactPartition(b);
        // No in-window observation → no interval claim; and nothing is wrong with the clocks,
        // so the skew flag must NOT fire (it would exclude the session from fleet medians).
        Assert.Empty(b.BlockingApps);
        Assert.False(b.QualityFlags.HasFlag(TimeAttributionFlags.ClockSkewDropped));
    }

    // ── non-computable sessions ─────────────────────────────────────────────

    [Theory]
    [InlineData("Incomplete")]   // terminal but deliberately NO DurationSeconds
    [InlineData("InProgress")]
    [InlineData("Pending")]
    public void NonComputableStatuses_ReturnNull(string status)
    {
        var input = Input(LinearSession(), completedAt: T0.AddMinutes(30), durationSeconds: 1800, status: status);
        Assert.Null(TimeAttributionCalculator.Compute(input));
    }

    [Fact]
    public void MissingDuration_ReturnsNull_NeverGuessesFromCompletedMinusStarted()
    {
        var input = Input(LinearSession(), completedAt: T0.AddMinutes(30), durationSeconds: 0);
        Assert.Null(TimeAttributionCalculator.Compute(input));
    }

    // ── no phase evidence at all ────────────────────────────────────────────

    [Fact]
    public void NoPhaseDeclarations_LeaveEverythingUnattributed()
    {
        var events = new List<EnrollmentEvent>
        {
            Evt(T0.AddMinutes(1), "network_state_change"),
            Evt(T0.AddMinutes(9), "enrollment_complete"),
        };

        var b = TimeAttributionCalculator.Compute(
            Input(events, completedAt: T0.AddMinutes(10), durationSeconds: 600))!;

        AssertExactPartition(b);
        Assert.Empty(b.Segments);
        Assert.Equal(600, b.UnattributedSeconds); // no evidence → no claim (rule 1)
    }
}
