using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>One fired regression: a rule whose hit rate rose beyond every gate (see <see cref="RuleRegressionRadar"/>).</summary>
public sealed class RuleRegressionFinding
{
    public string TenantId { get; init; } = string.Empty;
    public string RuleId { get; init; } = string.Empty;
    public string RuleTitle { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;

    public int WindowFireCount { get; init; }
    public int WindowSessionCount { get; init; }
    public int BaselineFireCount { get; init; }
    public int BaselineSessionCount { get; init; }

    public double WindowRatePct { get; init; }
    public double BaselineRatePct { get; init; }

    /// <summary>Window rate ÷ baseline rate, one decimal. Null when the baseline rate is 0 — a NEW signal has no finite lift, and we never invent one.</summary>
    public double? Lift { get; init; }

    public string WindowStartDate { get; init; } = string.Empty;
    public string WindowEndDate { get; init; } = string.Empty;
}

/// <summary>
/// Deterministic, I/O-free core of the F3 regression radar (insights spec §F3). Detects
/// "this analyze rule suddenly fires much more often" from the daily <see cref="RuleStatsEntry"/>
/// rows — RATES, not counts (absolute counts spike with every rollout wave): per (tenant, rule),
/// hit rate = FireCount ÷ SessionsEvaluated, audit-verified to be session-deduplicated for
/// analyze rules (gather rules are excluded until their per-batch dedup is fixed — Q6 follow-up,
/// never silently included with bad math). A regression fires only when ALL hold:
/// <list type="number">
/// <item>trailing 7-day window has ≥ 5 hit sessions AND ≥ 20 evaluated sessions;</item>
/// <item>window rate ≥ 2× the prior 28-day baseline rate;</item>
/// <item>the Wilson 95 % intervals are disjoint in the increase direction
/// (<see cref="MetricsMath.RateIncreaseSeparated"/>) — small-n noise never alerts.</item>
/// </list>
/// Suppressed (false positives are trust damage on a monitoring product): rules younger than
/// baseline + window (grace period), rules edited inside the window (edits legitimately change
/// hit rates), rules without an entity (deleted), and rules with an empty baseline denominator.
/// </summary>
public static class RuleRegressionRadar
{
    public const int WindowDays = 7;
    public const int BaselineDays = 28;
    public const int MinWindowHitSessions = 5;
    public const int MinWindowSessions = 20;
    public const double MinLift = 2.0;

    /// <summary>An active alert re-arms once the window rate falls back under 1.5× baseline (spec §F3 suppression).</summary>
    public const double ReArmLiftFactor = 1.5;

    /// <summary>Dimension-concentration gates: a value must cover ≥5 hit sessions at ≥2× lift, or the alert says "no clear dimension concentration".</summary>
    public const int MinDimensionSessions = 5;
    public const double MinDimensionLift = 2.0;

    /// <summary>Rule entity timestamps the suppression gates read (CreatedAt = grace period; UpdatedAt = edit-in-window).</summary>
    public readonly record struct RuleTimestamps(DateTime CreatedAt, DateTime UpdatedAt);

