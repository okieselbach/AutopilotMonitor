using System.Globalization;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Inputs for <see cref="TimeAttributionCalculator.Compute"/> — the session facts the terminal
/// writer persisted plus the session's full event stream in canonical order.
/// </summary>
public sealed class TimeAttributionInput
{
    public string TenantId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Session status string. Only "Succeeded" and "Failed" are computable (Incomplete deliberately carries no DurationSeconds).</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// The session's authoritative duration (second-sweep finding, insights spec §0.5): for
    /// WhiteGlove it is part 1 + part 2 WITHOUT the pause, and re-terminal stamps move
    /// CompletedAt — so this is the wall clock, never CompletedAt − StartedAt.
    /// </summary>
    public int? DurationSeconds { get; set; }

    public bool IsPreProvisioned { get; set; }

    /// <summary>WhiteGlove part-2 resume timestamp (session row), when present.</summary>
    public DateTime? ResumedAt { get; set; }

    /// <summary>Session events. Canonical order is Sequence ascending; the calculator re-sorts defensively.</summary>
    public IReadOnlyList<EnrollmentEvent> Events { get; set; } = Array.Empty<EnrollmentEvent>();
}

/// <summary>
/// Deterministic, I/O-free computation of a session's <see cref="SessionTimeBreakdown"/>
/// (F1, insights spec — AttributionVersion 1). Every number is traceable to observed events;
/// missing signals produce "unknown"/unattributed, never guesses (truthfulness rules 1/2).
///
/// Semantics pinned by the golden-fixture tests:
/// <list type="bullet">
/// <item><b>Wall clock = session DurationSeconds.</b> Observation windows are constructed to sum
/// to it exactly: non-WhiteGlove = [CompletedAt − Duration, CompletedAt]; WhiteGlove = two windows
/// in ONE session — [part1End − part1, part1End] and [ResumedAt, CompletedAt], where
/// part1 = Duration − part2 and part1End is anchored on <c>whiteglove_part1_complete</c>.
/// The pause between the windows is never attributed.</item>
/// <item><b>Segments from phase-declaration events only</b> (phase strategy: only declaration
/// events carry Phase != Unknown), bucketed Start/DevicePreparation/DeviceSetup → device_prep,
/// AppsDevice → esp_apps, AccountSetup + FinalizingSetup → identity_hello, AppsUser → user_esp,
/// Complete / <c>desktop_arrived</c> → desktop_handoff. A Failed-phase declaration ends
/// attribution (the tail is unattributed, not relabeled). Anchors that go backward in canonical
/// order are dropped (<see cref="TimeAttributionFlags.ClockSkewDropped"/>).</item>
/// <item><b>Exact-partition invariant:</b> span seconds are floored, the remainder —
/// unattributed holes plus sub-second dust — is reported as UnattributedSeconds, so
/// sum(spans) + unattributed == wall clock holds exactly and is never normalized.</item>
/// <item><b>Per-app intervals use event timestamps</b> (first started/download event → LAST
/// terminal event, covering IME retries) — never the agent payload timing fields, which freeze
/// at the first terminal transition (audit Q1).</item>
/// <item><b>Blocking membership is positive evidence</b> from the LATEST
/// <c>esp_config_detected</c> lists: listed ⇒ blocking; absent ⇒ unknown, never false (audit Q2).
/// No lists ⇒ <see cref="TimeAttributionFlags.BlockingSetUnknown"/> and occupancy null.</item>
/// <item><b>Reboot spans from event gaps</b> around the <c>lastBootUtc</c> payload — the
/// <c>system_reboot_detected</c> event is detection-time-stamped by the next run and never used
/// as the reboot moment (audit Q7). Reboots are a cross-cutting annotation, not a segment.</item>
/// </list>
/// </summary>
public static class TimeAttributionCalculator
{
    /// <summary>Bump on any semantic change so aggregates never mix definitions (truthfulness rule 8).</summary>
    // v2: PriorEnrollmentResidue flag — sessions whose phase anchors are distorted by
    // pre-enrollment state on disk are now flagged and excluded from fleet segment stats.
    public const int CurrentVersion = 2;

