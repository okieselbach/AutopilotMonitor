#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Single-shot detector for the Hybrid User-Driven sign-in gap (2026-05-01 trigger:
    /// session e58bcfdb-3e68-4f23-a3c2-437429ca9e78). After the Hybrid AAD-Join reboot the
    /// agent restarts and waits for the user to sign in with their AD account. When nobody
    /// signs in the agent goes silent until the backend watchdog fires; this detector emits a
    /// <c>hybrid_login_pending</c> warning after a short single-shot timer so the operator sees
    /// an explicit "still waiting for the sign-in" signal in the timeline.
    /// <para>
    /// What it measures (rewritten 2026-09-04, session a7140f98): the ABSENCE of a real-user
    /// desktop. Two cancel paths: the DesktopArrivalDetector resolving explorer.exe under a
    /// real user (the actual sign-in evidence), and the AadJoinWatcher seeing a real user in
    /// JoinInfo (the Entra-join flavour, where the OOBE sign-in completes the join). On a
    /// Hybrid join the AD sign-in never replaces the JoinInfo placeholder — only the later
    /// Entra device registration does — so the placeholder state is reported as a fact in the
    /// payload, never as the reason for the warning.
    /// </para>
    /// <para>
    /// Conditions checked at <see cref="Arm"/> by the composition root: post-reboot,
    /// <c>isHybridJoin == true</c>, and not a WhiteGlove device (a sealed device legitimately
    /// waits days for its user; the session status AwaitingUser already describes that).
    /// The detector owns only the timer and the cancel paths. No polling. Fires at most once
    /// per agent process.
    /// </para>
    /// </summary>
    internal sealed class HybridLoginPendingDetector : IDisposable
    {
        internal const int DefaultDelayMinutes = 10;
        internal const string SourceLabel = "HybridLoginPendingDetector";

        private readonly AadJoinWatcher _watcher;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly TimeSpan _delay;
        private readonly object _lock = new object();

        private Timer? _timer;
        private bool _armed;
        private bool _fired;
        private bool _cancelledByRealUser;
        private bool _cancelledByDesktop;
        private int _disposed;

        internal HybridLoginPendingDetector(
            AadJoinWatcher watcher,
            InformationalEventPost post,
            AgentLogger logger,
            TimeSpan? delay = null)
        {
            _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _delay = delay ?? TimeSpan.FromMinutes(DefaultDelayMinutes);

            // Subscribe up-front so a real-user join that arrives BEFORE Arm() is still
            // recorded — _cancelledByRealUser short-circuits any later Arm() call.
            _watcher.AadUserJoined += OnAadUserJoined;
        }

        /// <summary>
        /// Starts the single-shot timer. Idempotent — repeated calls after the first arm
        /// are no-ops. Has no effect if a real-user desktop or a real AAD user was already
        /// observed (the arming was racy and the cancel won), or if the detector already fired.
        /// </summary>
        public void Arm()
        {
            lock (_lock)
            {
                if (_disposed != 0) return;
                if (_armed || _fired) return;
                if (_cancelledByDesktop)
                {
                    _logger.Info("HybridLoginPendingDetector: arm requested but a real-user desktop was already observed — skipped");
                    return;
                }
                if (_cancelledByRealUser)
                {
                    _logger.Info("HybridLoginPendingDetector: arm requested but real AAD user already joined — skipped");
                    return;
                }

                _armed = true;
                _logger.Info(
                    $"HybridLoginPendingDetector: armed — will emit hybrid_login_pending in {_delay.TotalMinutes:F0} min if no real-user desktop appears by then");

                _timer = new Timer(OnTimer, null, _delay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// The DesktopArrivalDetector resolved explorer.exe under a real user — the sign-in
        /// happened. Cancels a running timer; remembered so a later <see cref="Arm"/> is a no-op.
        /// </summary>
        public void NotifyRealUserDesktop()
        {
            lock (_lock)
            {
                _cancelledByDesktop = true;
                if (!_armed || _fired) return;

                _timer?.Dispose();
                _timer = null;
                _logger.Info("HybridLoginPendingDetector: cancelled — real-user desktop observed before timeout");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _watcher.AadUserJoined -= OnAadUserJoined; } catch { }
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        // Test seam — bypasses the timer schedule. Same lock + state guards as the real path.
        internal void TriggerFromTest() => OnTimer(null);

        // Test seam — synthesises an AadUserJoined arrival without going through the
        // watcher's private event-raise path. The watcher's events can only be invoked from
        // inside the watcher class, so production unit tests have no way to observe the
        // detector's cancel-on-real-user behavior otherwise.
        internal void TriggerRealUserJoinedFromTest() =>
            OnAadUserJoined(this, new AadUserJoinedEventArgs("test@example.com", "test-thumbprint"));

        internal bool IsArmedForTest { get { lock (_lock) { return _armed; } } }
        internal bool IsCancelledByRealUserForTest { get { lock (_lock) { return _cancelledByRealUser; } } }
        internal bool IsCancelledByDesktopForTest { get { lock (_lock) { return _cancelledByDesktop; } } }
        internal bool HasFiredForTest { get { lock (_lock) { return _fired; } } }

        private void OnTimer(object? state) => EmitInternal(reason: "timer_fired");

        private void OnAadUserJoined(object sender, AadUserJoinedEventArgs e)
        {
            lock (_lock)
            {
                _cancelledByRealUser = true;
                if (!_armed || _fired) return;

                _timer?.Dispose();
                _timer = null;
                _logger.Info("HybridLoginPendingDetector: cancelled — real AAD user joined before timeout");
            }
        }

        private void EmitInternal(string reason)
        {
            lock (_lock)
            {
                // _armed guard (Codex review 2026-05-01): production is safe because the
                // timer is only created in Arm(), but explicit guard makes the contract
                // unambiguous — emission requires a deliberate Arm(), full stop.
                if (!_armed || _fired || _cancelledByRealUser || _cancelledByDesktop || _disposed != 0) return;
                _fired = true;
                _timer?.Dispose();
                _timer = null;
            }

            var placeholderActive = _watcher.PlaceholderObservedWithoutRealUser ? "true" : "false";
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["delayMinutes"] = ((int)_delay.TotalMinutes).ToString(),
                ["reason"] = reason,
                ["isHybridJoin"] = "true",  // armed only when composition root verified this
                ["realUserDesktopSeen"] = "false",
                ["placeholderActive"] = placeholderActive,
            };

            _post.Emit(
                eventType: SharedConstants.EventTypes.HybridLoginPending,
                source: SourceLabel,
                message: $"Hybrid AAD Join: {(int)_delay.TotalMinutes} min after reboot still no real-user desktop — sign-in overdue",
                severity: EventSeverity.Warning,
                immediateUpload: true,
                data: data);

            _logger.Warning("HybridLoginPendingDetector: emitted hybrid_login_pending warning");
        }
    }
}
