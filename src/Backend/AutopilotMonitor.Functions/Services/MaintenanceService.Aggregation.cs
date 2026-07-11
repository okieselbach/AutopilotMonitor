using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: Metrics aggregation, data cleanup, and platform stats recomputation.
    /// </summary>
    public partial class MaintenanceService
    {
        private async Task AggregateMetricsWithCatchUpAsync()
        {
            const int maxCatchUpDays = 7;
            var today = DateTime.UtcNow.Date;
            var aggregatedCount = 0;

            for (int daysBack = maxCatchUpDays; daysBack >= 1; daysBack--)
            {
                var date = today.AddDays(-daysBack);
                var dateStr = date.ToString("yyyy-MM-dd");

                try
                {
                    if (await _metricsRepo.HasUsageMetricsSnapshotAsync(dateStr))
                        continue;

                    _logger.LogInformation($"Catch-up: Aggregating metrics for missed date {dateStr}");
                    await AggregateMetricsForDateAsync(date);
                    aggregatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to aggregate metrics for {dateStr} during catch-up");
                }
            }

            if (aggregatedCount > 0)
                _logger.LogInformation($"Catch-up completed: aggregated {aggregatedCount} missed day(s)");
            else
                _logger.LogInformation("No missed days to catch up on");
        }

        /// <summary>
        /// Aggregates metrics for a specific date and saves them as historical snapshots
        /// </summary>
        private async Task AggregateMetricsForDateAsync(DateTime targetDate)
        {
            _logger.LogInformation($"Aggregating metrics for {targetDate:yyyy-MM-dd}...");
            var aggregateStart = Stopwatch.StartNew();

            try
            {
                var targetDateStr = targetDate.ToString("yyyy-MM-dd");

                var targetDateSessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(targetDate, targetDate.AddDays(1));

                if (targetDateSessions.Count == 0)
                {
                    _logger.LogInformation($"No sessions found for {targetDateStr}");
                    return;
                }

                var globalMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, "global", targetDateSessions);
                await _metricsRepo.SaveUsageMetricsSnapshotAsync(globalMetrics);

                var tenantGroups = targetDateSessions.GroupBy(s => s.TenantId);
                foreach (var tenantGroup in tenantGroups)
                {
                    var tenantMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, tenantGroup.Key, tenantGroup.ToList());
                    await _metricsRepo.SaveUsageMetricsSnapshotAsync(tenantMetrics);
                }

                // Aggregate rule stats from RuleResults table for this date
                await AggregateRuleStatsForDateAsync(targetDate, targetDateSessions);

                aggregateStart.Stop();
                _logger.LogInformation($"Aggregated metrics for {targetDateSessions.Count} sessions from {targetDateStr} in {aggregateStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to aggregate metrics for {targetDate:yyyy-MM-dd}");
                throw;
            }
        }

        /// <summary>
        /// Computes historical metrics for a specific date and tenant
        /// </summary>
        private async Task<UsageMetricsSnapshot> ComputeUsageMetricsSnapshotAsync(string date, string tenantId, List<SessionSummary> sessions)
        {
            var computeStart = Stopwatch.StartNew();

            var completed = sessions.Where(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed).ToList();
            var succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded);
            var successRate = completed.Count > 0 ? Math.Round((succeeded / (double)completed.Count) * 100, 1) : 0;

            var completedWithDuration = sessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
            double avgDuration = 0, medianDuration = 0, p95Duration = 0, p99Duration = 0;

            if (completedWithDuration.Any())
            {
                var durations = completedWithDuration.Select(s => s.DurationSeconds!.Value / 60.0).OrderBy(d => d).ToList();
                avgDuration = Math.Round(durations.Average(), 1);
                medianDuration = MetricsMath.Percentile(durations, 50);
                p95Duration = MetricsMath.Percentile(durations, 95);
                p99Duration = MetricsMath.Percentile(durations, 99);
            }

            var manufacturers = sessions
                .GroupBy(s => s.Manufacturer)
                .Select(g => new { Name = g.Key, Count = g.Count(), Percentage = Math.Round((g.Count() / (double)sessions.Count) * 100, 1) })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            var models = sessions
                .GroupBy(s => s.Model)
                .Select(g => new { Name = g.Key, Count = g.Count(), Percentage = Math.Round((g.Count() / (double)sessions.Count) * 100, 1) })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            var targetDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);
            var (uniqueUsers, loginCount) = await _metricsRepo.GetUserActivityForDateAsync(
                tenantId == "global" ? null : tenantId, targetDate);

            // App metrics: count apps per session from AppInstallSummaries table
            var sessionIdSet = new HashSet<string>(sessions.Select(s => s.SessionId));
            List<AppInstallSummary> appSummaries;
            // Bound the AppInstall scan to this snapshot's day. The session set is already
            // [targetDate, targetDate+1) and apps install during their session (StartedAt >= targetDate),
            // so relevantApps is unchanged; without sinceUtc this scanned the whole table per aggregated day.
            if (tenantId == "global")
                appSummaries = await _metricsRepo.GetAllAppInstallSummariesAsync(targetDate);
            else
                appSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId, targetDate);

            var relevantApps = appSummaries.Where(a => sessionIdSet.Contains(a.SessionId)).ToList();
            var appsPerSession = relevantApps.GroupBy(a => a.SessionId).Select(g => g.Count()).ToList();
            var avgAppsPerSession = appsPerSession.Count > 0 ? Math.Round(appsPerSession.Average(), 1) : 0;
            var totalUniqueApps = relevantApps.Select(a => a.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            // Script metrics: computed from session-level counters (zero extra queries)
            var totalPlatformScripts = sessions.Sum(s => s.PlatformScriptCount);
            var totalRemediationScripts = sessions.Sum(s => s.RemediationScriptCount);
            var avgPlatformScripts = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.PlatformScriptCount), 1) : 0;
            var avgRemediationScripts = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.RemediationScriptCount), 1) : 0;

            computeStart.Stop();

            return new UsageMetricsSnapshot
            {
                Date = date,
                TenantId = tenantId,
                ComputedAt = DateTime.UtcNow,
                ComputeDurationMs = (int)computeStart.ElapsedMilliseconds,
                SessionsTotal = sessions.Count,
                SessionsSucceeded = succeeded,
                SessionsFailed = sessions.Count(s => s.Status == SessionStatus.Failed),
                SessionsInProgress = sessions.Count(s => s.Status == SessionStatus.InProgress),
                SessionsIncomplete = sessions.Count(s => s.Status == SessionStatus.Incomplete),
                SessionsSuccessRate = successRate,
                AvgDurationMinutes = avgDuration,
                MedianDurationMinutes = medianDuration,
                P95DurationMinutes = p95Duration,
                P99DurationMinutes = p99Duration,
                UniqueTenants = tenantId == "global" ? sessions.Select(s => s.TenantId).Distinct().Count() : 0,
                UserDrivenSessions = sessions.Count(s => s.IsUserDriven),
                WhiteGloveSessions = sessions.Count(s => s.IsPreProvisioned),
                UniqueUsers = uniqueUsers,
                LoginCount = loginCount,
                TopManufacturers = JsonConvert.SerializeObject(manufacturers),
                TopModels = JsonConvert.SerializeObject(models),
                AvgAppsPerSession = avgAppsPerSession,
                TotalUniqueApps = totalUniqueApps,
                AvgPlatformScriptsPerSession = avgPlatformScripts,
                AvgRemediationScriptsPerSession = avgRemediationScripts,
                TotalPlatformScripts = totalPlatformScripts,
                TotalRemediationScripts = totalRemediationScripts
            };
        }

        // Plan §5 PR6 / §16 R14: the session retention loop that previously lived here is now
        // owned by SessionDeletionMaintenanceFunction (12h cadence, dedicated watchdog OpsEvents,
        // cascade-delete dispatch via SessionRetentionFanoutService). The non-session tail of
        // this method (UserUsageLog + RuleStats cleanup) was already a separate method
        // (CleanupOldUsageDataAsync) and is now called directly from RunAllAsync and RunManualAsync.

        /// <summary>
        /// Reconciles rule stats for a given date by computing global aggregate rows
        /// from per-tenant rows. This ensures consistency even if real-time global
        /// increments were missed (e.g. during transient failures).
        /// </summary>
        private async Task AggregateRuleStatsForDateAsync(DateTime targetDate, List<SessionSummary> sessions)
        {
            try
            {
                var dateStr = targetDate.ToString("yyyy-MM-dd");
                // Fetch all tenant-specific rows for this date (excluding existing global rows)
                var allEntries = await _metricsRepo.GetRuleStatsAsync(startDate: dateStr, endDate: dateStr);
                var tenantEntries = allEntries.Where(e => e.TenantId != "global").ToList();

                if (tenantEntries.Count == 0)
                {
                    _logger.LogInformation("No tenant rule stats for {Date}, skipping rule stats aggregation", dateStr);
                    return;
                }

                // Group by RuleId to compute global aggregates
                var groups = tenantEntries.GroupBy(e => e.RuleId);
                int written = 0;

                foreach (var group in groups)
                {
                    var first = group.First();
                    var totalFire = group.Sum(e => e.FireCount);
                    var totalEval = group.Sum(e => e.EvaluationCount);
                    var totalSessions = group.Sum(e => e.SessionsEvaluated);
                    var totalConfSum = group.Sum(e => e.ConfidenceScoreSum);

                    var globalEntry = new RuleStatsEntry
                    {
                        Date = dateStr,
                        TenantId = "global",
                        RuleId = first.RuleId,
                        RuleType = first.RuleType,
                        RuleTitle = first.RuleTitle,
                        Category = first.Category,
                        Severity = first.Severity,
                        FireCount = totalFire,
                        EvaluationCount = totalEval,
                        SessionsEvaluated = totalSessions,
                        ConfidenceScoreSum = totalConfSum,
                        AvgConfidenceScore = totalFire > 0 ? Math.Round((double)totalConfSum / totalFire, 1) : 0,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _metricsRepo.SaveRuleStatsEntryAsync(globalEntry);
                    written++;
                }

                _logger.LogInformation("Rule stats aggregation for {Date}: reconciled {Count} global rule entries from {TenantEntries} tenant entries",
                    dateStr, written, tenantEntries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to aggregate rule stats for {Date} (non-fatal)", targetDate.ToString("yyyy-MM-dd"));
            }
        }

        /// <summary>
        /// Deletes usage tracking records older than 90 days from UserUsageLog.
        /// </summary>
        private async Task CleanupOldUsageDataAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-90).ToString("yyyyMMdd");
                var deleted = await _userUsageRepo.DeleteRecordsOlderThanAsync(cutoffDate);

                if (deleted > 0)
                    _logger.LogInformation("Usage data cleanup: deleted {Count} records older than 90 days (cutoff: {Cutoff})", deleted, cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old usage data");
            }

            // Rule stats retention: delete entries older than 90 days
            try
            {
                var ruleStatsCutoff = DateTime.UtcNow.AddDays(-90);
                var deletedRuleStats = await _metricsRepo.DeleteRuleStatsOlderThanAsync(ruleStatsCutoff);

                if (deletedRuleStats > 0)
                    _logger.LogInformation("Rule stats cleanup: deleted {Count} entries older than 90 days", deletedRuleStats);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old rule stats");
            }

            // User activity retention: delete login rows older than 90 days. The UserActivity table is
            // append-only (one row per login) and is otherwise only wiped on tenant offboarding, so
            // without this it grows unbounded and the full-table activity-metric scans get slower.
            try
            {
                var userActivityCutoff = DateTime.UtcNow.AddDays(-90);
                var deletedUserActivity = await _metricsRepo.DeleteUserActivityOlderThanAsync(userActivityCutoff);

                if (deletedUserActivity > 0)
                    _logger.LogInformation("User activity cleanup: deleted {Count} login rows older than 90 days", deletedUserActivity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old user activity");
            }

            // Presence retention: delete stale presence rows older than 1 day. Presence is purely a
            // "currently active" view (read only for windows ≤60 min); historical activity is covered by
            // the UserActivity table, so older presence rows carry zero value. A 1-day window minimizes
            // data (a one-off tester's UPN doesn't linger) and keeps the LastSeen scan in
            // GetActivePresenceAsync (a cross-partition scan polled every 30s by the GA page) tiny.
            try
            {
                var presenceCutoff = DateTime.UtcNow.AddDays(-1);
                var deletedPresence = await _metricsRepo.DeleteUserPresenceOlderThanAsync(presenceCutoff);

                if (deletedPresence > 0)
                    _logger.LogInformation("Presence cleanup: deleted {Count} stale rows older than 1 day", deletedPresence);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup stale presence rows");
            }
        }

        /// <summary>
        /// Recomputes platform-wide stats from all tables.
        /// Used on the public landing page (no auth required).
        /// </summary>
        private async Task RecomputePlatformStatsAsync()
        {
            _logger.LogInformation("Recomputing platform stats...");
            var sw = Stopwatch.StartNew();

            try
            {
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                var allConfigs = await _tenantConfigService.GetAllConfigurationsAsync();
                long totalEnrollments = 0;
                long successfulEnrollments = 0;
                long totalEvents = 0;
                long totalUsers = 0;
                // "Active tenants" = tenants that have actually produced at least one enrollment
                // session. tenantIds comes from the TenantConfiguration table (every registered
                // tenant, including those that never granted consent and can never send data), so
                // it equals TotalSignedUpTenants and must NOT be used for the active count. Count
                // the tenants whose session query returns rows instead.
                int activeTenants = 0;
                var uniqueModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tid in tenantIds)
                {
                    var sessions = await _sessionRepo.GetSessionsAsync(tid);
                    if (sessions.Count > 0)
                        activeTenants++;
                    totalEnrollments += sessions.Count;
                    successfulEnrollments += sessions.Count(s => s.Status == SessionStatus.Succeeded);

                    foreach (var s in sessions)
                    {
                        var modelKey = $"{s.Manufacturer} {s.Model}".Trim();
                        if (!string.IsNullOrEmpty(modelKey))
                            uniqueModels.Add(modelKey);
                        totalEvents += s.EventCount;
                    }

                    var userMetrics = await _metricsRepo.GetUserActivityMetricsAsync(tid);
                    totalUsers += userMetrics.TotalUniqueUsers;

                    // Seed / self-heal the cumulative per-tenant enrollment counter: the live
                    // session count (within retention) is a lower bound for "since signup".
                    // Raise-only — retention prunes sessions, so recomputing/overwriting would
                    // regress the counter (same reasoning as the TotalUsers clamp below).
                    if (sessions.Count > 0)
                        await _metricsRepo.EnsureTenantStatFloorAsync(tid, "TotalEnrollments", sessions.Count);
                }

                var existingStats = await _metricsRepo.GetPlatformStatsAsync();

                // "Users Seen" is a cumulative, public-facing high-water-mark. UserActivity is pruned to
                // 90 days, so the recomputed `totalUsers` only reflects recent loginers — clamp it to the
                // previously-persisted value so the cumulative figure can never regress after a cleanup.
                var cumulativeUsers = Math.Max(totalUsers, existingStats?.TotalUsers ?? 0);

                var stats = new PlatformStats
                {
                    TotalEnrollments = totalEnrollments,
                    TotalUsers = cumulativeUsers,
                    TotalTenants = activeTenants,
                    TotalSignedUpTenants = allConfigs.Count,
                    UniqueDeviceModels = uniqueModels.Count,
                    TotalEventsProcessed = totalEvents,
                    SuccessfulEnrollments = successfulEnrollments,
                    IssuesDetected = existingStats?.IssuesDetected ?? 0,
                    LastFullCompute = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow
                };

                await _metricsRepo.SavePlatformStatsAsync(stats);
                await TryPublishPlatformStatsJsonAsync(stats);

                sw.Stop();
                _logger.LogInformation($"Platform stats recomputed in {sw.ElapsedMilliseconds}ms: " +
                    $"{totalEnrollments} enrollments, {cumulativeUsers} users (cumulative), {tenantIds.Count} tenants, {uniqueModels.Count} models");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recompute platform stats");
            }
        }

        /// <summary>
        /// Publishes versioned platform stats JSON + alias manifest to Blob Storage.
        /// This must never fail maintenance execution.
        /// </summary>
        private async Task TryPublishPlatformStatsJsonAsync(PlatformStats stats)
        {
            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                var containerSasUrl = adminConfig.PlatformStatsBlobSasUrl?.Trim();

                if (string.IsNullOrWhiteSpace(containerSasUrl))
                {
                    _logger.LogInformation("Skipping platform stats JSON publish: PlatformStatsBlobSasUrl is not configured.");
                    return;
                }

                var containerClient = new BlobContainerClient(new Uri(containerSasUrl));
                var generatedAtUtc = DateTime.UtcNow;
                var versionedFileName = $"platform-stats.{generatedAtUtc:yyyy-MM-dd}.json";

                var versionedPayload = new
                {
                    totalEnrollments = stats.TotalEnrollments,
                    totalUsers = stats.TotalUsers,
                    totalTenants = stats.TotalTenants,
                    totalSignedUpTenants = stats.TotalSignedUpTenants,
                    uniqueDeviceModels = stats.UniqueDeviceModels,
                    totalEventsProcessed = stats.TotalEventsProcessed,
                    successfulEnrollments = stats.SuccessfulEnrollments,
                    issuesDetected = stats.IssuesDetected,
                    lastFullCompute = stats.LastFullCompute,
                    lastUpdated = stats.LastUpdated
                };

                var aliasPayload = new
                {
                    latest = versionedFileName,
                    generatedAtUtc = generatedAtUtc.ToString("o")
                };

                await UploadJsonBlobAsync(containerClient, versionedFileName, versionedPayload, PlatformStatsVersionedCacheControl);
                await UploadJsonBlobAsync(containerClient, PlatformStatsAliasFileName, aliasPayload, PlatformStatsAliasCacheControl);

                _logger.LogInformation(
                    "Published platform stats JSON blobs: versioned={VersionedFile} and alias={AliasFile}",
                    versionedFileName,
                    PlatformStatsAliasFileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish platform stats JSON to Blob Storage. Maintenance continues.");
            }
        }

        private async Task UploadJsonBlobAsync(BlobContainerClient containerClient, string blobName, object payload, string cacheControl)
        {
            var blobClient = containerClient.GetBlobClient(blobName);
            var json = JsonConvert.SerializeObject(payload);
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            await blobClient.UploadAsync(stream, overwrite: true);
            await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders
            {
                ContentType = "application/json; charset=utf-8",
                CacheControl = cacheControl
            });
            await blobClient.SetAccessTierAsync(AccessTier.Hot);
        }

        /// <summary>
        /// Removes distress reports older than 14 days. Distress data is unverified
        /// and low-volume; short retention keeps Table Storage lean.
        /// </summary>
        private async Task CleanupOldDistressReportsAsync()
        {
            const int retentionDays = 14;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            _logger.LogInformation("Starting distress report cleanup (retention: {Days} days, cutoff: {Cutoff:yyyy-MM-dd})", retentionDays, cutoff);

            try
            {
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                var totalDeleted = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var deleted = await _distressReportRepo.DeleteDistressReportsOlderThanAsync(tenantId, cutoff);
                        if (deleted > 0)
                        {
                            totalDeleted += deleted;
                            _logger.LogInformation("Tenant {TenantId}: Deleted {Count} old distress reports", tenantId, deleted);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cleanup distress reports for tenant {TenantId}", tenantId);
                    }
                }

                _logger.LogInformation("Distress report cleanup complete: {Total} reports deleted across all tenants", totalDeleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Distress report cleanup failed");
            }
        }

        /// <summary>
        /// Verifies that agent binaries and bootstrap script are available on blob storage.
        /// Records an OpsEvent for each missing item so Global Admins see it in the dashboard.
        /// </summary>
        private async Task CheckAgentBlobStorageAsync()
        {
            _logger.LogInformation("Checking agent blob storage availability...");

            var zipUrl = $"{AutopilotMonitor.Shared.Constants.AgentBlobBaseUrl}/{AutopilotMonitor.Shared.Constants.AgentZipFileName}";
            var ps1Url = $"{AutopilotMonitor.Shared.Constants.AgentBlobBaseUrl}/Install-AutopilotMonitor.ps1";

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var zipRequest = new HttpRequestMessage(HttpMethod.Head, zipUrl);
                var ps1Request = new HttpRequestMessage(HttpMethod.Head, ps1Url);

                var results = await Task.WhenAll(
                    client.SendAsync(zipRequest),
                    client.SendAsync(ps1Request)
                );

                var zipResponse = results[0];
                var ps1Response = results[1];

                if (!zipResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Agent ZIP not available on blob storage: HTTP {StatusCode}", (int)zipResponse.StatusCode);
                    await _opsEventService.RecordBlobStorageMissingAsync("Agent ZIP (AutopilotMonitor-Agent.zip)", (int)zipResponse.StatusCode);
                }

                if (!ps1Response.IsSuccessStatusCode)
                {
                    _logger.LogError("Bootstrap script not available on blob storage: HTTP {StatusCode}", (int)ps1Response.StatusCode);
                    await _opsEventService.RecordBlobStorageMissingAsync("Bootstrap script (Install-AutopilotMonitor.ps1)", (int)ps1Response.StatusCode);
                }

                if (zipResponse.IsSuccessStatusCode && ps1Response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Agent blob storage check passed: all binaries available");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent blob storage check failed — storage unreachable");
                await _opsEventService.RecordBlobStorageUnreachableAsync(ex.Message);
            }
        }

        /// <summary>
        /// Removes operational events older than the configured retention period.
        /// Retention is controlled by AdminConfiguration.OpsEventRetentionDays (default: 90).
        /// </summary>
        private async Task CleanupOldOpsEventsAsync()
        {
            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                var retentionDays = adminConfig.OpsEventRetentionDays;

                if (retentionDays <= 0)
                {
                    _logger.LogInformation("OpsEvents cleanup disabled (OpsEventRetentionDays = 0)");
                    return;
                }

                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                _logger.LogInformation("Starting OpsEvents cleanup (retention: {Days} days, cutoff: {Cutoff:yyyy-MM-dd})", retentionDays, cutoff);

                var deleted = await _opsEventRepo.DeleteOpsEventsOlderThanAsync(cutoff);
                _logger.LogInformation("OpsEvents cleanup complete: {Deleted} events deleted", deleted);

                if (deleted > 0)
                {
                    await _opsEventService.RecordOpsEventCleanupAsync(deleted, retentionDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpsEvents cleanup failed");
            }
        }

        /// <summary>
        /// Retention cleanup for append-only tables that previously had no purge mechanism and
        /// therefore grew unbounded: GlobalNotifications + TenantNotifications (hybrid: dismissed 30d /
        /// unread 180d), HardwareRejectionNotificationTracker (30d), AuditLogs (180d), UsageMetrics
        /// (180d), BackupJobs (365d). Each table is handled in its own try/catch so one failure never
        /// blocks the others. Retention windows are fixed product decisions (no AdminConfig knob),
        /// mirroring the DistressReports pattern. PlatformStats is intentionally excluded — it is a
        /// single upserted row, not append-only.
        /// </summary>
        private async Task CleanupUnboundedTablesAsync()
        {
            // Notifications use a hybrid policy: dismissed rows drop at the short window, but an
            // unread (still-actionable) admin warning survives until the long window so it is never
            // silently lost inside the dismiss window.
            const int notificationDismissedRetentionDays = 30;
            const int notificationUnreadRetentionDays = 180;
            const int hardwareRejectionRetentionDays = 30;
            const int auditLogRetentionDays = 180;
            const int usageMetricsRetentionDays = 180;
            const int backupJobRetentionDays = 365;

            var now = DateTime.UtcNow;
            var notificationDismissedCutoff = now.AddDays(-notificationDismissedRetentionDays);
            var notificationUnreadCutoff = now.AddDays(-notificationUnreadRetentionDays);

            try
            {
                var deleted = await _notificationRepo.DeleteNotificationsByRetentionAsync(notificationDismissedCutoff, notificationUnreadCutoff);
                if (deleted > 0)
                    _logger.LogInformation("Global notifications cleanup: deleted {Count} rows (dismissed {DismissedDays}d / unread {UnreadDays}d)", deleted, notificationDismissedRetentionDays, notificationUnreadRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old global notifications");
            }

            try
            {
                var deleted = await _tenantNotificationRepo.DeleteNotificationsByRetentionAsync(notificationDismissedCutoff, notificationUnreadCutoff);
                if (deleted > 0)
                    _logger.LogInformation("Tenant notifications cleanup: deleted {Count} rows (dismissed {DismissedDays}d / unread {UnreadDays}d)", deleted, notificationDismissedRetentionDays, notificationUnreadRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old tenant notifications");
            }

            try
            {
                var deleted = await _hardwareRejectionTracker.DeleteOlderThanAsync(now.AddDays(-hardwareRejectionRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Hardware-rejection tracker cleanup: deleted {Count} rows older than {Days} days", deleted, hardwareRejectionRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old hardware-rejection tracker rows");
            }

            try
            {
                var deleted = await _maintenanceRepo.DeleteAuditLogsOlderThanAsync(now.AddDays(-auditLogRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Audit log cleanup: deleted {Count} entries older than {Days} days", deleted, auditLogRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old audit logs");
            }

            try
            {
                var cutoffDate = now.AddDays(-usageMetricsRetentionDays).ToString("yyyy-MM-dd");
                var deleted = await _metricsRepo.DeleteUsageMetricsSnapshotsOlderThanAsync(cutoffDate);
                if (deleted > 0)
                    _logger.LogInformation("Usage metrics cleanup: deleted {Count} snapshots older than {Cutoff}", deleted, cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old usage metrics snapshots");
            }

            try
            {
                var deleted = await _backupJobsRepo.DeleteJobsOlderThanAsync(now.AddDays(-backupJobRetentionDays));
                if (deleted > 0)
                    _logger.LogInformation("Backup job cleanup: deleted {Count} records older than {Days} days", deleted, backupJobRetentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old backup job records");
            }
        }
        /// <summary>
        /// Detects and cleans up orphaned events — events stored in the Events table
        /// whose session no longer exists in the Sessions table.
        /// Uses the EventSessionIndex side-table for efficient detection (no full Events scan).
        /// Grace period: 24 hours to protect against register+ingest race conditions.
        /// </summary>
        private async Task CleanupOrphanedEventsAsync()
        {
            try
            {
                _logger.LogInformation("Starting orphaned events cleanup...");
                var sw = Stopwatch.StartNew();

                var orphans = await _maintenanceRepo.GetOrphanedEventSessionsAsync(TimeSpan.FromHours(24));

                if (orphans.Count == 0)
                {
                    _logger.LogInformation("No orphaned events found");
                    return;
                }

                _logger.LogWarning("Found {Count} orphaned event sessions, cleaning up...", orphans.Count);

                int totalEventsDeleted = 0;
                int sessionsCleanedUp = 0;
                // Per-orphan breakdown for the OpsEvent so the dashboard shows which tenant/session
                // was cleaned (the worker LogInformation below does not reach App Insights).
                var cleanedOrphans = new List<OrphanedEventSession>();

                foreach (var orphan in orphans)
                {
                    try
                    {
                        // Ordering invariant: the index entry is removed ONLY after the event
                        // delete completed cleanly (DeleteSessionEventsAsync throws on failure).
                        // Orphan detection scans only the index — deleting it first would make
                        // surviving event rows permanently undiscoverable.
                        var deletedEvents = await _maintenanceRepo.DeleteSessionEventsAsync(orphan.TenantId, orphan.SessionId);
                        await _maintenanceRepo.DeleteEventSessionIndexEntryAsync(orphan.TenantId, orphan.SessionId);

                        totalEventsDeleted += deletedEvents;
                        sessionsCleanedUp++;
                        // Report the actual rows deleted (may differ from the index's recorded count).
                        cleanedOrphans.Add(new OrphanedEventSession
                        {
                            TenantId = orphan.TenantId,
                            SessionId = orphan.SessionId,
                            LastIngestAt = orphan.LastIngestAt,
                            EventCount = deletedEvents
                        });

                        _logger.LogInformation(
                            "Cleaned orphan: TenantId={TenantId}, SessionId={SessionId}, Events={Events}",
                            orphan.TenantId, orphan.SessionId, deletedEvents);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to cleanup orphan: TenantId={TenantId}, SessionId={SessionId}",
                            orphan.TenantId, orphan.SessionId);
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Orphaned events cleanup completed in {Ms}ms: {Sessions} sessions, {Events} events deleted",
                    sw.ElapsedMilliseconds, sessionsCleanedUp, totalEventsDeleted);

                await _opsEventService.RecordOrphanEventsCleanedAsync(sessionsCleanedUp, totalEventsDeleted, cleanedOrphans);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orphaned events cleanup failed");
            }
        }
    }
}

