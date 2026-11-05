using System.Diagnostics;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Wall-clock bound on one rule-evaluation pass: one analysis run (every active rule against a
    /// session) or one dry-run of a draft. Tenant-authored rules decide how much work a pass does
    /// (conditions × events × operator cost), so the pass is what gets bounded, not the single
    /// regex match (D-410). The engine checks it before every operator evaluation and in the
    /// evaluator loops that scan events without one; a spent budget or a signalled token throws
    /// <see cref="RuleEvaluationStoppedException"/>.
    /// <para>
    /// Within a run each rule gets its own share (<see cref="ForRule"/>): a rule that spends it stops
    /// alone and the run goes on, so one expensive rule — a device can make catalog rules slow by
    /// flooding its own session — cannot use up the run for every rule after it. The run's budget
    /// still caps the whole pass.
    /// </para>
    /// </summary>
    internal sealed class RuleEvaluationBudget
    {
        /// <summary>One analysis run, all rules together (queue, interim, "Analyze now"). The
        /// slowest production runs spend under 0.3 s outside storage I/O.</summary>
        internal static readonly TimeSpan AnalysisRun = TimeSpan.FromSeconds(5);

        /// <summary>One rule's share of an analysis run. Above the longest process stall measured
        /// (1.85 s), far above what a rule needs (a whole run takes under 0.3 s).</summary>
        internal static readonly TimeSpan AnalysisRule = TimeSpan.FromSeconds(2);

        /// <summary>One dry-run of a draft rule; the logparser pattern test has the same 2 s (D-379).</summary>
        internal static readonly TimeSpan DryRun = TimeSpan.FromSeconds(2);

        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly CancellationToken _cancellationToken;
        private readonly RuleEvaluationBudget? _run;

        internal RuleEvaluationBudget(TimeSpan limit, CancellationToken cancellationToken = default)
            : this(limit, cancellationToken, run: null)
        {
        }

        private RuleEvaluationBudget(TimeSpan limit, CancellationToken cancellationToken, RuleEvaluationBudget? run)
        {
            Limit = limit;
            _cancellationToken = cancellationToken;
            _run = run;
        }

        internal TimeSpan Limit { get; }

        internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);

        /// <summary>One rule's share of this run, starting now.</summary>
        internal RuleEvaluationBudget ForRule(TimeSpan share) => new(share, _cancellationToken, this);

        internal void ThrowIfSpent()
        {
            if (_cancellationToken.IsCancellationRequested)
                throw new RuleEvaluationStoppedException(EvaluationStopReasons.Cancelled, endsRun: true);

            // The run first: a spent run ends the run, whichever rule happens to be running.
            _run?.ThrowIfSpent();

            if (Elapsed >= Limit)
                throw new RuleEvaluationStoppedException(EvaluationStopReasons.TimeBudget, endsRun: _run == null);
        }
    }

    /// <summary>
    /// A rule evaluation ended early. <see cref="Reason"/> is one of <see cref="EvaluationStopReasons"/>:
    /// a regex hit its match timeout, the budget ran out, or the caller went away.
    /// <see cref="EndsRun"/> tells a stop of the whole pass (the run's budget, cancellation) from a stop
    /// of the one rule (a regex timeout, the rule's own share), after which the run goes on.
    /// </summary>
    internal sealed class RuleEvaluationStoppedException : Exception
    {
        internal RuleEvaluationStoppedException(string reason, bool endsRun) : base($"Rule evaluation stopped: {reason}")
        {
            Reason = reason;
            EndsRun = endsRun;
        }

        internal string Reason { get; }

        internal bool EndsRun { get; }
    }
}
