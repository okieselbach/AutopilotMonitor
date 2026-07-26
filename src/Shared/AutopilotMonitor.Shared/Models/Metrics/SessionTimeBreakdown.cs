using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Canonical segment keys for the F1 time-attribution partition (insights spec §F1).
    /// A segment may comprise multiple spans (e.g. identity_hello covers the AccountSetup
    /// span AND the FinalizingSetup/Hello-wait span; WhiteGlove sessions split every segment
    /// across two observation windows).
    /// </summary>
    public static class TimeAttributionSegments
    {
        /// <summary>Session start → first ESP apps-phase entry (Start/DevicePreparation/DeviceSetup phases).</summary>
        public const string DevicePrep = "device_prep";

        /// <summary>Device-scope ESP apps phase (EnrollmentPhase.AppsDevice spans).</summary>
        public const string EspApps = "esp_apps";

        /// <summary>Identity / sign-in / Windows Hello (AccountSetup + FinalizingSetup spans).</summary>
        public const string IdentityHello = "identity_hello";

        /// <summary>User-scope ESP apps phase (EnrollmentPhase.AppsUser spans), when present.</summary>
        public const string UserEsp = "user_esp";

        /// <summary>Last phase exit (desktop_arrived / Complete declaration) → terminal timestamp.</summary>
        public const string DesktopHandoff = "desktop_handoff";

        /// <summary>
        /// Explicit remainder — time inside the observation window(s) no observed signal
        /// accounts for. Reported, never redistributed (truthfulness rule 2: the partition
        /// is exact and never normalized to 100 %).
        /// </summary>
        public const string Unattributed = "unattributed";
    }

    /// <summary>
    /// Data-quality flags for a <see cref="SessionTimeBreakdown"/> (truthfulness rule 7:
    /// problems flag the record and exclude it from fleet aggregates with a disclosed count,
    /// rather than silently skewing them).
    /// </summary>
    [Flags]
    public enum TimeAttributionFlags
    {
        None = 0,

        /// <summary>At least one anchor/interval was dropped for going backward in time or falling outside the observation window.</summary>
        ClockSkewDropped = 1,

        /// <summary>The agent started late (<c>agent_late_start</c>): early phases are underobserved; excluded from fleet segment medians.</summary>
        PartialObservation = 2,

        /// <summary>No <c>esp_config_detected</c> tracking lists observed — per-app blocking membership is entirely unknown.</summary>
        BlockingSetUnknown = 4,

        /// <summary>The tracking lists hit the 50-per-category emission cap — positive evidence is incomplete.</summary>
        BlockingSetTruncated = 8,

        /// <summary>
        /// WhiteGlove window boundaries rest on fallback anchors (no <c>whiteglove_part1_complete</c>
        /// event, missing ResumedAt, or a derived part-1 length that contradicts the stored
        /// combined duration) — the two-window split is best-effort rather than event-anchored.
        /// </summary>
        WhiteGloveAnchorsIncomplete = 16
    }

    /// <summary>One contiguous attributed span inside an observation window.</summary>
    public class TimeAttributionSpan
    {
        /// <summary>Segment key — one of the <see cref="TimeAttributionSegments"/> constants (never "unattributed": the remainder is a scalar, not a span claim).</summary>
        public string SegmentKey { get; set; } = string.Empty;

        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }

        /// <summary>Whole seconds (floored). The sub-second dust lands in the unattributed remainder — never invented into a segment.</summary>
        public int Seconds { get; set; }
    }

    /// <summary>
    /// Install interval of one ESP-blocking app, measured from EVENT timestamps
    /// (first started/download event → last terminal event; source-data audit Q1 forbids the
    /// agent payload timing, which freezes at the first terminal state across IME retries).
    /// </summary>
    public class BlockingAppInterval
    {
        public string AppId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int Seconds { get; set; }
    }

    /// <summary>
    /// One observed reboot outage: the gap between the last pre-reboot event and the first
    /// post-reboot event, located via the <c>lastBootUtc</c> payload (audit Q7: the
    /// <c>system_reboot_detected</c> event itself is detection-time-stamped by the NEXT agent
    /// run and is never used as the reboot moment).
    /// </summary>
    public class RebootSpan
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int Seconds { get; set; }

        /// <summary>Segment the reboot started in (<see cref="TimeAttributionSegments"/> key, or "unattributed" when it began in an unattributed hole).</summary>
        public string SegmentKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-session enrollment time attribution (F1, insights spec) — an exact partition of the
    /// session's authoritative wall clock into named segments plus an explicit unattributed
    /// remainder. Invariant (unit-enforced): sum of all span seconds + <see cref="UnattributedSeconds"/>
    /// == <see cref="WallClockSeconds"/> == the session's <c>DurationSeconds</c> (which excludes
    /// the WhiteGlove pause by design — never CompletedAt − StartedAt, which diverges in 25 % of
    /// terminal sessions). Computed once at session-terminal processing by
    /// <c>TimeAttributionCalculator</c>; persisted in <c>SessionTimeBreakdowns</c> (F1 PR2).
    /// </summary>
    public class SessionTimeBreakdown
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Attribution algorithm version — a definition change bumps this so aggregates never silently mix semantics (truthfulness rule 8).</summary>
        public int AttributionVersion { get; set; }

        /// <summary>The session's authoritative DurationSeconds at compute time.</summary>
        public int WallClockSeconds { get; set; }

        /// <summary>Attributed spans, ordered by StartUtc. Multiple spans may share a segment key.</summary>
        public List<TimeAttributionSpan> Segments { get; set; } = new List<TimeAttributionSpan>();

        /// <summary>Exact remainder: <see cref="WallClockSeconds"/> − sum of span seconds.</summary>
        public int UnattributedSeconds { get; set; }

        /// <summary>Total observed reboot outage seconds. Cross-cutting annotation — reboots overlap the segments they occur in and are NOT part of the wall-clock partition.</summary>
        public int RebootSeconds { get; set; }

        public List<RebootSpan> RebootSpans { get; set; } = new List<RebootSpan>();

        /// <summary>
        /// Install intervals of ESP-blocking apps (positive-evidence join against the latest
        /// <c>esp_config_detected</c> lists), top 20 by duration. <see cref="BlockingAppCount"/>
        /// carries the uncapped count of matched blocking apps (including those without a
        /// measurable interval, e.g. unobserved start).
        /// </summary>
        public List<BlockingAppInterval> BlockingApps { get; set; } = new List<BlockingAppInterval>();

        /// <summary>Uncapped count of apps matched against the blocking set (see <see cref="BlockingApps"/>).</summary>
        public int BlockingAppCount { get; set; }

        /// <summary>
        /// Overlap-merged union of the blocking-app intervals clipped to the esp_apps spans —
        /// the critical-path occupancy. esp_apps total − occupancy = in-phase wait (provider
        /// stalls, settle, IME idle). Null when the blocking set is unknown (no lists observed):
        /// unknown, not zero.
        /// </summary>
        public int? EspAppsOccupancySeconds { get; set; }

        public TimeAttributionFlags QualityFlags { get; set; }

        /// <summary>Convenience rollup: total seconds per segment key across all spans (unattributed NOT included — read <see cref="UnattributedSeconds"/>).</summary>
        public Dictionary<string, int> GetSegmentTotals()
        {
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var span in Segments)
            {
                totals.TryGetValue(span.SegmentKey, out var current);
                totals[span.SegmentKey] = current + span.Seconds;
            }
            return totals;
        }
    }
}
