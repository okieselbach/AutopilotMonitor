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
    /// </summary>
    internal sealed class RuleEvaluationBudget
    {
        /// <summary>One analysis run, all rules together (queue, interim, "Analyze now"). The
        /// slowest production runs spend under 0.3 s outside storage I/O.</summary>
        internal static readonly TimeSpan AnalysisRun = TimeSpan.FromSeconds(5);

        /// <summary>One dry-run of a draft rule; the logparser pattern test has the same 2 s (D-379).</summary>
        internal static readonly TimeSpan DryRun = TimeSpan.FromSeconds(2);

        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly CancellationToken _cancellationToken;

        internal RuleEvaluationBudget(TimeSpan limit, CancellationToken cancellationToken = default)
        {
            Limit = limit;
            _cancellationToken = cancellationToken;
        }

        internal TimeSpan Limit { get; }

        internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);

        internal void ThrowIfSpent()
        {
            if (_cancellationToken.IsCancellationRequested)
                throw new RuleEvaluationStoppedException(EvaluationStopReasons.Cancelled);
            if (Elapsed >= Limit)
                throw new RuleEvaluationStoppedException(EvaluationStopReasons.TimeBudget);
        }
    }

    /// <summary>
    /// A rule evaluation ended early. <see cref="Reason"/> is one of <see cref="EvaluationStopReasons"/>:
    /// a regex hit its match timeout, the pass ran out of budget, or the caller went away.
    /// </summary>
    internal sealed class RuleEvaluationStoppedException : Exception
    {
        internal RuleEvaluationStoppedException(string reason) : base($"Rule evaluation stopped: {reason}")
        {
            Reason = reason;
        }

        internal string Reason { get; }
    }
}
