using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// The one definition of what the SLA targets are measured on. Both the breach evaluator
    /// (notifications) and the SLA dashboard headline read it, so an alert and the page it links
    /// to can never report different numbers for the same target.
    ///   Success rate + P95 duration: terminal sessions started in the last <see cref="WindowDays"/> days.
    ///   App installs: install attempts of the current ISO week.
    /// The window is rolling on purpose: a calendar month empties on its first day, which silently
    /// closes an active breach and blanks the headline until the next enrollment finishes.
    /// </summary>
    internal static class SlaEvaluationWindow
    {
        public const int WindowDays = 30;

        /// <summary>Period key of the rolling window on <c>SlaSnapshot.Period</c>.</summary>
        public const string PeriodKey = "last-30-days";

        public static DateTime WindowStart(DateTime nowUtc) => nowUtc.AddDays(-WindowDays);

        public static bool IsTerminal(SessionSummary s)
            => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed;

        public static List<SessionSummary> TerminalInWindow(IEnumerable<SessionSummary> sessions, DateTime nowUtc)
        {
            var windowStart = WindowStart(nowUtc);
            return sessions.Where(s => IsTerminal(s) && s.StartedAt >= windowStart).ToList();
        }

        /// <summary>
        /// Succeeded / terminal * 100, one decimal. Target comparisons use this rounded value —
        /// the number the admin reads is the number that is judged. 0 for an empty set; callers
        /// treat an empty set as "no data", never as a breach.
        /// </summary>
        public static double SuccessRate(IReadOnlyCollection<SessionSummary> terminal)
        {
            if (terminal.Count == 0) return 0;
            var succeeded = terminal.Count(s => s.Status == SessionStatus.Succeeded);
            return Math.Round(succeeded / (double)terminal.Count * 100, 1);
        }

        /// <summary>Succeeded / attempts * 100, one decimal — same rounding rule as <see cref="SuccessRate"/>.</summary>
        public static double AppInstallSuccessRate(IReadOnlyCollection<AppInstallSummary> attempts)
        {
            if (attempts.Count == 0) return 0;
            var succeeded = attempts.Count(a => a.Status == "Succeeded");
            return Math.Round(succeeded / (double)attempts.Count * 100, 1);
        }

        /// <summary>Durations in minutes, ascending; sessions without a positive duration are left out.</summary>
        public static List<double> DurationsMinutes(IEnumerable<SessionSummary> terminal)
            => terminal
                .Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0)
                .Select(s => s.DurationSeconds!.Value / 60.0)
                .OrderBy(d => d)
                .ToList();

        public static double P95Minutes(List<double> sortedDurationsMinutes)
            => MetricsMath.Percentile(sortedDurationsMinutes, 95);

        /// <summary>
        /// Install attempts of one ISO week: terminal rows only, skips excluded (a skip is not an
        /// attempt and must not pad the rate).
        /// </summary>
        public static List<AppInstallSummary> AppInstallAttemptsInWeek(IEnumerable<AppInstallSummary> installs, string isoWeekKey)
            => installs
                .Where(a => (a.Status == "Succeeded" || a.Status == "Failed")
                            && !MetricsMath.IsSkipTerminalState(a)
                            && SlaMetricsService.GetIsoWeekKey(a.StartedAt) == isoWeekKey)
                .ToList();

        /// <summary>
        /// Whether the population changed since the last breach notification. A lasting breach is
        /// repeated only when something new finished — a tenant without enrollments must not be told
        /// the same number every cooldown. Judged on the finish time, so a session that was still
        /// running at the last notification counts once it ends; a row without one falls back to its start.
        /// </summary>
        public static bool HasNewSince(IEnumerable<SessionSummary> terminal, DateTime lastNotifiedAt)
            => terminal.Any(s => (s.CompletedAt ?? s.StartedAt) > lastNotifiedAt);

        public static bool HasNewSince(IEnumerable<AppInstallSummary> attempts, DateTime lastNotifiedAt)
            => attempts.Any(a => (a.CompletedAt ?? a.StartedAt) > lastNotifiedAt);
    }
}
