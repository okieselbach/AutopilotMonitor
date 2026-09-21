using System;
using System.Collections.Generic;
using System.Globalization;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Collapses recurring health-script (proactive remediation) runs. IME re-runs every assigned
    /// policy on its own schedule for as long as the device is up; an enrollment that waits for
    /// its user for days would otherwise report every hourly run of every policy. A policy's
    /// first run and every run whose result differs from the last reported one are emitted in
    /// full; runs that repeat the last reported result are only counted and surface once per
    /// policy as a recurrence summary.
    /// <para>
    /// The start signal of an already known policy is held until its result is known, because
    /// the running indicators (web panel, session coverage) pair a start with a later result of
    /// the same policy — a start whose result was collapsed would read as "still running".
    /// </para>
    /// <para>
    /// The state is part of the tracker state file and is saved in the same write as the file
    /// positions: after a hard kill both fall back together, the lines after the last save are
    /// read again and counted again — nothing is lost and nothing counts twice.
    /// </para>
    /// Not thread-safe: called from the tracker's single poll thread only.
    /// </summary>
    public sealed class RecurringScriptGate
    {
        private readonly Dictionary<string, RecurringScriptPolicyState> _policies =
            new Dictionary<string, RecurringScriptPolicyState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when any call changed persisted state since the last <see cref="ConsumeDirty"/>.</summary>
        private bool _dirty;

        public bool ConsumeDirty()
        {
            var wasDirty = _dirty;
            _dirty = false;
            return wasDirty;
        }

        /// <summary>
        /// A start line was read. Returns <c>true</c> when the start signal is emitted right away
        /// (first run of the policy); <c>false</c> when it is held until the run's result is known.
        /// </summary>
        public bool OnStart(string policyId, DateTime startedAtUtc, string policyType, string patternId)
        {
            if (string.IsNullOrEmpty(policyId)) return true;

            var isKnown = _policies.TryGetValue(policyId, out var state);
            if (!isKnown)
            {
                state = new RecurringScriptPolicyState();
                _policies[policyId] = state;
            }

            CloseHeldRun(state);
            state.RunsObserved++;
            _dirty = true;

            if (!isKnown) return true;

            state.HeldStartAtUtc = startedAtUtc;
            state.HeldStartPolicyType = policyType;
            state.HeldStartPatternId = patternId;
            state.HeldRunSawResult = false;
            return false;
        }

        /// <summary>
        /// A result (one phase event) is about to be emitted. Returns <c>true</c> to emit it; when
        /// the run's start was held, <paramref name="releasedStart"/> carries the start signal to
        /// emit first. Returns <c>false</c> when the result repeats the last reported one.
        /// </summary>
        public bool OnResult(ScriptExecutionState script, DateTime observedAtUtc, out ScriptStartedInfo releasedStart)
        {
            releasedStart = null;
            if (script == null || string.IsNullOrEmpty(script.PolicyId)) return true;

            if (!_policies.TryGetValue(script.PolicyId, out var state))
            {
                state = new RecurringScriptPolicyState();
                _policies[script.PolicyId] = state;
            }

            var key = SignatureKey(script);
            var signature = Signature(script);
            _dirty = true;

            if (state.Signatures.TryGetValue(key, out var last) && string.Equals(last, signature, StringComparison.Ordinal))
            {
                state.CollapsedResults++;
                if (!state.FirstCollapsedAtUtc.HasValue) state.FirstCollapsedAtUtc = observedAtUtc;
                state.LastCollapsedAtUtc = observedAtUtc;
                if (state.HeldStartAtUtc.HasValue) state.HeldRunSawResult = true;
                return false;
            }

            state.Signatures[key] = signature;
            releasedStart = ReleaseHeldStart(script.PolicyId, state);
            return true;
        }

        /// <summary>
        /// Start signals that were held for at least <paramref name="minAge"/> without any result:
        /// the script did not finish, which is exactly what the running indicators are for.
        /// </summary>
        public List<ScriptStartedInfo> ReleaseStartsWithoutResult(DateTime nowUtc, TimeSpan minAge)
        {
            var released = new List<ScriptStartedInfo>();
            foreach (var kv in _policies)
            {
                var state = kv.Value;
                if (!state.HeldStartAtUtc.HasValue || state.HeldRunSawResult) continue;
                if (nowUtc - state.HeldStartAtUtc.Value < minAge) continue;
                released.Add(ReleaseHeldStart(kv.Key, state));
                _dirty = true;
            }
            return released;
        }

        /// <summary>
        /// One summary per policy whose collapsed-run count grew since the last summary. Marks
        /// the reported count, so a later stop of the same session only reports new growth.
        /// </summary>
        public List<ScriptRecurrenceSummary> TakeSummaries()
        {
            var summaries = new List<ScriptRecurrenceSummary>();
            foreach (var kv in _policies)
            {
                var state = kv.Value;
                var collapsedRuns = state.RunsCollapsed + (state.HeldStartAtUtc.HasValue && state.HeldRunSawResult ? 1 : 0);
                if (collapsedRuns <= state.RunsCollapsedAtLastSummary) continue;

                summaries.Add(new ScriptRecurrenceSummary
                {
                    PolicyId = kv.Key,
                    RunsObserved = state.RunsObserved,
                    RunsCollapsed = collapsedRuns,
                    RunsWithoutResult = state.RunsWithoutResult,
                    CollapsedResults = state.CollapsedResults,
                    FirstCollapsedAtUtc = state.FirstCollapsedAtUtc,
                    LastCollapsedAtUtc = state.LastCollapsedAtUtc,
                });
                state.RunsCollapsedAtLastSummary = collapsedRuns;
                _dirty = true;
            }
            return summaries;
        }

        public Dictionary<string, RecurringScriptPolicyState> ToPersisted() =>
            new Dictionary<string, RecurringScriptPolicyState>(_policies, StringComparer.OrdinalIgnoreCase);

        public void Restore(Dictionary<string, RecurringScriptPolicyState> persisted)
        {
            _policies.Clear();
            if (persisted == null) return;
            foreach (var kv in persisted)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                if (kv.Value.Signatures == null)
                    kv.Value.Signatures = new Dictionary<string, string>(StringComparer.Ordinal);
                _policies[kv.Key] = kv.Value;
            }
        }

        // A held run ends when the next start of the policy arrives: all its results repeated the
        // last report (collapsed), or it never produced one.
        private static void CloseHeldRun(RecurringScriptPolicyState state)
        {
            if (!state.HeldStartAtUtc.HasValue) return;
            if (state.HeldRunSawResult) state.RunsCollapsed++;
            else state.RunsWithoutResult++;
            ClearHeldStart(state);
        }

        private static ScriptStartedInfo ReleaseHeldStart(string policyId, RecurringScriptPolicyState state)
        {
            if (!state.HeldStartAtUtc.HasValue) return null;
            var info = new ScriptStartedInfo
            {
                PolicyId = policyId,
                ScriptType = "remediation",
                PolicyType = state.HeldStartPolicyType,
                SourceTimestampUtc = state.HeldStartAtUtc,
                PatternId = state.HeldStartPatternId,
            };
            ClearHeldStart(state);
            return info;
        }

        private static void ClearHeldStart(RecurringScriptPolicyState state)
        {
            state.HeldStartAtUtc = null;
            state.HeldStartPolicyType = null;
            state.HeldStartPatternId = null;
            state.HeldRunSawResult = false;
        }

        // The early compliance signal and the consolidated result of one phase differ in
        // completeness (only the latter knows RemediationStatus); keyed apart, or the two would
        // overwrite each other's signature on every run and nothing would ever collapse.
        private static string SignatureKey(ScriptExecutionState script) =>
            (script.ScriptPart ?? string.Empty) + "|" + (script.DurationBasis ?? string.Empty);

        private static string Signature(ScriptExecutionState script)
        {
            var culture = CultureInfo.InvariantCulture;
            return string.Join("|",
                script.ComplianceResult ?? string.Empty,
                script.ExitCode.HasValue ? script.ExitCode.Value.ToString(culture) : string.Empty,
                string.IsNullOrWhiteSpace(script.Stderr) ? "0" : "1",
                script.RemediationStatus.HasValue ? script.RemediationStatus.Value.ToString(culture) : string.Empty,
                script.ErrorCode.HasValue ? script.ErrorCode.Value.ToString(culture) : string.Empty);
        }
    }

    /// <summary>Persisted per-policy state of <see cref="RecurringScriptGate"/>.</summary>
    public class RecurringScriptPolicyState
    {
        /// <summary>Last reported result per phase and signal kind.</summary>
        public Dictionary<string, string> Signatures { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Start lines read for the policy.</summary>
        public int RunsObserved { get; set; }

        /// <summary>Runs whose every result repeated the last reported one.</summary>
        public int RunsCollapsed { get; set; }

        /// <summary>Runs that were followed by the next start without any result in between.</summary>
        public int RunsWithoutResult { get; set; }

        /// <summary>Phase events that were not emitted.</summary>
        public int CollapsedResults { get; set; }

        public DateTime? FirstCollapsedAtUtc { get; set; }
        public DateTime? LastCollapsedAtUtc { get; set; }

        public DateTime? HeldStartAtUtc { get; set; }
        public string HeldStartPolicyType { get; set; }
        public string HeldStartPatternId { get; set; }
        public bool HeldRunSawResult { get; set; }

        public int RunsCollapsedAtLastSummary { get; set; }
    }

    /// <summary>What <see cref="RecurringScriptGate"/> collapsed for one policy.</summary>
    public class ScriptRecurrenceSummary
    {
        public string PolicyId { get; set; }
        public int RunsObserved { get; set; }
        public int RunsCollapsed { get; set; }
        public int RunsWithoutResult { get; set; }
        public int CollapsedResults { get; set; }
        public DateTime? FirstCollapsedAtUtc { get; set; }
        public DateTime? LastCollapsedAtUtc { get; set; }
    }
}
