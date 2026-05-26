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

        // ── Cascade-Delete Maintenance (Plan §5 PR6 / §16 R14) ─────────────────
        // Four event types dispatched by SessionDeletionMaintenanceFunction. Each is also
        // listed in OpsAlertRulesSection.tsx OPS_EVENT_TYPES (memory feedback_ops_event_types_dual_register).

        /// <summary>Watchdog: maintenance run still in flight 30 minutes after start. Warning-level early signal.</summary>
        public Task RecordSessionDeletionMaintenanceLongRunningAsync(int elapsedMinutes, int tenantsProcessed, int sessionsEnqueued)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceLongRunning", OpsEventSeverity.Warning,
                $"SessionDeletionMaintenance still running after {elapsedMinutes}min (tenants={tenantsProcessed}, enqueued={sessionsEnqueued})",
                null, "System.Maintenance", new { elapsedMinutes, tenantsProcessed, sessionsEnqueued });

        /// <summary>Watchdog: maintenance run still in flight 60 minutes after start. Error-level escalation in case the operator missed the 30min warning.</summary>
        public Task RecordSessionDeletionMaintenanceLongRunningSevereAsync(int elapsedMinutes, int tenantsProcessed, int sessionsEnqueued)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceLongRunningSevere", OpsEventSeverity.Error,
                $"SessionDeletionMaintenance has been running for {elapsedMinutes}min — Azure Functions host will abort at 60min (tenants={tenantsProcessed}, enqueued={sessionsEnqueued})",
                null, "System.Maintenance", new { elapsedMinutes, tenantsProcessed, sessionsEnqueued });

        /// <summary>Unhandled exception path. Re-thrown after this audit so the Azure Functions runtime records the failure.</summary>
        public Task RecordSessionDeletionMaintenanceFailedAsync(string exceptionType, string message, string stackPreview)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceFailed", OpsEventSeverity.Error,
                $"SessionDeletionMaintenance failed: {exceptionType}: {message}",
                null, "System.Maintenance", new { exceptionType, message, stackPreview });

        /// <summary>Stale Queued state detected (no worker pickup) — operator must inspect the manifest + progress blobs. No auto-clear.</summary>
        public Task RecordSessionDeletionStrandedQueuedAsync(string tenantId, string sessionId, DateTime queuedSince, string manifestId)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionStrandedQueued", OpsEventSeverity.Warning,
                $"Session {sessionId} stuck in DeletionState=Queued since {queuedSince:o} (manifestId={manifestId})",
                tenantId, "System.Maintenance", new { tenantId, sessionId, queuedSince = queuedSince.ToString("o"), manifestId });

        /// <summary>
        /// Cascade max-dequeue exhaustion (PR-B follow-up): the worker has moved the envelope to
        /// the poison queue and CAS-transitioned the Sessions row to <see cref="SessionDeletionState.Poisoned"/>.
        /// Replaces the prior <c>deletion_poisoned</c> tenant audit — tenant admins see only the
        /// lifecycle endpoints (<c>deletion_started</c>, <c>deletion_completed</c>, <c>deletion_restored</c>),
        /// while operators get this OpsEvent for the Session Cleanup admin page + Telegram routing.
        /// <para>
        /// Codex follow-ups F4 + F2: <paramref name="failureType"/> / <paramref name="failureMessage"/>
        /// / <paramref name="observedResidualCount"/> / <paramref name="residualSamplePreviewJson"/>
        /// are populated from <see cref="DeletionProgress"/> fields the handler writes before
        /// throwing. Worker reads the progress blob in its poison path and passes whatever is
        /// present (all are nullable so a worker that pre-dates the progress-schema bump still
        /// emits a useful event, just without root-cause data).
        /// </para>
        /// <para>
        /// <paramref name="observedResidualCount"/> is the verifier's <b>observed</b> count, not
        /// the true total: <c>CascadeVerificationService</c> caps at
        /// <see cref="DeletionProgressConstants.VerificationResidualSampleSize"/> rows per table
        /// and short-circuits after the first failing table. Operators reading this number should
        /// treat it as a lower bound, especially when it equals the cap.
        /// </para>
        /// <para>
        /// <paramref name="residualSamplePreviewJson"/> is a small (≤
        /// <see cref="DeletionProgressConstants.OpsEventResidualSamplePreviewSize"/>) preview that
        /// fits under the OpsEvents table's 4096-char Details truncation. The full progress-blob
        /// sample (up to <see cref="DeletionProgressConstants.VerificationResidualSampleSize"/>
        /// entries) is available via the Session Cleanup admin page's stored-manifest modal.
        /// </para>
        /// </summary>
        public Task RecordSessionDeletionPoisonedAsync(
            string tenantId, string sessionId, string manifestId, string reason, string messageId, int dequeueCount,
            string? failureType = null, string? failureMessage = null,
            int? observedResidualCount = null, string? residualSamplePreviewJson = null)
        {
            var cause = !string.IsNullOrEmpty(failureType)
                ? $" — cause: {failureType}{(string.IsNullOrEmpty(failureMessage) ? "" : $" ({failureMessage})")}"
                : string.Empty;
            return WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionPoisoned", OpsEventSeverity.Error,
                $"Session {sessionId} cascade poisoned after {dequeueCount} attempts (manifestId={manifestId}){cause}",
                tenantId, "System.Maintenance",
                new
                {
                    tenantId, sessionId, manifestId, reason, messageId, dequeueCount,
                    failureType, failureMessage, observedResidualCount,
                    residualSamplePreviewJson,
                });
        }

        /// <summary>
        /// Successful end of a <see cref="Functions.Maintenance.SessionDeletionMaintenanceFunction"/>
        /// run — records the per-block totals so dashboards can fold the cadence into the timeline.
        /// PR6 follow-up F3: replaces the prior <c>LogAuditEntryAsync(null!, ...)</c> call, which
        /// silently failed because the AuditLogs schema requires a non-null PartitionKey (tenantId).
        /// </summary>
        public Task RecordSessionDeletionMaintenanceCompletedAsync(
            bool killSwitchActive, int tenantsProcessed, int sessionsEnqueued,
            int sessionsSkipped, int rateLimitedTenants, int blobsTtlGced, int preparingRowsCleared,
            int strandedQueuedDetected, int durationMs, bool abortedByKillSwitch)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceCompleted", OpsEventSeverity.Info,
                $"SessionDeletionMaintenance completed in {durationMs}ms — tenants={tenantsProcessed} enqueued={sessionsEnqueued} skipped={sessionsSkipped} blobsTtlGced={blobsTtlGced} preparingCleared={preparingRowsCleared} stranded={strandedQueuedDetected} killSwitch={killSwitchActive}",
                null, "System.Maintenance", new {
                    killSwitchActive, tenantsProcessed, sessionsEnqueued,
                    sessionsSkipped, rateLimitedTenants, blobsTtlGced, preparingRowsCleared,
                    strandedQueuedDetected, durationMs, abortedByKillSwitch,
                });

        /// <summary>
        /// Records that the retention fanout half of a <see cref="Functions.Maintenance.SessionDeletionMaintenanceFunction"/>
        /// run was skipped because the global kill-switch was active at entry. The three GCs
        /// (manifest TTL sweep, stale-Preparing, stranded-Queued) still ran — see the totals on
        /// the paired <see cref="RecordSessionDeletionMaintenanceCompletedAsync"/> event.
        /// </summary>
        public Task RecordSessionDeletionMaintenanceFanoutSkippedAsync(
            int blobsTtlGced, int preparingRowsCleared, int strandedQueuedDetected)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceFanoutSkipped", OpsEventSeverity.Info,
                $"SessionDeletionMaintenance fanout skipped (kill-switch active) — GCs ran: blobsTtlGced={blobsTtlGced} preparingCleared={preparingRowsCleared} stranded={strandedQueuedDetected}",
                null, "System.Maintenance", new { reason = "SessionDeletionKillSwitch", blobsTtlGced, preparingRowsCleared, strandedQueuedDetected });

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

        public Task RecordEmbeddedCertExpiringSoonAsync(string role, string subject, string thumbprint, DateTime notAfterUtc, int daysUntilExpiry)
            => WriteAsync(OpsEventCategory.Security, "EmbeddedCertExpiringSoon", OpsEventSeverity.Warning,
                $"Newest embedded Intune {role.ToLowerInvariant()} '{subject}' expires in {daysUntilExpiry}d ({notAfterUtc:u}) - source a successor PEM and embed it before {notAfterUtc:yyyy-MM-dd}",
                null, "System.Maintenance",
                new { role, subject, thumbprint, notAfterUtc = notAfterUtc.ToString("u"), daysUntilExpiry });

        public Task RecordEmbeddedCertExpiringUrgentAsync(string role, string subject, string thumbprint, DateTime notAfterUtc, int daysUntilExpiry)
            => WriteAsync(OpsEventCategory.Security, "EmbeddedCertExpiringUrgent", OpsEventSeverity.Error,
                $"URGENT: newest embedded Intune {role.ToLowerInvariant()} '{subject}' expires in {daysUntilExpiry}d ({notAfterUtc:u}) and no successor is in the bundle - agent mTLS will break without rotation",
                null, "System.Maintenance",
                new { role, subject, thumbprint, notAfterUtc = notAfterUtc.ToString("u"), daysUntilExpiry });

        public Task RecordEmbeddedCertExpiredAsync(string role, string subject, string thumbprint, DateTime notAfterUtc, int daysUntilExpiry)
            => WriteAsync(OpsEventCategory.Security, "EmbeddedCertExpired", OpsEventSeverity.Critical,
                daysUntilExpiry < 0
                    ? $"CRITICAL: newest embedded Intune {role.ToLowerInvariant()} '{subject}' EXPIRED {-daysUntilExpiry}d ago ({notAfterUtc:u}) and no successor is embedded - agent mTLS validation broken"
                    : $"CRITICAL: newest embedded Intune {role.ToLowerInvariant()} '{subject}' expires in {daysUntilExpiry}d ({notAfterUtc:u}) and no successor is embedded",
                null, "System.Maintenance",
                new { role, subject, thumbprint, notAfterUtc = notAfterUtc.ToString("u"), daysUntilExpiry });

        public Task RecordEmbeddedCertBundleEmptyAsync()
            => WriteAsync(OpsEventCategory.Security, "EmbeddedCertBundleEmpty", OpsEventSeverity.Critical,
                "No embedded Intune root certificates loaded - agent mTLS validator is failing closed for ALL clients",
                null, "System.Maintenance", new { });

        public Task RecordSignalRConnectionsHighAsync(int observed, int limit, int percent, string resourceId)
            => WriteAsync(OpsEventCategory.Security, "SignalRConnectionsHigh", OpsEventSeverity.Warning,
                $"SignalR concurrent connections at {percent}% of free-tier limit ({observed}/{limit}) - watch for 429s; consider scaling to Standard before saturation",
                null, "System.Monitoring",
                new { metric = "ConnectionCount", aggregation = "Maximum", windowMinutes = 60, observed, limit, percent, resourceId });

        public Task RecordSignalRConnectionsCriticalAsync(int observed, int limit, int percent, string resourceId)
            => WriteAsync(OpsEventCategory.Security, "SignalRConnectionsCritical", OpsEventSeverity.Error,
                $"CRITICAL: SignalR concurrent connections at {percent}% of free-tier limit ({observed}/{limit}) - new client connections will be 429'd at 100%; scale to Standard now",
                null, "System.Monitoring",
                new { metric = "ConnectionCount", aggregation = "Maximum", windowMinutes = 60, observed, limit, percent, resourceId });

        public Task RecordSignalRMessagesHighAsync(long observed, long limit, int percent, string resourceId)
            => WriteAsync(OpsEventCategory.Security, "SignalRMessagesHigh", OpsEventSeverity.Warning,
                $"SignalR daily message count at {percent}% of free-tier limit ({observed}/{limit}) - quota resets at 00:00 UTC; consider scaling if pattern persists",
                null, "System.Monitoring",
                new { metric = "MessageCount", aggregation = "Total", windowDay = "UTC", observed, limit, percent, resourceId });

        public Task RecordSignalRMessagesCriticalAsync(long observed, long limit, int percent, string resourceId)
            => WriteAsync(OpsEventCategory.Security, "SignalRMessagesCritical", OpsEventSeverity.Error,
                $"CRITICAL: SignalR daily message count at {percent}% of free-tier limit ({observed}/{limit}) - hub will throttle at 100% until 00:00 UTC reset; scale to Standard now",
                null, "System.Monitoring",
                new { metric = "MessageCount", aggregation = "Total", windowDay = "UTC", observed, limit, percent, resourceId });

        public Task RecordPoisonQueueBacklogHighAsync(string queueName, long count, int threshold)
            => WriteAsync(OpsEventCategory.Security, "PoisonQueueBacklogHigh", OpsEventSeverity.Warning,
                $"Poison queue '{queueName}' backlog at {count} message(s) (threshold: {threshold}) — async worker handler failing repeatedly; inspect dead-letter contents",
                null, "System.Maintenance",
                new { queueName, count, threshold });

        public Task RecordPoisonQueueBacklogCriticalAsync(string queueName, long count, int threshold)
            => WriteAsync(OpsEventCategory.Security, "PoisonQueueBacklogCritical", OpsEventSeverity.Error,
                $"CRITICAL: poison queue '{queueName}' backlog at {count} messages (threshold: {threshold}) — sustained handler failure, downstream work is silently dropping",
                null, "System.Maintenance",
                new { queueName, count, threshold });

        // ── Critical-Table Backup ─────────────────────────────────────────────

        /// <summary>Backup run finished with all tables successfully captured. Info-level — visible in the timeline, not alertable by default.</summary>
        public Task RecordCriticalTableBackupCompletedAsync(string backupId, int tableCount, int durationMs, string container, string manifestBlobName, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "CriticalTableBackupCompleted", OpsEventSeverity.Info,
                $"Critical-table backup {backupId} completed: {tableCount} tables, {durationMs}ms (triggeredBy={triggeredBy})",
                null, "System.Maintenance",
                new { backupId, tableCount, durationMs, container, manifestBlobName, triggeredBy });

        /// <summary>Backup run wrote a manifest but at least one table Failed or Skipped. Warning-level — operator should inspect manifest perTableFailures.</summary>
        public Task RecordCriticalTableBackupPartialAsync(string backupId, int totalTables, int failedOrSkipped, int durationMs, string container, string manifestBlobName, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "CriticalTableBackupPartial", OpsEventSeverity.Warning,
                $"Critical-table backup {backupId} PARTIAL: {failedOrSkipped}/{totalTables} tables failed or skipped, manifest written ({durationMs}ms, triggeredBy={triggeredBy})",
                null, "System.Maintenance",
                new { backupId, totalTables, failedOrSkipped, durationMs, container, manifestBlobName, triggeredBy });

        /// <summary>Backup run never produced a valid manifest (fatal exception, storage outage). Error-level. Queue-path emits this AFTER 5x retry + poison-move; timer emits immediately.</summary>
        public Task RecordCriticalTableBackupFailedAsync(string? backupId, string errorMessage, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "CriticalTableBackupFailed", OpsEventSeverity.Error,
                $"Critical-table backup FAILED (backupId={backupId ?? "n/a"}, triggeredBy={triggeredBy}): {errorMessage}",
                null, "System.Maintenance",
                new { backupId, errorMessage, triggeredBy });

        /// <summary>Backup or restore was skipped because the maintenance lease was already held by another job. Info-level — not a failure.</summary>
        public Task RecordCriticalTableBackupSkippedLockedAsync(string reason, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "CriticalTableBackupSkippedLocked", OpsEventSeverity.Info,
                $"Critical-table backup skipped — {reason} (triggeredBy={triggeredBy})",
                null, "System.Maintenance",
                new { reason, triggeredBy });

        // ── Tenant ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when a departing admin submits free-form feedback in the offboarding
        /// drain-barrier banner. Information-tier — not actionable in itself, but the kind
        /// of signal product wants to be notified about so feedback gets read promptly.
        /// </summary>
        public Task RecordOffboardingFeedbackReceivedAsync(
            string tenantId, string submittedBy, string? domainName, string historyRowKey)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName)
                ? tenantId
                : $"{domainName} ({tenantId})";

            var details = new Dictionary<string, object?>
            {
                ["domainName"] = domainName,
                ["historyRowKey"] = historyRowKey,
            };

            return WriteAsync(OpsEventCategory.Tenant, "OffboardingFeedbackReceived", OpsEventSeverity.Info,
                $"Offboarding feedback received from {tenantLabel}",
                tenantId, submittedBy, details);
        }

        public Task RecordTenantOffboardedAsync(string tenantId, string performedBy, Dictionary<string, int> deletedCounts, string? domainName = null)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName)
                ? tenantId
                : $"{domainName} ({tenantId})";

            // Wrap deletedCounts + domainName so the OpsEvents details panel still surfaces the
            // per-table counts AND the domain (needed because Table Storage is gone by emit time).
            var details = new Dictionary<string, object?>
            {
                ["domainName"] = domainName,
                ["deletedCounts"] = deletedCounts,
            };

            return WriteAsync(OpsEventCategory.Tenant, "TenantOffboarded", OpsEventSeverity.Warning,
                $"Tenant {tenantLabel} offboarded — all data deleted",
                tenantId, performedBy, details);
        }

        /// <summary>
        /// Fired by the tenant-offboarding worker when the cascade fails closed (kill-switch
        /// active mid-enqueue, drain timeout, expectations blob corruption, ETag/CAS exhaustion,
        /// SafeWipe verify abort, …). Marker stays Failed until operator action; this event is
        /// the Telegram-routable signal that something needs human attention. Plan Rev-4 Q2.
        /// </summary>
        public Task RecordTenantOffboardingFailedAsync(
            string tenantId, string performedBy, string failedPhase, string errorMessage,
            int retryCount, string? domainName = null)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName)
                ? tenantId
                : $"{domainName} ({tenantId})";

            return WriteAsync(OpsEventCategory.Tenant, "TenantOffboardingFailed", OpsEventSeverity.Error,
                $"Tenant {tenantLabel} offboarding failed at phase '{failedPhase}': {errorMessage}",
                tenantId, performedBy,
                new { domainName, failedPhase, errorMessage, retryCount });
        }

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
