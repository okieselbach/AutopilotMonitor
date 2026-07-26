using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Distribution of one attribution segment across the clean sessions of an aggregate row.
    /// Values are whole seconds; a session that has no span of this segment contributes 0 —
    /// the distribution answers "time spent in this segment per enrollment of this class",
    /// not "per enrollment that happened to enter it".
    /// </summary>
    public class TimeAttributionSegmentStat
    {
        /// <summary>Segment key (<see cref="TimeAttributionSegments"/>), including "unattributed" — the stack must sum to the wall clock (truthfulness rule 2).</summary>
        public string SegmentKey { get; set; } = string.Empty;

        public int MedianSeconds { get; set; }
        public int P75Seconds { get; set; }
        public int P90Seconds { get; set; }
    }

    /// <summary>
    /// Per-app rollup of ESP-blocking install intervals across the clean sessions of an
    /// aggregate row. The what-if numbers are the per-session critical-path savings from
    /// removing this app (recomputed union end without it) — an upper BOUND by construction:
    /// UI copy must say "up to", never "you will save" (truthfulness rule 3).
    /// </summary>
    public class TimeAttributionBlockingAppStat
    {
        public string AppId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;

        /// <summary>Clean sessions with a measured interval for this app (row gate: ≥5).</summary>
        public int SessionCount { get; set; }

        public int MedianSeconds { get; set; }
        public int P75Seconds { get; set; }

        public int MedianSavingSeconds { get; set; }
        public int P75SavingSeconds { get; set; }
    }

    /// <summary>
    /// Daily fleet rollup of session time attribution for one (tenant, enrollment class, date) —
    /// F1 PR2 (insights spec §F1 "Data &amp; compute changes"). Enrollment classes are never
    /// mixed (a WhiteGlove flow has a structurally different time profile than user-driven).
    /// Only breakdowns with <see cref="TimeAttributionFlags.None"/> enter the statistics;
    /// flagged sessions are excluded WITH a disclosed count (truthfulness rule 7). Rows are
    /// written even below the ≥20-session UI gate — the UI needs the n to say
    /// "insufficient data (n=3)" instead of silently rendering a small-n median (rule 4).
    /// Recomputed idempotently by the rolling 30-day maintenance sweep.
    /// </summary>
    public class TimeAttributionDailyAggregate
    {
        /// <summary>Calendar day (UTC) the sessions STARTED, "yyyy-MM-dd" — same bucketing as the usage snapshots.</summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>Tenant GUID, or "global" for the cross-tenant row.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Enrollment class: "user_driven", "whiteglove", "self_deploying" or "device_preparation" (WDP v2).</summary>
        public string EnrollmentClass { get; set; } = string.Empty;

        /// <summary>Attribution algorithm version the underlying breakdowns were computed with (rule 8: never mix definitions).</summary>
        public int AttributionVersion { get; set; }

        /// <summary>Breakdowns with QualityFlags == None that formed the statistics.</summary>
        public int CleanSessionCount { get; set; }

        /// <summary>Flagged breakdowns excluded from the statistics (disclosed, never silent).</summary>
        public int FlaggedExcludedCount { get; set; }

        /// <summary>Terminal sessions of this bucket without a computable breakdown (e.g. events aged out before backfill).</summary>
        public int MissingBreakdownCount { get; set; }

        public List<TimeAttributionSegmentStat> SegmentStats { get; set; } = new List<TimeAttributionSegmentStat>();

        /// <summary>Top blocking apps by median interval, gated at ≥5 sessions per app, capped at 20.</summary>
        public List<TimeAttributionBlockingAppStat> TopBlockingApps { get; set; } = new List<TimeAttributionBlockingAppStat>();

        public DateTime ComputedAt { get; set; }
    }
}
