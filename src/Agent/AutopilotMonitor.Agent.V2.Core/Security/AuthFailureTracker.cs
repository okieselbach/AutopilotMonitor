using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Tracks consecutive 401/403 responses from the backend. When either the consecutive-count
    /// ceiling (<c>MaxAuthFailures</c>) or the elapsed-time ceiling (<c>AuthFailureTimeoutMinutes</c>)
    /// is exceeded, <see cref="ThresholdExceeded"/> fires exactly once so <c>Program.RunAgent</c>
    /// can trigger a soft shutdown.
    /// <para>
    /// Without this tracker a device whose certificate is revoked (or whose tenant has been deleted)
    /// would retry the config fetch and every telemetry upload indefinitely, flooding the backend
    /// distress channel and the local log. The tracker is the missing enforcement for the
    /// <c>MaxAuthFailures</c> and <c>AuthFailureTimeoutMinutes</c> knobs on
    /// <c>AgentConfigResponse</c> (defaults: 5 consecutive, time window disabled).
    /// </para>
    /// <para>
    /// V1 parity (<c>EventUploadOrchestrator.HandleAuthFailure</c>): the tracker is the single
    /// dispatch point for distress reports. The <b>first</b> failure emits a distress signal
    /// via the optional <see cref="DistressReporter"/> with
    /// <see cref="DistressErrorType.AuthCertificateRejected"/> for 401 and
    /// <see cref="DistressErrorType.DeviceNotRegistered"/> for 403. Subsequent failures are
    /// logged but do not spam the distress channel — there is no point asking the backend
    /// twice about a certificate it already rejected.
    /// </para>
    /// <para>
    /// Only a 401/403 the backend authored counts (<see cref="ResponseOriginClassifier"/>). One
    /// from something on the network path (TLS inspection, a proxy page) never counts and never
    /// touches the streak — it says nothing about this device's authorization — and is reported
    /// once per process as <see cref="DistressErrorType.ClientCertificateNotReceived"/>.
    /// </para>
    /// <para>
    /// Thread-safety: consecutive-count uses <see cref="Interlocked"/>; the first-failure timestamp
    /// is protected by a single monitor lock. <see cref="ThresholdExceeded"/> is raised outside the
    /// lock to prevent handler re-entrancy deadlocks.
    /// </para>
    /// </summary>
    public sealed class AuthFailureTracker
    {
        private readonly IClock _clock;
        private readonly AgentLogger _logger;
        private readonly DistressReporter _distressReporter;
        private readonly object _windowLock = new object();

        private int _maxFailures;              // 0 = disabled
        private TimeSpan? _timeoutWindow;      // null = disabled
        private int _consecutiveFailures;
        private DateTime? _firstFailureUtc;
        private int _thresholdFired;           // 0/1 — Interlocked exchange makes the event single-shot
        private int _distressDispatched;       // 0/1 per failure streak — reset by RecordSuccess
        private int _foreignDistressDispatched; // 0/1 per process — never reset
        private Task _foreignDistressSend;     // the per-process ClientCertificateNotReceived send; null until dispatched

        public AuthFailureTracker(
            int maxFailures,
            int timeoutMinutes,
            IClock clock,
            AgentLogger logger,
            DistressReporter distressReporter = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _distressReporter = distressReporter;
            UpdateLimits(maxFailures, timeoutMinutes);
        }

        /// <summary>Fires once when either threshold is crossed. Listener is responsible for shutting the agent down.</summary>
        public event EventHandler<AuthFailureThresholdEventArgs> ThresholdExceeded;

        /// <summary>Updates the limits from the merged remote config. Safe to call at any time; does not reset the counter.</summary>
        public void UpdateLimits(int maxFailures, int timeoutMinutes)
        {
            _maxFailures = maxFailures < 0 ? 0 : maxFailures;
            _timeoutWindow = timeoutMinutes > 0 ? (TimeSpan?)TimeSpan.FromMinutes(timeoutMinutes) : null;
        }

        /// <summary>Current consecutive-failure count. Exposed for observability and tests.</summary>
        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        /// <summary>Reset counter + window anchor after a successful authenticated response.</summary>
        public void RecordSuccess()
        {
            if (Interlocked.Exchange(ref _consecutiveFailures, 0) == 0) return; // no-op if already zero
            Interlocked.Exchange(ref _distressDispatched, 0);
            lock (_windowLock) { _firstFailureUtc = null; }
        }

        /// <summary>
        /// Record a 401/403 from the given <paramref name="operation"/>. V1 parity — the first
        /// qualifying failure of a streak fires a single distress report via the
        /// constructor-injected <see cref="DistressReporter"/> (401 → AuthCertificateRejected,
        /// 403 → DeviceNotRegistered). Subsequent failures are logged + counted but do not
        /// dispatch further distress signals. Emits <see cref="ThresholdExceeded"/> the first
        /// time either ceiling is crossed; subsequent calls after termination is armed are no-ops.
        /// </summary>
        /// <param name="classification">
        /// Who answered (<see cref="ResponseOriginClassifier"/>); null = the backend.
        /// <see cref="ResponseOrigin.PlatformStopped"/> counts but suppresses the distress dispatch:
        /// the report would go to the very endpoint that just answered with its stopped page, and
        /// DeviceNotRegistered would be wrong anyway; a later failure in the same streak from a live
        /// backend still dispatches the streak's report. <see cref="ResponseOrigin.Foreign"/> never
        /// counts — see <see cref="RecordForeignResponse"/>.
        /// </param>
        public void RecordFailure(int statusCode, string operation, ResponseClassification classification = null)
        {
            if (classification?.IsForeign == true)
            {
                RecordForeignResponse(statusCode, operation, classification);
                return;
            }

            if (Volatile.Read(ref _thresholdFired) == 1) return;

            var count = Interlocked.Increment(ref _consecutiveFailures);
            DateTime now = _clock.UtcNow;
            DateTime firstFailureAt;

            lock (_windowLock)
            {
                if (_firstFailureUtc == null) _firstFailureUtc = now;
                firstFailureAt = _firstFailureUtc.Value;
            }

            // V1 parity — only one distress report per failure streak. Repeat hits use the local
            // log only; the backend already knows from the first report that the device is
            // unauthorized. Stopped-endpoint failures never dispatch (nothing to send to) but
            // also never consume the streak's report slot.
            if (classification?.Origin == ResponseOrigin.PlatformStopped)
            {
                if (_distressReporter != null && Volatile.Read(ref _distressDispatched) == 0)
                {
                    _logger.Info(
                        $"AuthFailureTracker: distress report suppressed for {operation} (http {statusCode}) — " +
                        "endpoint unavailable (platform error page), a report cannot reach a stopped endpoint.");
                }
            }
            else if (_distressReporter != null &&
                     Interlocked.CompareExchange(ref _distressDispatched, 1, 0) == 0)
            {
                var distressType = statusCode == 403
                    ? DistressErrorType.DeviceNotRegistered
                    : DistressErrorType.AuthCertificateRejected;
                try
                {
                    _ = _distressReporter.TrySendAsync(
                        distressType,
                        $"Backend returned {statusCode} during {operation}",
                        httpStatusCode: statusCode);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"AuthFailureTracker: distress dispatch threw: {ex.Message}");
                }
            }

            bool countExceeded = _maxFailures > 0 && count >= _maxFailures;
            bool windowExceeded = _timeoutWindow.HasValue && (now - firstFailureAt) >= _timeoutWindow.Value;

            if (!countExceeded && !windowExceeded)
            {
                _logger.Warning(
                    $"Authentication failure {count}/{_maxFailures} (first failure at {firstFailureAt:HH:mm:ss}, " +
                    $"http {statusCode}, {operation}).");
                return;
            }

            if (Interlocked.Exchange(ref _thresholdFired, 1) == 1) return; // another thread won the race

            var reason = countExceeded
                ? $"consecutive auth failures reached limit ({count} >= {_maxFailures})"
                : $"auth-failure time window exceeded ({(now - firstFailureAt).TotalMinutes:F0}min >= {_timeoutWindow.Value.TotalMinutes:F0}min)";

            _logger.Error(
                $"=== AGENT SHUTDOWN: {count} consecutive authentication failures (401/403) — " +
                "backend rejecting requests. Terminating agent to prevent distress spam. ===");
            _logger.Error($"AuthFailureTracker: {reason}. Last operation: {operation}, statusCode={statusCode}.");

            try
            {
                ThresholdExceeded?.Invoke(this, new AuthFailureThresholdEventArgs(
                    consecutiveFailures: count,
                    firstFailureUtc: firstFailureAt,
                    lastOperation: operation,
                    lastStatusCode: statusCode,
                    reason: reason));
            }
            catch (Exception ex)
            {
                _logger.Warning($"AuthFailureTracker: ThresholdExceeded handler threw: {ex.Message}");
            }
        }

        /// <summary>
        /// A 401/403 that something on the network path authored. Never counts and never touches
        /// the streak — an interception says nothing about this device's authorization, and five
        /// of them must not shut a healthy agent down. The first one per process dispatches
        /// <see cref="DistressErrorType.ClientCertificateNotReceived"/>: the distress channel needs
        /// no client certificate, so it gets through where the request did not. Message order is
        /// operation, status, sub-reason, page title, server-certificate issuer (the reporter cuts
        /// at 256 characters).
        /// </summary>
        private void RecordForeignResponse(int statusCode, string operation, ResponseClassification classification)
        {
            var finding =
                $"{operation} http {statusCode} {classification.ForeignReason}; " +
                $"page \"{classification.PageTitle ?? "(none)"}\"; " +
                $"server cert issuer \"{classification.ServerCertIssuer ?? "(unknown)"}\"";
            _logger.Info($"AuthFailureTracker: answer did not come from the backend — not an authentication failure, not counted ({finding}).");

            if (_distressReporter == null || Interlocked.CompareExchange(ref _foreignDistressDispatched, 1, 0) != 0)
                return;

            try
            {
                // Fire-and-forget like every other dispatch here, but kept: a caller about to end
                // the process waits for it (WaitForPendingForeignDistress).
                Volatile.Write(ref _foreignDistressSend, _distressReporter.TrySendAsync(
                    DistressErrorType.ClientCertificateNotReceived, finding, httpStatusCode: statusCode));
            }
            catch (Exception ex)
            {
                _logger.Debug($"AuthFailureTracker: distress dispatch threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Gives this process's <see cref="DistressErrorType.ClientCertificateNotReceived"/> send up
        /// to <paramref name="bound"/> to leave the machine. The dispatch is fire-and-forget; a caller
        /// about to end the process (a start whose registration failed exits or cleans up right away)
        /// waits here so the report is not abandoned mid-flight. Returns true when nothing is pending
        /// or the send finished within the bound. Never throws.
        /// </summary>
        public bool WaitForPendingForeignDistress(TimeSpan bound)
        {
            var send = Volatile.Read(ref _foreignDistressSend);
            if (send == null || send.IsCompleted) return true;
            try
            {
                return send.Wait(bound);
            }
            catch (Exception)
            {
                return true; // faulted or cancelled: finished, nothing left to wait for
            }
        }
    }

    public sealed class AuthFailureThresholdEventArgs : EventArgs
    {
        public AuthFailureThresholdEventArgs(
            int consecutiveFailures,
            DateTime firstFailureUtc,
            string lastOperation,
            int lastStatusCode,
            string reason)
        {
            ConsecutiveFailures = consecutiveFailures;
            FirstFailureUtc = firstFailureUtc;
            LastOperation = lastOperation;
            LastStatusCode = lastStatusCode;
            Reason = reason;
        }

        public int ConsecutiveFailures { get; }
        public DateTime FirstFailureUtc { get; }
        public string LastOperation { get; }
        public int LastStatusCode { get; }
        public string Reason { get; }
    }
}
