using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Machine-readable vocabulary for <see cref="SessionSummary.VerdictPath"/> — WHICH code path
    /// produced the session's current <see cref="SessionStatus"/>. Format is
    /// <c>origin:detail</c>, lower-case, stable: values are a persisted contract that the verdict
    /// calibration aggregate (docs/backend/verdict-calibration.md) counts over days and tenants, so
    /// entries are append-only — never rename or reuse one. Unlike <c>FailureSource</c> (written
    /// only on Failed) the path is stamped for every status write, so a sweep "Incomplete", a late
    /// agent upgrade and an admin override are all countable.
    /// </summary>
    public static class VerdictPaths
    {
        // ── Agent-declared (ingest of the agent's own terminal/phase events) ──────────────────
        public const string AgentComplete = "agent:complete";
        public const string AgentCompleteSoft = "agent:complete_soft";
        public const string AgentFailed = "agent:failed";
        public const string AgentEspFailureFallback = "agent:esp_failure_fallback";
        public const string AgentGatherComplete = "agent:gather_complete";
        public const string AgentWhiteGlovePending = "agent:whiteglove_pending";
        public const string AgentWhiteGloveResumed = "agent:whiteglove_resumed";
        public const string AgentStallProbe = "agent:stall_probe";
        public const string AgentStallHeal = "agent:stall_heal";

        // ── Backend-declared, non-classifier ─────────────────────────────────────────────────
        public const string IngestWhiteGloveAwaiting = "ingest:wg_awaiting";
        public const string SweepStalled = "sweep:stalled";
        public const string SweepWhiteGloveAwaiting = "sweep:wg_awaiting";
        public const string SweepSelfDeployingReconcile = "sweep:sd_reconcile";
        public const string RetroSelfDeployingReconcile = "retro:sd_reconcile";
        public const string RetroSuperseded = "retro:superseded";
        public const string RegisterSuperseded = "register:superseded";
        /// <summary>Status InProgress set by a fresh session registration.</summary>
        public const string RegisterNew = "register:new";
        /// <summary>WhiteGlove Part-2 re-registration flipped Pending to InProgress.</summary>
        public const string RegisterWhiteGloveResume = "register:whiteglove_resume";
        public const string ManualFailed = "manual:failed";
        public const string ManualSucceeded = "manual:succeeded";

        /// <summary>Read-side derivation could not attribute a pre-instrumentation row. Never written.</summary>
        public const string LegacyUnknown = "legacy:unknown";

        // ── Origins whose detail is a classifier rule id (see <see cref="ClassifierRules"/>) ──
        public const string OriginMaxLifetime = "maxlife";
        public const string OriginLateReconcile = "late";
        public const string OriginSweep = "sweep";
        public const string OriginRetro = "retro";
        public const string OriginRule = "rule";

        /// <summary><c>{origin}:{detail}</c> — e.g. <c>sweep:r5_incomplete</c>, <c>rule:ANALYZE-ESP-001</c>.</summary>
        public static string Compose(string origin, string detail)
        {
            if (string.IsNullOrWhiteSpace(origin)) throw new ArgumentException("origin required", nameof(origin));
            if (string.IsNullOrWhiteSpace(detail)) throw new ArgumentException("detail required", nameof(detail));
            return origin + ":" + detail;
        }

        /// <summary>The <c>origin</c> half of a path (<c>"sweep"</c> for <c>"sweep:r6"</c>); the whole string when there is no colon.</summary>
        public static string Origin(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var i = path.IndexOf(':');
            return i < 0 ? path : path.Substring(0, i);
        }

        /// <summary>
        /// True for a verdict produced by a silence/timeout classifier RULE (<c>sweep:r5_incomplete</c>,
        /// <c>maxlife:r1</c>, <c>late:r4</c>, <c>retro:r6</c>) — the rows whose calibration drives rule
        /// tuning. Non-rule verdicts from the same origins (<c>sweep:stalled</c>, <c>retro:superseded</c>)
        /// are excluded.
        /// </summary>
        public static bool IsClassifierPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var origin = Origin(path);
            if (origin != OriginSweep && origin != OriginMaxLifetime && origin != OriginLateReconcile && origin != OriginRetro)
                return false;
            var detail = path.Length > origin.Length + 1 ? path.Substring(origin.Length + 1) : string.Empty;
            return detail.Length >= 2 && detail[0] == 'r' && char.IsDigit(detail[1]);
        }
    }

    /// <summary>
    /// Rule ids of <c>EnrollmentTimeoutClassifier.ClassifyTimedOutSession</c> — the <c>detail</c> half
    /// of a classifier verdict path. One id per <c>return</c> so the calibration matrix attributes a
    /// verdict to the exact rule, including the grace-dependent forks (1b, 5).
    /// </summary>
    public static class ClassifierRules
    {
        public const string R1ExplicitFailure = "r1";
        public const string R1bWhiteGloveAwaiting = "r1b_awaiting";
        public const string R1bWhiteGloveSucceeded = "r1b_succeeded";
        public const string R1cSelfDeploying = "r1c";
        public const string R2AccountSetupComplete = "r2";
        public const string R3EmergencyBreak = "r3";
        public const string R4DesktopHello = "r4";
        public const string R5DeviceSetupAwaiting = "r5_awaiting";
        public const string R5DeviceSetupIncomplete = "r5_incomplete";
        public const string R6Fallthrough = "r6";
    }
}
