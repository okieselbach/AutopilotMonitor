using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Per-session timestamp-delta samples collected during the terminal counter reconcile's
    /// single event-partition scan (zero extra storage reads). Samples are split by producing
    /// source: CMTrace-derived events (Source == "ImeLogTracker") versus everything else.
    /// Consumed by the CMTrace time-skew tripwire, which compares the two sides' median
    /// (ReceivedAt − OccurredUtc) deltas — shared upload/spool latency is common-mode and
    /// cancels in that difference; a timezone mis-conversion shifts only the IME side.
    /// </summary>
    public class SessionSkewScan
    {
        /// <summary>(ReceivedAt − OccurredUtc) in minutes for events with Source == "ImeLogTracker".</summary>
        public List<double> ImeDeltaMinutes { get; } = new List<double>();

        /// <summary>(ReceivedAt − OccurredUtc) in minutes for all other event sources.</summary>
        public List<double> OtherDeltaMinutes { get; } = new List<double>();

        /// <summary>
        /// Distinct ReceivedAt values seen on the IME side. ReceivedAt is stamped once per
        /// upload BATCH, not per event — a median over one or two batches is a
        /// batch-composition artifact, not a session property.
        /// </summary>
        public int ImeDistinctBatchCount { get; set; }

        /// <summary>Distinct ReceivedAt values seen on the non-IME side.</summary>
        public int OtherDistinctBatchCount { get; set; }
    }
}
