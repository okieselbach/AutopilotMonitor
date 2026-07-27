using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>One attempt-histogram bucket: how many completed journeys took exactly <see cref="Attempts"/> attempts.</summary>
    public class DeviceJourneyAttemptBucket
    {
        public int Attempts { get; set; }
        public int JourneyCount { get; set; }
    }

    /// <summary>
    /// Daily First-Time-Right rollup for one (tenant, date) — F2 PR4 (insights spec §F2).
    /// A journey counts on the StartedAt date of its COMPLETING success session (same
    /// StartedAt-date bucketing as every other daily aggregate; the rolling sweep re-buckets
    /// late-terminating sessions idempotently). Only journeys that ended with a terminal
    /// success are counted — open journeys (no success yet, incl. WhiteGlove waiting for its
    /// user session) and gap-abandoned journeys never enter numerator or denominator.
    /// Counts are additive across days, so a window rate is the sum of daily rows — no
    /// rolling-window row is needed (unlike the median-based time-attribution aggregates).
    /// Rows are written even below the ≥20-completed-journeys UI gate: the UI needs the n to
    /// say "insufficient data (n=3)" (truthfulness rule 4). Junk-serial exclusions are
    /// disclosed per day (rule 7). "global" mirror rows sum all tenants.
    /// </summary>
    public class DeviceJourneyDailyAggregate
    {
        /// <summary>Calendar day (UTC, "yyyy-MM-dd") the completing success sessions STARTED.</summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>Tenant GUID, or "global" for the cross-tenant row.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Journey-grouping algorithm version the counts were computed with (rule 8).</summary>
        public int JourneyVersion { get; set; }

        /// <summary>Journeys completed (first terminal success) on this date — the FTR denominator.</summary>
        public int CompletedJourneyCount { get; set; }

        /// <summary>Completed journeys with attempt count == 1 — the FTR numerator.</summary>
        public int FirstTimeRightCount { get; set; }

        /// <summary>Attempt distribution across the completed journeys, ordered by attempts ascending.</summary>
        public List<DeviceJourneyAttemptBucket> AttemptHistogram { get; set; } = new List<DeviceJourneyAttemptBucket>();

        /// <summary>Terminal sessions on this date excluded for junk/placeholder serials — disclosed, never silent (rule 7).</summary>
        public int ExcludedSessionCount { get; set; }

        public DateTime ComputedAt { get; set; }
    }
}
