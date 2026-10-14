using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Analyze;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Records a placed device block on the session it stopped: the <c>Block*</c> fields of the
    /// session row and a <c>device_blocked</c> timeline event, then an interim analyze run for
    /// the rules that evaluate on that event. The tenant sees the block only through this
    /// record — the block list itself is platform-only.
    /// </summary>
    public class SessionBlockRecorder
    {
        private readonly ISessionRepository _sessionRepo;
        private readonly IAnalyzeOnEnrollmentEndProducer _analyzeProducer;
        private readonly ILogger<SessionBlockRecorder> _logger;

        public SessionBlockRecorder(
            ISessionRepository sessionRepo, IAnalyzeOnEnrollmentEndProducer analyzeProducer, ILogger<SessionBlockRecorder> logger)
        {
            _sessionRepo = sessionRepo;
            _analyzeProducer = analyzeProducer;
            _logger = logger;
        }

        /// <summary>
        /// Call after the block is in force. Never throws and never undoes the block: a failed
        /// record costs the explanation, not the protection.
        /// </summary>
        public async Task RecordAsync(string tenantId, string sessionId, string serialNumber, SessionBlockRecord record)
        {
            try
            {
                var session = await _sessionRepo.GetSessionAsync(tenantId, sessionId);
                if (session == null)
                {
                    _logger.LogWarning(
                        "Block record skipped: session {SessionId} not found in tenant {TenantId}", sessionId, tenantId);
                    return;
                }

                // The session id comes from the caller; it must belong to the blocked device.
                if (!string.Equals(session.SerialNumber.Trim(), serialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Block record skipped: session {SessionId} (tenant {TenantId}) belongs to another device than {SerialNumber}",
                        sessionId, tenantId, serialNumber);
                    return;
                }

                if (!await _sessionRepo.RecordSessionBlockAsync(tenantId, sessionId, record))
                    return;

                var existing = await _sessionRepo.GetSessionEventsAsync(tenantId, sessionId);
                var evt = BuildDeviceBlockedEvent(session, record, existing);
                var batch = new List<EnrollmentEvent> { evt };
                await _sessionRepo.StoreEventsBatchAsync(batch);

                // Backend-authored events bypass the ingest processor: without the index upsert the
                // event is invisible to every search-by-eventType surface; without the recount the
                // stored EventCount misses it.
                await _sessionRepo.UpsertEventTypeIndexBatchAsync(tenantId, sessionId, batch);
                await _sessionRepo.ReconcileSessionCountersAsync(tenantId, sessionId);

                // The event never passes the ingest processor, whose batch scan enqueues the
                // on_event interim runs — enqueue the one for device_blocked here.
                await _analyzeProducer.EnqueueAsync(new AnalyzeOnEnrollmentEndEnvelope
                {
                    TenantId = tenantId,
                    SessionId = sessionId,
                    Reason = AnalyzeOnEnrollmentEndHandler.ReasonInterimTrigger,
                    TriggerEventTypes = new List<string> { evt.EventType },
                    EnqueuedAt = DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to record the device block on session {SessionId} (tenant {TenantId})", sessionId, tenantId);
            }
        }

        /// <summary>
        /// Builds the <c>device_blocked</c> event: one past the session's last Sequence and stamped
        /// now, so it sorts after everything the agent delivered before the block.
        /// </summary>
        internal static EnrollmentEvent BuildDeviceBlockedEvent(
            SessionSummary session, SessionBlockRecord record, IReadOnlyList<EnrollmentEvent> existingEvents)
        {
            var maxSequence = existingEvents != null && existingEvents.Count > 0
                ? existingEvents.Max(e => e.Sequence)
                : 0L;
            var isEventLimit = record.Cause == SessionBlockCauses.EventLimit;

            var data = new Dictionary<string, object>
            {
                ["action"] = record.Action,
                ["cause"] = record.Cause,
            };
            if (record.BlockedUntil.HasValue)
                data["blockedUntil"] = record.BlockedUntil.Value.ToString("o");
            if (isEventLimit && record.EventCount.HasValue)
                data["eventCount"] = record.EventCount.Value;
            if (isEventLimit && record.EventThreshold.HasValue)
                data["threshold"] = record.EventThreshold.Value;

            return new EnrollmentEvent
            {
                TenantId = session.TenantId,
                SessionId = session.SessionId,
                EventType = AutopilotMonitor.Shared.Constants.EventTypes.DeviceBlocked,
                Source = isEventLimit ? "System.Maintenance" : "System.Operator",
                Severity = EventSeverity.Warning,
                Phase = EnrollmentPhase.Unknown,
                Timestamp = record.BlockedAt,
                Sequence = maxSequence + 1,
                Message = BuildMessage(record),
                Data = data,
            };
        }

        internal static string BuildMessage(SessionBlockRecord record)
        {
            var isKill = record.Action == DeviceBlockActions.Kill;
            var hours = record.BlockedUntil.HasValue
                ? Math.Max(1, (int)Math.Round((record.BlockedUntil.Value - record.BlockedAt).TotalHours))
                : 0;

            if (record.Cause == SessionBlockCauses.EventLimit && record.EventCount.HasValue && record.EventThreshold.HasValue)
            {
                var volume = string.Format(CultureInfo.InvariantCulture,
                    "this session sent {0:N0} events (limit {1:N0})", record.EventCount.Value, record.EventThreshold.Value);
                return isKill
                    ? $"Monitoring ended: {volume}; the agent removes itself and the device stays unmonitored for {hours} h."
                    : $"Monitoring paused: {volume}, so the device's uploads are blocked for {hours} h.";
            }

            return isKill
                ? $"Monitoring ended by the platform operator; the agent removes itself and the device stays unmonitored for {hours} h."
                : $"Monitoring paused by the platform operator for {hours} h.";
        }
    }
}
