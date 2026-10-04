using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>What the registry reconciliation may do with a saved IME result right now.</summary>
    internal enum PlatformRunClaim
    {
        /// <summary>The run is handed out for reconciliation; it is never handed out again.</summary>
        Claimed,
        /// <summary>Not decidable yet: the tracker has not read past the save, or the run is not emitted yet.</summary>
        Wait,
        /// <summary>No run of this agent owns the save (none seen, or already reconciled).</summary>
        None,
    }

    /// <summary>One platform-script run as the tracker emitted it; the register hands out copies.</summary>
    internal sealed class PlatformScriptRun
    {
        public string PolicyId;
        public string UserId;
        public string RunId;
        public DateTime StartSeenUtc;
        public bool Emitted;
        public bool Claimed;
        public string Result;
        public string ResultSource;
        public int? ExitCode;
        /// <summary>As emitted: null when no output line of the run was read, empty when it said so.</summary>
        public string Stdout;
        public string Stderr;

        public PlatformScriptRun Clone() => (PlatformScriptRun)MemberwiseClone();
    }

    /// <summary>
    /// The platform-script runs the tracker has seen, the latest per (IME user id, policy id), for the
    /// registry reconciliation (D-316). IME saves a run's output under
    /// <c>Policies\&lt;UserId&gt;\&lt;PolicyId&gt;</c> only after it reported the whole script batch, so the
    /// observer reading it must know which run a changed key belongs to and what was emitted for it.
    /// Written on the tracker's poll thread, claimed on the registry host's timer thread: every member
    /// locks, and nothing calls out while holding the lock.
    /// <para>
    /// A save belongs to the latest run of its key: IME runs a policy once per batch and saves the
    /// batch after its last script, so the next run of the same key starts minutes later at the
    /// earliest. A save is paired only after the tracker completed a pass that started
    /// <see cref="TrackerCatchUp"/> after the save was seen — every line written before the save, the
    /// run's start among them, has been read by then, a stalled poll loop included.
    /// </para>
    /// </summary>
    internal sealed class PlatformScriptRunRegister
    {
        /// <summary>
        /// How far behind a seen save the tracker must have started a completed pass before the save
        /// is paired; covers the ledger's verify cadence (1 s) for a start line hidden by an overwrite.
        /// </summary>
        internal static readonly TimeSpan TrackerCatchUp = TimeSpan.FromSeconds(2);

        internal const int MaxRuns = 256;

        private readonly object _sync = new object();
        private readonly Dictionary<string, PlatformScriptRun> _latest =
            new Dictionary<string, PlatformScriptRun>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastCompletedPassStartUtc = DateTime.MinValue;
        private long _verified;
        private long _corrected;
        private bool _countersChanged;

        private static string KeyOf(string userId, string policyId) => (userId ?? string.Empty) + "|" + policyId;

        /// <summary>A platform run began: its start line, or its result when no start line was seen.</summary>
        public void NoteStarted(string userId, string policyId, DateTime seenUtc)
        {
            if (string.IsNullOrEmpty(policyId)) return;
            lock (_sync)
            {
                _latest[KeyOf(userId, policyId)] = new PlatformScriptRun { PolicyId = policyId, UserId = userId, StartSeenUtc = seenUtc };
                TrimLocked();
            }
        }

        /// <summary>The tracker emitted the run with these end-block values.</summary>
        public void NoteEmitted(ScriptExecutionState script, DateTime emittedUtc)
        {
            if (script == null || string.IsNullOrEmpty(script.PolicyId)) return;
            lock (_sync)
            {
                var key = KeyOf(script.UserId, script.PolicyId);
                if (!_latest.TryGetValue(key, out var run) || run.Emitted)
                {
                    run = new PlatformScriptRun { PolicyId = script.PolicyId, UserId = script.UserId, StartSeenUtc = emittedUtc };
                    _latest[key] = run;
                }
                run.Emitted = true;
                run.RunId = script.RunId;
                run.Result = script.Result;
                run.ResultSource = script.ResultSource;
                run.ExitCode = script.ExitCode;
                run.Stdout = script.Stdout;
                run.Stderr = script.Stderr;
                TrimLocked();
            }
        }

        /// <summary>The tracker finished a poll pass that started at <paramref name="passStartedUtc"/>.</summary>
        public void NotePassCompleted(DateTime passStartedUtc)
        {
            lock (_sync)
            {
                if (passStartedUtc > _lastCompletedPassStartUtc) _lastCompletedPassStartUtc = passStartedUtc;
            }
        }

        /// <summary>
        /// Hands out the run a save of (<paramref name="userId"/>, <paramref name="policyId"/>) seen at
        /// <paramref name="savedSeenUtc"/> belongs to — once. A run whose user id the tracker could not
        /// read is keyed without one.
        /// </summary>
        public PlatformRunClaim TryClaim(string userId, string policyId, DateTime savedSeenUtc, out PlatformScriptRun run)
        {
            run = null;
            lock (_sync)
            {
                if (_lastCompletedPassStartUtc < savedSeenUtc + TrackerCatchUp) return PlatformRunClaim.Wait;

                if (!_latest.TryGetValue(KeyOf(userId, policyId), out var latest)
                    && !_latest.TryGetValue(KeyOf(null, policyId), out latest))
                    return PlatformRunClaim.None;
                if (latest.Claimed) return PlatformRunClaim.None;
                if (!latest.Emitted) return PlatformRunClaim.Wait;

                latest.Claimed = true;
                run = latest.Clone();
                return PlatformRunClaim.Claimed;
            }
        }

        public void CountVerified()
        {
            lock (_sync) { _verified++; _countersChanged = true; }
        }

        public void CountCorrected()
        {
            lock (_sync) { _corrected++; _countersChanged = true; }
        }

        /// <summary>Runs whose emitted output IME's saved result confirmed (restart-safe, session-cumulative).</summary>
        public long Verified { get { lock (_sync) return _verified; } }

        /// <summary>Runs whose emitted output IME's saved result corrected (restart-safe, session-cumulative).</summary>
        public long Corrected { get { lock (_sync) return _corrected; } }

        public void RestoreCounters(long verified, long corrected)
        {
            lock (_sync)
            {
                _verified = verified;
                _corrected = corrected;
            }
        }

        /// <summary>True once after a counter moved — the tracker then persists its state.</summary>
        public bool ConsumeCountersChanged()
        {
            lock (_sync)
            {
                var changed = _countersChanged;
                _countersChanged = false;
                return changed;
            }
        }

        private void TrimLocked()
        {
            while (_latest.Count > MaxRuns)
            {
                string oldestKey = null;
                var oldest = DateTime.MaxValue;
                foreach (var kv in _latest)
                {
                    if (kv.Value.StartSeenUtc < oldest)
                    {
                        oldest = kv.Value.StartSeenUtc;
                        oldestKey = kv.Key;
                    }
                }
                if (oldestKey == null) return;
                _latest.Remove(oldestKey);
            }
        }
    }
}
