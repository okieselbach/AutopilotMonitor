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
/// (docs/backend/verdict-calibration.md) — the same statistics as the rule-regression radar
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
/// Derived (<c>legacy:*</c>) paths are excluded from all three — pre-instrumentation attribution
/// is too weak to alert on. Agent-declared paths (<c>agent:*</c>, <c>register:*</c>) are excluded
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
    public const int MinWindowHits = RuleRegressionRadar.MinWindowHitSessions;
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

    /// <summary>True when an ACTIVE alert may re-arm given the current horizon sums (or when its path is no longer eligible — episodes from an earlier, broader gate clear themselves).</summary>
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
                var key = (b.VerdictPath, b.Status);
                h.Paths.TryGetValue(key, out var sums);
                h.Paths[key] = inWindow
                    ? new PathSums(sums.WindowHits + b.Count, sums.BaselineHits)
                    : new PathSums(sums.WindowHits, sums.BaselineHits + b.Count);

                var origin = VerdictPaths.Origin(b.VerdictPath);
                if (origin == VerdictPaths.OriginSweep || origin == VerdictPaths.OriginMaxLifetime)
                {
                    if (inWindow) h.SilenceWindowHits += b.Count; else h.SilenceBaselineHits += b.Count;
                }
                if (VerdictPaths.IsClassifierPath(b.VerdictPath))
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
