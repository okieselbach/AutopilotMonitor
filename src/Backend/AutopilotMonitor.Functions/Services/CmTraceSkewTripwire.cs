using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Regression tripwire for the agent's CMTrace time resolution (per-line self-anchoring,
    /// docs/agent/cmtrace-time-resolution.md). Runs once per session on the terminal ingest
    /// batch, over samples the counter reconcile collected in its existing partition scan.
    ///
    /// Detection: diff = median(Δ_IME) − median(Δ_other), where Δ = ReceivedAt − OccurredUtc.
    /// Shared upload/spool latency is common-mode and cancels in the difference; a timezone
    /// mis-conversion shifts only the IME side and lands on the 15-minute offset grid.
    /// Replayed old log lines produce large OFF-grid deltas — the grid-residual test is what
    /// separates the two (field measurement 2026-08-20: without it, 48 instead of 26 true hits).
    /// The median alone is not enough, though: when a dead/relaunched agent re-tails the IME log,
    /// one upload burst can carry the majority of the session's IME samples with a CONTINUUM of
    /// ages (field 2026-08-28, session d832560a: 508 of 627 samples, 46…733 min) whose median
    /// lands on the grid by chance. A genuine zone mis-conversion shifts every line by the same
    /// grid multiple, so the grid-conformity test is applied per sample as well: the session
    /// only counts when the bulk of the IME deltas individually sit on the grid.
    ///
    /// Samples are additionally windowed to the session's most recent INGEST ERA. A session
    /// partition is not one agent run: a pre-provisioning session's Part 1 is written weeks
    /// before Part 2, by whatever agent build was current back then (field 2026-09-01, sessions
    /// e797117b / c06d639d / d7c8032b: 26 IME samples at exactly −60 min from a 2026-08-20
    /// technician leg under agent 2.0.1409 — pre per-line anchoring — dominated the 3…9 clean
    /// samples of the same day's user leg and fired the tripwire against a build that had
    /// already self-updated to 2.0.1445). Those hits are real skew but ancient history, not a
    /// regression of the running build, and no scan-wide statistic can separate them because the
    /// stored event rows carry no agent version. The era boundary does: ReceivedAt gaps larger
    /// than <see cref="EraGapHours"/> split legs, and only the newest leg is judged.
    ///
    /// Goal state: this never fires. Any hit is either a case the per-line anchoring does not
    /// cover or a detector bug — both are actionable findings, never noise to tune away.
    /// </summary>
    internal static class CmTraceSkewTripwire
    {
        // Same grid the agent anchors on and the same residual tolerance as its own grid
        // guard (docs/agent/cmtrace-time-resolution.md) — keep these in lockstep with the
        // agent so a future anchoring change stays traceable to this detector.
        internal const int GridMinutes = 15;
        internal const double ResidualToleranceMinutes = 2.0;
        internal const int MinSamplesPerSide = 20;
        // ReceivedAt is stamped per upload batch, not per event; a side backed by fewer
        // distinct batches yields a batch-composition artifact, not a session property.
        internal const int MinDistinctBatchesPerSide = 3;
        // Share of IME samples whose own delta (relative to the other side's median) must sit
        // within ResidualToleranceMinutes of SOME grid multiple (zero included). A replay
        // continuum hits that window only ~2·2/15 ≈ 27 % of the time by chance; a real skew,
        // even one mixing two writer eras, hits it for essentially every line.
        internal const double MinGridConformantFraction = 0.8;
        // A silence longer than this ends an ingest era. A live agent uploads continuously
        // (self-metrics and heartbeats every few minutes; it reports session_stalled after
        // 60 min idle, and the maintenance sweep classifies silence at 2 h), so no gap this
        // large occurs INSIDE one leg — while a pre-provisioning handover is hours to weeks.
        internal const double EraGapHours = 2.0;

        internal sealed class Result
        {
            public double MedianImeDeltaMinutes { get; set; }
            public double MedianOtherDeltaMinutes { get; set; }
            public double DiffMinutes { get; set; }
            public int GridSteps { get; set; }
            public double ResidualMinutes { get; set; }
            public int ImeSampleCount { get; set; }
            public int OtherSampleCount { get; set; }
            public int ImeBatchCount { get; set; }
            public int OtherBatchCount { get; set; }
            public double GridConformantFraction { get; set; }
            /// <summary>ReceivedAt of the oldest batch still inside the judged ingest era.</summary>
            public DateTime EraStartUtc { get; set; }
            /// <summary>IME samples dropped as belonging to an older ingest era.</summary>
            public int ImeSamplesOutsideEra { get; set; }
            /// <summary>Non-IME samples dropped as belonging to an older ingest era.</summary>
            public int OtherSamplesOutsideEra { get; set; }
        }

        /// <summary>
        /// Pure detector: non-null exactly when the session's most recent ingest era shows a
        /// grid-aligned divergence between IME-derived and other event timestamps. All
        /// suppression inputs live in the scan itself so the decision is fully unit-testable.
        /// </summary>
        internal static Result? Evaluate(SessionSkewScan? scan)
        {
            if (scan == null)
                return null;

            var eraStart = ResolveEraStartUtc(scan);
            if (eraStart == null)
                return null;

            var ime = SelectEra(scan.ImeSamples, eraStart.Value);
            var other = SelectEra(scan.OtherSamples, eraStart.Value);

            if (ime.Deltas.Count < MinSamplesPerSide || other.Deltas.Count < MinSamplesPerSide)
                return null;
            if (ime.BatchCount < MinDistinctBatchesPerSide || other.BatchCount < MinDistinctBatchesPerSide)
                return null;

            var medianIme = PercentileMath.Median(ime.Deltas);
            var medianOther = PercentileMath.Median(other.Deltas);
            var diff = medianIme - medianOther;
            var steps = (int)Math.Round(diff / GridMinutes, MidpointRounding.AwayFromZero);
            var residual = Math.Abs(diff - steps * (double)GridMinutes);

            if (Math.Abs(steps) < 1 || residual >= ResidualToleranceMinutes)
                return null;

            var conformant = ComputeGridConformantFraction(ime.Deltas, medianOther);
            if (conformant < MinGridConformantFraction)
                return null;

            return new Result
            {
                MedianImeDeltaMinutes = medianIme,
                MedianOtherDeltaMinutes = medianOther,
                DiffMinutes = diff,
                GridSteps = steps,
                ResidualMinutes = residual,
                ImeSampleCount = ime.Deltas.Count,
                OtherSampleCount = other.Deltas.Count,
                ImeBatchCount = ime.BatchCount,
                OtherBatchCount = other.BatchCount,
                GridConformantFraction = conformant,
                EraStartUtc = eraStart.Value,
                ImeSamplesOutsideEra = ime.Dropped,
                OtherSamplesOutsideEra = other.Dropped,
            };
        }

        /// <summary>
        /// Start of the session's most recent ingest era: walk the distinct batch stamps of BOTH
        /// sides backwards from the newest one and stop at the first gap wider than
        /// <see cref="EraGapHours"/>. Null when the scan holds no samples at all.
        /// </summary>
        internal static DateTime? ResolveEraStartUtc(SessionSkewScan scan)
        {
            var stamps = new HashSet<DateTime>();
            foreach (var sample in scan.ImeSamples)
                stamps.Add(sample.ReceivedAtUtc);
            foreach (var sample in scan.OtherSamples)
                stamps.Add(sample.ReceivedAtUtc);
            if (stamps.Count == 0)
                return null;

            var ordered = new List<DateTime>(stamps);
            ordered.Sort();

            var eraStart = ordered[ordered.Count - 1];
            for (int i = ordered.Count - 1; i > 0; i--)
            {
                if ((ordered[i] - ordered[i - 1]).TotalHours > EraGapHours)
                    break;
                eraStart = ordered[i - 1];
            }
            return eraStart;
        }

        /// <summary>
        /// Splits one side's samples at the era boundary: the deltas inside the era, the number
        /// of distinct upload batches backing them, and how many samples were dropped as older.
        /// </summary>
        private static (List<double> Deltas, int BatchCount, int Dropped) SelectEra(
            IReadOnlyList<SkewSample> samples, DateTime eraStartUtc)
        {
            var deltas = new List<double>(samples.Count);
            var batches = new HashSet<DateTime>();
            int dropped = 0;
            foreach (var sample in samples)
            {
                if (sample.ReceivedAtUtc < eraStartUtc)
                {
                    dropped++;
                    continue;
                }
                deltas.Add(sample.DeltaMinutes);
                batches.Add(sample.ReceivedAtUtc);
            }
            return (deltas, batches.Count, dropped);
        }

        /// <summary>
        /// Fraction of samples whose delta minus <paramref name="reference"/> lies within
        /// <see cref="ResidualToleranceMinutes"/> of any multiple of <see cref="GridMinutes"/>
        /// (zero included).
        /// </summary>
        internal static double ComputeGridConformantFraction(IReadOnlyList<double> deltas, double reference)
        {
            if (deltas.Count == 0)
                return 0;

            int hits = 0;
            foreach (var delta in deltas)
            {
                var rel = delta - reference;
                var nearest = Math.Round(rel / GridMinutes, MidpointRounding.AwayFromZero) * GridMinutes;
                if (Math.Abs(rel - nearest) < ResidualToleranceMinutes)
                    hits++;
            }
            return (double)hits / deltas.Count;
        }

        /// <summary>
        /// True when the session's IME events are dominated by writer-declared ("bias")
        /// offsets — a bias offset comes verbatim from the log line's own zone marker and
        /// cannot be an anchoring regression, so a grid hit there is not ours to alarm on.
        /// measuredWriterOffsetMinutes is deliberately NOT consulted: the per-file
        /// observation sticks to a stale value after an era flip-back (monotonicity guard),
        /// making it non-authoritative for exactly the sessions this tripwire inspects.
        /// </summary>
        internal static bool IsBiasDominated(IReadOnlyDictionary<string, int>? originHistogram)
        {
            if (originHistogram == null || originHistogram.Count == 0)
                return false;

            int total = 0, bias = 0;
            foreach (var kv in originHistogram)
            {
                total += kv.Value;
                if (string.Equals(kv.Key, "bias", StringComparison.OrdinalIgnoreCase))
                    bias += kv.Value;
            }
            return total > 0 && bias * 2 >= total;
        }
    }
}
