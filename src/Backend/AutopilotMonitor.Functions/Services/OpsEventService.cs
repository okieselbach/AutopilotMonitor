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

        public Task RecordAppHomingFlippedAsync(string tenantId, string userId, string oldApp, string newApp, string reason, bool forced)
            => WriteAsync(OpsEventCategory.Consent, "AppHomingFlipped", OpsEventSeverity.Info,
                $"App-reg homing flipped {oldApp} -> {newApp} by {userId} ({reason}{(forced ? ", FORCED" : "")})",
                tenantId, userId, new { oldApp, newApp, reason, forced });

        public Task RecordAppHomingFlippedWithEntraRolesAsync(string tenantId, string userId, string oldApp, string newApp)
            => WriteAsync(OpsEventCategory.Consent, "AppHomingFlippedWithEntraRoles", OpsEventSeverity.Warning,
                $"App-reg homing flipped {oldApp} -> {newApp} for a tenant with Entra app roles — re-assign roles on the new enterprise app",
                tenantId, userId, new { oldApp, newApp });

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

        /// <summary>
        /// Heartbeat of the hourly <c>SessionSweep</c> timer (the :30 interleave of the stalled-session
        /// sweep — the 2h Maintenance chain still runs the same sweep at minute 0). Emitted every tick,
        /// counts included, so a dead timer is detectable and dashboards can separate the interleave
        /// from full maintenance runs.
        /// </summary>
        public Task RecordSessionSweepCompletedAsync(int stalledMarked, int timedOut, int durationMs)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionSweepCompleted", OpsEventSeverity.Info,
                $"Hourly session sweep completed in {durationMs}ms — {stalledMarked} marked Stalled, {timedOut} terminalized",
                null, "System.SessionSweep", new { stalledMarked, timedOut, durationMs });

        public Task RecordSessionSweepFailedAsync(string error)
            => WriteAsync(OpsEventCategory.Maintenance, "SessionSweepFailed", OpsEventSeverity.Error,
                $"Hourly session sweep failed: {error}",
                null, "System.SessionSweep", new { error });

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

        /// <summary>
        /// Fired by the tenant-auto-approve queue worker when a new signup was activated
        /// automatically (AutoApproveNewTenants enabled). Info-tier audit + Telegram-routable
        /// signal so the operator sees which tenants entered without a manual approval click.
        /// Dual-registered in OpsAlertRulesSection.tsx OPS_EVENT_TYPES
        /// (memory feedback_ops_event_types_dual_register).
        /// </summary>
        public Task RecordTenantAutoApprovedAsync(string tenantId, string? domainName, string signupUpn)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "TenantAutoApproved", OpsEventSeverity.Info,
                $"Tenant {tenantLabel} was auto-activated after signup (signup by {signupUpn})",
                tenantId, "System (auto-approve)", new { domainName, signupUpn });
        }

        // ── Activation welcome mail ──
        // The mail is a courtesy side effect that fails soft, which used to make every failure
        // mode invisible: the "no address" and "provider rejected" branches only wrote
        // LogInformation, and worker application logs never reach Application Insights below
        // Warning. Between 2026-08-21 and 2026-08-26 that hid a total stop of the welcome mail.
        // These three types are the visible record. All dual-registered in
        // OpsAlertRulesSection.tsx OPS_EVENT_TYPES (memory feedback_ops_event_types_dual_register).

        /// <summary>The provider accepted the activation welcome mail. Info-tier confirmation.</summary>
        public Task RecordWelcomeEmailSentAsync(string tenantId, string? domainName, string toEmail, string addressSource)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "WelcomeEmailSent", OpsEventSeverity.Info,
                $"Welcome email sent to {toEmail} for tenant {tenantLabel} (address from {addressSource})",
                tenantId, "System.Activation", new { domainName, toEmail, addressSource });
        }

        /// <summary>
        /// The tenant was activated but no welcome mail went out, because no address could be
        /// resolved (neither the activation-page address nor the tenant contact address).
        /// Warning-tier: nothing is broken technically, but a customer was onboarded in silence.
        /// </summary>
        public Task RecordWelcomeEmailSkippedAsync(string tenantId, string? domainName, string reason)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "WelcomeEmailSkipped", OpsEventSeverity.Warning,
                $"No welcome email for tenant {tenantLabel} — {reason}",
                tenantId, "System.Activation", new { domainName, reason });
        }

        /// <summary>
        /// An address was resolved and handed to the provider, which did not accept it (or the
        /// send threw). Error-tier: this one IS a defect — provider key, rejection or outage.
        /// </summary>
        public Task RecordWelcomeEmailFailedAsync(string tenantId, string? domainName, string toEmail, string reason)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "WelcomeEmailFailed", OpsEventSeverity.Error,
                $"Welcome email to {toEmail} failed for tenant {tenantLabel} — {reason}",
                tenantId, "System.Activation", new { domainName, toEmail, reason });
        }

        // ── Tenant trial lifecycle (informational — enforcement is read-time) ──
        // Both types are dual-registered in OpsAlertRulesSection.tsx OPS_EVENT_TYPES
        // (memory feedback_ops_event_types_dual_register). Dispatched by TrialExpirySweepFunction.

        /// <summary>Heads-up: a Pro trial ends within the next few days. Info-tier visibility signal.</summary>
        public Task RecordTenantTrialExpiringAsync(string tenantId, string? domainName, DateTime trialExpiresUtc, int daysLeft)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "TenantTrialExpiring", OpsEventSeverity.Info,
                $"Pro trial for tenant {tenantLabel} expires in {daysLeft} day(s) ({trialExpiresUtc:yyyy-MM-dd HH:mm}Z)",
                tenantId, "System.TrialSweep", new { domainName, trialExpiresUtc, daysLeft });
        }

        /// <summary>
        /// A Pro trial expired within the last sweep window — the tenant silently degraded
        /// to Community at read time (retention cap, rate limits, MSP delegation, MCP plan).
        /// Warning-tier: a conversion moment an operator likely wants a Telegram ping for.
        /// </summary>
        public Task RecordTenantTrialExpiredAsync(string tenantId, string? domainName, DateTime trialExpiredUtc)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "TenantTrialExpired", OpsEventSeverity.Warning,
                $"Pro trial for tenant {tenantLabel} expired ({trialExpiredUtc:yyyy-MM-dd HH:mm}Z) — tenant is now Community",
                tenantId, "System.TrialSweep", new { domainName, trialExpiredUtc });
        }

        // ── Plan downgrade / retention grace (informational — enforcement is read-time) ──
        // All three types are dual-registered in OpsAlertRulesSection.tsx OPS_EVENT_TYPES
        // (memory feedback_ops_event_types_dual_register). Downgraded is dispatched by
        // PlanManagementFunction, the grace events by TrialExpirySweepFunction.

        /// <summary>
        /// A GA plan mutation dropped the tenant's EFFECTIVE edition Pro → Community. The
        /// retention downgrade grace period starts now; data older than the Community cap is
        /// deleted only after it ends. Warning-tier: the business moment (e.g. non-payment)
        /// an operator wants a ping for.
        /// </summary>
        public Task RecordTenantPlanDowngradedAsync(
            string tenantId, string? domainName, string caller, DateTime? retentionGraceEndsUtc, int storedRetentionDays)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            var graceNote = retentionGraceEndsUtc is DateTime graceEnd
                ? $"retention grace until {graceEnd:yyyy-MM-dd HH:mm}Z"
                : "no retention grace applicable";
            return WriteAsync(OpsEventCategory.Tenant, "TenantPlanDowngraded", OpsEventSeverity.Warning,
                $"Tenant {tenantLabel} was downgraded Pro → Community by {caller} — {graceNote} (stored retention {storedRetentionDays}d)",
                tenantId, caller, new { domainName, retentionGraceEndsUtc, storedRetentionDays });
        }

        /// <summary>
        /// Heads-up: a downgraded tenant's retention grace ends within the next few days —
        /// after that, the sweep enforces the Community cap and deletes the older data.
        /// Emitted only for tenants that actually have data at risk (stored retention above
        /// the Community cap). Re-fired daily during the heads-up window (no dedup state).
        /// </summary>
        public Task RecordTenantRetentionGraceExpiringAsync(
            string tenantId, string? domainName, DateTime graceEndsUtc, int daysLeft, int storedRetentionDays)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "TenantRetentionGraceExpiring", OpsEventSeverity.Warning,
                $"Retention grace for downgraded tenant {tenantLabel} ends in {daysLeft} day(s) ({graceEndsUtc:yyyy-MM-dd HH:mm}Z) — " +
                $"data older than the Community cap will then be deleted (stored retention {storedRetentionDays}d)",
                tenantId, "System.TrialSweep", new { domainName, graceEndsUtc, daysLeft, storedRetentionDays });
        }

        /// <summary>
        /// A downgraded tenant's retention grace ended within the last sweep window — from now
        /// on the retention sweep enforces the Community cap and starts deleting the older data.
        /// Last call for a GA intervention (re-upgrade / retention decision).
        /// </summary>
        public Task RecordTenantRetentionGraceEndedAsync(
            string tenantId, string? domainName, DateTime graceEndedUtc, int storedRetentionDays)
        {
            var tenantLabel = string.IsNullOrWhiteSpace(domainName) ? tenantId : $"{domainName} ({tenantId})";
            return WriteAsync(OpsEventCategory.Tenant, "TenantRetentionGraceEnded", OpsEventSeverity.Warning,
                $"Retention grace for downgraded tenant {tenantLabel} ended ({graceEndedUtc:yyyy-MM-dd HH:mm}Z) — " +
                $"the Community retention cap is now enforced and older data will be deleted (stored retention {storedRetentionDays}d)",
                tenantId, "System.TrialSweep", new { domainName, graceEndedUtc, storedRetentionDays });
        }

        /// <summary>
        /// F3 regression radar (insights spec §F3): an analyze rule's 7-day hit rate rose ≥2×
        /// over its 28-day baseline with disjoint Wilson intervals. Fired ONCE per episode (the
        /// notification-tracker row is the dedup); the message carries the full numbers so the
        /// event is verifiable without a portal round-trip. Dimension wording is correlation
        /// only — never causal. Dual-registered in the web OPS_EVENT_TYPES catalog
        /// (memory: feedback_ops_event_types_dual_register).
        /// </summary>
        public Task RecordRuleFrequencyRegressionAsync(
            string tenantId, string ruleId, string ruleTitle,
            int windowFireCount, int windowSessionCount, double windowRatePct,
            int baselineFireCount, int baselineSessionCount, double baselineRatePct,
            double? lift, string? dimensionSummary)
            => WriteAsync(OpsEventCategory.Tenant, "RuleFrequencyRegression", OpsEventSeverity.Warning,
                $"Rule '{ruleTitle}' ({ruleId}) fired in {windowFireCount}/{windowSessionCount} sessions ({windowRatePct}%) over 7d " +
                $"vs {baselineRatePct}% baseline ({baselineFireCount}/{baselineSessionCount} over 28d)" +
                (lift.HasValue ? $" — lift {lift.Value}x" : " — new signal (no baseline fires)") +
                (string.IsNullOrEmpty(dimensionSummary) ? string.Empty : $". {dimensionSummary}"),
                tenantId, "System.Maintenance",
                new
                {
                    ruleId,
                    windowFireCount,
                    windowSessionCount,
                    windowRatePct,
                    baselineFireCount,
                    baselineSessionCount,
                    baselineRatePct,
                    lift,
                });

        /// <summary>
        /// App-version duration regression radar: an app's newest version installs with a median
        /// duration ≥2× (and ≥5 min absolute) over the previous version's median. Fired ONCE per
        /// (app, version) episode (the notification-tracker row is the dedup); the message
        /// carries the full numbers so the event is verifiable without a portal round-trip.
        /// Dual-registered in the web OPS_EVENT_TYPES catalog
        /// (memory: feedback_ops_event_types_dual_register).
        /// </summary>
        public Task RecordAppVersionDurationRegressionAsync(
            string tenantId, string appName, string currentVersion, string previousVersion,
            int currentMedianSeconds, int previousMedianSeconds,
            int currentMeasuredCount, int previousMeasuredCount, double lift)
            => WriteAsync(OpsEventCategory.Tenant, "AppVersionDurationRegression", OpsEventSeverity.Warning,
                $"App '{appName}' median install duration rose from {Math.Round(previousMedianSeconds / 60.0, 1)} to " +
                $"{Math.Round(currentMedianSeconds / 60.0, 1)} min after version {currentVersion} " +
                $"({currentMeasuredCount} measured installs vs {previousMeasuredCount} on {previousVersion}) — lift {lift}x",
                tenantId, "System.Maintenance",
                new
                {
                    appName,
                    currentVersion,
                    previousVersion,
                    currentMedianSeconds,
                    previousMedianSeconds,
                    currentMeasuredCount,
                    previousMeasuredCount,
                    lift,
                });

        /// <summary>
        /// Verdict-calibration drift radar (docs/backend/verdict-calibration.md): a verdict
        /// path's share of sessions doubled over its 28d baseline, the silence share
        /// (sweep+maxlife — agent went quiet, backend had to decide) doubled, or the pure
        /// fallthrough rule r6 decides ≥20 % of classifier verdicts. Operator-only diagnostic —
        /// there is deliberately NO tenant bell. Fired ONCE per (kind, path, status) episode
        /// (tracker-deduped). Dual-registered in the web OPS_EVENT_TYPES catalog
        /// (memory: feedback_ops_event_types_dual_register).
        /// </summary>
        public Task RecordVerdictCalibrationDriftAsync(
            string tenantId, string kind, string verdictPath, string status,
            int windowHitCount, int windowSessionCount, double windowRatePct,
            int baselineHitCount, int baselineSessionCount, double baselineRatePct,
            double? lift, string? dimensionSummary)
            => WriteAsync(OpsEventCategory.Maintenance, "VerdictCalibrationDrift", OpsEventSeverity.Warning,
                $"Verdict calibration [{kind}] {verdictPath}/{status}: {windowHitCount}/{windowSessionCount} ({windowRatePct}%) over 7d " +
                $"vs {baselineRatePct}% baseline ({baselineHitCount}/{baselineSessionCount} over 28d)" +
                (lift.HasValue ? $" — lift {lift.Value}x" : string.Empty) +
                (string.IsNullOrEmpty(dimensionSummary) ? string.Empty : $". {dimensionSummary}"),
                tenantId, "System.Maintenance",
                new
                {
                    kind,
                    verdictPath,
                    status,
                    windowHitCount,
                    windowSessionCount,
                    windowRatePct,
                    baselineHitCount,
                    baselineSessionCount,
                    baselineRatePct,
                    lift,
                });

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

        /// <summary>
        /// An agent reported that its RUNNING exe's SHA-256 differs from the hash the backend
        /// advertises for its version — the binary in the field is not the published build
        /// (tamper, stale blob, or a build that never came from a committed tree). Session
        /// e9753578 (2026-08-20) carried exactly this report, but it landed only in App
        /// Insights and was invisible to every product surface while the field damage was
        /// mis-attributed to committed code for hours. Warning-tier so operators can wire a
        /// Telegram rule; the message carries both hashes verbatim.
        /// </summary>
        public Task RecordAgentBinaryIntegrityMismatchAsync(string tenantId, string? sessionId, string? agentVersion, string message)
            => WriteAsync(OpsEventCategory.Agent, "AgentBinaryIntegrityMismatch", OpsEventSeverity.Warning,
                $"Agent binary integrity mismatch on session {sessionId ?? "?"} (agent {agentVersion ?? "?"}): {message}",
                tenantId, "System.EmergencyChannel", new { sessionId, agentVersion });

        /// <summary>
        /// The CMTrace time-skew tripwire fired: a terminal session's IME-derived event
        /// timestamps diverge from its other events by a clean 15-minute-grid multiple —
        /// the signature of a timezone mis-conversion the per-line self-anchoring
        /// (docs/agent/cmtrace-time-resolution.md) failed to catch. Goal state: this event
        /// NEVER fires; any occurrence is a real anchoring regression or a detector bug,
        /// both actionable. Emitted by <see cref="EventIngestProcessor"/> once per session
        /// (gated on the terminal status transition). The message carries the full numbers
        /// so the event is verifiable without a portal round-trip.
        /// Dual-register per memory feedback_ops_event_types_dual_register.
        /// </summary>
        public Task RecordCmTraceTimeSkewRegressionAsync(string tenantId, string sessionId, string? agentVersion, string message, object details)
            => WriteAsync(OpsEventCategory.Agent, "CmTraceTimeSkewRegression", OpsEventSeverity.Warning,
                $"CMTrace time-skew tripwire on session {sessionId} (agent {agentVersion ?? "?"}): {message}",
                tenantId, "System.Ingest", details);

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

        // ── Platform (Azure Monitor) ───────────────────────────────────────────

        /// <summary>
        /// Records an Azure Monitor alert notification delivered via the ops alert webhook
        /// (AzureMonitorAlertWebhookFunction). severity is already mapped onto the ops scale;
        /// azureSeverity keeps the original Sev0–Sev4 value for the details payload.
        /// </summary>
        public Task RecordAzureMonitorAlertAsync(string alertRule, string severity, string monitorCondition,
            string? description, string? azureSeverity, string? monitoringService, string? targetResource,
            double? metricValue)
        {
            var suffix = string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description}";
            return WriteAsync(OpsEventCategory.Platform, "AzureMonitorAlert", severity,
                $"Azure Monitor alert '{alertRule}' {monitorCondition}{suffix}",
                null, "System.AzureMonitor",
                new { alertRule, monitorCondition, azureSeverity, monitoringService, targetResource, metricValue });
        }

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
