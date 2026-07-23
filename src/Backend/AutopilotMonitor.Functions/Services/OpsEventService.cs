using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Early-warning signal: a maintenance run finished but took longer than the soft threshold.
        /// The 2h Maintenance timer shares the host's 60min functionTimeout; a run that is climbing
        /// toward that ceiling (e.g. a large first-time retention backlog) surfaces here as Warning so
        /// operators are alerted before a future run is hard-aborted (which would emit no event at all).
        /// </summary>
        public Task RecordMaintenanceLongRunningAsync(int durationMs, int thresholdMinutes, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "MaintenanceLongRunning", OpsEventSeverity.Warning,
                $"Maintenance took {durationMs}ms (> {thresholdMinutes}min soft threshold; host aborts at 60min) — triggered by {triggeredBy}",
                null, triggeredBy, new { durationMs, thresholdMinutes });

        public Task RecordOpsEventCleanupAsync(int deletedCount, int retentionDays)
            => WriteAsync(OpsEventCategory.Maintenance, "OpsEventCleanup", OpsEventSeverity.Info,
                $"Cleaned up {deletedCount} ops events older than {retentionDays} days",
                null, "System.Maintenance", new { deletedCount, retentionDays });

        /// <summary>
        /// Records the orphan-cleanup result. <paramref name="cleanedOrphans"/> carries the
        /// per-session breakdown (which tenant + session lost lingering events and how many),
        /// so the ops dashboard shows *what* was cleaned, not just a count. The list is capped
        /// to keep the OpsEvents Table row under the per-property (32 KB) / entity (~1 MB) limits;
        /// the full count always survives in <c>orphanSessions</c> and a <c>detailsTruncated</c>
        /// flag flags any clipping. Note: an "orphan" is a session row that no longer exists while
        /// its events lingered past the 24h grace — the tenant itself usually still exists.
        /// </summary>
        public Task RecordOrphanEventsCleanedAsync(int orphanSessions, int totalEventsDeleted,
            IReadOnlyList<OrphanedEventSession>? cleanedOrphans = null)
        {
            const int maxDetailRows = 50;

            var orphanList = cleanedOrphans ?? Array.Empty<OrphanedEventSession>();
            var detail = orphanList
                .OrderByDescending(o => o.EventCount)
                .Take(maxDetailRows)
                .Select(o => new { tenantId = o.TenantId, sessionId = o.SessionId, eventCount = o.EventCount })
                .ToList();

            return WriteAsync(OpsEventCategory.Maintenance, "OrphanEventsCleaned", OpsEventSeverity.Warning,
                $"Cleaned {totalEventsDeleted} orphaned events across {orphanSessions} sessions",
                null, "System.Maintenance",
                new
                {
                    orphanSessions,
                    totalEventsDeleted,
                    orphans = detail,
                    detailsTruncated = orphanList.Count > maxDetailRows
                });
        }

        // ── Cascade-Delete Maintenance (Plan §5 PR6 / §16 R14) ─────────────────
        // Event types dispatched by SessionDeletionMaintenanceFunction: Started, LongRunning,
        // LongRunningSevere, BudgetExceeded, SkippedLocked, Failed, Completed, FanoutSkipped
        // (+ StrandedQueued / Poisoned from the GCs and the cascade worker). Each is also
        // listed in OpsAlertRulesSection.tsx OPS_EVENT_TYPES (memory feedback_ops_event_types_dual_register).

        /// <summary>
        /// Run started (timer tick or manual trigger). Emitted after the maintenance lease was
        /// acquired, so a lease-skip never masquerades as an active run — the Session Cleanup
        /// UI banner treats "latest Started newer than latest Completed/Failed" as run-active.
        /// </summary>
        public Task RecordSessionDeletionMaintenanceStartedAsync(string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceStarted", OpsEventSeverity.Info,
                $"SessionDeletionMaintenance run started (triggered by {triggeredBy})",
                null, "System.Maintenance", new { triggeredBy });

        /// <summary>
        /// The retention fanout stopped cleanly at the run-budget deadline. Not an error: the
        /// remaining backlog is picked up by the next run (12h cadence) or a manual trigger.
        /// Paired with a Completed event whose details carry <c>abortedByBudget=true</c>.
        /// </summary>
        public Task RecordSessionDeletionMaintenanceBudgetExceededAsync(int budgetMinutes, int tenantsProcessed, int sessionsEnqueued)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceBudgetExceeded", OpsEventSeverity.Warning,
                $"SessionDeletionMaintenance stopped at the {budgetMinutes}min run budget — tenants={tenantsProcessed} enqueued={sessionsEnqueued}; remaining backlog resumes on the next run",
                null, "System.Maintenance", new { budgetMinutes, tenantsProcessed, sessionsEnqueued });

        /// <summary>
        /// A run (timer or manual) was skipped because another run holds the session-deletion
        /// maintenance lease. Mirrors <c>RecordCriticalTableBackupSkippedLockedAsync</c>.
        /// </summary>
        public Task RecordSessionDeletionMaintenanceSkippedLockedAsync(string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceSkippedLocked", OpsEventSeverity.Info,
                $"SessionDeletionMaintenance skipped — another run holds the maintenance lease (triggeredBy={triggeredBy})",
                null, "System.Maintenance", new { reason = "lease held by another run", triggeredBy });

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
            int strandedQueuedDetected, int durationMs, bool abortedByKillSwitch, bool abortedByBudget)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionDeletionMaintenanceCompleted", OpsEventSeverity.Info,
                $"SessionDeletionMaintenance completed in {durationMs}ms — tenants={tenantsProcessed} enqueued={sessionsEnqueued} skipped={sessionsSkipped} blobsTtlGced={blobsTtlGced} preparingCleared={preparingRowsCleared} stranded={strandedQueuedDetected} killSwitch={killSwitchActive} abortedByBudget={abortedByBudget}",
                null, "System.Maintenance", new {
                    killSwitchActive, tenantsProcessed, sessionsEnqueued,
                    sessionsSkipped, rateLimitedTenants, blobsTtlGced, preparingRowsCleared,
                    strandedQueuedDetected, durationMs, abortedByKillSwitch, abortedByBudget,
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

        // ExcessiveDataBlocked was removed 2026-07-22 together with the time-window detector that
        // raised it — it blocked on session span alone and fired on ordinary overnight enrollments.
        // Automatic blocks now come only from the event-count path below.

        public Task RecordVersionBlockedAsync(string pattern, string blockedBy)
            => WriteAsync(OpsEventCategory.Security, "VersionBlocked", OpsEventSeverity.Warning,
                $"Agent version pattern '{pattern}' blocked",
                null, blockedBy, new { pattern });

        /// <summary>
        /// Fired by <see cref="KillSwitchEvaluator"/> when a Kill signal was actually SERVED to
        /// an agent (as opposed to DeviceBlocked/VersionBlocked, which fire when the admin
        /// creates the rule). This is the delivery confirmation operators wire Telegram rules
        /// on — throttled at the evaluator (24h per tenant+serial+pattern) because a kill-blind
        /// old agent keeps hitting the endpoint every few seconds. Details carry
        /// <c>serialNumber</c> so the Ops Events detail modal's Block/Kill shortcuts deep-link.
        /// </summary>
        public Task RecordKillSignalDeliveredAsync(
            string tenantId, string? serialNumber, string? agentVersion, string? matchedPattern,
            string trigger, string channel)
            => WriteAsync(OpsEventCategory.Security, "KillSignalDelivered", OpsEventSeverity.Warning,
                trigger == "version"
                    ? $"Kill signal delivered via {channel} to agent {agentVersion ?? "?"} on device {serialNumber ?? "?"} (pattern: {matchedPattern})"
                    : $"Kill signal delivered via {channel} to device {serialNumber ?? "?"}",
                tenantId, "System.KillSwitch",
                new { serialNumber, agentVersion, matchedPattern, trigger, channel });

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
                $"SignalR concurrent connections at {percent}% of plan limit ({observed}/{limit}) - watch for 429s; consider adding units before saturation",
                null, "System.Monitoring",
                new { metric = "ConnectionCount", aggregation = "Maximum", windowMinutes = 60, observed, limit, percent, resourceId });

        public Task RecordSignalRConnectionsCriticalAsync(int observed, int limit, int percent, string resourceId)
            => WriteAsync(OpsEventCategory.Security, "SignalRConnectionsCritical", OpsEventSeverity.Error,
                $"CRITICAL: SignalR concurrent connections at {percent}% of plan limit ({observed}/{limit}) - new client connections will be 429'd at 100%; add units now",
                null, "System.Monitoring",
                new { metric = "ConnectionCount", aggregation = "Maximum", windowMinutes = 60, observed, limit, percent, resourceId });

        public Task RecordSignalRMessagesHighAsync(long observed, long limit, int percent, string resourceId)
            => WriteAsync(OpsEventCategory.Security, "SignalRMessagesHigh", OpsEventSeverity.Warning,
                $"SignalR daily message count at {percent}% of included plan quota ({observed}/{limit}) - resets at 00:00 UTC; overage is billed per extra million messages",
                null, "System.Monitoring",
                new { metric = "MessageCount", aggregation = "Total", windowDay = "UTC", observed, limit, percent, resourceId });

        public Task RecordSignalRMessagesCriticalAsync(long observed, long limit, int percent, string resourceId)
            => WriteAsync(OpsEventCategory.Security, "SignalRMessagesCritical", OpsEventSeverity.Error,
                $"CRITICAL: SignalR daily message count at {percent}% of included plan quota ({observed}/{limit}) - overage beyond 100% is billed per extra million messages; review traffic or add units",
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

        /// <summary>
        /// A GA operator restored a single row from a backup (plan §PR2). Warning-level
        /// because the restore overwrote (or created) live data via ETag-CAS — operators
        /// frequently want a Telegram ping for this event so a parallel admin sees the
        /// audit trail in near-real-time. Payload carries the canonical
        /// <c>{ container, blobName }</c> only — no SAS URL, per plan §Medium #6.
        /// </summary>
        public Task RecordBackupRowRestoredAsync(
            string backupId, string tableName, string partitionKey, string rowKey, string actor, string outcome)
            => WriteAsync(OpsEventCategory.Maintenance, "BackupRowRestored", OpsEventSeverity.Warning,
                $"Critical-table row restored: {tableName} (pk='{partitionKey}', rk='{rowKey}') from backup {backupId} by {actor} → {outcome}",
                null, actor,
                new
                {
                    backupId,
                    tableName,
                    partitionKey,
                    rowKey,
                    outcome,
                    container = AutopilotMonitor.Shared.Constants.BlobContainers.CriticalTableBackups,
                    blobName = $"{backupId}/{tableName}.ndjson",
                });

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

        // ── Tenant trial lifecycle (informational — enforcement is read-time) ──
        // Both types are dual-registered in OpsAlertRulesSection.tsx OPS_EVENT_TYPES
        // (memory feedback_ops_event_types_dual_register). Dispatched by TrialExpirySweepFunction.

        /// <summary>Heads-up: an Enterprise trial ends within the next few days. Info-tier visibility signal.</summary>
        public Task RecordTenantTrialExpiringAsync(string tenantId, string? domainName, DateTime trialExpiresUtc, int daysLeft)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "TenantTrialExpiring", OpsEventSeverity.Info,
                $"Enterprise trial for tenant {tenantLabel} expires in {daysLeft} day(s) ({trialExpiresUtc:yyyy-MM-dd HH:mm}Z)",
                tenantId, "System.TrialSweep", new { domainName, trialExpiresUtc, daysLeft });
        }

        /// <summary>
        /// An Enterprise trial expired within the last sweep window — the tenant silently degraded
        /// to Community at read time (retention cap, rate limits, MSP delegation, MCP plan).
        /// Warning-tier: a conversion moment an operator likely wants a Telegram ping for.
        /// </summary>
        public Task RecordTenantTrialExpiredAsync(string tenantId, string? domainName, DateTime trialExpiredUtc)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "TenantTrialExpired", OpsEventSeverity.Warning,
                $"Enterprise trial for tenant {tenantLabel} expired ({trialExpiredUtc:yyyy-MM-dd HH:mm}Z) — tenant is now Community",
                tenantId, "System.TrialSweep", new { domainName, trialExpiredUtc });
        }

        // ── Agent ──────────────────────────────────────────────────────────────

        public Task RecordSessionTimeoutsAsync(string tenantId, int sessionCount, int timeoutHours)
            => WriteAsync(OpsEventCategory.Agent, "SessionTimeouts", OpsEventSeverity.Info,
                $"{sessionCount} session(s) timed out after {timeoutHours}h",
                tenantId, "System.Maintenance", new { sessionCount, timeoutHours });

        /// <summary>
        /// An agent reported its absolute session-age emergency break (48h cap) over the
        /// emergency channel — it cleaned itself up and exited on a session that never reached
        /// a terminal state. This is the "are we silently losing agents?" signal; emitted by
        /// <see cref="Functions.Ingest.ReportAgentErrorFunction"/> once per session (guarded by
        /// the timeline-event idempotency check). Warning-tier so operators can wire a Telegram
        /// rule; if real-world volume turns out noisy, downgrade or remove — the timeline event
        /// on the session is the durable record.
        /// </summary>
        public Task RecordAgentEmergencyBreakAsync(string tenantId, string sessionId, string? agentVersion, string message)
            => WriteAsync(OpsEventCategory.Agent, "AgentEmergencyBreak", OpsEventSeverity.Warning,
                $"Agent emergency break on session {sessionId} (agent {agentVersion ?? "?"}): {message}",
                tenantId, "System.EmergencyChannel", new { sessionId, agentVersion });

        public Task RecordExcessiveSessionEventsAsync(string tenantId, string sessionId, int eventCount, int threshold)
            => WriteAsync(OpsEventCategory.Agent, "ExcessiveSessionEvents", OpsEventSeverity.Warning,
                $"Session {sessionId} has {eventCount} events (threshold {threshold}) — likely agent loop bug",
                tenantId, "System.Maintenance", new { sessionId, eventCount, threshold });

        /// <summary>
        /// Fired when maintenance auto-blocks or auto-kills a device after its session crossed
        /// <see cref="AutopilotMonitor.Shared.Models.AdminConfiguration.ExcessiveEventAutoActionThreshold"/>.
        /// Critical-severity so operators can wire a Telegram rule independent of the warn-tier
        /// <c>ExcessiveSessionEvents</c> rule. Details carry the resolved <c>serialNumber</c>
        /// so the Ops Events detail modal's Block/Kill shortcuts deep-link correctly.
        /// </summary>
        public Task RecordExcessiveSessionEventsAutoActionedAsync(
            string tenantId, string sessionId, string serialNumber, int eventCount, int threshold,
            string action, int durationHours)
            => WriteAsync(OpsEventCategory.Security, "ExcessiveSessionEventsAutoActioned", OpsEventSeverity.Critical,
                $"Auto-{action.ToLowerInvariant()} device {serialNumber} for session {sessionId} ({eventCount} events ≥ {threshold}, {durationHours}h)",
                tenantId, "System.Maintenance",
                new { sessionId, serialNumber, eventCount, threshold, action, durationHours });

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