    /// <summary>
    /// Enrollment class for fleet aggregation — classes are NEVER mixed in one aggregate (a
    /// WhiteGlove or WDP flow has a structurally different time profile than user-driven ESP).
    /// Precedence: WhiteGlove (both parts live in one session row) → Windows Device Preparation
    /// (EnrollmentType "v2" — different phase machinery) → self-deploying profile → user-driven.
    /// </summary>
    public static string GetEnrollmentClass(SessionSummary session)
    {
        if (session.IsPreProvisioned) return "whiteglove";
        if (session.EnrollmentType == "v2") return "device_preparation";
        if (session.IsSelfDeployingProfile) return "self_deploying";
        return "user_driven";
    }

    /// <summary>Cap for the persisted per-app interval list (spec: top 20 by duration + uncapped count).</summary>
    internal const int MaxBlockingAppIntervals = 20;

    private readonly struct Window
    {
        public Window(DateTime start, DateTime end) { Start = start; End = end; }
        public DateTime Start { get; }
        public DateTime End { get; }
    }

    private sealed class Anchor
    {
        public DateTime Ts;
        /// <summary>Segment key, or null = attribution ends here (Failed-phase declaration).</summary>
        public string? Bucket;
    }

    private sealed class AppActivity
    {
        public string AppId = string.Empty;
        public string AppName = string.Empty;
        public DateTime? FirstStart;
        public DateTime? LastTerminal;
        /// <summary>Start of the currently open activity segment (start seen, terminal pending).</summary>
        public DateTime? OpenStart;
        /// <summary>Closed activity segments: one per fully-observed install pass/attempt (start → terminal).</summary>
        public List<(DateTime Start, DateTime End)> Segments = new();
    }

    /// <summary>
    /// Returns the breakdown, or null when the session has no computable wall clock
    /// (non-terminal, Incomplete — which deliberately stores no DurationSeconds — or a
    /// missing/non-positive duration).
    /// </summary>
    public static SessionTimeBreakdown? Compute(TimeAttributionInput input)
    {
        if (input.Status != "Succeeded" && input.Status != "Failed") return null;
        if (!input.CompletedAt.HasValue) return null;
        if (!input.DurationSeconds.HasValue || input.DurationSeconds.Value <= 0) return null;

        var completedAt = input.CompletedAt.Value;
        var wallClock = input.DurationSeconds.Value;
        var flags = TimeAttributionFlags.None;

        // Canonical order (Sequence asc) — the authoritative event order per repo convention.
        var events = input.Events.OrderBy(e => e.Sequence).ToList();

        var windows = BuildWindows(input, completedAt, wallClock, events, ref flags);

        var anchors = BuildAnchors(events, ref flags);
        var spans = BuildSegmentSpans(windows, anchors);

        var attributedSeconds = spans.Sum(s => s.Seconds);
        var unattributed = wallClock - attributedSeconds;

        // Blocking set: latest esp_config_detected emission that carries lists (positive
        // evidence is never erased by a later probe failure; lists grow progressively).
        EspBlockingSets? blockingSets = null;
        for (var i = events.Count - 1; i >= 0 && blockingSets == null; i--)
        {
            if (events[i].EventType == Constants.EventTypes.EspConfigDetected)
                blockingSets = EspBlockingSets.FromEventData(events[i].Data);
        }
        if (blockingSets == null || blockingSets.ListedCount == 0)
            flags |= TimeAttributionFlags.BlockingSetUnknown;
        else if (blockingSets.IsTruncated)
            flags |= TimeAttributionFlags.BlockingSetTruncated;

        var (blockingApps, blockingAppCount, blockingAppSegments) = BuildBlockingAppIntervals(events, blockingSets, windows, ref flags);

        int? occupancy = null;
        if (blockingSets != null && blockingSets.ListedCount > 0)
        {
            var espAppsSpans = spans.Where(s => s.SegmentKey == TimeAttributionSegments.EspApps).ToList();
            // Occupancy merges the per-pass SEGMENTS, not the per-app hulls — a hull spans
            // the idle gap between install passes, which is not occupied by this app.
            occupancy = ComputeOccupancySeconds(blockingAppSegments, espAppsSpans);
        }

        var rebootSpans = BuildRebootSpans(events, windows, spans);

        if (events.Any(e => e.EventType == Constants.EventTypes.AgentLateStart))
            flags |= TimeAttributionFlags.PartialObservation;

        // Prior-enrollment residue (session f475e697): an IME log predating this enrollment
        // means the disk was not wiped — pre-installed apps complete as instant detections and
        // pull the phase anchors ahead of the real ESP page, so the segment durations lie.
        // historic_ime_replay_detected is the ONLY trigger on purpose: registry_app_baseline
        // with successes is by-design normal for WDP (DPP Batch-1 apps install before the
        // agent exists) and would starve that class's aggregates (see DurationCriticalFlags).
        if (events.Any(e => e.EventType == Constants.EventTypes.HistoricImeReplayDetected))
            flags |= TimeAttributionFlags.PriorEnrollmentResidue;

        return new SessionTimeBreakdown
        {
            TenantId = input.TenantId,
            SessionId = input.SessionId,
            AttributionVersion = CurrentVersion,
            WallClockSeconds = wallClock,
            Segments = spans,
            UnattributedSeconds = unattributed,
            RebootSeconds = rebootSpans.Sum(r => r.Seconds),
            RebootSpans = rebootSpans,
            BlockingApps = blockingApps
                .OrderByDescending(a => a.Seconds)
                .ThenBy(a => a.AppName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxBlockingAppIntervals)
                .ToList(),
            BlockingAppCount = blockingAppCount,
            EspAppsOccupancySeconds = occupancy,
            QualityFlags = flags,
        };
    }

