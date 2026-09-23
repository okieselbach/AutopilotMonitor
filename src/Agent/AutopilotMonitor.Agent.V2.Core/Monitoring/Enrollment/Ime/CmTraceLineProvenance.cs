using System;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// How ONE CMTrace line's UTC value was arrived at, captured at the line itself.
    /// <para>
    /// The tracker exposes the provenance of the line it matched last. A consumer that acts later —
    /// a platform-script result held for its executor end block, a run duration spanning a start
    /// line and a result line — must describe ITS line, not whichever line happened to be matched
    /// last, so the snapshot travels on <see cref="ScriptExecutionState"/> (persisted as a plain
    /// DTO; null on state files written before it existed).
    /// </para>
    /// </summary>
    public sealed class CmTraceLineProvenance
    {
        /// <summary>
        /// The line's local time exactly as the writer wrote it (Kind Unspecified, no zone). Null
        /// when the value did not come from a CMTrace line at all (clock fallback).
        /// </summary>
        public DateTime? SourceLocalTs { get; set; }

        /// <summary>How the offset applied to <see cref="SourceLocalTs"/> was obtained.</summary>
        public CmTraceOffsetOrigin Origin { get; set; }

        /// <summary>The offset that was applied, in minutes (local = UTC + offset).</summary>
        public int? OffsetMinutes { get; set; }

        /// <summary>For <see cref="CmTraceOffsetOrigin.EraAnchored"/>: which anchor established the era's offset.</summary>
        public string EraAnchorKind { get; set; }

        /// <summary>What the calibrator measured for the source file — observational, never applied.</summary>
        public int? MeasuredWriterOffsetMinutes { get; set; }

        /// <summary>Name of the log file the line was read from — the closest thing to a writer identity the tracker has.</summary>
        public string SourceFileName { get; set; }

        /// <summary>
        /// True when the applied offset was ASSUMED for this line rather than declared by the
        /// writer or measured against the agent clock: the reader's own zone (fallback), a writer
        /// era's anchor, or the retired per-file calibration. A clock-derived value (no source
        /// timestamp) sits on the true UTC axis and is not an assumption.
        /// </summary>
        public bool IsAssumed()
        {
            if (!SourceLocalTs.HasValue) return false;
            switch (Origin)
            {
                case CmTraceOffsetOrigin.Bias:
                case CmTraceOffsetOrigin.LineAnchored:
                    return false;
                default:
                    return true;
            }
        }
    }
}
