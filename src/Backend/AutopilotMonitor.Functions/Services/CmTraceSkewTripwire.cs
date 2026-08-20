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
        }

        /// <summary>
        /// Pure detector: non-null exactly when the session shows a grid-aligned divergence
        /// between IME-derived and other event timestamps. All suppression inputs live in the
        /// scan itself so the decision is fully unit-testable.
        /// </summary>
        internal static Result? Evaluate(SessionSkewScan? scan)
        {
            if (scan == null)
                return null;
            if (scan.ImeDeltaMinutes.Count < MinSamplesPerSide || scan.OtherDeltaMinutes.Count < MinSamplesPerSide)
                return null;
            if (scan.ImeDistinctBatchCount < MinDistinctBatchesPerSide || scan.OtherDistinctBatchCount < MinDistinctBatchesPerSide)
                return null;

            var medianIme = PercentileMath.Median(scan.ImeDeltaMinutes);
            var medianOther = PercentileMath.Median(scan.OtherDeltaMinutes);
            var diff = medianIme - medianOther;
            var steps = (int)Math.Round(diff / GridMinutes, MidpointRounding.AwayFromZero);
            var residual = Math.Abs(diff - steps * (double)GridMinutes);

            if (Math.Abs(steps) < 1 || residual >= ResidualToleranceMinutes)
                return null;

            return new Result
            {
                MedianImeDeltaMinutes = medianIme,
                MedianOtherDeltaMinutes = medianOther,
                DiffMinutes = diff,
                GridSteps = steps,
                ResidualMinutes = residual,
                ImeSampleCount = scan.ImeDeltaMinutes.Count,
                OtherSampleCount = scan.OtherDeltaMinutes.Count,
                ImeBatchCount = scan.ImeDistinctBatchCount,
                OtherBatchCount = scan.OtherDistinctBatchCount,
            };
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