    /// <summary>
    /// Evaluates one tenant's analyze-rule stats over the [target − 34d, target] horizon.
    /// <paramref name="tenantEntries"/> must be the tenant's rows only (no "global" mirror);
    /// gather rows are ignored defensively. <paramref name="ruleTimestamps"/> maps ruleId →
    /// entity timestamps — a rule without an entry is suppressed (deleted rules never alert).
    /// </summary>
    public static List<RuleRegressionFinding> Evaluate(
        IReadOnlyList<RuleStatsEntry> tenantEntries,
        DateTime targetDateUtc,
        IReadOnlyDictionary<string, RuleTimestamps> ruleTimestamps)
    {
        var findings = new List<RuleRegressionFinding>();
        foreach (var group in AnalyzeRuleGroups(tenantEntries))
        {
            var sums = SumWindows(group.Value, targetDateUtc);

            if (sums.WindowFire < MinWindowHitSessions) continue;
            if (sums.WindowSessions < MinWindowSessions) continue;

            // Entity gates: deleted rules never alert; new rules get the full baseline+window
            // grace period; an edit inside the window legitimately changes the rate.
            if (!ruleTimestamps.TryGetValue(group.Key, out var timestamps)) continue;
            var windowStart = targetDateUtc.Date.AddDays(-(WindowDays - 1));
            if (timestamps.CreatedAt > targetDateUtc.Date.AddDays(-(WindowDays - 1 + BaselineDays))) continue;
            if (timestamps.UpdatedAt >= windowStart) continue;

            // No observed baseline denominator (e.g. rule disabled throughout the baseline,
            // re-enabled recently) — no honest comparison basis.
            if (sums.BaselineSessions <= 0) continue;

            var windowRate = (double)sums.WindowFire / sums.WindowSessions;
            var baselineRate = (double)sums.BaselineFire / sums.BaselineSessions;
            if (windowRate < MinLift * baselineRate) continue;
            if (!MetricsMath.RateIncreaseSeparated(
                    sums.WindowFire, sums.WindowSessions, sums.BaselineFire, sums.BaselineSessions))
                continue;

            var first = group.Value[0];
            findings.Add(new RuleRegressionFinding
            {
                TenantId = first.TenantId,
                RuleId = group.Key,
                RuleTitle = first.RuleTitle,
                Severity = first.Severity,
                Category = first.Category,
                WindowFireCount = sums.WindowFire,
                WindowSessionCount = sums.WindowSessions,
                BaselineFireCount = sums.BaselineFire,
                BaselineSessionCount = sums.BaselineSessions,
                WindowRatePct = Math.Round(windowRate * 100, 1),
                BaselineRatePct = Math.Round(baselineRate * 100, 1),
                Lift = baselineRate > 0 ? Math.Round(windowRate / baselineRate, 1) : null,
                WindowStartDate = windowStart.ToString("yyyy-MM-dd"),
                WindowEndDate = targetDateUtc.Date.ToString("yyyy-MM-dd"),
            });
        }
        return findings
            .OrderByDescending(f => f.WindowRatePct)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True when an ACTIVE alert for this rule may re-arm: nothing fired in the window anymore,
    /// or the window rate fell back under <see cref="ReArmLiftFactor"/>× the baseline rate.
    /// A zero-baseline alert ("new signal") re-arms only via the fires-stopped branch — there
    /// is no rate threshold to fall under.
    /// </summary>
    public static bool ShouldReArm(IReadOnlyList<RuleStatsEntry> ruleEntries, DateTime targetDateUtc)
    {
        var sums = SumWindows(ruleEntries, targetDateUtc);
        if (sums.WindowFire == 0) return true;
        if (sums.BaselineFire <= 0 || sums.BaselineSessions <= 0 || sums.WindowSessions <= 0) return false;
        var windowRate = (double)sums.WindowFire / sums.WindowSessions;
        var baselineRate = (double)sums.BaselineFire / sums.BaselineSessions;
        return windowRate < ReArmLiftFactor * baselineRate;
    }

    /// <summary>
    /// The dominant dimension concentration among a regression's hit sessions, or null when no
    /// dimension value reaches ≥<see cref="MinDimensionLift"/>× lift with
    /// ≥<see cref="MinDimensionSessions"/> hit sessions — the alert then says
    /// "no clear dimension concentration" instead of stretching for one (truthfulness rules 1/6).
    /// Wording contract for consumers: "correlated — not necessarily causal".
    /// </summary>
    public static RuleRegressionDimension? ComputeDimensionConcentration(
        IReadOnlyList<SessionSummary> hitSessions, IReadOnlyList<SessionSummary> allSessions)
    {
        if (hitSessions.Count == 0 || allSessions.Count == 0) return null;

        var dimensions = new (string Name, Func<SessionSummary, string?> Selector)[]
        {
            ("osBuild", s => s.OsBuild),
            ("model", s => $"{s.Manufacturer} {s.Model}".Trim()),
            ("agentVersion", s => s.AgentVersion),
            ("imeVersion", s => s.ImeAgentVersion),
        };

        RuleRegressionDimension? best = null;
        foreach (var (name, selector) in dimensions)
        {
            var allCounts = CountValues(allSessions, selector);
            foreach (var hit in CountValues(hitSessions, selector))
            {
                if (hit.Value < MinDimensionSessions) continue;
                if (!allCounts.TryGetValue(hit.Key, out var allCount) || allCount <= 0) continue;

                var hitShare = (double)hit.Value / hitSessions.Count;
                var allShare = (double)allCount / allSessions.Count;
                var lift = hitShare / allShare;
                if (lift < MinDimensionLift) continue;

                if (best == null || lift > best.Lift || (lift == best.Lift && hit.Value > best.HitCount))
                {
                    best = new RuleRegressionDimension
                    {
                        Dimension = name,
                        Value = hit.Key,
                        HitCount = hit.Value,
                        HitSharePct = Math.Round(hitShare * 100, 1),
                        AllSharePct = Math.Round(allShare * 100, 1),
                        Lift = Math.Round(lift, 1),
                    };
                }
            }
        }
        return best;
    }

    private static Dictionary<string, int> CountValues(
        IReadOnlyList<SessionSummary> sessions, Func<SessionSummary, string?> selector)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var value = selector(session)?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            counts.TryGetValue(value!, out var current);
            counts[value!] = current + 1;
        }
        return counts;
    }

