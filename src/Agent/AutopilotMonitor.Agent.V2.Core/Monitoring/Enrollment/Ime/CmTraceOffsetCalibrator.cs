using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>How the UTC value of a CMTrace line was arrived at. Recorded as evidence.</summary>
    public enum CmTraceOffsetOrigin
    {
        /// <summary>No offset known — the caller fell back to its own clock.</summary>
        None = 0,

        /// <summary>The writer declared its offset in the line ("+480"). Authoritative.</summary>
        Bias = 1,

        /// <summary>
        /// A per-FILE measured offset was applied. Retired by the 2026-08-20 revert (04b1a7c6):
        /// one offset per file is wrong when a file holds lines from two writer eras. Kept so
        /// stored events from agent 2.0.1410 remain interpretable.
        /// </summary>
        Calibrated = 2,

        /// <summary>
        /// The line anchored ITSELF: it was read in a pass whose lines are provably fresh
        /// (written since the previous poll), so its own distance to the agent clock, rounded
        /// to the 15-minute offset grid, IS the writer's offset — no cross-line state involved.
        /// Immune to interleaved writer eras by construction. See
        /// <see cref="CmTraceOffsetCalibrator.TryMeasureOffset"/>.
        /// </summary>
        LineAnchored = 3,

        /// <summary>
        /// A backlog line resolved through its writer ERA: the stretch of log written by one
        /// process lifetime, anchored by an entry inside that era whose UTC instant the agent
        /// knows exactly (the install marker vs. the IME's own record of the bootstrap script's
        /// result — a few hundred milliseconds apart). An anchor applies to a whole era or not
        /// at all, never across an "EMS Agent Started" boundary. See <c>ImeLogEraPreScan</c>
        /// (2026-09-04, session a7140f98).
        /// </summary>
        EraAnchored = 4,
    }

    /// <summary>
    /// Measures the UTC offset that the process WRITING a CMTrace log believes it is in.
    ///
    /// <para>
    /// A CMTrace line carries local time and no offset — IME 1.104 writes
    /// <c>DateTime.Now.TimeOfDay</c> in both of its trace listeners. Converting such a line with
    /// the agent's own <c>TimeZoneInfo.Local</c> assumes the two processes agree, and they do not:
    /// both cache their zone at process start and neither follows a later <c>tzutil</c> or a
    /// Windows auto-timezone change. The resulting error is
    /// <c>Offset_writer - Offset_agent</c> and is zero only by coincidence. Field measurement over
    /// 11,068 sessions found 26 sessions carrying a real zone-offset error, from +1 h
    /// (GMT to W. Europe) to -17 h (a writer still on the Pacific OOBE default while the agent had
    /// already moved to E. Australia).
    /// </para>
    ///
    /// <para>
    /// So the offset is MEASURED, not assumed. The tracker tails the log on a 100 ms poll, so a
    /// line that appeared since the previous pass was written essentially "now":
    /// <code>
    ///   candidate = lineLocalTime - agentUtcNow
    ///   offset    = round(candidate / 15 min) * 15 min
    ///   lineUtc   = lineLocalTime - offset
    /// </code>
    /// Every real UTC offset is a whole multiple of 15 minutes, so the rounding absorbs poll and
    /// flush latency without ambiguity.
    /// </para>
    ///
    /// <para>
    /// THE FRESHNESS REQUIREMENT IS WHAT MAKES THIS CORRECT. An anchor must be the newest line of
    /// a pass in which the file actually GREW. Without that rule a backlog line that happens to be
    /// an exact multiple of 15 minutes old (30 min, 45 min, ...) rounds to a clean value with zero
    /// residual and calibrates the offset to a wrong figure — the residual guard alone cannot
    /// catch it. Callers must only offer anchors they know to be fresh.
    /// </para>
    ///
    /// <para>
    /// Calibration is kept per source file: IntuneManagementExtension.log and AppWorkload.log are
    /// written by different processes and can hold different beliefs. Re-calibrating on every
    /// fresh anchor makes this self-healing — if the writing process restarts and picks up the
    /// current zone, the measurement follows within one poll.
    /// </para>
    /// </summary>
    public sealed class CmTraceOffsetCalibrator
    {
        /// <summary>Grid that every real UTC offset sits on.</summary>
        internal static readonly TimeSpan OffsetGrid = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How far a candidate may sit off the grid before it is rejected. Poll latency (100 ms)
        /// plus the writer's flush latency are far below this; anything larger means the anchor
        /// was not actually fresh.
        /// </summary>
        internal static readonly TimeSpan MaxGridResidual = TimeSpan.FromMinutes(2);

        /// <summary>Real UTC offsets span UTC-12 (Dateline) to UTC+14 (Line Islands).</summary>
        internal static readonly TimeSpan MinOffset = TimeSpan.FromHours(-12);

        /// <summary>Upper bound of a real UTC offset. See <see cref="MinOffset"/>.</summary>
        internal static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

        private sealed class Anchor
        {
            public TimeSpan Offset;
            public DateTime LocalTimestamp;
        }

        private readonly object _lock = new object();

        private readonly Dictionary<string, Anchor> _bySource =
            new Dictionary<string, Anchor>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Offer a freshly appended line as a calibration anchor.
        /// <para>
        /// The caller MUST only pass a line it knows to be fresh — the newest line of a pass in
        /// which the file grew. See the freshness note on this class.
        /// </para>
        /// </summary>
        /// <param name="sourceKey">Log file identity; the file name is enough.</param>
        /// <param name="localTimestamp">The line's local time exactly as written (Kind is ignored).</param>
        /// <param name="agentUtcNow">The agent's UTC clock at parse time.</param>
        /// <returns><c>true</c> when the anchor was accepted and the offset updated.</returns>
        /// <summary>
        /// The pure measurement: the distance between a FRESH line's local time and the agent's
        /// UTC clock, rounded to the 15-minute offset grid, is the writer's UTC offset.
        /// <para>
        /// Freshness is the caller's obligation and is what makes this correct — for a line
        /// written within the last poll interval, <c>local - now</c> differs from the writer's
        /// true offset only by seconds, which the grid rounding absorbs. Returns <c>false</c>
        /// when the residual exceeds <see cref="MaxGridResidual"/> (the line is not fresh, or
        /// the writer's clock is broken) or the rounded value is not a real timezone offset.
        /// </para>
        /// <para>
        /// Known, accepted edge: a freshly WRITTEN line carrying an old REPLAYED timestamp whose
        /// age happens to sit within the residual of an exact grid multiple measures that age as
        /// an "offset" — the line then resolves to roughly the read time instead of its replayed
        /// past. The error is bounded by construction (an anchored line always resolves to
        /// now ± <see cref="MaxGridResidual"/>), the case needs an age of exactly N×15 min ± 2 min,
        /// and sub-24h replays are treated as current by the historic-replay guard anyway.
        /// </para>
        /// </summary>
        public static bool TryMeasureOffset(DateTime localTimestamp, DateTime agentUtcNow, out TimeSpan offset)
        {
            var candidate = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified)
                          - DateTime.SpecifyKind(agentUtcNow, DateTimeKind.Unspecified);

            var gridUnits = Math.Round(candidate.TotalMinutes / OffsetGrid.TotalMinutes,
                                       MidpointRounding.AwayFromZero);
            offset = TimeSpan.FromMinutes(gridUnits * OffsetGrid.TotalMinutes);

            // Off-grid by more than the tolerance: the line was not fresh, or the writer's own
            // clock is broken. Either way it must not become an offset.
            if ((candidate - offset).Duration() > MaxGridResidual) return false;

            // Not a value any real timezone can produce.
            if (offset < MinOffset || offset > MaxOffset) return false;

            return true;
        }

        public bool TryCalibrate(string sourceKey, DateTime localTimestamp, DateTime agentUtcNow)
        {
            if (string.IsNullOrEmpty(sourceKey)) return false;

            TimeSpan offset;
            if (!TryMeasureOffset(localTimestamp, agentUtcNow, out offset)) return false;

            lock (_lock)
            {
                if (_bySource.TryGetValue(sourceKey, out var existing))
                {
                    // Only ever move the anchor forward. A line older than the current anchor
                    // carries nothing newer and could re-introduce a stale belief.
                    if (localTimestamp <= existing.LocalTimestamp) return false;
                    existing.Offset = offset;
                    existing.LocalTimestamp = localTimestamp;
                }
                else
                {
                    _bySource[sourceKey] = new Anchor
                    {
                        Offset = offset,
                        LocalTimestamp = localTimestamp,
                    };
                }
            }

            return true;
        }

        /// <summary>The offset measured for this source, when one has been established.</summary>
        public bool TryGetOffset(string sourceKey, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (string.IsNullOrEmpty(sourceKey)) return false;

            lock (_lock)
            {
                Anchor anchor;
                if (!_bySource.TryGetValue(sourceKey, out anchor)) return false;
                offset = anchor.Offset;
                return true;
            }
        }

        /// <summary>
        /// Convert a line's local time to UTC using the measured offset. Returns <c>false</c> when
        /// this source has not been calibrated yet — the caller then picks its own fallback and
        /// should mark the result as not source-grounded.
        /// </summary>
        public bool TryResolveUtc(string sourceKey, DateTime localTimestamp, out DateTime utc)
        {
            utc = default(DateTime);

            TimeSpan offset;
            if (!TryGetOffset(sourceKey, out offset)) return false;

            utc = DateTime.SpecifyKind(
                DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified) - offset,
                DateTimeKind.Utc);
            return true;
        }
    }
}
