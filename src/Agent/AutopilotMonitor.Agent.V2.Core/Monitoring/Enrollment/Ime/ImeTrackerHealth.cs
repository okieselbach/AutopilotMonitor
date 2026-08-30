using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Immutable snapshot of the IME log tracker's cumulative health counters and per-pattern
    /// match counts for the current session (restart-safe: the counters ride the persisted
    /// tracker state). Consumed by the periodic <c>agent_metrics_snapshot</c> (counters) and the
    /// session-end <c>ime_pattern_hits</c> event (counters + histogram).
    /// <para>
    /// The counters' expected value in the fleet is zero for everything except
    /// <see cref="LinesRead"/> / <see cref="EntriesMatched"/>: oversized lines, regex timeouts
    /// and budget breaks only happen on hostile or malformed input, held tails only when a
    /// writer is caught mid-line. They are surfaced so that "the tracker skipped work" is never
    /// invisible outside the client log.
    /// </para>
    /// </summary>
    public sealed class ImeTrackerHealth
    {
        public long LinesRead { get; set; }
        public long EntriesMatched { get; set; }
        public long OversizedLines { get; set; }
        public long RegexTimeouts { get; set; }

        /// <summary>
        /// Regex timeouts absorbed by the one gated retry: the timed-out attempt consumed
        /// almost no CPU (a starved thread, not a spinning pattern) and the retry recovered
        /// the match. Expected nonzero on CPU-saturated VMs; NOT skipped work — only a
        /// timeout that survives (or is denied) the retry counts into <see cref="RegexTimeouts"/>.
        /// </summary>
        public long RegexTimeoutRetries { get; set; }

        public long BudgetBreaks { get; set; }
        public long HeldTails { get; set; }
        public int HealthScriptResultParseFailures { get; set; }

        /// <summary>Enabled patterns compiled without a leading '^' (custom / pre-anchor cached configs).</summary>
        public int UnanchoredPatterns { get; set; }

        /// <summary>Files matched by the tracker's name patterns in the last pass.</summary>
        public int FilesTailed { get; set; }

        /// <summary>Σ(file length − bookmark) over tailed files at the end of the last pass — the tracker's queue.</summary>
        public long BacklogBytes { get; set; }

        /// <summary>IME version as logged by IME itself ("Agent version is: …"), null until seen.</summary>
        public string ImeAgentVersion { get; set; }

        /// <summary>Match count per enabled pattern ID — every enabled pattern is present, zeros included.</summary>
        public IReadOnlyDictionary<string, int> PatternHits { get; set; }

        public bool HasSkippedWork => OversizedLines > 0 || RegexTimeouts > 0 || BudgetBreaks > 0;
    }
}