    // ── observation windows ─────────────────────────────────────────────────

    /// <summary>
    /// Windows are END-anchored on the authoritative timestamps (CompletedAt; part1End for WG
    /// part 1) with the start derived so window lengths sum EXACTLY to DurationSeconds — the
    /// invariant is against the stored duration, not against CompletedAt − StartedAt.
    /// </summary>
    private static List<Window> BuildWindows(
        TimeAttributionInput input, DateTime completedAt, int wallClock,
        List<EnrollmentEvent> events, ref TimeAttributionFlags flags)
    {
        if (input.IsPreProvisioned)
        {
            if (input.ResumedAt.HasValue && input.ResumedAt.Value < completedAt)
            {
                var resumedAt = input.ResumedAt.Value;
                // Same truncation the terminal writer used for part2, so part1 comes out as the
                // exact stored part-1 duration.
                var part2 = (int)(completedAt - resumedAt).TotalSeconds;
                var part1 = wallClock - part2;
                if (part1 > 0)
                {
                    var part1End = ResolvePart1End(events, resumedAt, ref flags);
                    if (part1End.HasValue)
                    {
                        var w1Start = part1End.Value.AddSeconds(-part1);
                        return new List<Window>
                        {
                            new Window(w1Start, part1End.Value),
                            new Window(resumedAt, completedAt),
                        };
                    }
                }
                // part1 <= 0 (stored duration contradicts the resume stamp) or no usable part-1
                // anchor: fall through to a single derived window rather than inventing a split.
            }
            // WhiteGlove without a resolvable two-window split — the partition is still exact,
            // but the WG semantics (pause exclusion boundaries) rest on no anchor.
            flags |= TimeAttributionFlags.WhiteGloveAnchorsIncomplete;
        }

        return new List<Window> { new Window(completedAt.AddSeconds(-wallClock), completedAt) };
    }

    /// <summary>
    /// Part-1 end anchor: the <c>whiteglove_part1_complete</c> event; fallback: the last event
    /// at/before ResumedAt (flagged — the boundary is then observational, not declarative).
    /// An anchor after ResumedAt is clamped to ResumedAt (windows must not overlap).
    /// </summary>
    private static DateTime? ResolvePart1End(
        List<EnrollmentEvent> events, DateTime resumedAt, ref TimeAttributionFlags flags)
    {
        DateTime? part1End = null;
        foreach (var evt in events)
        {
            if (evt.EventType == Constants.EventTypes.WhiteGlovePart1Complete)
                part1End = evt.Timestamp;
        }

        if (!part1End.HasValue)
        {
            flags |= TimeAttributionFlags.WhiteGloveAnchorsIncomplete;
            foreach (var evt in events)
            {
                if (evt.Timestamp <= resumedAt &&
                    (!part1End.HasValue || evt.Timestamp > part1End.Value))
                {
                    part1End = evt.Timestamp;
                }
            }
        }
        else if (part1End.Value > resumedAt)
        {
            flags |= TimeAttributionFlags.WhiteGloveAnchorsIncomplete;
            part1End = resumedAt;
        }

        return part1End;
    }

