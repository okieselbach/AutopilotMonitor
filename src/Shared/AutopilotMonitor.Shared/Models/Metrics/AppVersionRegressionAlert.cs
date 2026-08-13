using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One ACTIVE app-version duration regression: an app whose newest version's median
    /// install duration rose ≥2× (and ≥5 minutes absolute) over the previous version's
    /// median, both sides with enough measured installs. Persisted as the
    /// <c>appversionregression|{app}|{version}</c> keyspace of the notification tracker
    /// table — the row IS the dedup (one bell per episode), and the
    /// <c>versionRegressions[]</c> payload on the app-analytics response. Deleted when the
    /// episode re-arms (median falls back under 1.5× or the version drains out of the
    /// horizon) or by the tracker's 30-day retention sweep. Numbers refresh on every radar
    /// pass while the episode stays active; <see cref="FirstNotifiedAt"/> never moves.
    /// </summary>
    public class AppVersionRegressionAlert
    {
        public string TenantId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;

        /// <summary>The regressed (newer) version — episode key together with the app.</summary>
        public string CurrentVersion { get; set; } = string.Empty;

        /// <summary>The comparison version: latest first-seen strictly before the current version's first-seen.</summary>
        public string PreviousVersion { get; set; } = string.Empty;

        public int CurrentMedianSeconds { get; set; }
        public int PreviousMedianSeconds { get; set; }

        /// <summary>Measured installs backing each median (gate: both ≥10).</summary>
        public int CurrentMeasuredCount { get; set; }
        public int PreviousMeasuredCount { get; set; }

        /// <summary>Current median ÷ previous median (gate: ≥2.0; previous median is never 0 — measured durations are ≥1s).</summary>
        public double Lift { get; set; }

        /// <summary>When the episode first fired (bell + ops event moment). Never moves on refresh; drives the 30d retention re-arm.</summary>
        public DateTime FirstNotifiedAt { get; set; }

        /// <summary>Last radar pass that re-confirmed/refreshed this episode.</summary>
        public DateTime LastEvaluatedAt { get; set; }
    }
}
