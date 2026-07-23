using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Detects when a real user desktop becomes available (explorer.exe under a non-system user).
    /// Fires DesktopArrived event exactly once per agent lifetime.
    /// Used for enrollment completion in no-ESP scenarios (WDP v2, ESP disabled) and
    /// as a backup signal for AccountSetup phase correction.
    /// <para>
    /// Owner resolution (session 4d5a0b78 fix, 2026-06-11): primary path is a WTS session
    /// query (<c>WTSQuerySessionInformation</c> with WTSUserName/WTSDomainName) — fast and
    /// independent of the WinMgmt service; WMI <c>Win32_Process.GetOwner</c> is the fallback.
    /// On the affected device class GetOwner failed on every poll, so the owner was never
    /// resolved and the Desktop half of the completion AND-gate starved.
    /// </para>
    /// <para>
    /// <b>Liveness telemetry</b> (state-change-only, max 3 events per detector lifetime, NOT periodic):
    /// </para>
    /// <list type="bullet">
    ///   <item><c>desktop_detector_started</c> — fired once when Start() or ResetForRealUserSwitch() arms the polling timer.</item>
    ///   <item><c>desktop_detector_first_poll</c> — fired once after the first PollForDesktop() completes (proves Timer + WMI plumbing is alive).</item>
    ///   <item><c>desktop_detector_no_candidate</c> — fired once after the configurable threshold (default 10 min) of polling without ANY explorer.exe resolution (neither excluded-user nor real-user).</item>
    /// </list>
    /// <para>
    /// The three liveness events together distinguish three failure modes that all look identical
    /// when only <c>desktop_arrived</c> is missing: (a) DAD never started after reboot, (b) DAD
    /// started but timer/WMI dead, (c) DAD running but user never logged in.
    /// </para>
    /// </summary>
    public class DesktopArrivalDetector : IDisposable
    {
        private readonly AgentLogger _logger;
        private Timer _pollingTimer;
        private int _pollInProgress; // Interlocked reentrancy guard for PollForDesktop (ARCH-F8)
        private bool _desktopArrived;
        private bool _excludedUserTraced; // Emit trace event only once for excluded-user skips
        private const int PollingIntervalSeconds = 30;

        // Liveness state (Plan: DAD-liveness telemetry, 2026-05-15) — all flags are
        // single-shot per detector instance; ResetForRealUserSwitch re-arms them.
        private DateTime _startedAtUtc;
        private bool _firstPollDone;
        private int _pollCount;
        private bool _explorerEverSeen;            // any explorer.exe observed at all (session 0 OR not)
        private bool _anyNonZeroSessionEverSeen;   // explorer.exe in a non-SYSTEM session observed
        private int _wmiErrorCountSinceStart;
        private int _wtsErrorCountSinceStart;
        private bool _noCandidateFired;
        private readonly int _noCandidateTimeoutMinutes;

        // Test seams (session 4d5a0b78 fix): owner resolution is injectable so unit tests can
        // drive the WTS-primary / WMI-fallback order without a live session manager or WinMgmt.
        // Null = production defaults (GetSessionOwnerViaWts / GetProcessOwnerViaWmi).
        internal Func<int, string> SessionOwnerResolver { get; set; }
        internal Func<int, string> ProcessOwnerResolver { get; set; }

        // Test seam for the WinRT OOBE-state sample (null = OobeStateReader.Read).
        internal Func<string> OobeStateProvider { get; set; }

        // OOBE-state flip tracking (observational only — see OobeStateReader contract).
        // _oobeCompletedEmitted is one-shot per agent lifetime and deliberately NOT reset
        // by ResetForRealUserSwitch: the OOBE flip is a global OS transition, not per-user.
        private string _lastOobeState;
        private bool _oobeCompletedEmitted;

        /// <summary>
        /// System/service account names that should NOT be considered real users.
        /// </summary>
        private static readonly string[] ExcludedUserNames =
        {
            "SYSTEM",
            "LOCAL SERVICE",
            "NETWORK SERVICE",
            "DefaultUser0",
            "DefaultUser1",
            "defaultuser0",
            "defaultuser1"
        };

        /// <summary>
        /// Fired exactly once when a real user desktop is detected (explorer.exe under a real user).
        /// </summary>
        public event EventHandler DesktopArrived;

        /// <summary>
        /// Fired with the validated real-user owner string (<c>DOMAIN\User</c> or
        /// <c>User</c>) right before <see cref="DesktopArrived"/>. Used by the composition
        /// root to resolve the user's SID (<c>UserSidResolver</c>) so the
        /// <see cref="RealmJoinWatcher"/> can attach its HKU-scope package watcher.
        /// Carries the raw owner to keep the SID-resolution logic out of this detector;
        /// the consumer is responsible for any PII handling.
        /// </summary>
        public event EventHandler<string> RealUserOwnerObserved;

        /// <summary>
        /// Optional callback for trace events (decision, reason, context).
        /// Wired by MonitoringService to emit agent_trace events to the backend.
        /// </summary>
        public Action<string, string, Dictionary<string, object>> OnTraceEvent { get; set; }

        public DesktopArrivalDetector(AgentLogger logger, int noCandidateTimeoutMinutes = 10)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // Clamp negative values to 0 (disabled). Upper bound left open — admin config UI
            // already enforces a sane 60-min ceiling.
            _noCandidateTimeoutMinutes = noCandidateTimeoutMinutes < 0 ? 0 : noCandidateTimeoutMinutes;
        }

        public void Start()
        {
            _logger.Info($"DesktopArrivalDetector: starting (polling every {PollingIntervalSeconds}s, no-candidate threshold={_noCandidateTimeoutMinutes} min)");
            ArmTimer(startReason: "initial");
        }

        public void Stop()
        {
            _pollingTimer?.Dispose();
            _pollingTimer = null;
        }

        /// <summary>
        /// Resets desktop-arrival tracking after a placeholder→real-user transition (Hybrid
        /// User-Driven completion-gap fix, 2026-05-01). Used by the composition root when
        /// <see cref="AadJoinWatcher.AadUserJoined"/> fires for a real user — the previous
        /// fooUser desktop the detector observed is invalidated, and polling restarts so the
        /// AD-user desktop after the Hybrid reboot is detected as the actual real desktop.
        /// <para>
        /// Idempotent: safe to call multiple times. No-op if the detector already fired
        /// after the reset (a subsequent real-user join after a real-user desktop is also
        /// idempotent because the polling timer is restarted regardless and the next match
        /// against IsExcludedUser will short-circuit on the still-valid desktop).
        /// </para>
        /// </summary>
        public void ResetForRealUserSwitch()
        {
            _logger.Info("DesktopArrivalDetector: reset for real-user switch (placeholder→real user transition)");

            // Drop any prior arrival state — subsequent polls must re-evaluate the current
            // explorer.exe owner against IsExcludedUser anew.
            _desktopArrived = false;
            _excludedUserTraced = false;

            // Reset liveness state too — the post-reset polling cycle is effectively a fresh
            // detector lifetime for diagnostic purposes (first_poll fires again, no_candidate
            // threshold restarts from this moment).
            _firstPollDone = false;
            _pollCount = 0;
            _explorerEverSeen = false;
            _anyNonZeroSessionEverSeen = false;
            _wmiErrorCountSinceStart = 0;
            _wtsErrorCountSinceStart = 0;
            _noCandidateFired = false;

            ArmTimer(startReason: "real-user-switch-reset");
        }

        /// <summary>
        /// (Re-)arms the polling timer + emits the <c>desktop_detector_started</c> liveness
        /// event. Dispose any existing timer first to avoid leaking ticks. Idempotent except
        /// for the emit — every Start/Reset call is a real lifecycle event we want visible.
        /// </summary>
        private void ArmTimer(string startReason)
        {
            _startedAtUtc = DateTime.UtcNow;

            // Baseline OOBE-state sample so the first poll can already detect a flip
            // (self-contained try/catch inside, like the Emit* helpers).
            CheckOobeStateFlip();

            _pollingTimer?.Dispose();
            _pollingTimer = new Timer(
                PollForDesktop,
                null,
                TimeSpan.FromSeconds(5), // Initial check after 5s
                TimeSpan.FromSeconds(PollingIntervalSeconds));

            EmitDetectorStarted(startReason);
        }

        private void PollForDesktop(object state)
        {
            if (_desktopArrived)
                return;

            // Reentrancy guard (review ARCH-F8): WMI GetOwner can run long enough that the 30 s
            // timer queues a second callback before the first returns. Without this, two
            // overlapping polls could both pass the _desktopArrived check and double-fire
            // RealUserOwnerObserved → RealmJoin HKU-watcher arming. Skip if a poll is in flight;
            // the finally below always clears the flag (covers the mid-method success return).
            if (Interlocked.CompareExchange(ref _pollInProgress, 1, 0) != 0)
                return;

            _pollCount++;

            CheckOobeStateFlip();

            try
            {
                var explorerProcesses = Process.GetProcessesByName("explorer");
                int explorerCountThisPoll = explorerProcesses.Length;
                if (explorerCountThisPoll > 0)
                    _explorerEverSeen = true;

                foreach (var proc in explorerProcesses)
                {
                    try
                    {
                        // Session 0 = SYSTEM session, skip
                        if (proc.SessionId == 0)
                            continue;

                        _anyNonZeroSessionEverSeen = true;

                        var owner = ResolveOwner(proc.Id, proc.SessionId);
                        if (string.IsNullOrEmpty(owner))
                            continue;

                        // Check against exclusion list
                        if (IsExcludedUser(owner))
                        {
                            _logger.Debug($"DesktopArrivalDetector: explorer.exe PID {proc.Id} owned by excluded user '{owner}' — skipping");

                            // Trace this decision once so it's visible in the backend
                            if (!_excludedUserTraced)
                            {
                                _excludedUserTraced = true;
                                try
                                {
                                    OnTraceEvent?.Invoke(
                                        "desktop_excluded_user",
                                        $"explorer.exe found but owned by excluded user '{owner}' — not a real user desktop",
                                        new Dictionary<string, object> { { "pid", proc.Id }, { "session", proc.SessionId }, { "owner", owner } });
                                }
                                catch (Exception ex) { _logger.Verbose($"DesktopArrivalDetector: OnTraceEvent failed: {ex.Message}"); }
                            }
                            continue;
                        }

                        // Real user desktop detected
                        _desktopArrived = true;
                        _logger.Info($"DesktopArrivalDetector: real user desktop detected (explorer.exe PID {proc.Id}, session {proc.SessionId}, user '{owner}')");

                        try
                        {
                            OnTraceEvent?.Invoke(
                                "desktop_real_user_detected",
                                $"Real user desktop detected (explorer.exe PID {proc.Id}, user '[redacted]')",
                                new Dictionary<string, object> { { "pid", proc.Id }, { "session", proc.SessionId }, { "owner", "[redacted]" } });
                        }
                        catch (Exception ex) { _logger.Verbose($"DesktopArrivalDetector: OnTraceEvent failed: {ex.Message}"); }

                        // First-poll observability snapshot still fires before we stop polling —
                        // there is value in distinguishing "found on first poll" (Stage already
                        // past AccountSetup at agent start) from "took N polls" (live transition).
                        EmitFirstPollIfNeeded(explorerCountThisPoll);

                        // Stop polling
                        _pollingTimer?.Dispose();
                        _pollingTimer = null;

                        // Surface the raw owner so the composition root can resolve the SID for
                        // the RealmJoinWatcher's HKU-scope attach. Fire BEFORE DesktopArrived so
                        // dependent host wiring is in place by the time downstream handlers run.
                        try { RealUserOwnerObserved?.Invoke(this, owner); }
                        catch (Exception ex) { _logger.Warning($"DesktopArrivalDetector: RealUserOwnerObserved handler failed: {ex.Message}"); }

                        try { DesktopArrived?.Invoke(this, EventArgs.Empty); }
                        catch (Exception ex) { _logger.Warning($"DesktopArrivalDetector: DesktopArrived handler failed: {ex.Message}"); }
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"DesktopArrivalDetector: error checking explorer.exe PID {proc.Id}: {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                // No resolution this poll (no real user found, no excluded user, or all
                // candidates were session 0). Emit first-poll snapshot now if pending.
                EmitFirstPollIfNeeded(explorerCountThisPoll);

                // Threshold-triggered observability: after the configured timeout, emit a
                // single no-candidate event so dashboards can distinguish "user never logged
                // in" from "detector broken". Fires exactly once per detector lifetime.
                EmitNoCandidateIfThresholdReached();
            }
            catch (Exception ex)
            {
                _wmiErrorCountSinceStart++;
                _logger.Debug($"DesktopArrivalDetector: polling error: {ex.Message}");

                // Still emit the first-poll snapshot if this was the first poll — the error
                // count carries the failure mode to the backend.
                EmitFirstPollIfNeeded(explorerProcessCount: 0);
            }
            finally
            {
                Interlocked.Exchange(ref _pollInProgress, 0);
            }
        }

        /// <summary>
        /// One-shot liveness emit after the first <see cref="PollForDesktop"/> completes,
        /// regardless of outcome. Proves the Timer + Process.GetProcessesByName path is alive
        /// — distinguishing "DAD started but timer/WMI dead" from "DAD running, no user logged
        /// in yet" in failed sessions.
        /// </summary>
        private void EmitFirstPollIfNeeded(int explorerProcessCount)
        {
            if (_firstPollDone) return;
            _firstPollDone = true;

            try
            {
                var elapsedMs = Math.Round((DateTime.UtcNow - _startedAtUtc).TotalMilliseconds, 0);
                OnTraceEvent?.Invoke(
                    SharedEventTypes.DesktopDetectorFirstPoll,
                    $"DAD first poll complete (explorer.exe count={explorerProcessCount}, elapsedMs={elapsedMs}).",
                    new Dictionary<string, object>
                    {
                        { "elapsedMsSinceStart", elapsedMs },
                        { "explorerProcessCount", explorerProcessCount },
                        { "anyNonZeroSessionObserved", _anyNonZeroSessionEverSeen },
                        { "wmiErrorCount", _wmiErrorCountSinceStart },
                        { "wtsErrorCount", _wtsErrorCountSinceStart },
                    });
            }
            catch (Exception ex) { _logger.Verbose($"DesktopArrivalDetector: first-poll emit failed: {ex.Message}"); }
        }

        /// <summary>
        /// One-shot threshold-triggered emit: when polling has run for the configured number
        /// of minutes without ANY resolution (real-user OR excluded-user) AND the threshold
        /// is enabled (&gt; 0), fire a single <c>desktop_detector_no_candidate</c> event.
        /// Distinguishes "user never logged in" from "DAD wiring broken" in sessions where
        /// <c>desktop_arrived</c> never lands.
        /// </summary>
        private void EmitNoCandidateIfThresholdReached()
        {
            if (_noCandidateFired) return;
            if (_noCandidateTimeoutMinutes <= 0) return; // disabled
            if (_excludedUserTraced) return; // we already emitted SOME resolution observability via desktop_excluded_user

            var minutesSinceStart = (DateTime.UtcNow - _startedAtUtc).TotalMinutes;
            if (minutesSinceStart < _noCandidateTimeoutMinutes) return;

            _noCandidateFired = true;
            try
            {
                OnTraceEvent?.Invoke(
                    SharedEventTypes.DesktopDetectorNoCandidate,
                    $"DAD threshold {_noCandidateTimeoutMinutes} min reached without explorer.exe resolution (polls={_pollCount}, explorerEverSeen={_explorerEverSeen}).",
                    new Dictionary<string, object>
                    {
                        { "pollsSinceStart", _pollCount },
                        { "minutesSinceStart", Math.Round(minutesSinceStart, 1) },
                        { "thresholdMinutes", _noCandidateTimeoutMinutes },
                        { "explorerEverSeen", _explorerEverSeen },
                        { "anyNonZeroSessionEverSeen", _anyNonZeroSessionEverSeen },
                        { "wmiErrorCount", _wmiErrorCountSinceStart },
                        { "wtsErrorCount", _wtsErrorCountSinceStart },
                    });
            }
            catch (Exception ex) { _logger.Verbose($"DesktopArrivalDetector: no-candidate emit failed: {ex.Message}"); }
        }

        /// <summary>
        /// One-shot lifecycle emit at Start/Reset. Confirms detector wiring after a post-
        /// reboot agent restart — absence of this event after <c>agent_started</c> proves the
        /// composition-root never instantiated/armed the detector.
        /// </summary>
        private void EmitDetectorStarted(string startReason)
        {
            try
            {
                OnTraceEvent?.Invoke(
                    SharedEventTypes.DesktopDetectorStarted,
                    $"DAD armed (startReason={startReason}, pollIntervalSeconds={PollingIntervalSeconds}, noCandidateThresholdMinutes={_noCandidateTimeoutMinutes}).",
                    new Dictionary<string, object>
                    {
                        { "pollIntervalSeconds", PollingIntervalSeconds },
                        { "noCandidateThresholdMinutes", _noCandidateTimeoutMinutes },
                        { "startReason", startReason },
                    });
            }
            catch (Exception ex) { _logger.Verbose($"DesktopArrivalDetector: detector-started emit failed: {ex.Message}"); }
        }

        /// <summary>
        /// Samples the WinRT OOBE state (<see cref="OobeStateReader"/>) and emits the one-shot
        /// <c>oobe_state_completed</c> trace event on the in_progress-&gt;completed flip.
        /// Observational only — an owner-independent desktop corroboration for sessions where
        /// WTS+WMI owner resolution starves and <c>desktop_arrived</c> never fires. Often absent
        /// in healthy sessions: the polling timer is disposed at desktop arrival, which is when
        /// the flip lands (empirical probe, session 9c404ae9).
        /// <para>
        /// Self-contained try/catch: a throw here must NOT reach <see cref="PollForDesktop"/>'s
        /// outer catch, which would inflate <see cref="_wmiErrorCountSinceStart"/> and skew the
        /// session-4d5a0b78 liveness diagnostics. "unavailable" readings never overwrite the last
        /// known state, so a transient reflection failure between two polls cannot mask the flip.
        /// One-shot flag survives <see cref="ResetForRealUserSwitch"/> — the flip is a global OS
        /// transition, not per-user.
        /// </para>
        /// </summary>
        private void CheckOobeStateFlip()
        {
            try
            {
                var current = (OobeStateProvider ?? OobeStateReader.Read)();
                if (string.IsNullOrEmpty(current) || current == OobeStateReader.Unavailable)
                    return;

                if (!_oobeCompletedEmitted
                    && _lastOobeState == "in_progress"
                    && current == "completed")
                {
                    _oobeCompletedEmitted = true;
                    var minutesSinceStart = Math.Round((DateTime.UtcNow - _startedAtUtc).TotalMinutes, 1);
                    _logger.Info("DesktopArrivalDetector: OOBE state flipped in_progress->completed (WinRT SystemSetupInfo)");
                    OnTraceEvent?.Invoke(
                        SharedEventTypes.OobeStateCompleted,
                        $"OOBE state flipped in_progress->completed (WinRT SystemSetupInfo, poll {_pollCount}) — owner-independent desktop corroboration.",
                        new Dictionary<string, object>
                        {
                            { "previousState", "in_progress" },
                            { "pollCount", _pollCount },
                            { "minutesSinceStart", minutesSinceStart },
                            { "desktopArrived", _desktopArrived },
                            { "wmiErrorCount", _wmiErrorCountSinceStart },
                            { "wtsErrorCount", _wtsErrorCountSinceStart },
                        });
                }

                _lastOobeState = current;
            }
            catch (Exception ex)
            {
                _logger.Verbose($"DesktopArrivalDetector: OOBE state check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Drives a single synchronous poll for unit tests — the production poll is private and
        /// timer-driven with a fixed 30 s interval (precedent: <see cref="ResolveOwner"/> is
        /// likewise internal for direct testing).
        /// </summary>
        internal void PollOnceForTest() => PollForDesktop(null);

        /// <summary>
        /// Resolves the owning user of an explorer.exe candidate. Session 4d5a0b78 fix
        /// (2026-06-11): the WMI <c>Win32_Process.GetOwner</c> call failed on every poll on
        /// some devices (wmiErrorCount == pollCount) while the agent ran for over an hour —
        /// the owner was never resolved, <c>desktop_arrived</c> never fired, and the Desktop
        /// half of the completion AND-gate starved. WTS is now the primary path: a single
        /// kernel round-trip against the session manager (no WinMgmt dependency, much
        /// cheaper than a WMI query), keyed by the process's session id — explorer.exe runs
        /// as the session's logged-on user. WMI remains the fallback when WTS returns no
        /// user for the session.
        /// <para>
        /// Returns "DOMAIN\User" or "User", or null when both paths fail. Each path's
        /// failures are counted separately (<see cref="_wtsErrorCountSinceStart"/> /
        /// <see cref="_wmiErrorCountSinceStart"/>) and surfaced via the first_poll /
        /// no_candidate liveness payloads.
        /// </para>
        /// </summary>
        internal string ResolveOwner(int processId, int sessionId)
        {
            string owner = null;
            var wtsErrored = false;
            try
            {
                if (SessionOwnerResolver != null)
                    owner = SessionOwnerResolver(sessionId);
                else
                    owner = ProcessOwnerLookup.ViaWts(sessionId, out wtsErrored);
            }
            catch (Exception ex)
            {
                wtsErrored = true;
                _logger.Debug($"DesktopArrivalDetector: WTS owner resolution threw for session {sessionId}: {ex.Message}");
            }
            if (!string.IsNullOrEmpty(owner))
                return owner;

            // L15: count only real API failures. A session without a logged-on user yet is the
            // normal OOBE polling case and must not inflate the liveness payloads' error rate
            // (the WMI counter next door has failure-only semantics — keep them comparable).
            if (wtsErrored)
                _wtsErrorCountSinceStart++;
            return (ProcessOwnerResolver ?? GetProcessOwnerViaWmi)(processId);
        }

        /// <summary>
        /// Default WMI fallback seam. Delegates the <c>Win32_Process.GetOwner</c> query to the
        /// shared <see cref="ProcessOwnerLookup"/> and bumps <see cref="_wmiErrorCountSinceStart"/>
        /// when the query errored, so the first_poll / no_candidate liveness payloads keep
        /// reporting the real GetOwner failure rate (P3 fix 2026-05-15). Tests replace this seam
        /// via <see cref="ProcessOwnerResolver"/>.
        /// </summary>
        private string GetProcessOwnerViaWmi(int processId)
        {
            var owner = ProcessOwnerLookup.ViaWmi(processId, out var wmiErrored);
            if (wmiErrored)
                _wmiErrorCountSinceStart++;
            return owner;
        }

        /// <summary>
        /// Returns true if the user name matches any excluded system/service account.
        /// Handles "User", "DOMAIN\User", and UPN ("user@domain") formats.
        /// Also matches the patterns DefaultUser* and the Autopilot provisioning placeholders
        /// foouser@* / autopilot@* (case-insensitive). The placeholder match prevents the
        /// fooUser OOBE shell on Hybrid User-Driven enrollments from being treated as a
        /// real user desktop.
        /// </summary>
        internal static bool IsExcludedUser(string fullUserName)
        {
            if (string.IsNullOrEmpty(fullUserName))
                return true;

            // UPN form (user@domain) — delegate to the same placeholder oracle the
            // AadJoinWatcher uses, so the foouser@/autopilot@ list stays in one place.
            if (fullUserName.IndexOf('@') >= 0
                && AadJoinInfo.IsPlaceholderUserEmail(fullUserName))
                return true;

            // Extract just the username part (after backslash if present)
            var userName = fullUserName;
            var backslashIndex = fullUserName.LastIndexOf('\\');
            if (backslashIndex >= 0 && backslashIndex < fullUserName.Length - 1)
                userName = fullUserName.Substring(backslashIndex + 1);

            // Check exact matches
            foreach (var excluded in ExcludedUserNames)
            {
                if (string.Equals(userName, excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check DefaultUser* pattern
            if (userName.StartsWith("DefaultUser", StringComparison.OrdinalIgnoreCase))
                return true;

            // DOMAIN\foouser / DOMAIN\autopilot — the WMI GetOwner call sometimes returns a
            // synthetic local-machine domain instead of the UPN. EXACT match the bare
            // username here (not prefix), so legitimate real accounts like
            // CONTOSO\autopilotadmin or DOMAIN\foouserservice stay through the gate.
            // Codex review 2026-05-01 (Finding 3): the previous prefix match was too broad
            // and reused via UserProfileResolver, which would have resolved to the wrong
            // user profile for any account starting with "autopilot" or "foouser".
            // The UPN form (foouser@*, autopilot@*) is still handled above by
            // AadJoinInfo.IsPlaceholderUserEmail — that path is what covers the
            // real-world Autopilot placeholders (foouser@<tenant>.onmicrosoft.com).
            if (string.Equals(userName, "foouser", StringComparison.OrdinalIgnoreCase)
                || string.Equals(userName, "autopilot", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
