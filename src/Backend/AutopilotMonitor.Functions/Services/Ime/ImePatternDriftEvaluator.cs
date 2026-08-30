using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Pure drift statistics over <c>ImePatternStats</c> rows — no I/O, fully unit-testable.
    /// <para>
    /// No hard-coded "must-hit" list: a pattern is <b>expected</b> when it matched in at least
    /// <see cref="ExpectedHitRate"/> of the sessions on the <b>baseline</b> version — the
    /// version with the most reporting sessions (≥ <see cref="MinBaselineSessions"/>, never the
    /// candidate itself). Conditional patterns (platform/remediation scripts, WinGet, errors)
    /// fall below the threshold on their own and cannot raise false alarms. Drift is suspected
    /// for a candidate version once it has ≥ <see cref="MinCandidateSessions"/> reporting
    /// sessions and an expected pattern has matched in NONE of them.
    /// </para>
    /// </summary>
    public static class ImePatternDriftEvaluator
    {
        public const int MinBaselineSessions = 100;
        public const double ExpectedHitRate = 0.8;
        public const int MinCandidateSessions = 25;

        public sealed record DriftFinding(string Version, string PatternId, string BaselineVersion, double BaselineRate, int Sessions);

        /// <summary>The baseline version for <paramref name="candidateVersion"/>, or null when no version qualifies.</summary>
        public static string? SelectBaseline(IReadOnlyCollection<ImePatternStatsEntry> all, string? candidateVersion)
        {
            return all
                .Where(e => !string.Equals(e.Version, candidateVersion, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.Version, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Version = g.Key, Sessions = g.Max(e => e.Sessions) })
                .Where(v => v.Sessions >= MinBaselineSessions)
                .OrderByDescending(v => v.Sessions)
                .ThenByDescending(v => v.Version, StringComparer.OrdinalIgnoreCase)
                .Select(v => v.Version)
                .FirstOrDefault();
        }

        /// <summary>Baseline hit rate per pattern (patternId → rate) for the given baseline version.</summary>
        public static Dictionary<string, double> BaselineRates(IReadOnlyCollection<ImePatternStatsEntry> all, string baselineVersion)
        {
            return all
                .Where(e => string.Equals(e.Version, baselineVersion, StringComparison.OrdinalIgnoreCase) && e.Sessions > 0)
                .ToDictionary(e => e.PatternId, e => e.HitRate, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Drift findings for <paramref name="candidateVersion"/>: expected patterns (per the
        /// baseline) that never matched on the candidate although enough sessions reported.
        /// Cells already carrying <see cref="ImePatternStatsEntry.DriftFlaggedAt"/> are skipped —
        /// one OpsEvent per cell.
        /// </summary>
        public static IReadOnlyList<DriftFinding> Evaluate(IReadOnlyCollection<ImePatternStatsEntry> all, string candidateVersion)
        {
            var candidateRows = all
                .Where(e => string.Equals(e.Version, candidateVersion, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidateRows.Count == 0) return Array.Empty<DriftFinding>();

            var candidateSessions = candidateRows.Max(e => e.Sessions);
            if (candidateSessions < MinCandidateSessions) return Array.Empty<DriftFinding>();

            var baseline = SelectBaseline(all, candidateVersion);
            if (baseline == null) return Array.Empty<DriftFinding>();

            var baselineRates = BaselineRates(all, baseline);
            var findings = new List<DriftFinding>();
            foreach (var row in candidateRows)
            {
                if (row.DriftFlaggedAt.HasValue) continue;
                if (row.SessionsWithHit > 0) continue;
                if (row.Sessions < MinCandidateSessions) continue;
                if (!baselineRates.TryGetValue(row.PatternId, out var rate) || rate < ExpectedHitRate) continue;
                findings.Add(new DriftFinding(candidateVersion, row.PatternId, baseline, rate, row.Sessions));
            }
            return findings;
        }
    }
}
