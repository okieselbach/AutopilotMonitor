using AutopilotMonitor.Functions.Functions.Apps;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>One fired duration regression: an app whose newest version installs much slower than its predecessor (see <see cref="AppVersionRegressionRadar"/>).</summary>
public sealed class AppVersionDurationRegressionFinding
{
    public string TenantId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string PreviousVersion { get; init; } = string.Empty;

    public int CurrentMedianSeconds { get; init; }
    public int PreviousMedianSeconds { get; init; }
    public int CurrentMeasuredCount { get; init; }
    public int PreviousMeasuredCount { get; init; }

    /// <summary>Current median ÷ previous median, one decimal. Never null — measured durations are ≥1s, so the previous median cannot be 0.</summary>
    public double Lift { get; init; }
}

/// <summary>
/// Deterministic, I/O-free core of the app-version duration regression radar. Detects
/// "version X made this app's installs much slower" from the per-(session, app) install
/// summaries: per (tenant, app), the newest version's MEDIAN measured install duration is
/// compared against the previous version's median. Medians (nearest-rank, shared with the
/// apps dashboard via <see cref="AppsAnalyticsHelper.Percentile"/>) rather than means —
/// a single back-stamped straggler must not fire a fleet alert. A regression fires only
/// when ALL hold:
/// <list type="number">
/// <item>both versions have ≥ 10 measured installs inside the horizon;</item>
/// <item>current median ≥ 2× the previous version's median;</item>
/// <item>absolute increase ≥ 300 s — a 20s→45s app is noise, not an incident.</item>
/// </list>
/// Measured population: Succeeded, non-skip terminal state, plausible observed duration
/// (<see cref="MetricsMath.HasMeasuredDuration"/>), no AppId collision, non-empty
/// AppVersion. Version ORDER comes from first-seen install time (min StartedAt) — version
/// strings are never sorted lexically ("9.1" vs "2024.10" would lie). "Previous" is the
/// version with the latest first-seen strictly before the current version's first-seen;
/// with parallel ring rollouts that can be a concurrently-deployed version — accepted for
/// v1, the episode key (app, current version) still caps it at one bell per version.
/// </summary>
public static class AppVersionRegressionRadar
{
    /// <summary>Days of install summaries the radar loads and reasons over (mirrors the rule radar's 7+28 horizon).</summary>
    public const int HorizonDays = 35;
    public const int MinMeasuredInstalls = 10;
    public const double MinMedianLift = 2.0;
    public const int MinAbsoluteIncreaseSeconds = 300;

    /// <summary>An active alert re-arms once the current median falls back under 1.5× the previous version's median.</summary>
    public const double ReArmLiftFactor = 1.5;

