using System;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for recording operational events into the OpsEvents table.
    /// Provides typed helper methods for each event category so callers
    /// don't need to construct OpsEventEntry manually.
    /// All writes are fire-and-forget safe — failures are logged but never thrown.
    /// </summary>
    public class OpsEventService
    {
        private readonly IOpsEventRepository _repository;
        private readonly ILogger<OpsEventService> _logger;
        private readonly OpsAlertDispatchService _alertDispatch;

        public OpsEventService(IOpsEventRepository repository, ILogger<OpsEventService> logger,
            OpsAlertDispatchService alertDispatch)
        {
            _repository = repository;
            _logger = logger;
            _alertDispatch = alertDispatch;
        }

        // ── Consent ────────────────────────────────────────────────────────────

        public Task RecordConsentFlowStartedAsync(string tenantId, string userId, string redirectUri)
            => WriteAsync(OpsEventCategory.Consent, "ConsentFlowStarted", OpsEventSeverity.Info,
                $"Admin consent flow started by {userId}",
                tenantId, userId, new { redirectUri });

        public Task RecordConsentFlowSuccessAsync(string tenantId, string userId, string trigger)
            => WriteAsync(OpsEventCategory.Consent, "ConsentFlowSuccess", OpsEventSeverity.Info,
                $"Admin consent confirmed for {trigger} by {userId}",
                tenantId, userId, new { trigger });

        public Task RecordConsentFlowFailedAsync(string tenantId, string userId, string error, string errorDescription)
            => WriteAsync(OpsEventCategory.Consent, "ConsentFlowFailed", OpsEventSeverity.Error,
                $"Admin consent failed: {error}",
                tenantId, userId, new { error, errorDescription });

        public Task RecordConsentRedirectUriMismatchAsync(string tenantId, string userId, string redirectUri, string redirectPath)
            => WriteAsync(OpsEventCategory.Consent, "ConsentRedirectUriMismatch", OpsEventSeverity.Critical,
                $"Redirect URI path '{redirectPath}' not in registered paths — consent will fail with AADSTS50011",
                tenantId, userId, new { redirectUri, redirectPath });

        // ── Maintenance ────────────────────────────────────────────────────────

        public Task RecordMaintenanceCompletedAsync(int durationMs, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "MaintenanceCompleted", OpsEventSeverity.Info,
                $"Maintenance completed in {durationMs}ms (triggered by {triggeredBy})",
                null, triggeredBy, new { durationMs });

        public Task RecordMaintenanceFailedAsync(string error, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "MaintenanceFailed", OpsEventSeverity.Error,
                $"Maintenance failed: {error}",
                null, triggeredBy, new { error });

        public Task RecordOpsEventCleanupAsync(int deletedCount, int retentionDays)
            => WriteAsync(OpsEventCategory.Maintenance, "OpsEventCleanup", OpsEventSeverity.Info,
                $"Cleaned up {deletedCount} ops events older than {retentionDays} days",
                null, "System.Maintenance", new { deletedCount, retentionDays });

        public Task RecordOrphanEventsCleanedAsync(int orphanSessions, int totalEventsDeleted)
            => WriteAsync(OpsEventCategory.Maintenance, "OrphanEventsCleaned", OpsEventSeverity.Warning,
                $"Cleaned {totalEventsDeleted} orphaned events across {orphanSessions} sessions",
                null, "System.Maintenance", new { orphanSessions, totalEventsDeleted });

        // ── Security ───────────────────────────────────────────────────────────

        public Task RecordDeviceBlockedAsync(string tenantId, string serialNumber, string reason, string blockedBy)
            => WriteAsync(OpsEventCategory.Security, "DeviceBlocked", OpsEventSeverity.Warning,
                $"Device {serialNumber} blocked: {reason}",
                tenantId, blockedBy, new { serialNumber, reason });

        public Task RecordExcessiveDataBlockedAsync(string tenantId, int devicesBlocked, int windowHours)
            => WriteAsync(OpsEventCategory.Security, "ExcessiveDataBlocked", OpsEventSeverity.Warning,
                $"{devicesBlocked} device(s) auto-blocked for excessive data (>{windowHours}h window)",
                tenantId, "System.Maintenance", new { devicesBlocked, windowHours });

        public Task RecordVersionBlockedAsync(string pattern, string blockedBy)
            => WriteAsync(OpsEventCategory.Security, "VersionBlocked", OpsEventSeverity.Warning,
                $"Agent version pattern '{pattern}' blocked",
                null, blockedBy, new { pattern });

        // ── Tenant ─────────────────────────────────────────────────────────────

        public Task RecordTenantOffboardedAsync(string tenantId, string performedBy, Dictionary<string, int> deletedCounts)
            => WriteAsync(OpsEventCategory.Tenant, "TenantOffboarded", OpsEventSeverity.Warning,
                $"Tenant {tenantId} offboarded — all data deleted",
                tenantId, performedBy, deletedCounts);

        // ── Agent ──────────────────────────────────────────────────────────────

        public Task RecordSessionTimeoutsAsync(string tenantId, int sessionCount, int timeoutHours)
            => WriteAsync(OpsEventCategory.Agent, "SessionTimeouts", OpsEventSeverity.Info,
                $"{sessionCount} session(s) timed out after {timeoutHours}h",
                tenantId, "System.Maintenance", new { sessionCount, timeoutHours });

        public Task RecordExcessiveSessionEventsAsync(string tenantId, string sessionId, int eventCount, int threshold)
            => WriteAsync(OpsEventCategory.Agent, "ExcessiveSessionEvents", OpsEventSeverity.Warning,
                $"Session {sessionId} has {eventCount} events (threshold {threshold}) — likely agent loop bug",
                tenantId, "System.Maintenance", new { sessionId, eventCount, threshold });

        public Task RecordNewImeVersionDetectedAsync(string version, string tenantId, string sessionId)
            => WriteAsync(OpsEventCategory.Agent, "NewImeVersionDetected", OpsEventSeverity.Warning,
                $"New IME agent version detected: {version}",
                tenantId, "System.Ingest", new { version, sessionId });

        public Task RecordBlobStorageMissingAsync(string missingItem, int statusCode)
            => WriteAsync(OpsEventCategory.Agent, "BlobStorageMissing", OpsEventSeverity.Critical,
                $"Agent blob storage check failed: {missingItem} is missing or unreachable (HTTP {statusCode})",
                null, "System.Maintenance", new { missingItem, statusCode });

        public Task RecordBlobStorageUnreachableAsync(string error)
            => WriteAsync(OpsEventCategory.Agent, "BlobStorageUnreachable", OpsEventSeverity.Critical,
                $"Agent blob storage unreachable: {error}",
                null, "System.Maintenance", new { error });

        // ── SLA ────────────────────────────────────────────────────────────────

        public Task RecordSlaBreachNotificationAsync(string tenantId, string breachType,
            double currentRate, double targetRate, int totalSessions, int failedSessions)
            => WriteAsync(OpsEventCategory.Sla, "SlaBreachNotification", OpsEventSeverity.Warning,
                $"SLA breach notification sent for tenant {tenantId}: {breachType} {currentRate:F1}% (target {targetRate:F1}%)",
                tenantId, "System.SlaEvaluation",
                new { breachType, currentRate, targetRate, totalSessions, failedSessions });

        public Task RecordSlaConsecutiveFailuresAsync(string tenantId, int count, string? lastDevice, string? lastReason)
            => WriteAsync(OpsEventCategory.Sla, "SlaConsecutiveFailures", OpsEventSeverity.Error,
                $"Consecutive failure alert for tenant {tenantId}: {count} failures in a row",
                tenantId, "System.SlaEvaluation",
                new { count, lastDevice, lastReason });

        public Task RecordSlaEvaluationCompletedAsync(int tenantsEvaluated, int breachesDetected, int notificationsSent, int durationMs)
            => WriteAsync(OpsEventCategory.Sla, "SlaEvaluationCompleted", OpsEventSeverity.Info,
                $"SLA evaluation: {tenantsEvaluated} tenants checked, {breachesDetected} breaches, {notificationsSent} notifications sent",
                null, "System.SlaEvaluation",
                new { tenantsEvaluated, breachesDetected, notificationsSent, durationMs });

        // ── Core write method ──────────────────────────────────────────────────

        private async Task WriteAsync(string category, string eventType, string severity,
            string message, string? tenantId, string? userId, object? details)
        {
            try
            {
                var entry = new OpsEventEntry
                {
                    Category  = category,
                    EventType = eventType,
                    Severity  = severity,
                    TenantId  = tenantId,
                    UserId    = userId,
                    Message   = message,
                    Details   = details != null ? JsonSerializer.Serialize(details) : null,
                    Timestamp = DateTime.UtcNow,
                };

                await _repository.SaveOpsEventAsync(entry);

                // Fire-and-forget: dispatch alerts to enabled providers.
                // TrySendAlerts has its own top-level try/catch so unobserved exceptions are safe.
                _ = _alertDispatch.DispatchAsync(category, eventType, severity, message, tenantId);
            }
            catch (Exception ex)
            {
                // Never throw from ops event recording — it must not break the calling flow
                _logger.LogWarning(ex, "Failed to record ops event {Category}/{EventType}", category, eventType);
            }
        }
    }
}