    // ── segment spans ───────────────────────────────────────────────────────

    private static string? MapPhaseToSegment(EnrollmentPhase phase) => phase switch
    {
        EnrollmentPhase.Start => TimeAttributionSegments.DevicePrep,
        EnrollmentPhase.DevicePreparation => TimeAttributionSegments.DevicePrep,
        EnrollmentPhase.DeviceSetup => TimeAttributionSegments.DevicePrep,
        EnrollmentPhase.AppsDevice => TimeAttributionSegments.EspApps,
        EnrollmentPhase.AccountSetup => TimeAttributionSegments.IdentityHello,
        EnrollmentPhase.AppsUser => TimeAttributionSegments.UserEsp,
        EnrollmentPhase.FinalizingSetup => TimeAttributionSegments.IdentityHello,
        EnrollmentPhase.Complete => TimeAttributionSegments.DesktopHandoff,
        // Failed: attribution ends — the post-failure tail stays unattributed rather than
        // being relabeled into a segment no phase ever declared.
        EnrollmentPhase.Failed => null,
        _ => null,
    };

    /// <summary>
    /// Anchor list in canonical order: phase-declaration events (Phase != Unknown) plus
    /// <c>desktop_arrived</c> (Phase=Unknown by convention, but the spec-designated handoff
    /// anchor). Consecutive same-bucket anchors collapse; anchors whose timestamp goes
    /// backward vs. the previously accepted anchor are dropped with ClockSkewDropped.
    /// </summary>
    private static List<Anchor> BuildAnchors(List<EnrollmentEvent> events, ref TimeAttributionFlags flags)
    {
        var anchors = new List<Anchor>();
        foreach (var evt in events)
        {
            string? bucket;
            var isDeclaration = false;
            if (evt.EventType == Constants.EventTypes.DesktopArrived)
            {
                bucket = TimeAttributionSegments.DesktopHandoff;
                isDeclaration = true;
            }
            else if (evt.Phase != EnrollmentPhase.Unknown)
            {
                bucket = MapPhaseToSegment(evt.Phase);
                isDeclaration = true;
            }
            else
            {
                bucket = null;
            }

            if (!isDeclaration) continue;

            if (anchors.Count > 0)
            {
                var prev = anchors[anchors.Count - 1];
                if (evt.Timestamp < prev.Ts)
                {
                    flags |= TimeAttributionFlags.ClockSkewDropped;
                    continue;
                }
                if (prev.Bucket == bucket)
                    continue; // same-phase re-declaration (re-registration, replay) — no boundary
            }

            anchors.Add(new Anchor { Ts = evt.Timestamp, Bucket = bucket });
        }
        return anchors;
    }

    /// <summary>
    /// Slices each window at the in-window anchors and buckets every slice by the phase context
    /// active at its start. Context carries INTO window 0 from anchors at/before its start, but
    /// never across the WhiteGlove pause into window 1+ (the resume gap makes no phase claim).
    /// The leading gap of window 0 with no context is device_prep only when the first observed
    /// anchor is still pre/at apps entry (the spec defines device_prep as session start → first
    /// apps-phase entry); otherwise it stays unattributed.
    /// </summary>
    private static List<TimeAttributionSpan> BuildSegmentSpans(List<Window> windows, List<Anchor> anchors)
    {
        var spans = new List<TimeAttributionSpan>();

        for (var w = 0; w < windows.Count; w++)
        {
            var window = windows[w];

            string? context = null;
            if (w == 0)
            {
                foreach (var a in anchors)
                {
                    if (a.Ts <= window.Start) context = a.Bucket;
                    else break;
                }
            }

            var inWindow = anchors.Where(a => a.Ts > window.Start && a.Ts < window.End).ToList();

            if (w == 0 && context == null)
            {
                var first = inWindow.FirstOrDefault();
                if (first != null &&
                    (first.Bucket == TimeAttributionSegments.DevicePrep ||
                     first.Bucket == TimeAttributionSegments.EspApps))
                {
                    context = TimeAttributionSegments.DevicePrep;
                }
            }

            var cursor = window.Start;
            foreach (var anchor in inWindow)
            {
                AddSpan(spans, context, cursor, anchor.Ts);
                cursor = anchor.Ts;
                context = anchor.Bucket;
            }
            AddSpan(spans, context, cursor, window.End);
        }

        return spans;
    }

