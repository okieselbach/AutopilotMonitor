using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// One drift finding of the verdict-calibration radar (pure output; the maintenance partial
/// turns it into a tracker episode + ops event).
/// </summary>
public class VerdictCalibrationFinding
{
    public string Kind { get; set; } = string.Empty;
    public string VerdictPath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int WindowHitCount { get; set; }
    public int WindowSessionCount { get; set; }
    public int BaselineHitCount { get; set; }
    public int BaselineSessionCount { get; set; }
    public double WindowRatePct { get; set; }
    public double BaselineRatePct { get; set; }
    public double? Lift { get; set; }
    public string WindowStartDate { get; set; } = string.Empty;
    public string WindowEndDate { get; set; } = string.Empty;
}

/// <summary>
/// Pure drift detection over one tenant's verdict-calibration daily rows
/// (internal/docs/backend/verdict-calibration.md) — the same statistics as the rule-regression radar
/// (7-day window vs the prior 28 days, lift ≥2×, Wilson-separated via
/// <see cref="MetricsMath.RateIncreaseSeparated"/>, small-n gates) applied to three questions:
/// <list type="bullet">
/// <item><b>share_regression</b> — one verdict path's share of all sessions doubled. Points at a rule
/// (or an agent path) whose conditions suddenly match much more often.</item>
/// <item><b>silence_share_regression</b> — the share of sessions the backend had to decide because
/// the agent went silent (sweep:* + maxlife:*) doubled. An agent-liveness signal, not a classifier one.</item>
/// <item><b>evidence_gap</b> — absolute gate: the pure fallthrough rule (r6) decides ≥20 % of the
/// window's classifier verdicts. The evidence the other rules need is not arriving.</item>
/// </list>
/// Derived (<c>legacy:*</c>) paths never alert per-path — pre-instrumentation attribution is too
/// weak for that — but their rule-shaped subset DOES feed the silence and classifier-verdict
/// group sums AND the per-path BASELINE of the matching <c>sweep:{rule}</c> path, so both the
/// groups and the per-path comparisons stay continuous across the 2026-08-23 instrumentation
/// cutover (see <see cref="Summarize"/>). Agent-declared paths (<c>agent:*</c>, <c>register:*</c>) are excluded
/// from the per-path share regression: their share tracks the customer's workflow mix (a
/// WhiteGlove rollout week doubles <c>agent:whiteglove_pending</c>; a Continue-Anyway tenant
/// lives on <c>agent:complete_soft</c>) — real signals, but not about the BACKEND classifier
/// this radar calibrates. First production pass (2026-08-23) fired exactly those three. Re-arm
/// mirrors the rule radar: share back under 1.5× baseline or the path stops occurring; the
/// evidence gap re-arms under 15 %.
/// </summary>
public static class VerdictCalibrationRadar
{
    public const int WindowDays = RuleRegressionRadar.WindowDays;
    public const int BaselineDays = RuleRegressionRadar.BaselineDays;
    /// <summary>
    /// Minimum window hits before a share finding fires or keeps its episode. Deliberately
    /// higher than the rule radar's 5: verdict shares swing on tiny counts, and the first
    /// production month kept a 3-hit maxlife:r5_awaiting episode alive as pure noise
    /// (calibration read 2026-08-27).
    /// </summary>
    public const int MinWindowHits = 10;
    /// <summary>
    /// Minimum baseline hits before a PER-PATH share regression is meaningful. A path with no
    /// established baseline cannot regress — it is new vocabulary (or a renamed path), and
    /// alerting on it just counts the rollout: the 2026-08-23 instrumentation cut over from
    /// legacy:* to sweep:*/register:* names and fired seven zero-baseline artifacts. The floor
    /// alone did not close the cutover: the first stamped day already delivered 8 sweep:r6 /
    /// 15 sweep:r5_incomplete baseline hits and the rename fired again a week later (lift 8×
    /// against a one-day baseline, read 2026-09-02) — the legacy continuity in
    /// <see cref="Summarize"/> is what carries the baseline. The group kinds are exempt from the
    /// floor for the same reason.
    /// </summary>
    public const int MinBaselineHits = 5;
    public const int MinWindowSessions = RuleRegressionRadar.MinWindowSessions;
    public const double MinLift = RuleRegressionRadar.MinLift;
    public const double ReArmLiftFactor = RuleRegressionRadar.ReArmLiftFactor;

    /// <summary>Evidence gap fires at ≥20 % r6 share of the window's classifier verdicts (≥20 verdicts) and re-arms under 15 %.</summary>
    public const double EvidenceGapFirePct = 20.0;
    public const double EvidenceGapReArmPct = 15.0;
    public const int EvidenceGapMinVerdicts = 20;

