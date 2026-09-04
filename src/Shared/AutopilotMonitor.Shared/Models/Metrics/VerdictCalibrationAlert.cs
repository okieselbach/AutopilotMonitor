using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>Kinds of verdict-calibration drift the radar detects (internal/docs/backend/verdict-calibration.md).</summary>
    public static class VerdictCalibrationAlertKinds
    {
        /// <summary>One verdict path's share of all sessions rose ≥2× over its 28-day baseline (Wilson-separated).</summary>
        public const string ShareRegression = "share_regression";

        /// <summary>The combined sweep:* + maxlife:* share (agent went silent, backend had to decide) rose ≥2× — an agent-liveness signal, not a classifier one.</summary>
        public const string SilenceShareRegression = "silence_share_regression";

        /// <summary>The pure fallthrough rule (r6) decides ≥20 % of classifier verdicts — the evidence the rules need is missing.</summary>
        public const string EvidenceGap = "evidence_gap";
    }

    /// <summary>
    /// One ACTIVE verdict-calibration alert episode (operator-only). Persisted as the
    /// <c>verdictcalibration|{kind}|{path}|{status}</c> keyspace of the notification tracker —
    /// the row IS the dedup (one ops event per episode) and the <c>alerts[]</c> payload of the
    /// calibration endpoint. Deleted when the signal re-arms (share back under 1.5× baseline,
    /// path stops occurring, evidence-gap share back under 15 %) or by the tracker's 30-day
    /// retention sweep. Numbers refresh on every radar pass; <see cref="FirstNotifiedAt"/> never moves.
    /// Dimension concentration is CORRELATION only — every consumer says so.
    /// </summary>
    public class VerdictCalibrationAlert
    {
        public string TenantId { get; set; } = string.Empty;

        /// <summary>One of <see cref="VerdictCalibrationAlertKinds"/>.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Verdict path, or the group label for group kinds ("sweep+maxlife", "r6").</summary>
        public string VerdictPath { get; set; } = string.Empty;

        /// <summary>Status the path produced, or "*" for group kinds.</summary>
        public string Status { get; set; } = string.Empty;

        public int WindowHitCount { get; set; }
        public int WindowSessionCount { get; set; }
        public int BaselineHitCount { get; set; }
        public int BaselineSessionCount { get; set; }
        public double WindowRatePct { get; set; }
        public double BaselineRatePct { get; set; }

        /// <summary>Window rate ÷ baseline rate. Null when the baseline rate is 0 or the kind is absolute (evidence gap).</summary>
        public double? Lift { get; set; }

        public string WindowStartDate { get; set; } = string.Empty;
        public string WindowEndDate { get; set; } = string.Empty;

        /// <summary>Dimension concentration captured when the alert FIRST fired; null = no clear concentration.</summary>
        public RuleRegressionDimension? Dimension { get; set; }

        public DateTime FirstNotifiedAt { get; set; }
        public DateTime LastEvaluatedAt { get; set; }
    }
}