    private static void AddSpan(List<TimeAttributionSpan> spans, string? bucket, DateTime start, DateTime end)
    {
        if (bucket == null || end <= start) return;
        var seconds = (int)Math.Floor((end - start).TotalSeconds);
        if (seconds <= 0) return; // sub-second slivers land in the unattributed remainder
        spans.Add(new TimeAttributionSpan { SegmentKey = bucket, StartUtc = start, EndUtc = end, Seconds = seconds });
    }

    // ── blocking-app intervals + occupancy ──────────────────────────────────

    private static (List<BlockingAppInterval> Intervals, int MatchedCount, List<BlockingAppInterval> OccupancySegments) BuildBlockingAppIntervals(
        List<EnrollmentEvent> events, EspBlockingSets? sets, List<Window> windows, ref TimeAttributionFlags flags)
    {
        var intervals = new List<BlockingAppInterval>();
        if (sets == null || sets.ListedCount == 0)
            return (intervals, 0, intervals);

        // Fold per-app activity from EVENT timestamps (never payload timing — audit Q1).
        var apps = new Dictionary<string, AppActivity>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
        {
            var isStart = evt.EventType == Constants.EventTypes.AppInstallStart ||
                          evt.EventType == Constants.EventTypes.AppDownloadStarted;
            var isTerminal = evt.EventType == Constants.EventTypes.AppInstallComplete ||
                             evt.EventType == Constants.EventTypes.AppInstallFailed ||
                             evt.EventType == Constants.EventTypes.AppInstallSkipped;
            if (!isStart && !isTerminal) continue;
            if (evt.Data == null || !evt.Data.TryGetValue("appId", out var appIdObj)) continue;

            var appId = appIdObj?.ToString()?.Trim();
            if (string.IsNullOrEmpty(appId)) continue;

            if (!apps.TryGetValue(appId!, out var activity))
            {
                activity = new AppActivity { AppId = appId! };
                apps[appId!] = activity;
            }
            if (string.IsNullOrEmpty(activity.AppName) &&
                evt.Data.TryGetValue("appName", out var nameObj))
            {
                activity.AppName = nameObj?.ToString()?.Trim() ?? string.Empty;
            }

            if (isStart)
            {
                if (!activity.FirstStart.HasValue || evt.Timestamp < activity.FirstStart.Value)
                    activity.FirstStart = evt.Timestamp;
                // A download start followed by an install start is ONE active window —
                // the earliest open start survives until a terminal closes the segment.
                activity.OpenStart ??= evt.Timestamp;
            }
            if (isTerminal)
            {
                if (!activity.LastTerminal.HasValue || evt.Timestamp > activity.LastTerminal.Value)
                    activity.LastTerminal = evt.Timestamp;
                // Segment pairing (2026-08 attempt-duration change): each fully-observed
                // pass/attempt (start → terminal, sequence-ordered) is its own activity
                // segment. The IME processes the app list in multiple passes; the idle gap
                // between passes belongs to the apps actually running then, not to this one.
                // A failed attempt still counts as active time — the retry opens a new
                // segment, so the old "LAST terminal wins" retry intent is preserved.
                if (activity.OpenStart.HasValue)
                {
                    if (evt.Timestamp >= activity.OpenStart.Value)
                        activity.Segments.Add((activity.OpenStart.Value, evt.Timestamp));
                    else
                        flags |= TimeAttributionFlags.ClockSkewDropped;
                    activity.OpenStart = null;
                }
                // Terminal without an observed start: no segment claim (attach window,
                // audit §0.5) — same rule as before.
            }
        }

        var matched = 0;
        var occupancySegments = new List<BlockingAppInterval>();
        foreach (var activity in apps.Values)
        {
            if (!sets.Contains(activity.AppId)) continue; // absent ⇒ unknown, never "not blocking"
            matched++;

            if (activity.Segments.Count == 0)
                continue; // no fully-observed attempt → no interval claim (attach window, audit §0.5)

            // Clamp each segment; the persisted per-app interval is the hull of the clamped
            // segments with Seconds = the SUM of in-window active time (not the hull span —
            // the inter-pass idle gap is not this app's time).
            var total = 0;
            DateTime? hullStart = null, hullEnd = null;
            foreach (var segment in activity.Segments)
            {
                var clamped = ClampToWindows(segment.Start, segment.End, windows);
                if (!clamped.HasValue) continue;
                total += clamped.Value.Seconds;
                if (hullStart == null || clamped.Value.Start < hullStart.Value) hullStart = clamped.Value.Start;
                if (hullEnd == null || clamped.Value.End > hullEnd.Value) hullEnd = clamped.Value.End;
                occupancySegments.Add(new BlockingAppInterval
                {
                    AppId = activity.AppId,
                    AppName = activity.AppName,
                    StartUtc = clamped.Value.Start,
                    EndUtc = clamped.Value.End,
                    Seconds = clamped.Value.Seconds,
                });
            }
            if (hullStart == null || hullEnd == null)
            {
                // No segment overlapped any window. Outside the hull entirely →
                // pathological timestamps (flagged, as before). Wholly inside the
                // WhiteGlove pause → no in-window observation, nothing wrong with clocks.
                if (activity.LastTerminal!.Value < windows[0].Start ||
                    activity.FirstStart!.Value > windows[windows.Count - 1].End)
                {
                    flags |= TimeAttributionFlags.ClockSkewDropped;
                }
                continue;
            }

            intervals.Add(new BlockingAppInterval
            {
                AppId = activity.AppId,
                AppName = activity.AppName,
                StartUtc = hullStart.Value,
                EndUtc = hullEnd.Value,
                Seconds = total,
            });
        }

        return (intervals, matched, occupancySegments);
    }

