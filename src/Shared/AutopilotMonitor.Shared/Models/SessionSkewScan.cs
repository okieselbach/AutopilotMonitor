using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One timestamp-delta sample: <c>(ReceivedAt − OccurredUtc)</c> in minutes together with the
    /// <c>ReceivedAt</c> it was measured against. ReceivedAt is stamped once per upload BATCH, so
    /// it doubles as the batch identity (distinct-batch guard) and as the ingest-era coordinate
    /// the tripwire windows on.
    /// </summary>
    public readonly struct SkewSample
    {
        /// <summary>Creates a sample from its delta and the batch's ReceivedAt (UTC).</summary>
        public SkewSample(double deltaMinutes, DateTime receivedAtUtc)
        {
            DeltaMinutes = deltaMinutes;
            ReceivedAtUtc = receivedAtUtc;
        }

        /// <summary>(ReceivedAt − OccurredUtc) in minutes.</summary>
        public double DeltaMinutes { get; }

        /// <summary>The upload batch's server-side receive time (UTC).</summary>
        public DateTime ReceivedAtUtc { get; }
    }

    /// <summary>
    /// Per-session timestamp-delta samples collected during the terminal counter reconcile's
    /// single event-partition scan (zero extra storage reads). Samples are split by producing
    /// source: CMTrace-derived events (Source == "ImeLogTracker") versus everything else.
    /// Consumed by the CMTrace time-skew tripwire, which compares the two sides' median
    /// (ReceivedAt − OccurredUtc) deltas — shared upload/spool latency is common-mode and
    /// cancels in that difference; a timezone mis-conversion shifts only the IME side.
    /// <para>
    /// Every sample carries its ReceivedAt: a session partition can hold events from several
    /// ingest eras days apart (pre-provisioning Part 1 → Part 2, written by different agent
    /// builds), and the detector only judges the most recent one. Distinct-batch counts are
    /// therefore NOT accumulated here — they are derived inside the detector from the same
    /// era-filtered sample set the medians come from.
    /// </para>
    /// </summary>
    public class SessionSkewScan
    {
        /// <summary>
        /// Memory backstop for pathological sessions. The buffers keep the NEWEST samples per
        /// side: the era window judges the most recent ingest era, so discarding the tail would
        /// blind the detector on exactly the sessions large enough to hit this cap.
        /// </summary>
        public const int MaxSamplesPerSide = 20_000;

        // Trimming in chunks keeps the per-add cost amortized O(1) once the cap is reached
        // (removing one element per add would be an O(cap) memmove on every further event).
        private const int TrimChunk = 4096;

        /// <summary>Samples for events with Source == "ImeLogTracker", in scan order.</summary>
        public List<SkewSample> ImeSamples { get; } = new List<SkewSample>();

        /// <summary>Samples for all other event sources, in scan order.</summary>
        public List<SkewSample> OtherSamples { get; } = new List<SkewSample>();

        /// <summary>
        /// Appends a sample to the IME or the other side, enforcing <see cref="MaxSamplesPerSide"/>
        /// by dropping the oldest entries once the buffer grows past the cap.
        /// </summary>
        public void Add(bool isIme, double deltaMinutes, DateTime receivedAtUtc)
        {
            var side = isIme ? ImeSamples : OtherSamples;
            side.Add(new SkewSample(deltaMinutes, receivedAtUtc));
            if (side.Count >= MaxSamplesPerSide + TrimChunk)
                side.RemoveRange(0, side.Count - MaxSamplesPerSide);
        }
    }
}
