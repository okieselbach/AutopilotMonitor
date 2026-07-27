using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One terminal session of a device inside its <see cref="DeviceHistory.Chain"/> — the
    /// compact ref the F2 journey grouping runs on (insights spec §F2). Only terminal sessions
    /// (Succeeded / Failed / Incomplete) become refs: a WhiteGlove <c>Pending</c> or an
    /// AwaitingUser/Stalled session is an OPEN session and must never appear as an attempt.
    /// </summary>
    public class DeviceSessionRef
    {
        public string SessionId { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }

        /// <summary>Terminal timestamp; feeds the 30-day journey-gap rule (fallback: StartedAt).</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary><see cref="SessionStatus"/> name ("Succeeded" / "Failed" / "Incomplete").</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>"v1" (Autopilot Classic/ESP) or "v2" (Windows Device Preparation).</summary>
        public string EnrollmentType { get; set; } = "v1";

        public bool IsPreProvisioned { get; set; }

        /// <summary>
        /// The session's authoritative <c>DurationSeconds</c> verbatim (WhiteGlove pause excluded
        /// by design) — F2 surfaces must never recompute CompletedAt − StartedAt, which is later
        /// in 25 % of terminal sessions. Null for Incomplete (deliberately stores no duration).
        /// </summary>
        public int? DurationSeconds { get; set; }

        /// <summary>An administrator flipped this session's terminal status via the portal — the chain flags it (truthfulness guard §F2).</summary>
        public bool AdminMarked { get; set; }
    }

    /// <summary>
    /// Per-device enrollment history for F2 (insights spec §F2) — one row per device key
    /// (TenantId, normalized serial), holding the compact chain of the device's terminal
    /// sessions (capped at the 20 most recent) plus derived journey counts. Written inline at
    /// every session-terminal transition (so the session-detail banner is fresh) and healed by
    /// the rolling maintenance sweep, which also drops refs of deleted sessions (tombstone-driven)
    /// and deletes the row when the chain empties. Junk serials (placeholder identities) never
    /// get a row. Persisted in <c>DeviceHistories</c>; wiped on tenant offboarding — deliberately
    /// NOT part of the per-session deletion manifest, because the row aggregates many sessions.
    /// </summary>
    public class DeviceHistory
    {
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Normalized device key component: trimmed + lower-cased serial (spec: trim + case-fold).</summary>
        public string SerialKey { get; set; } = string.Empty;

        /// <summary>Display serial as last reported (trimmed, original casing).</summary>
        public string SerialNumber { get; set; } = string.Empty;

        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;

        /// <summary>Terminal session refs ordered by StartedAt ascending, capped at the 20 most recent.</summary>
        public List<DeviceSessionRef> Chain { get; set; } = new List<DeviceSessionRef>();

        /// <summary>Attempt count of the LAST journey in the chain (open or completed) — the "Attempt N" the session banner shows.</summary>
        public int CurrentJourneyAttempts { get; set; }

        /// <summary>Journeys represented in the retained chain (the 20-cap can hide older journeys — this is a chain-scoped count, not a lifetime claim).</summary>
        public int JourneyCount { get; set; }

        /// <summary>Journey-grouping algorithm version (truthfulness rule 8: a definition change never silently mixes semantics).</summary>
        public int JourneyVersion { get; set; }

        public DateTime LastUpdated { get; set; }
    }
}