    /// <summary>
    /// Clips [start, end] to the observation windows. Start/End are the clamped chronological
    /// endpoints; <c>Seconds</c> counts ONLY time inside the windows — the WhiteGlove pause
    /// between part 1 and part 2 lies inside the hull but is excluded, matching the session's
    /// own <c>DurationSeconds</c> semantics (pause excluded by design). Null when the interval
    /// overlaps no window at all: either it lies outside the hull entirely (pathological
    /// timestamps) or wholly inside the pause (no in-window observation).
    /// </summary>
    private static (DateTime Start, DateTime End, int Seconds)? ClampToWindows(DateTime start, DateTime end, List<Window> windows)
    {
        DateTime? s = null, e = null;
        double total = 0;
        foreach (var window in windows)
        {
            var ws = start < window.Start ? window.Start : start;
            var we = end > window.End ? window.End : end;
            if (we < ws) continue;
            if (s == null || ws < s.Value) s = ws;
            if (e == null || we > e.Value) e = we;
            total += (we - ws).TotalSeconds;
        }
        if (s == null || e == null) return null;
        return (s.Value, e.Value, (int)Math.Floor(total));
    }

    /// <summary>
    /// Critical-path occupancy: overlap-merged union of the blocking intervals, intersected
    /// with the esp_apps spans. Whole-second floor on the total (consistent with span math).
    /// </summary>
    internal static int ComputeOccupancySeconds(
        List<BlockingAppInterval> intervals, List<TimeAttributionSpan> espAppsSpans)
    {
        if (intervals.Count == 0 || espAppsSpans.Count == 0) return 0;

        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var iv in intervals.OrderBy(i => i.StartUtc))
        {
            if (merged.Count > 0 && iv.StartUtc <= merged[merged.Count - 1].End)
            {
                var last = merged[merged.Count - 1];
                if (iv.EndUtc > last.End)
                    merged[merged.Count - 1] = (last.Start, iv.EndUtc);
            }
            else
            {
                merged.Add((iv.StartUtc, iv.EndUtc));
            }
        }