    /// <summary>Per-version duration stats inside the horizon (see <see cref="ComputeVersionStats"/>).</summary>
    public sealed class VersionStats
    {
        public string Version { get; init; } = string.Empty;
        public DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; init; }
        public int MeasuredCount { get; init; }
        public int MedianSeconds { get; init; }
    }

    /// <summary>
    /// Evaluates one tenant's install summaries (loaded for the last <see cref="HorizonDays"/>
    /// days). Returns at most one finding per app — the newest version vs. its predecessor;
    /// older version pairs are yesterday's news, not an alert. Deterministic ordering:
    /// lift descending, then app name ordinal.
    /// </summary>
    public static List<AppVersionDurationRegressionFinding> Evaluate(IReadOnlyList<AppInstallSummary> tenantSummaries)
    {
        var findings = new List<AppVersionDurationRegressionFinding>();
        foreach (var app in MeasuredAppGroups(tenantSummaries))
        {
            var stats = ComputeVersionStats(app.Value);
            var (current, previous) = SelectComparisonPair(stats);
            if (current == null || previous == null) continue;

            if (current.MeasuredCount < MinMeasuredInstalls) continue;
            if (previous.MeasuredCount < MinMeasuredInstalls) continue;
            if (current.MedianSeconds < MinMedianLift * previous.MedianSeconds) continue;
            if (current.MedianSeconds - previous.MedianSeconds < MinAbsoluteIncreaseSeconds) continue;

            findings.Add(new AppVersionDurationRegressionFinding
            {
                TenantId = app.Value[0].TenantId,
                AppName = app.Key,
                CurrentVersion = current.Version,
                PreviousVersion = previous.Version,
                CurrentMedianSeconds = current.MedianSeconds,
                PreviousMedianSeconds = previous.MedianSeconds,
                CurrentMeasuredCount = current.MeasuredCount,
                PreviousMeasuredCount = previous.MeasuredCount,
                Lift = Math.Round((double)current.MedianSeconds / previous.MedianSeconds, 1),
            });
        }
        return findings
            .OrderByDescending(f => f.Lift)
            .ThenBy(f => f.AppName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True when an ACTIVE alert may re-arm: the alerted version has drained out of the
    /// horizon (fewer than <see cref="MinMeasuredInstalls"/> measured installs left — it was
    /// superseded or pulled), or its recomputed median fell back under
    /// <see cref="ReArmLiftFactor"/>× the previous version's recomputed median. When the
    /// previous version itself has drained there is no comparison basis anymore — the
    /// episode is kept until the current version drains (fires-stopped-style re-arm only).
    /// </summary>
    public static bool ShouldReArm(IReadOnlyList<AppInstallSummary> tenantSummaries, AppVersionRegressionAlert alert)
    {
        var stats = ComputeVersionStatsForApp(tenantSummaries, alert.AppName);
        var current = stats.FirstOrDefault(s => string.Equals(s.Version, alert.CurrentVersion, StringComparison.Ordinal));
        if (current == null || current.MeasuredCount < MinMeasuredInstalls) return true;

        var previous = stats.FirstOrDefault(s => string.Equals(s.Version, alert.PreviousVersion, StringComparison.Ordinal));
        if (previous == null || previous.MeasuredCount < MinMeasuredInstalls) return false;

        return current.MedianSeconds < ReArmLiftFactor * previous.MedianSeconds;
    }

    /// <summary>Per-version stats for one app (measured rows only), or empty when the app has none.</summary>
    public static List<VersionStats> ComputeVersionStatsForApp(IReadOnlyList<AppInstallSummary> tenantSummaries, string appName)
    {
        foreach (var app in MeasuredAppGroups(tenantSummaries))
        {
            if (string.Equals(app.Key, appName, StringComparison.OrdinalIgnoreCase))
                return ComputeVersionStats(app.Value);
        }
        return new List<VersionStats>();
    }

    /// <summary>
    /// Groups the MEASURED rows by app name: succeeded, non-skip, plausible duration,
    /// no AppId collision (a second app's outcomes must not fire this app's alert),
    /// non-empty version. Rate/failure signals are deliberately out of scope — this
    /// radar alerts on duration only.
    /// </summary>
    internal static Dictionary<string, List<AppInstallSummary>> MeasuredAppGroups(IReadOnlyList<AppInstallSummary> summaries)
    {
        var groups = new Dictionary<string, List<AppInstallSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in summaries)
        {
            if (string.IsNullOrEmpty(summary.AppName)) continue;
            if (string.IsNullOrEmpty(summary.AppVersion)) continue;
            if (summary.AppIdCollision) continue;
            if (summary.Status != "Succeeded") continue;
            if (MetricsMath.IsSkipTerminalState(summary)) continue;
            if (!MetricsMath.HasMeasuredDuration(summary)) continue;

            if (!groups.TryGetValue(summary.AppName, out var list))
            {
                list = new List<AppInstallSummary>();
                groups[summary.AppName] = list;
            }
            list.Add(summary);
        }
        return groups;
    }

    /// <summary>Per-version first/last-seen and nearest-rank median over one app's measured rows.</summary>
    internal static List<VersionStats> ComputeVersionStats(IReadOnlyList<AppInstallSummary> measuredAppRows)
    {
        return measuredAppRows
            .GroupBy(s => s.AppVersion, StringComparer.Ordinal)
            .Select(g =>
            {
                var durations = g.Select(s => s.DurationSeconds).ToList();
                return new VersionStats
                {
                    Version = g.Key,
                    FirstSeen = g.Min(s => s.StartedAt),
                    LastSeen = g.Max(s => s.StartedAt),
                    MeasuredCount = durations.Count,
                    MedianSeconds = AppsAnalyticsHelper.Percentile(durations, 0.50),
                };
            })
            .ToList();
    }

    /// <summary>
    /// Current = the version of the most recent measured install (ties broken by later
    /// first-seen, then version ordinal — deterministic); previous = the version with the
    /// latest first-seen strictly before the current version's first-seen (same tie-break).
    /// Either is null when the app has fewer than two versions in the horizon.
    /// </summary>
    internal static (VersionStats? Current, VersionStats? Previous) SelectComparisonPair(IReadOnlyList<VersionStats> stats)
    {
        if (stats.Count < 2) return (null, null);

        var current = stats
            .OrderByDescending(s => s.LastSeen)
            .ThenByDescending(s => s.FirstSeen)
            .ThenByDescending(s => s.Version, StringComparer.Ordinal)
            .First();

        var previous = stats
            .Where(s => s.FirstSeen < current.FirstSeen)
            .OrderByDescending(s => s.FirstSeen)
            .ThenByDescending(s => s.LastSeen)
            .ThenByDescending(s => s.Version, StringComparer.Ordinal)
            .FirstOrDefault();

        return (current, previous);
    }
}