    public const string SilenceGroupPath = "sweep+maxlife";
    public const string EvidenceGapPath = "r6";
    public const string GroupStatus = "*";

    private const string LegacyOrigin = "legacy";

    /// <summary>
    /// True when a path is eligible for the per-path share regression: backend-decided paths only
    /// (sweep / maxlife / late / retro / rule / manual / ingest). Agent-declared and registration
    /// paths mirror customer workflow, legacy paths are derived.
    /// </summary>
    public static bool IsShareRegressionEligible(string path)
    {
        var origin = VerdictPaths.Origin(path);
        return origin != LegacyOrigin
            && origin != "agent"
            && origin != "register";
    }

    public static List<VerdictCalibrationFinding> Evaluate(IReadOnlyList<VerdictCalibrationDailyAggregate> tenantRows, DateTime targetDateUtc)
    {
        var findings = new List<VerdictCalibrationFinding>();
        var horizon = Summarize(tenantRows, targetDateUtc);
        var windowStart = targetDateUtc.Date.AddDays(-(WindowDays - 1)).ToString("yyyy-MM-dd");
        var windowEnd = targetDateUtc.Date.ToString("yyyy-MM-dd");

        // 1. Per-path share regression.
        foreach (var pair in horizon.Paths)
        {
            if (!IsShareRegressionEligible(pair.Key.Path)) continue;
            var f = TryShareRegression(VerdictCalibrationAlertKinds.ShareRegression, pair.Key.Path, pair.Key.Status,
                pair.Value.WindowHits, horizon.WindowSessions, pair.Value.BaselineHits, horizon.BaselineSessions, windowStart, windowEnd);
            if (f != null) findings.Add(f);
        }

        // 2. Silence-share regression (group).
        {
            var f = TryShareRegression(VerdictCalibrationAlertKinds.SilenceShareRegression, SilenceGroupPath, GroupStatus,
                horizon.SilenceWindowHits, horizon.WindowSessions, horizon.SilenceBaselineHits, horizon.BaselineSessions, windowStart, windowEnd);
            if (f != null) findings.Add(f);
        }

        // 3. Evidence gap (absolute).
        if (horizon.ClassifierWindowVerdicts >= EvidenceGapMinVerdicts)
        {
            var pct = 100.0 * horizon.R6WindowHits / horizon.ClassifierWindowVerdicts;
            if (pct >= EvidenceGapFirePct)
            {
                findings.Add(new VerdictCalibrationFinding
                {
                    Kind = VerdictCalibrationAlertKinds.EvidenceGap,
                    VerdictPath = EvidenceGapPath,
                    Status = GroupStatus,
                    WindowHitCount = horizon.R6WindowHits,
                    WindowSessionCount = horizon.ClassifierWindowVerdicts,
                    BaselineHitCount = horizon.R6BaselineHits,
                    BaselineSessionCount = horizon.ClassifierBaselineVerdicts,
                    WindowRatePct = Math.Round(pct, 1),
                    BaselineRatePct = horizon.ClassifierBaselineVerdicts > 0
                        ? Math.Round(100.0 * horizon.R6BaselineHits / horizon.ClassifierBaselineVerdicts, 1) : 0,
                    Lift = null,
                    WindowStartDate = windowStart,
                    WindowEndDate = windowEnd,
                });
            }
        }

        return findings
            .OrderByDescending(f => f.WindowRatePct)
            .ThenBy(f => f.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.VerdictPath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when an ACTIVE alert may re-arm given the current horizon sums (or when its path/numbers no longer pass the fire gates — episodes from an earlier, broader gate clear themselves).</summary>
    public static bool ShouldReArm(VerdictCalibrationAlert alert, IReadOnlyList<VerdictCalibrationDailyAggregate> tenantRows, DateTime targetDateUtc)
    {
        if (alert.Kind == VerdictCalibrationAlertKinds.ShareRegression && !IsShareRegressionEligible(alert.VerdictPath))
            return true;

        var horizon = Summarize(tenantRows, targetDateUtc);
        switch (alert.Kind)
        {
            case VerdictCalibrationAlertKinds.EvidenceGap:
                if (horizon.ClassifierWindowVerdicts < EvidenceGapMinVerdicts) return true;
                return 100.0 * horizon.R6WindowHits / horizon.ClassifierWindowVerdicts < EvidenceGapReArmPct;

            case VerdictCalibrationAlertKinds.SilenceShareRegression:
                return ReArmByRate(horizon.SilenceWindowHits, horizon.WindowSessions, horizon.SilenceBaselineHits, horizon.BaselineSessions);

            default:
                horizon.Paths.TryGetValue((alert.VerdictPath, alert.Status), out var sums);
                // Below the fire floors the episode is noise-retention, not an elevated share —
                // clear it (it re-fires only by passing the full gates again).
                if (sums.WindowHits < MinWindowHits || sums.BaselineHits < MinBaselineHits) return true;
                return ReArmByRate(sums.WindowHits, horizon.WindowSessions, sums.BaselineHits, horizon.BaselineSessions);
        }
    }

    /// <summary>Current numbers for an ongoing episode (refresh without re-firing).</summary>
    public static (int WindowHits, int WindowSessions, int BaselineHits, int BaselineSessions) CurrentSums(
        VerdictCalibrationAlert alert, IReadOnlyList<VerdictCalibrationDailyAggregate> tenantRows, DateTime targetDateUtc)
    {
        var horizon = Summarize(tenantRows, targetDateUtc);
        switch (alert.Kind)
        {
            case VerdictCalibrationAlertKinds.EvidenceGap:
                return (horizon.R6WindowHits, horizon.ClassifierWindowVerdicts, horizon.R6BaselineHits, horizon.ClassifierBaselineVerdicts);
            case VerdictCalibrationAlertKinds.SilenceShareRegression:
                return (horizon.SilenceWindowHits, horizon.WindowSessions, horizon.SilenceBaselineHits, horizon.BaselineSessions);
            default:
                horizon.Paths.TryGetValue((alert.VerdictPath, alert.Status), out var sums);
                return (sums.WindowHits, horizon.WindowSessions, sums.BaselineHits, horizon.BaselineSessions);
        }
    }

    private static bool ReArmByRate(int windowHits, int windowSessions, int baselineHits, int baselineSessions)
    {
        if (windowHits == 0 || windowSessions == 0) return true;
        if (baselineSessions == 0 || baselineHits == 0) return false; // zero-baseline episode re-arms only via hits-stopped
        var windowRate = (double)windowHits / windowSessions;
        var baselineRate = (double)baselineHits / baselineSessions;
        return windowRate < ReArmLiftFactor * baselineRate;
    }

    private static VerdictCalibrationFinding? TryShareRegression(string kind, string path, string status,
        int windowHits, int windowSessions, int baselineHits, int baselineSessions, string windowStart, string windowEnd)
    {
        if (windowHits < MinWindowHits) return null;
        if (windowSessions < MinWindowSessions) return null;
        if (baselineSessions <= 0) return null;
        // Per-path only: no established baseline → no regression claim (new/renamed vocabulary).
        if (kind == VerdictCalibrationAlertKinds.ShareRegression && baselineHits < MinBaselineHits) return null;

        var windowRate = (double)windowHits / windowSessions;
        var baselineRate = (double)baselineHits / baselineSessions;
        if (windowRate < MinLift * baselineRate) return null;
        if (!MetricsMath.RateIncreaseSeparated(windowHits, windowSessions, baselineHits, baselineSessions)) return null;

        return new VerdictCalibrationFinding
        {
            Kind = kind,
            VerdictPath = path,
            Status = status,
            WindowHitCount = windowHits,
            WindowSessionCount = windowSessions,
            BaselineHitCount = baselineHits,
            BaselineSessionCount = baselineSessions,
            WindowRatePct = Math.Round(windowRate * 100, 1),
            BaselineRatePct = Math.Round(baselineRate * 100, 1),
            Lift = baselineRate > 0 ? Math.Round(windowRate / baselineRate, 1) : null,
            WindowStartDate = windowStart,
            WindowEndDate = windowEnd,
        };
    }

    /// <summary>Derived classifier-rule path (<c>legacy:r5_incomplete</c>) — same detail shape as <see cref="VerdictPaths.IsClassifierPath"/>, legacy origin.</summary>
    private static bool IsLegacyClassifierPath(string path, string origin)
    {
        if (origin != LegacyOrigin) return false;
        var detail = path.Length > origin.Length + 1 ? path.Substring(origin.Length + 1) : string.Empty;
        return detail.Length >= 2 && detail[0] == 'r' && char.IsDigit(detail[1]);
    }

    /// <summary>The stamped path a derived rule row continues: <c>legacy:r6</c> → <c>sweep:r6</c>.</summary>
    private static string LegacyContinuityPath(string legacyPath)
        => VerdictPaths.Compose(VerdictPaths.OriginSweep, legacyPath.Substring(LegacyOrigin.Length + 1));

    private static void AddPathHits(Horizon h, (string Path, string Status) key, int count, bool inWindow)
    {
        h.Paths.TryGetValue(key, out var sums);
        h.Paths[key] = inWindow
            ? new PathSums(sums.WindowHits + count, sums.BaselineHits)
            : new PathSums(sums.WindowHits, sums.BaselineHits + count);
    }

    internal readonly record struct PathSums(int WindowHits, int BaselineHits);

    internal sealed class Horizon
    {
        public int WindowSessions;
        public int BaselineSessions;
        public int SilenceWindowHits;
        public int SilenceBaselineHits;
        public int R6WindowHits;
        public int R6BaselineHits;
        public int ClassifierWindowVerdicts;
        public int ClassifierBaselineVerdicts;
        public readonly Dictionary<(string Path, string Status), PathSums> Paths = new();
    }

    /// <summary>
    /// Sums the daily rows into the trailing window [target−6d, target] and the prior baseline
    /// [target−34d, target−7d] (ISO-string date compare, rows outside the horizon ignored).
    /// Silence group = sweep:* + maxlife:* (incl. non-rule sweep paths like sweep:stalled — the
    /// question is "how often did the backend have to decide", not which rule did). Classifier
    /// verdicts = stamped rule paths (<see cref="VerdictPaths.IsClassifierPath"/>); r6 = their
    /// fallthrough subset.
    /// Legacy continuity: derived rule-shaped paths (<c>legacy:r5_incomplete</c>, <c>legacy:r6</c>, …)
    /// were the SAME backend decisions before the 2026-08-23 instrumentation — the derivation reads
    /// the writers' own reason literals, which is strong enough for a group sum (though still too
    /// weak for per-path alerting). Counting them symmetrically (window AND baseline) keeps the
    /// silence and classifier-verdict groups continuous across the cutover; without this the groups
    /// had a near-zero baseline by construction and fired a lift-124 artifact. The same rows also
    /// carry the per-path BASELINE of <c>sweep:{rule}</c> (the sweep is the bare-literal writer
    /// the derivation cannot tell from a late reconcile): a stamped <c>sweep:r6</c> window is
    /// compared against <c>legacy:r6</c> + <c>sweep:r6</c> baseline days, so the rename itself
    /// never reads as a share regression (2026-09-02: lift 8× against the single stamped
    /// baseline day). Baseline only — a legacy row never contributes WINDOW hits to a stamped
    /// path, so derived data can never be what a per-path alert points at. Legacy rows only
    /// exist for pre-instrumentation sessions, so both terms self-deprecate as they age out.
    /// (Non-rule legacy paths — <c>legacy:superseded</c>, <c>legacy:wg_awaiting</c>,
    /// <c>legacy:unknown</c> — stay excluded: superseded is a registration write, unknown is
    /// unattributable, and wg_awaiting is a negligible tail.)
    /// </summary>
    internal static Horizon Summarize(IReadOnlyList<VerdictCalibrationDailyAggregate> rows, DateTime targetDateUtc)
    {
        var windowStart = targetDateUtc.Date.AddDays(-(WindowDays - 1)).ToString("yyyy-MM-dd");
        var baselineStart = targetDateUtc.Date.AddDays(-(WindowDays - 1 + BaselineDays)).ToString("yyyy-MM-dd");
        var target = targetDateUtc.Date.ToString("yyyy-MM-dd");

        var h = new Horizon();
        foreach (var row in rows)
        {
            if (string.CompareOrdinal(row.Date, target) > 0) continue;
            bool inWindow;
            if (string.CompareOrdinal(row.Date, windowStart) >= 0) inWindow = true;
            else if (string.CompareOrdinal(row.Date, baselineStart) >= 0) inWindow = false;
            else continue;

            if (inWindow) h.WindowSessions += row.SessionCount; else h.BaselineSessions += row.SessionCount;

            foreach (var b in row.Buckets)
            {
                if (b.Count == 0) continue;
                var origin = VerdictPaths.Origin(b.VerdictPath);
                var legacyRule = IsLegacyClassifierPath(b.VerdictPath, origin);

                AddPathHits(h, (b.VerdictPath, b.Status), b.Count, inWindow);
                if (legacyRule && !inWindow)
                    AddPathHits(h, (LegacyContinuityPath(b.VerdictPath), b.Status), b.Count, inWindow: false);
                if (origin == VerdictPaths.OriginSweep || origin == VerdictPaths.OriginMaxLifetime || legacyRule)
                {
                    if (inWindow) h.SilenceWindowHits += b.Count; else h.SilenceBaselineHits += b.Count;
                }
                if (VerdictPaths.IsClassifierPath(b.VerdictPath) || legacyRule)
                {
                    if (inWindow) h.ClassifierWindowVerdicts += b.Count; else h.ClassifierBaselineVerdicts += b.Count;
                    if (b.VerdictPath.EndsWith(":" + ClassifierRules.R6Fallthrough, StringComparison.Ordinal))
                    {
                        if (inWindow) h.R6WindowHits += b.Count; else h.R6BaselineHits += b.Count;
                    }
                }
            }
        }
        return h;
    }
}
