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

        /// <summary>
        /// Times the bookmark of a multi-writer log (AgentExecutor.log, IntuneManagementExtension.log)
        /// was moved back because bytes behind it had been overwritten by a concurrent IME
        /// process. Expected nonzero whenever user-context scripts or detection scripts ran —
        /// recovered data, not skipped work.
        /// </summary>
        public long OverwriteRewinds { get; set; }

        /// <summary>Bytes re-read by those rewinds (only entries that changed are processed again).</summary>
        public long OverwriteBytesReprocessed { get; set; }

        /// <summary>Full ledger verifications run (cadence while scripts are in flight, end signals, fragments).</summary>
        public long VerifyPasses { get; set; }

        /// <summary>Bytes re-read for verification, guard-zone checks included — the cost of the overwrite detection.</summary>
        public long VerifiedBytes { get; set; }

        /// <summary>
        /// Longest single poll pass this session (one <c>CheckLogFilesAsync</c> plus the
        /// flushes), milliseconds on a monotonic clock. A pass spends its time in the file
        /// reads, the ledger verifications and the regex matching; the fleet baseline is well
        /// under 100 ms.
        /// </summary>
        public long PassMaxMs { get; set; }

        /// <summary>
        /// Longest pause between the end of one poll pass and the start of the next this
        /// session, milliseconds. The loop sleeps 100 ms between passes and saves its state now
        /// and then; anything far above that is the loop not being scheduled — session c3ecb568
        /// lost 26 s this way at 100 % VM CPU while the rest of the agent kept running. Late
        /// reading, not lost data: every event keeps its log line's own timestamp, and a
        /// result read before its executor end block waits for it.
        /// </summary>
        public long PassGapMaxMs { get; set; }

        /// <summary>Match count per enabled pattern ID — every enabled pattern is present, zeros included.</summary>
        public IReadOnlyDictionary<string, int> PatternHits { get; set; }

        public bool HasSkippedWork => OversizedLines > 0 || RegexTimeouts > 0 || BudgetBreaks > 0;
    }
}