        double total = 0;
        foreach (var span in espAppsSpans)
        {
            foreach (var (ms, me) in merged)
            {
                var s = ms > span.StartUtc ? ms : span.StartUtc;
                var e = me < span.EndUtc ? me : span.EndUtc;
                if (e > s) total += (e - s).TotalSeconds;
            }
        }
        return (int)Math.Floor(total);
    }

    // ── what-if bound (fleet aggregation, F1 PR2) ───────────────────────────

    /// <summary>
    /// Upper-bound saving from removing app <paramref name="appId"/> from the ESP blocking set
    /// of ONE session: the critical-path end (latest interval end) recomputed without the app's
    /// interval — <c>max(0, cpEnd − cpEndWithoutX)</c> per the spec. When the app is the only
    /// measured blocking interval, the fallback baseline is its own start (nothing else was
    /// observed holding the path). An upper bound BY CONSTRUCTION — removing an app cannot slow
    /// anything down, but hidden serialization may reduce real savings — so consumers must
    /// phrase it as "up to", never as a promise (truthfulness rule 3). Returns 0 when the app
    /// has no measured interval or does not end last.
    /// </summary>
    public static int WhatIfSavingSeconds(IReadOnlyList<BlockingAppInterval> intervals, string appId)
    {
        BlockingAppInterval? target = null;
        DateTime? cpEnd = null, cpEndWithout = null;

        foreach (var interval in intervals)
        {
            if (!cpEnd.HasValue || interval.EndUtc > cpEnd.Value)
                cpEnd = interval.EndUtc;

            if (string.Equals(interval.AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                target = interval; // one interval per app per session by construction
            }
            else if (!cpEndWithout.HasValue || interval.EndUtc > cpEndWithout.Value)
            {
                cpEndWithout = interval.EndUtc;
            }
        }

        if (target == null || !cpEnd.HasValue) return 0;

        // Spec formula verbatim: cpEnd − cpEndWithoutX. When X's removal exposes an earlier
        // path end, the idle gap before X's start counts too — the ESP was waiting for X to
        // START as much as to finish. Negative/zero → another app ends at or after X: no claim.
        var baseline = cpEndWithout ?? target.StartUtc;
        var saving = (cpEnd.Value - baseline).TotalSeconds;
        return saving > 0 ? (int)Math.Floor(saving) : 0;
    }

    // ── reboot spans ────────────────────────────────────────────────────────

    /// <summary>
    /// One span per distinct observed boot: the event-stream gap bracketing <c>lastBootUtc</c>
    /// (last event before the boot → first event at/after it). Fallback without a usable
    /// payload: previous event → the detection event itself (which is genuinely post-boot —
    /// usable as an observation bound, never as the boot moment). Spans are clipped to the
    /// observation hull, so a WhiteGlove-pause reboot contributes nothing.
    /// </summary>
    private static List<RebootSpan> BuildRebootSpans(
        List<EnrollmentEvent> events, List<Window> windows, List<TimeAttributionSpan> spans)
    {
        var result = new List<RebootSpan>();
        var seenBoots = new HashSet<DateTime>();

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            if (evt.EventType != Constants.EventTypes.SystemRebootDetected) continue;

            DateTime? lastBoot = null;
            if (evt.Data != null && evt.Data.TryGetValue("lastBootUtc", out var lastBootObj))
            {
                var raw = lastBootObj?.ToString();
                if (!string.IsNullOrEmpty(raw) &&
                    DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    lastBoot = parsed;
                }
            }

            DateTime? gapStart = null, gapEnd = null;
            if (lastBoot.HasValue)
            {
                if (!seenBoots.Add(lastBoot.Value)) continue; // duplicate detection of the same boot
                foreach (var e in events)
                {
                    if (e.Timestamp < lastBoot.Value)
                    {
                        if (!gapStart.HasValue || e.Timestamp > gapStart.Value) gapStart = e.Timestamp;
                    }
                    else if (!gapEnd.HasValue || e.Timestamp < gapEnd.Value)
                    {
                        gapEnd = e.Timestamp;
                    }
                }
            }
            else if (i > 0)
            {
                gapStart = events[i - 1].Timestamp;
                gapEnd = evt.Timestamp;
            }

            if (!gapStart.HasValue || !gapEnd.HasValue || gapEnd.Value <= gapStart.Value) continue;

            var clamped = ClampToWindows(gapStart.Value, gapEnd.Value, windows);
            if (!clamped.HasValue) continue;
            // In-window seconds — a reboot gap bracketing the WhiteGlove pause contributes
            // only its in-window flanks, never the pause itself.
            var seconds = clamped.Value.Seconds;
            if (seconds <= 0) continue;

            result.Add(new RebootSpan
            {
                StartUtc = clamped.Value.Start,
                EndUtc = clamped.Value.End,
                Seconds = seconds,
                SegmentKey = SegmentAt(spans, clamped.Value.Start),
            });
        }

        return result;
    }

    private static string SegmentAt(List<TimeAttributionSpan> spans, DateTime ts)
    {
        foreach (var span in spans)
        {
            if (ts >= span.StartUtc && ts < span.EndUtc)
                return span.SegmentKey;
        }
        return TimeAttributionSegments.Unattributed;
    }
}
