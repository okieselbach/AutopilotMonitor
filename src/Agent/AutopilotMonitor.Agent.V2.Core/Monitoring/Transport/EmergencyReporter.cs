using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// Sends critical agent-side errors to the backend emergency channel endpoint.
    ///
    /// Anti-flood guarantees (per session):
    ///   - Only fires after <see cref="ConsecutiveFailureThreshold"/> consecutive upload failures
    ///   - Same error key (ErrorType + HTTP status code) is only ever sent once per session
    ///   - At most <see cref="MaxReportsPerSession"/> emergency reports total per session
    ///   - At most one report every <see cref="MinIntervalMinutes"/> minutes regardless of error type
    ///     (the terminal <see cref="AgentErrorType.TelemetryRejectedSessionUnknown"/> report is exempt)
    ///
    /// A failure in this reporter is silently swallowed — it must never cascade into the main loop.
    /// </summary>
    public class EmergencyReporter
    {
        /// <summary>
        /// Number of consecutive upload failures required before the first emergency report is sent.
        /// </summary>
        public const int ConsecutiveFailureThreshold = 3;

        private const int MaxReportsPerSession = 5;
        private const int MinIntervalMinutes = 10;

        private readonly BackendApiClient _apiClient;
        private volatile string _sessionId;
        private readonly string _tenantId;
        private readonly string _agentVersion;
        private readonly AgentLogger _logger;
        private readonly IClock _clock;

        private readonly HashSet<string> _reportedKeys = new HashSet<string>();
        private readonly object _antiFloodLock = new object();
        private DateTime? _lastReportTime;
        private int _reportCount;

        public EmergencyReporter(
            BackendApiClient apiClient,
            string sessionId,
            string tenantId,
            string agentVersion,
            AgentLogger logger,
            IClock clock = null)
        {
            _apiClient = apiClient;
            _sessionId = sessionId;
            _tenantId = tenantId;
            _agentVersion = agentVersion;
            _logger = logger;
            _clock = clock ?? SystemClock.Instance;
        }

        /// <summary>
        /// SESSION-OWNER-BINDING: the reporter is built before registration and snapshots the
        /// SessionId; a rotation during registration must be reflected here or later reports
        /// would name the refused session.
        /// </summary>
        public void UpdateSessionId(string sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                _sessionId = sessionId;
        }

        /// <summary>
        /// Attempts to send an emergency report. Returns immediately without awaiting
        /// (intended to be called as fire-and-forget via <c>_ = reporter.TrySendAsync(...)</c>).
        ///
        /// All anti-flood checks are applied synchronously before the async HTTP call,
        /// so repeated calls are cheap when suppressed. The anti-flood reservation is
        /// taken up front (so concurrent same-key calls cannot double-send) but ROLLED
        /// BACK when every attempt fails: the budget and the once-per-session dedup are
        /// meant to count DELIVERED reports — the previous mark-before-send made the very
        /// first transport failure permanently suppress that error key for the session
        /// (found via the 48h emergency-break report, which is a single shot at boot).
        ///
        /// <paramref name="attempts"/>/<paramref name="perAttemptTimeout"/>/<paramref name="retryDelay"/>
        /// exist for callers whose process is about to exit and cannot come back later
        /// (emergency break): retries stay INSIDE the one reservation, so they never
        /// multiply against the per-session budget.
        ///
        /// <paramref name="sessionAgeHours"/> is the emergency break's measured session age
        /// (<see cref="AgentErrorReport.SessionAgeHours"/>) and <paramref name="priorRegistrationFailure"/>
        /// its record of earlier starts that never registered
        /// (<see cref="AgentErrorReport.PriorRegistrationFailure"/>); null for every other error type.
        /// </summary>
        public virtual async Task TrySendAsync(
            AgentErrorType errorType,
            string message,
            int? httpStatusCode = null,
            long? sequenceNumber = null,
            int attempts = 1,
            TimeSpan? perAttemptTimeout = null,
            TimeSpan? retryDelay = null,
            double? sessionAgeHours = null,
            RegistrationFailureSummary priorRegistrationFailure = null)
        {
            // Deduplicate by error type + status code: same failure category sent only once per session.
            // All anti-flood checks are protected by a lock to prevent race conditions when
            // TrySendAsync is called concurrently (fire-and-forget from multiple threads).
            var key = $"{errorType}:{httpStatusCode}";
            var now = _clock.UtcNow;
            int currentReportCount;
            DateTime? previousLastReportTime;

            lock (_antiFloodLock)
            {
                if (_reportCount >= MaxReportsPerSession)
                {
                    _logger?.Debug($"[EmergencyChannel] Suppressed ({_reportCount}/{MaxReportsPerSession} reports used): {key}");
                    return;
                }

                if (_reportedKeys.Contains(key))
                {
                    _logger?.Debug($"[EmergencyChannel] Suppressed (already reported): {key}");
                    return;
                }

                // The session-unknown report is terminal and sent at most once per process (the
                // termination follows immediately): a routine report minutes earlier must not cost
                // the last word about the session. Only the cooldown is waived; key dedup and the
                // per-session maximum still apply.
                var exemptFromCooldown = errorType == AgentErrorType.TelemetryRejectedSessionUnknown;
                if (!exemptFromCooldown && _lastReportTime != null &&
                    (now - _lastReportTime.Value).TotalMinutes < MinIntervalMinutes)
                {
                    _logger?.Debug($"[EmergencyChannel] Suppressed (cooldown active, last report at {_lastReportTime.Value:HH:mm:ss}): {key}");
                    return;
                }

                previousLastReportTime = _lastReportTime;
                _reportedKeys.Add(key);
                _reportCount++;
                _lastReportTime = now;
                currentReportCount = _reportCount;
            }

            var statusText = httpStatusCode.HasValue ? $" HTTP {httpStatusCode}" : string.Empty;
            _logger?.Warning($"[EmergencyChannel] Sending report {currentReportCount}/{MaxReportsPerSession}: {errorType}{statusText}");

            var report = new AgentErrorReport
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                ErrorType = errorType,
                Message = message,
                HttpStatusCode = httpStatusCode,
                SequenceNumber = sequenceNumber,
                SessionAgeHours = sessionAgeHours,
                PriorRegistrationFailure = priorRegistrationFailure,
                AgentVersion = _agentVersion,
                Timestamp = now
            };

            var delivered = false;
            var totalAttempts = attempts < 1 ? 1 : attempts;
            try
            {
                for (var attempt = 1; attempt <= totalAttempts && !delivered; attempt++)
                {
                    if (attempt > 1 && retryDelay.HasValue)
                    {
                        await Task.Delay(retryDelay.Value).ConfigureAwait(false);
                    }

                    delivered = await _apiClient.ReportAgentErrorAsync(report, perAttemptTimeout).ConfigureAwait(false);
                    if (!delivered && totalAttempts > 1)
                    {
                        _logger?.Debug($"[EmergencyChannel] Attempt {attempt}/{totalAttempts} failed: {key}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Swallow — emergency channel must never cascade failures into the caller
                _logger?.Debug($"[EmergencyChannel] Failed to send report: {ex.Message}");
            }

            if (!delivered)
            {
                lock (_antiFloodLock)
                {
                    _reportedKeys.Remove(key);
                    _reportCount--;
                    _lastReportTime = previousLastReportTime;
                }
                _logger?.Debug($"[EmergencyChannel] Report not delivered — reservation rolled back: {key}");
            }
        }
    }
}
