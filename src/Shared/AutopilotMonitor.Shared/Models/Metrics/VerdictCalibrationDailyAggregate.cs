using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One (verdict path × status) bucket of a daily verdict-calibration row. <c>Count</c> is the
    /// sessions whose CURRENT status was produced by this path; the <c>Overridden*</c> counters
    /// are sessions that once carried this path and were then overridden (their
    /// <c>PriorVerdictPath</c> names this bucket) — the correction stream. Re-enrollment is the
    /// delayed proxy signal: of the sessions old enough to judge (<see cref="Eligible7d"/>), how
    /// many saw the same device register another terminal session within 7 days.
    /// </summary>
    public class VerdictCalibrationBucket
    {
        /// <summary><see cref="VerdictPaths"/> value, or the read-side derivation for pre-instrumentation rows.</summary>
        public string VerdictPath { get; set; } = string.Empty;

        /// <summary><see cref="SessionStatus"/> name the path produced.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Sessions currently on this path/status.</summary>
        public int Count { get; set; }

        /// <summary>Of <see cref="Count"/>: rows without a stamped VerdictPath, attributed by <c>VerdictPathDerivation</c> (weaker evidence).</summary>
        public int DerivedCount { get; set; }

        /// <summary>Of <see cref="Count"/>: sessions whose terminal moment lies ≥ 7 days before the compute time — the re-enrollment denominator.</summary>
        public int Eligible7d { get; set; }

        /// <summary>Of <see cref="Eligible7d"/>: the device registered another terminal session within 7 days of this one's end.</summary>
        public int ReEnrolled7d { get; set; }

        /// <summary>Sessions that carried this path and were then flipped by an administrator (MarkSessionSucceeded/Failed).</summary>
        public int OverriddenByAdmin { get; set; }

        /// <summary>Sessions that carried this path and were then upgraded to Succeeded by a late agent completion report.</summary>
        public int OverriddenByLateCompletion { get; set; }

        /// <summary>Sessions that carried this path and were overridden by any other writer (retro-reclassification, grace expiry, supersede).</summary>
        public int OverriddenOther { get; set; }
    }

    /// <summary>
    /// Daily verdict-calibration rollup for one (tenant, date) — the operator's thermometer for the
    /// rule classifier (docs/backend/verdict-calibration.md). A session buckets on its StartedAt
    /// date (same convention as every other daily aggregate; the rolling sweep re-buckets
    /// late-terminating sessions idempotently). Every status is counted — non-terminal rows are
    /// part of the picture (a growing Stalled share is a signal) — and
    /// <see cref="TerminalSessionCount"/> carries the share denominator for terminal paths.
    /// Counts are additive across days; a window is the sum of daily rows. "global" mirror rows
    /// sum all tenants. Regenerable from Sessions + DeviceHistories at any time.
    /// </summary>
    public class VerdictCalibrationDailyAggregate
    {
        /// <summary>Bucketing/derivation algorithm version — a definition change never silently mixes semantics.</summary>
        public const int CurrentVersion = 1;

        /// <summary>Calendar day (UTC, "yyyy-MM-dd") the sessions STARTED.</summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>Tenant GUID, or "global" for the cross-tenant row.</summary>
        public string TenantId { get; set; } = string.Empty;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>All sessions started on this date (any status).</summary>
        public int SessionCount { get; set; }

        /// <summary>Sessions whose status is Succeeded / Failed / Incomplete — the share denominator for terminal paths.</summary>
        public int TerminalSessionCount { get; set; }

        /// <summary>Ordered by VerdictPath, then Status (ordinal) for deterministic output.</summary>
        public List<VerdictCalibrationBucket> Buckets { get; set; } = new List<VerdictCalibrationBucket>();

        public DateTime ComputedAt { get; set; }
    }
}