    /// <summary>Groups a tenant's entries by ruleId, analyze rows only (defensive re-filter).</summary>
    internal static Dictionary<string, List<RuleStatsEntry>> AnalyzeRuleGroups(IReadOnlyList<RuleStatsEntry> entries)
    {
        var groups = new Dictionary<string, List<RuleStatsEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.RuleType, "analyze", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.RuleId)) continue;
            if (!groups.TryGetValue(entry.RuleId, out var list))
            {
                list = new List<RuleStatsEntry>();
                groups[entry.RuleId] = list;
            }
            list.Add(entry);
        }
        return groups;
    }

    internal readonly record struct WindowSums(int WindowFire, int WindowSessions, int BaselineFire, int BaselineSessions);

    /// <summary>
    /// Sums one rule's daily rows into the trailing window [target−6d, target] and the prior
    /// baseline [target−34d, target−7d]. Dates are compared as ISO strings (the rows' own key
    /// format); rows outside the horizon are ignored.
    /// </summary>
    internal static WindowSums SumWindows(IReadOnlyList<RuleStatsEntry> ruleEntries, DateTime targetDateUtc)
    {
        var windowStart = targetDateUtc.Date.AddDays(-(WindowDays - 1)).ToString("yyyy-MM-dd");
        var baselineStart = targetDateUtc.Date.AddDays(-(WindowDays - 1 + BaselineDays)).ToString("yyyy-MM-dd");
        var target = targetDateUtc.Date.ToString("yyyy-MM-dd");

        int windowFire = 0, windowSessions = 0, baselineFire = 0, baselineSessions = 0;
        foreach (var entry in ruleEntries)
        {
            if (string.CompareOrdinal(entry.Date, target) > 0) continue;
            if (string.CompareOrdinal(entry.Date, windowStart) >= 0)
            {
                windowFire += entry.FireCount;
                windowSessions += entry.SessionsEvaluated;
            }
            else if (string.CompareOrdinal(entry.Date, baselineStart) >= 0)
            {
                baselineFire += entry.FireCount;
                baselineSessions += entry.SessionsEvaluated;
            }
        }
        return new WindowSums(windowFire, windowSessions, baselineFire, baselineSessions);
    }
}
