using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== HISTORICAL METRICS METHODS =====

        /// <summary>
        /// Saves a historical metrics snapshot
        /// </summary>
        public async Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);

                var entity = new TableEntity(metrics.Date, metrics.TenantId)
                {
                    ["ComputedAt"] = metrics.ComputedAt,
                    ["ComputeDurationMs"] = metrics.ComputeDurationMs,
                    ["SessionsTotal"] = metrics.SessionsTotal,
                    ["SessionsSucceeded"] = metrics.SessionsSucceeded,
                    ["SessionsFailed"] = metrics.SessionsFailed,
                    ["SessionsInProgress"] = metrics.SessionsInProgress,
                    ["SessionsIncomplete"] = metrics.SessionsIncomplete,
                    ["SessionsSuccessRate"] = metrics.SessionsSuccessRate,
                    ["AvgDurationMinutes"] = metrics.AvgDurationMinutes,
                    ["MedianDurationMinutes"] = metrics.MedianDurationMinutes,
                    ["P95DurationMinutes"] = metrics.P95DurationMinutes,
                    ["P99DurationMinutes"] = metrics.P99DurationMinutes,
                    ["UniqueTenants"] = metrics.UniqueTenants,
                    ["UniqueUsers"] = metrics.UniqueUsers,
                    ["LoginCount"] = metrics.LoginCount,
                    ["TopManufacturers"] = metrics.TopManufacturers,
                    ["TopModels"] = metrics.TopModels,
                    ["UserDrivenSessions"] = metrics.UserDrivenSessions,
                    ["WhiteGloveSessions"] = metrics.WhiteGloveSessions,
                    ["AvgAppsPerSession"] = metrics.AvgAppsPerSession,
                    ["TotalUniqueApps"] = metrics.TotalUniqueApps,
                    ["AvgPlatformScriptsPerSession"] = metrics.AvgPlatformScriptsPerSession,
                    ["AvgRemediationScriptsPerSession"] = metrics.AvgRemediationScriptsPerSession,
                    ["TotalPlatformScripts"] = metrics.TotalPlatformScripts,
                    ["TotalRemediationScripts"] = metrics.TotalRemediationScripts
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogInformation($"Saved historical metrics for {metrics.Date} / {metrics.TenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save historical metrics for {metrics.Date} / {metrics.TenantId}");
                return false;
            }
        }

        /// <summary>
        /// Gets historical metrics for a date range
        /// </summary>
        public async Task<List<UsageMetricsSnapshot>> GetUsageMetricsSnapshotAsync(string? tenantId = null, string? startDate = null, string? endDate = null, int maxResults = 100)
        {
            if (!string.IsNullOrEmpty(tenantId))
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);

                // Build filter
                var filters = new List<string>();

                if (!string.IsNullOrEmpty(startDate))
                    filters.Add($"PartitionKey ge '{startDate}'");

                if (!string.IsNullOrEmpty(endDate))
                    filters.Add($"PartitionKey le '{endDate}'");

                if (!string.IsNullOrEmpty(tenantId))
                    filters.Add($"RowKey eq '{tenantId}'");

                var filter = filters.Count > 0 ? string.Join(" and ", filters) : null;
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var results = new List<UsageMetricsSnapshot>();
                await foreach (var entity in query)
                {
                    results.Add(new UsageMetricsSnapshot
                    {
                        Date = entity.PartitionKey,
                        TenantId = entity.RowKey,
                        ComputedAt = entity.GetDateTimeOffset("ComputedAt")?.UtcDateTime ?? DateTime.UtcNow,
                        ComputeDurationMs = entity.GetInt32("ComputeDurationMs") ?? 0,
                        SessionsTotal = entity.GetInt32("SessionsTotal") ?? 0,
                        SessionsSucceeded = entity.GetInt32("SessionsSucceeded") ?? 0,
                        SessionsFailed = entity.GetInt32("SessionsFailed") ?? 0,
                        SessionsInProgress = entity.GetInt32("SessionsInProgress") ?? 0,
                        SessionsIncomplete = entity.GetInt32("SessionsIncomplete") ?? 0,
                        SessionsSuccessRate = entity.GetDouble("SessionsSuccessRate") ?? 0,
                        AvgDurationMinutes = entity.GetDouble("AvgDurationMinutes") ?? 0,
                        MedianDurationMinutes = entity.GetDouble("MedianDurationMinutes") ?? 0,
                        P95DurationMinutes = entity.GetDouble("P95DurationMinutes") ?? 0,
                        P99DurationMinutes = entity.GetDouble("P99DurationMinutes") ?? 0,
                        UniqueTenants = entity.GetInt32("UniqueTenants") ?? 0,
                        UniqueUsers = entity.GetInt32("UniqueUsers") ?? 0,
                        LoginCount = entity.GetInt32("LoginCount") ?? 0,
                        TopManufacturers = entity.GetString("TopManufacturers") ?? "[]",
                        TopModels = entity.GetString("TopModels") ?? "[]",
                        UserDrivenSessions = entity.GetInt32("UserDrivenSessions") ?? 0,
                        WhiteGloveSessions = entity.GetInt32("WhiteGloveSessions") ?? 0,
                        AvgAppsPerSession = entity.GetDouble("AvgAppsPerSession") ?? 0,
                        TotalUniqueApps = entity.GetInt32("TotalUniqueApps") ?? 0,
                        AvgPlatformScriptsPerSession = entity.GetDouble("AvgPlatformScriptsPerSession") ?? 0,
                        AvgRemediationScriptsPerSession = entity.GetDouble("AvgRemediationScriptsPerSession") ?? 0,
                        TotalPlatformScripts = entity.GetInt32("TotalPlatformScripts") ?? 0,
                        TotalRemediationScripts = entity.GetInt32("TotalRemediationScripts") ?? 0
                    });

                    if (results.Count >= maxResults) break;
                }

                return results.OrderByDescending(m => m.Date).Take(maxResults).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get historical metrics");
                return new List<UsageMetricsSnapshot>();
            }
        }

        /// <summary>
        /// Checks if a global usage metrics snapshot exists for a given date.
        /// Used by maintenance catch-up to determine which dates need aggregation.
        /// </summary>
        public async Task<bool> HasUsageMetricsSnapshotAsync(string date)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);
                await tableClient.GetEntityAsync<TableEntity>(date, "global");
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to check usage metrics snapshot for {date}");
                return false;
            }
        }

        /// <summary>
        /// Retention cleanup: deletes UsageMetrics snapshot rows whose date (PartitionKey,
        /// "yyyy-MM-dd") is strictly older than <paramref name="cutoffDate"/> (also "yyyy-MM-dd").
        /// The table holds one row per (date, tenant + "global") and is otherwise never pruned,
        /// so without this it grows by ~(tenants+1) rows per day forever. PartitionKey is a
        /// lexically-sortable date string, so the server-side range filter is partition-efficient.
        /// </summary>
        public async Task<int> DeleteUsageMetricsSnapshotsOlderThanAsync(string cutoffDate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);
                var filter = $"PartitionKey lt '{cutoffDate.Replace("'", "''")}'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    try
                    {
                        await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete usage metrics snapshot {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} usage metrics snapshots older than {Cutoff}", deleted, cutoffDate);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old usage metrics snapshots");
                return 0;
            }
        }

        // ===== APP INSTALL SUMMARIES METHODS =====

        /// <summary>
        /// Stores or updates an app install summary.
        /// Merges with any existing record so StartedAt is never overwritten with a later timestamp.
        /// PartitionKey: TenantId, RowKey: {SessionId}_{AppName}
        /// </summary>
        public async Task<bool> StoreAppInstallSummaryAsync(AppInstallSummary summary)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var rowKey = SanitizeTableKey($"{summary.SessionId}_{summary.AppName}");

                // Merge with existing record to preserve StartedAt from a prior batch
                var existingResult = await tableClient.GetEntityIfExistsAsync<TableEntity>(summary.TenantId, rowKey);
                if (existingResult.HasValue)
                {
                    var existing = existingResult.Value!;
                    var existingStartedAt = existing.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    if (existingStartedAt.HasValue && existingStartedAt.Value != DateTime.MinValue)
                    {
                        // Keep the earlier StartedAt; recalculate duration if CompletedAt is now known
                        if (summary.StartedAt == DateTime.MinValue || existingStartedAt.Value < summary.StartedAt)
                        {
                            summary.StartedAt = existingStartedAt.Value;
                            if (summary.CompletedAt.HasValue && summary.CompletedAt.Value >= summary.StartedAt)
                            {
                                summary.DurationSeconds = (int)(summary.CompletedAt.Value - summary.StartedAt).TotalSeconds;
                            }
                        }
                    }

                    // Preserve DownloadDurationSeconds and DownloadBytes from prior batch if current batch has no value
                    if (summary.DownloadDurationSeconds == 0)
                    {
                        var existingDlDuration = existing.GetInt32("DownloadDurationSeconds");
                        if (existingDlDuration.HasValue && existingDlDuration.Value > 0)
                            summary.DownloadDurationSeconds = existingDlDuration.Value;
                    }
                    if (summary.DownloadBytes == 0)
                    {
                        var existingDlBytes = existing.GetInt64("DownloadBytes");
                        if (existingDlBytes.HasValue && existingDlBytes.Value > 0)
                            summary.DownloadBytes = existingDlBytes.Value;
                    }

                    // Preserve DO telemetry from prior batch if current has none.
                    // Use DoDownloadMode as the indicator (>= 0 means DO data exists),
                    // NOT DoBytesFromPeers which is 0 when there are no peers (0% peer caching).
                    if (summary.DoDownloadMode < 0)
                    {
                        var existingDoMode = existing.GetInt32("DoDownloadMode");
                        if (existingDoMode.HasValue && existingDoMode.Value >= 0)
                        {
                            summary.DoFileSize = existing.GetInt64("DoFileSize") ?? 0;
                            summary.DoTotalBytesDownloaded = existing.GetInt64("DoTotalBytesDownloaded") ?? 0;
                            summary.DoBytesFromPeers = existing.GetInt64("DoBytesFromPeers") ?? 0;
                            summary.DoBytesFromHttp = existing.GetInt64("DoBytesFromHttp") ?? 0;
                            summary.DoPercentPeerCaching = existing.GetInt32("DoPercentPeerCaching") ?? 0;
                            summary.DoDownloadMode = existingDoMode.Value;
                            summary.DoDownloadDuration = existing.GetString("DoDownloadDuration") ?? string.Empty;
                            summary.DoBytesFromLanPeers = existing.GetInt64("DoBytesFromLanPeers") ?? 0;
                            summary.DoBytesFromGroupPeers = existing.GetInt64("DoBytesFromGroupPeers") ?? 0;
                            summary.DoBytesFromInternetPeers = existing.GetInt64("DoBytesFromInternetPeers") ?? 0;
                            summary.DoBytesFromLinkLocalPeers = existing.GetInt64("DoBytesFromLinkLocalPeers") ?? 0;
                            summary.DoBytesFromCacheServer = existing.GetInt64("DoBytesFromCacheServer") ?? 0;
                            summary.DoCacheHost = existing.GetString("DoCacheHost") ?? string.Empty;
                        }
                    }

                    // Preserve app metadata fields: AppVersion, AppType, AttemptNumber come from app_install_started
                    // and must not be wiped by a later _completed/_failed batch that doesn't re-emit them.
                    if (string.IsNullOrEmpty(summary.AppVersion))
                    {
                        var existingAppVersion = existing.GetString("AppVersion");
                        if (!string.IsNullOrEmpty(existingAppVersion))
                            summary.AppVersion = existingAppVersion;
                    }
                    if (string.IsNullOrEmpty(summary.AppType))
                    {
                        var existingAppType = existing.GetString("AppType");
                        if (!string.IsNullOrEmpty(existingAppType))
                            summary.AppType = existingAppType;
                    }
                    if (summary.AttemptNumber == 0)
                    {
                        var existingAttempt = existing.GetInt32("AttemptNumber");
                        if (existingAttempt.HasValue && existingAttempt.Value > 0)
                            summary.AttemptNumber = existingAttempt.Value;
                    }
                    // InstallerPhase only makes sense on failure — preserve if current batch didn't set one.
                    if (string.IsNullOrEmpty(summary.InstallerPhase))
                    {
                        var existingPhase = existing.GetString("InstallerPhase");
                        if (!string.IsNullOrEmpty(existingPhase))
                            summary.InstallerPhase = existingPhase;
                    }
                    // ExitCode: preserve prior value if current batch didn't emit one (nullable).
                    if (!summary.ExitCode.HasValue)
                    {
                        var existingExitCode = existing.GetInt32("ExitCode");
                        if (existingExitCode.HasValue)
                            summary.ExitCode = existingExitCode.Value;
                    }
                    // DetectionResult: preserve prior value.
                    if (string.IsNullOrEmpty(summary.DetectionResult))
                    {
                        var existingDetection = existing.GetString("DetectionResult");
                        if (!string.IsNullOrEmpty(existingDetection))
                            summary.DetectionResult = existingDetection;
                    }

                }

                var entity = BuildAppInstallSummaryEntity(summary, rowKey);

                // Merge-mode (default) preserves columns absent from the entity. Combined with the
                // dynamic property-add inside BuildAppInstallSummaryEntity this means a batch that
                // observed only progress / telemetry events for an app cannot clobber a prior
                // terminal Status / CompletedAt / DurationSeconds / FailureCode / FailureMessage.
                await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Merge);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store app install summary for {summary.AppName}");
                return false;
            }
        }

        /// <summary>
        /// Gets app install summaries for a tenant (fleet-level metrics). When <paramref name="sinceUtc"/>
        /// is supplied, a server-side <c>StartedAt ge</c> filter is applied so a windowed view (e.g. the
        /// app dashboard's days=30) does not dematerialize the tenant's entire StartedAt history. The
        /// filter still scans the partition (no secondary index on StartedAt), but only the in-window rows
        /// are deserialized and returned over the wire. <paramref name="sinceUtc"/> is a server-derived
        /// DateTime (never caller-supplied text), so interpolating it into the OData filter is injection-safe.
        /// </summary>
        public async Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId, DateTime? sinceUtc = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var filter = $"PartitionKey eq '{tenantId}'";
                if (sinceUtc.HasValue)
                    filter += $" and StartedAt ge datetime'{sinceUtc.Value:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var summaries = new List<AppInstallSummary>();
                await foreach (var entity in query)
                {
                    summaries.Add(MapToAppInstallSummary(entity));
                }

                return summaries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get app install summaries for tenant {tenantId}");
                return new List<AppInstallSummary>();
            }
        }

        /// <summary>
        /// Gets all app install summaries across all tenants (for global admin mode). When
        /// <paramref name="sinceUtc"/> is supplied, a server-side <c>StartedAt ge</c> filter scopes the
        /// (otherwise full-table) scan to the window so only in-window rows are deserialized.
        /// <paramref name="sinceUtc"/> is server-derived, so interpolating it is injection-safe.
        /// </summary>
        public async Task<List<AppInstallSummary>> GetAllAppInstallSummariesAsync(DateTime? sinceUtc = null)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var filter = sinceUtc.HasValue
                    ? $"StartedAt ge datetime'{sinceUtc.Value:yyyy-MM-ddTHH:mm:ss}Z'"
                    : null;
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var summaries = new List<AppInstallSummary>();
                await foreach (var entity in query)
                {
                    summaries.Add(MapToAppInstallSummary(entity));
                }

                return summaries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all app install summaries");
                return new List<AppInstallSummary>();
            }
        }

        private AppInstallSummary MapToAppInstallSummary(TableEntity entity)
        {
            return new AppInstallSummary
            {
                AppName = entity.GetString("AppName") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                Status = entity.GetString("Status") ?? "InProgress",
                DurationSeconds = entity.GetInt32("DurationSeconds") ?? 0,
                DownloadBytes = entity.GetInt64("DownloadBytes") ?? 0,
                DownloadDurationSeconds = entity.GetInt32("DownloadDurationSeconds") ?? 0,
                FailureCode = entity.GetString("FailureCode") ?? string.Empty,
                FailureMessage = entity.GetString("FailureMessage") ?? string.Empty,
                StartedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MinValue,
                CompletedAt = entity.GetDateTimeOffset("CompletedAt")?.UtcDateTime,
                // Delivery Optimization telemetry
                DoFileSize = entity.GetInt64("DoFileSize") ?? 0,
                DoTotalBytesDownloaded = entity.GetInt64("DoTotalBytesDownloaded") ?? 0,
                DoBytesFromPeers = entity.GetInt64("DoBytesFromPeers") ?? 0,
                DoBytesFromHttp = entity.GetInt64("DoBytesFromHttp") ?? 0,
                DoPercentPeerCaching = entity.GetInt32("DoPercentPeerCaching") ?? 0,
                DoDownloadMode = entity.GetInt32("DoDownloadMode") ?? -1,
                DoDownloadDuration = entity.GetString("DoDownloadDuration") ?? string.Empty,
                DoBytesFromLanPeers = entity.GetInt64("DoBytesFromLanPeers") ?? 0,
                DoBytesFromGroupPeers = entity.GetInt64("DoBytesFromGroupPeers") ?? 0,
                DoBytesFromInternetPeers = entity.GetInt64("DoBytesFromInternetPeers") ?? 0,
                DoBytesFromLinkLocalPeers = entity.GetInt64("DoBytesFromLinkLocalPeers") ?? 0,
                DoBytesFromCacheServer = entity.GetInt64("DoBytesFromCacheServer") ?? 0,
                DoCacheHost = entity.GetString("DoCacheHost") ?? string.Empty,
                // App metadata (from IME log parsing)
                AppVersion = entity.GetString("AppVersion") ?? string.Empty,
                AppType = entity.GetString("AppType") ?? string.Empty,
                AttemptNumber = entity.GetInt32("AttemptNumber") ?? 0,
                InstallerPhase = entity.GetString("InstallerPhase") ?? string.Empty,
                ExitCode = entity.GetInt32("ExitCode"),
                DetectionResult = entity.GetString("DetectionResult") ?? string.Empty
            };
        }

        // ===== PLATFORM STATS METHODS =====

        /// <summary>
        /// Gets the current platform stats (single row: global/current)
        /// </summary>
        public async Task<PlatformStats?> GetPlatformStatsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);
                var response = await tableClient.GetEntityAsync<TableEntity>("global", "current");
                var entity = response.Value;

                return new PlatformStats
                {
                    TotalEnrollments = entity.GetInt64("TotalEnrollments") ?? 0,
                    TotalUsers = entity.GetInt64("TotalUsers") ?? 0,
                    TotalTenants = entity.GetInt64("TotalTenants") ?? 0,
                    TotalSignedUpTenants = entity.GetInt64("TotalSignedUpTenants") ?? 0,
                    UniqueDeviceModels = entity.GetInt64("UniqueDeviceModels") ?? 0,
                    TotalEventsProcessed = entity.GetInt64("TotalEventsProcessed") ?? 0,
                    SuccessfulEnrollments = entity.GetInt64("SuccessfulEnrollments") ?? 0,
                    IssuesDetected = entity.GetInt64("IssuesDetected") ?? 0,
                    LastFullCompute = entity.GetDateTimeOffset("LastFullCompute")?.UtcDateTime ?? DateTime.MinValue,
                    LastUpdated = entity.GetDateTimeOffset("LastUpdated")?.UtcDateTime ?? DateTime.MinValue
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get platform stats");
                return null;
            }
        }

        /// <summary>
        /// Saves the full platform stats (upsert)
        /// </summary>
        public async Task<bool> SavePlatformStatsAsync(PlatformStats stats)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);

                var entity = new TableEntity("global", "current")
                {
                    ["TotalEnrollments"] = stats.TotalEnrollments,
                    ["TotalUsers"] = stats.TotalUsers,
                    ["TotalTenants"] = stats.TotalTenants,
                    ["TotalSignedUpTenants"] = stats.TotalSignedUpTenants,
                    ["UniqueDeviceModels"] = stats.UniqueDeviceModels,
                    ["TotalEventsProcessed"] = stats.TotalEventsProcessed,
                    ["SuccessfulEnrollments"] = stats.SuccessfulEnrollments,
                    ["IssuesDetected"] = stats.IssuesDetected,
                    ["LastFullCompute"] = stats.LastFullCompute,
                    ["LastUpdated"] = stats.LastUpdated
                };

                await tableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save platform stats");
                return false;
            }
        }

        /// <summary>
        /// Increments a specific platform stat counter atomically.
        /// Reads current value, increments, and writes back.
        /// </summary>
        public async Task IncrementPlatformStatAsync(string field, long amount = 1)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);

                TableEntity entity;

                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>("global", "current");
                    entity = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    entity = new TableEntity("global", "current")
                    {
                        ["TotalEnrollments"] = 0L,
                        ["TotalUsers"] = 0L,
                        ["TotalTenants"] = 0L,
                        ["TotalSignedUpTenants"] = 0L,
                        ["UniqueDeviceModels"] = 0L,
                        ["TotalEventsProcessed"] = 0L,
                        ["SuccessfulEnrollments"] = 0L,
                        ["IssuesDetected"] = 0L,
                        ["LastFullCompute"] = DateTime.MinValue,
                        ["LastUpdated"] = DateTime.UtcNow
                    };
                }

                var current = entity.GetInt64(field) ?? 0;
                entity[field] = current + amount;
                entity["LastUpdated"] = DateTime.UtcNow;

                await tableClient.UpsertEntityAsync(entity);
            }
            catch (Exception ex)
            {
                // Non-fatal: don't break the caller if stats update fails
                _logger.LogWarning(ex, $"Failed to increment platform stat {field}");
            }
        }

        // ===== TENANT STATS METHODS =====
        // Cumulative per-tenant counters in the PlatformStats table (PartitionKey: tenantId,
        // RowKey: "current"; the platform row's "global" partition can never collide with a
        // tenant GUID). Unlike PlatformStats these counters are NEVER recomputed from live data
        // (retention prunes sessions), so a lost increment is permanent — writes use ETag CAS
        // with retries instead of the platform row's last-writer-wins upsert.

        private const string TenantStatsRowKey = "current";
        private const int TenantStatsCasRetries = 4;

        /// <summary>
        /// Gets the cumulative per-tenant counters, or null if none were recorded yet.
        /// </summary>
        public async Task<TenantStats?> GetTenantStatsAsync(string tenantId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, TenantStatsRowKey);
                var entity = response.Value;

                return new TenantStats
                {
                    TotalEnrollments = entity.GetInt64("TotalEnrollments") ?? 0,
                    LastUpdated = entity.GetDateTimeOffset("LastUpdated")?.UtcDateTime ?? DateTime.MinValue
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tenant stats for tenant {TenantId}", tenantId);
                return null;
            }
        }

        /// <summary>
        /// Increments a cumulative per-tenant counter. ETag CAS with retries; fail-soft
        /// (registration must never break because a stats write failed).
        /// </summary>
        public async Task IncrementTenantStatAsync(string tenantId, string field, long amount = 1)
        {
            await MutateTenantStatAsync(tenantId, field,
                current => current + amount,
                missingRowValue: amount);
        }

        /// <summary>
        /// Raises a cumulative per-tenant counter to at least <paramref name="floor"/> — used by the
        /// maintenance recompute to seed pre-existing tenants and self-heal lost increments from the
        /// live session count (a lower bound, since retention prunes). Never lowers the counter.
        /// </summary>
        public async Task EnsureTenantStatFloorAsync(string tenantId, string field, long floor)
        {
            await MutateTenantStatAsync(tenantId, field,
                current => Math.Max(current, floor),
                missingRowValue: floor);
        }

        private async Task MutateTenantStatAsync(string tenantId, string field, Func<long, long> mutate, long missingRowValue)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);

                for (int attempt = 1; attempt <= TenantStatsCasRetries; attempt++)
                {
                    try
                    {
                        TableEntity entity;
                        try
                        {
                            var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, TenantStatsRowKey);
                            entity = response.Value;
                        }
                        catch (RequestFailedException ex) when (ex.Status == 404)
                        {
                            // AddEntity (not upsert) so a concurrent creator surfaces as 409 → retry
                            // lands in the update branch instead of clobbering the winner's value.
                            var fresh = new TableEntity(tenantId, TenantStatsRowKey)
                            {
                                [field] = missingRowValue,
                                ["LastUpdated"] = DateTime.UtcNow
                            };
                            await tableClient.AddEntityAsync(fresh);
                            return;
                        }

                        var current = entity.GetInt64(field) ?? 0;
                        var next = mutate(current);
                        if (next == current)
                            return;

                        entity[field] = next;
                        entity["LastUpdated"] = DateTime.UtcNow;
                        await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
                        return;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409)
                    {
                        if (attempt == TenantStatsCasRetries)
                        {
                            _logger.LogWarning(
                                "Tenant stat {Field} update for tenant {TenantId} lost the CAS race {Retries} times — giving up (status {Status})",
                                field, tenantId, TenantStatsCasRetries, ex.Status);
                            return;
                        }
                        await Task.Delay(50 * attempt);
                    }
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: don't break the caller if stats update fails
                _logger.LogWarning(ex, "Failed to update tenant stat {Field} for tenant {TenantId}", field, tenantId);
            }
        }

        // ===== USER ACTIVITY METHODS =====

        /// <summary>
        /// Records a user login activity
        /// PartitionKey: TenantId, RowKey: {invertedTicks}_{Guid} for reverse-chronological ordering
        /// </summary>
        public async Task RecordUserLoginAsync(string tenantId, string upn, string? displayName, string? objectId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var now = DateTime.UtcNow;
                var invertedTicks = (DateTime.MaxValue.Ticks - now.Ticks).ToString("D20");

                var entity = new TableEntity(tenantId, $"{invertedTicks}_{Guid.NewGuid():N}")
                {
                    ["Upn"] = upn ?? string.Empty,
                    ["DisplayName"] = displayName ?? string.Empty,
                    ["ObjectId"] = objectId ?? string.Empty,
                    ["LoginAt"] = now
                };

                await tableClient.AddEntityAsync(entity);
                _logger.LogDebug($"Recorded login for {upn} in tenant {tenantId}");
            }
            catch (Exception ex)
            {
                // Don't fail the login if activity recording fails
                _logger.LogWarning(ex, $"Failed to record login activity for {upn}");
            }
        }

        // ===== LIVE PRESENCE METHODS =====

        /// <summary>
        /// Derives a collision-free, case-insensitive Azure Table RowKey from a UPN: the SHA-256 hash
        /// of the lowercased UPN as hex. A hash (rather than char-replacement) guarantees distinct UPNs
        /// never collide — replacing disallowed chars with '_' would map e.g. "a/b@x" and "a_b@x" to the
        /// same key, letting one user overwrite the other. The original UPN is kept in the Upn column.
        /// </summary>
        internal static string PresenceRowKey(string upn)
        {
            var normalized = (upn ?? string.Empty).ToLowerInvariant();
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Upserts a single presence row (PK=tenantId, RK=hash(UPN)) stamped with LastSeen=now.
        /// One row per user — overwritten on every call, so the table never grows past the distinct-user count.
        /// </summary>
        public async Task RecordUserPresenceAsync(string tenantId, string upn, string userRole)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserPresence);
                var entity = new TableEntity(tenantId, PresenceRowKey(upn))
                {
                    ["Upn"] = upn ?? string.Empty,
                    ["UserRole"] = userRole ?? string.Empty,
                    ["LastSeen"] = DateTime.UtcNow
                };

                await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                // Presence is best-effort observability — never fail the request it rode in on.
                _logger.LogDebug(ex, "Failed to record presence for {Upn} in tenant {TenantId}", upn, tenantId);
            }
        }

        /// <summary>
        /// Returns all users whose LastSeen is within the given window (cross-tenant), newest first.
        /// Intentionally NOT wrapped in a swallow-and-return-empty try/catch: a storage failure must
        /// surface to the caller so the operator-facing endpoint returns 5xx, rather than masquerade as
        /// "0 users active". The query projects only the columns it needs to keep the response lean.
        /// </summary>
        public async Task<List<UserPresenceEntry>> GetActivePresenceAsync(TimeSpan window)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserPresence);
            var cutoff = DateTime.UtcNow - window;
            var filter = $"LastSeen ge datetime'{cutoff:yyyy-MM-ddTHH:mm:ss}Z'";
            // PartitionKey is included explicitly: it carries TenantId (read below) and the SDK's
            // projection only guarantees the columns named in select.
            var query = tableClient.QueryAsync<TableEntity>(
                filter: filter,
                select: new[] { "PartitionKey", "Upn", "UserRole", "LastSeen" });

            var results = new List<UserPresenceEntry>();
            await foreach (var entity in query)
            {
                results.Add(new UserPresenceEntry
                {
                    TenantId = entity.PartitionKey ?? string.Empty,
                    Upn = entity.GetString("Upn") ?? string.Empty,
                    UserRole = entity.GetString("UserRole") ?? string.Empty,
                    LastSeen = entity.GetDateTime("LastSeen") ?? DateTime.MinValue
                });
            }

            results.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
            return results;
        }

        /// <summary>
        /// Retention cleanup: deletes presence rows whose LastSeen is older than the cutoff. Keeps the
        /// table to genuinely-recent users — one-off testers don't linger indefinitely (data minimization).
        /// </summary>
        public async Task<int> DeleteUserPresenceOlderThanAsync(DateTime cutoffUtc)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserPresence);
                var filter = $"LastSeen lt datetime'{cutoffUtc:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                    deleted++;
                }

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} stale presence rows older than {Cutoff:yyyy-MM-dd}", deleted, cutoffUtc);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete stale presence rows");
                return 0;
            }
        }

        /// <summary>
        /// Gets user activity metrics for a specific tenant
        /// </summary>
        public async Task<UserActivityMetrics> GetUserActivityMetricsAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var now = DateTime.UtcNow;
                var today = now.Date;
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                var allUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var todayUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last7Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last30Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int todayLogins = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    var loginAt = entity.GetDateTime("LoginAt") ?? DateTime.MinValue;

                    if (string.IsNullOrEmpty(upn)) continue;

                    allUpns.Add(upn);

                    if (loginAt >= last30Days) last30Upns.Add(upn);
                    if (loginAt >= last7Days) last7Upns.Add(upn);
                    if (loginAt >= today)
                    {
                        todayUpns.Add(upn);
                        todayLogins++;
                    }
                }

                return new UserActivityMetrics
                {
                    TotalUniqueUsers = allUpns.Count,
                    DailyLogins = todayLogins,
                    ActiveUsersLast7Days = last7Upns.Count,
                    ActiveUsersLast30Days = last30Upns.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get user activity metrics for tenant {tenantId}");
                return new UserActivityMetrics();
            }
        }

        /// <summary>
        /// Gets user activity metrics across all tenants (for global admin)
        /// </summary>
        public async Task<UserActivityMetrics> GetAllUserActivityMetricsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var query = tableClient.QueryAsync<TableEntity>();

                var now = DateTime.UtcNow;
                var today = now.Date;
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                var allUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var todayUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last7Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last30Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int todayLogins = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    var loginAt = entity.GetDateTime("LoginAt") ?? DateTime.MinValue;

                    if (string.IsNullOrEmpty(upn)) continue;

                    allUpns.Add(upn);

                    if (loginAt >= last30Days) last30Upns.Add(upn);
                    if (loginAt >= last7Days) last7Upns.Add(upn);
                    if (loginAt >= today)
                    {
                        todayUpns.Add(upn);
                        todayLogins++;
                    }
                }

                return new UserActivityMetrics
                {
                    TotalUniqueUsers = allUpns.Count,
                    DailyLogins = todayLogins,
                    ActiveUsersLast7Days = last7Upns.Count,
                    ActiveUsersLast30Days = last30Upns.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all user activity metrics");
                return new UserActivityMetrics();
            }
        }

        /// <summary>
        /// Gets user login count for a specific date range (used by daily maintenance)
        /// </summary>
        public async Task<(int uniqueUsers, int loginCount)> GetUserActivityForDateAsync(string? tenantId, DateTime date)
        {
            if (!string.IsNullOrEmpty(tenantId) && tenantId != "global")
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var startOfDay = date.Date;
                var endOfDay = startOfDay.AddDays(1);

                string filter;
                if (!string.IsNullOrEmpty(tenantId) && tenantId != "global")
                {
                    filter = $"PartitionKey eq '{tenantId}' and LoginAt ge datetime'{startOfDay:yyyy-MM-ddTHH:mm:ss}Z' and LoginAt lt datetime'{endOfDay:yyyy-MM-ddTHH:mm:ss}Z'";
                }
                else
                {
                    filter = $"LoginAt ge datetime'{startOfDay:yyyy-MM-ddTHH:mm:ss}Z' and LoginAt lt datetime'{endOfDay:yyyy-MM-ddTHH:mm:ss}Z'";
                }

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int loginCount = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    if (!string.IsNullOrEmpty(upn))
                    {
                        upns.Add(upn);
                        loginCount++;
                    }
                }

                return (upns.Count, loginCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get user activity for date {date:yyyy-MM-dd}");
                return (0, 0);
            }
        }

        /// <summary>
        /// Retention cleanup: deletes UserActivity login rows whose LoginAt is older than the cutoff.
        /// The table is append-only (one row per login) and is otherwise only wiped on tenant offboarding,
        /// so without this it grows unbounded and the full-table metrics scans get progressively slower.
        /// </summary>
        public async Task<int> DeleteUserActivityOlderThanAsync(DateTime cutoffUtc)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var filter = $"LoginAt lt datetime'{cutoffUtc:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                    deleted++;
                }

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} user activity rows older than {Cutoff:yyyy-MM-dd}", deleted, cutoffUtc);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old user activity rows");
                return 0;
            }
        }

        // ===== RULE STATS METHODS =====

        /// <summary>
        /// Increments rule stats counters atomically for a single rule evaluation.
        /// Creates the row if it doesn't exist. Uses read-modify-write pattern.
        /// Called once per rule per session evaluation (for both tenant-specific and global rows).
        /// </summary>
        public async Task IncrementRuleStatAsync(
            string date, string tenantId, string ruleId, string ruleType,
            string ruleTitle, string category, string severity,
            bool fired, int? confidenceScore)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);
                var rowKey = $"{tenantId}_{ruleId}";

                TableEntity entity;
                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>(date, rowKey);
                    entity = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    entity = new TableEntity(date, rowKey)
                    {
                        ["RuleId"] = ruleId,
                        ["RuleType"] = ruleType,
                        ["RuleTitle"] = ruleTitle,
                        ["Category"] = category,
                        ["Severity"] = severity,
                        ["FireCount"] = 0,
                        ["EvaluationCount"] = 0,
                        ["SessionsEvaluated"] = 0,
                        ["ConfidenceScoreSum"] = 0L,
                        ["AvgConfidenceScore"] = 0.0,
                        ["UpdatedAt"] = DateTime.UtcNow
                    };
                }

                // Always increment evaluation and session counters
                entity["EvaluationCount"] = (entity.GetInt32("EvaluationCount") ?? 0) + 1;
                entity["SessionsEvaluated"] = (entity.GetInt32("SessionsEvaluated") ?? 0) + 1;

                if (fired)
                {
                    var newFireCount = (entity.GetInt32("FireCount") ?? 0) + 1;
                    entity["FireCount"] = newFireCount;

                    if (confidenceScore.HasValue)
                    {
                        var newSum = (entity.GetInt64("ConfidenceScoreSum") ?? 0) + confidenceScore.Value;
                        entity["ConfidenceScoreSum"] = newSum;
                        entity["AvgConfidenceScore"] = newFireCount > 0 ? (double)newSum / newFireCount : 0.0;
                    }
                }

                // Keep metadata fresh
                entity["RuleTitle"] = ruleTitle;
                entity["Category"] = category;
                entity["Severity"] = severity;
                entity["UpdatedAt"] = DateTime.UtcNow;

                await tableClient.UpsertEntityAsync(entity);
            }
            catch (Exception ex)
            {
                // Non-fatal: don't break the caller if stats update fails
                _logger.LogWarning(ex, "Failed to increment rule stat for {RuleId} / {TenantId}", ruleId, tenantId);
            }
        }

        /// <summary>
        /// Saves a fully computed rule stats entry (used by daily aggregation).
        /// </summary>
        public async Task<bool> SaveRuleStatsEntryAsync(RuleStatsEntry entry)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);
                var rowKey = $"{entry.TenantId}_{entry.RuleId}";

                var entity = new TableEntity(entry.Date, rowKey)
                {
                    ["RuleId"] = entry.RuleId,
                    ["RuleType"] = entry.RuleType,
                    ["RuleTitle"] = entry.RuleTitle,
                    ["Category"] = entry.Category,
                    ["Severity"] = entry.Severity,
                    ["FireCount"] = entry.FireCount,
                    ["EvaluationCount"] = entry.EvaluationCount,
                    ["SessionsEvaluated"] = entry.SessionsEvaluated,
                    ["AvgConfidenceScore"] = entry.AvgConfidenceScore,
                    ["ConfidenceScoreSum"] = entry.ConfidenceScoreSum,
                    ["UpdatedAt"] = entry.UpdatedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save rule stats entry for {RuleId} / {TenantId} / {Date}",
                    entry.RuleId, entry.TenantId, entry.Date);
                return false;
            }
        }

        /// <summary>
        /// Gets rule stats entries for a date range, optionally filtered by tenant and/or rule type.
        /// </summary>
        public async Task<List<RuleStatsEntry>> GetRuleStatsAsync(
            string? tenantId = null, string? startDate = null, string? endDate = null,
            string? ruleType = null, int maxResults = 500)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);

                var filter = BuildRuleStatsFilter(tenantId, startDate, endDate, ruleType);
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var results = new List<RuleStatsEntry>();
                await foreach (var entity in query)
                {
                    results.Add(MapToRuleStatsEntry(entity));
                    if (results.Count >= maxResults) break;
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get rule stats");
                return new List<RuleStatsEntry>();
            }
        }

        /// <summary>
        /// Builds the OData filter for <see cref="GetRuleStatsAsync"/>. All inputs are caller-supplied
        /// (query-string params, JWT tenant) and are interpolated, so every value MUST be escaped via
        /// <see cref="ODataSanitizer.EscapeValue"/>. Without it, input such as ruleType="x' or RowKey ge '"
        /// would inject an OR clause that escapes the RowKey-prefix tenant scope and reads every tenant's rows.
        /// Extracted as a pure function so the escaping is unit-testable.
        /// </summary>
        internal static string? BuildRuleStatsFilter(string? tenantId, string? startDate, string? endDate, string? ruleType)
        {
            var filters = new List<string>();

            if (!string.IsNullOrEmpty(startDate))
                filters.Add($"PartitionKey ge '{ODataSanitizer.EscapeValue(startDate)}'");
            if (!string.IsNullOrEmpty(endDate))
                filters.Add($"PartitionKey le '{ODataSanitizer.EscapeValue(endDate)}'");

            // Filter by tenant via RowKey prefix
            if (!string.IsNullOrEmpty(tenantId))
            {
                var safeTenantId = ODataSanitizer.EscapeValue(tenantId);
                filters.Add($"RowKey ge '{safeTenantId}_'");
                filters.Add($"RowKey lt '{safeTenantId}_~'");  // ~ is after all printable ASCII
            }

            if (!string.IsNullOrEmpty(ruleType))
                filters.Add($"RuleType eq '{ODataSanitizer.EscapeValue(ruleType)}'");

            return filters.Count > 0 ? string.Join(" and ", filters) : null;
        }

        /// <summary>
        /// Deletes rule stats entries older than a given date (retention cleanup).
        /// </summary>
        public async Task<int> DeleteRuleStatsOlderThanAsync(DateTime cutoffDate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);
                var cutoffStr = cutoffDate.ToString("yyyy-MM-dd");
                var filter = $"PartitionKey lt '{cutoffStr}'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                    deleted++;
                }

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} rule stats entries older than {Cutoff}", deleted, cutoffStr);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old rule stats entries");
                return 0;
            }
        }

        private static RuleStatsEntry MapToRuleStatsEntry(TableEntity entity)
        {
            var rowKey = entity.RowKey ?? string.Empty;
            var separatorIndex = rowKey.IndexOf('_');
            var tenantId = separatorIndex > 0 ? rowKey.Substring(0, separatorIndex) : rowKey;
            // RuleId may contain underscores, so take everything after the first underscore
            var ruleId = separatorIndex > 0 && separatorIndex < rowKey.Length - 1
                ? rowKey.Substring(separatorIndex + 1)
                : entity.GetString("RuleId") ?? string.Empty;

            return new RuleStatsEntry
            {
                Date = entity.PartitionKey ?? string.Empty,
                TenantId = tenantId,
                RuleId = entity.GetString("RuleId") ?? ruleId,
                RuleType = entity.GetString("RuleType") ?? string.Empty,
                RuleTitle = entity.GetString("RuleTitle") ?? string.Empty,
                Category = entity.GetString("Category") ?? string.Empty,
                Severity = entity.GetString("Severity") ?? string.Empty,
                FireCount = entity.GetInt32("FireCount") ?? 0,
                EvaluationCount = entity.GetInt32("EvaluationCount") ?? 0,
                SessionsEvaluated = entity.GetInt32("SessionsEvaluated") ?? 0,
                AvgConfidenceScore = entity.GetDouble("AvgConfidenceScore") ?? 0,
                ConfidenceScoreSum = entity.GetInt64("ConfidenceScoreSum") ?? 0,
                UpdatedAt = entity.GetDateTimeOffset("UpdatedAt")?.UtcDateTime ?? DateTime.UtcNow
            };
        }

        // Builds the TableEntity for an upsert in such a way that "no observation" sentinels
        // (empty Status, null CompletedAt, non-positive DurationSeconds, empty FailureCode /
        // FailureMessage) are simply absent from the entity. Combined with TableUpdateMode.Merge
        // this prevents a progress / telemetry-only batch from clobbering a prior terminal state.
        // Always-known fields (AppName, SessionId, TenantId, StartedAt) and fields whose own
        // sentinel handling lives elsewhere (DownloadBytes, DO telemetry, app metadata) stay
        // present unconditionally — those have established preserve-from-existing logic upstream
        // in StoreAppInstallSummaryAsync.
        internal static TableEntity BuildAppInstallSummaryEntity(AppInstallSummary summary, string rowKey)
        {
            var entity = new TableEntity(summary.TenantId, rowKey)
            {
                ["AppName"] = summary.AppName ?? string.Empty,
                ["SessionId"] = summary.SessionId ?? string.Empty,
                ["TenantId"] = summary.TenantId ?? string.Empty,
                ["DownloadBytes"] = summary.DownloadBytes,
                ["DownloadDurationSeconds"] = summary.DownloadDurationSeconds,
                ["StartedAt"] = EnsureUtc(summary.StartedAt),
                // Delivery Optimization telemetry
                ["DoFileSize"] = summary.DoFileSize,
                ["DoTotalBytesDownloaded"] = summary.DoTotalBytesDownloaded,
                ["DoBytesFromPeers"] = summary.DoBytesFromPeers,
                ["DoBytesFromHttp"] = summary.DoBytesFromHttp,
                ["DoPercentPeerCaching"] = summary.DoPercentPeerCaching,
                ["DoDownloadMode"] = summary.DoDownloadMode,
                ["DoDownloadDuration"] = summary.DoDownloadDuration ?? string.Empty,
                ["DoBytesFromLanPeers"] = summary.DoBytesFromLanPeers,
                ["DoBytesFromGroupPeers"] = summary.DoBytesFromGroupPeers,
                ["DoBytesFromInternetPeers"] = summary.DoBytesFromInternetPeers,
                ["DoBytesFromLinkLocalPeers"] = summary.DoBytesFromLinkLocalPeers,
                ["DoBytesFromCacheServer"] = summary.DoBytesFromCacheServer,
                ["DoCacheHost"] = summary.DoCacheHost ?? string.Empty,
                // App metadata (from IME log parsing)
                ["AppVersion"] = summary.AppVersion ?? string.Empty,
                ["AppType"] = summary.AppType ?? string.Empty,
                ["AttemptNumber"] = summary.AttemptNumber,
                ["InstallerPhase"] = summary.InstallerPhase ?? string.Empty,
                ["ExitCode"] = summary.ExitCode,
                ["DetectionResult"] = summary.DetectionResult ?? string.Empty
            };

            // Sentinel-gated lifecycle columns: only write when the current batch observed them.
            if (!string.IsNullOrEmpty(summary.Status))
                entity["Status"] = summary.Status;
            if (summary.DurationSeconds > 0)
                entity["DurationSeconds"] = summary.DurationSeconds;
            if (summary.CompletedAt.HasValue)
                entity["CompletedAt"] = EnsureUtc(summary.CompletedAt.Value);
            if (!string.IsNullOrEmpty(summary.FailureCode))
                entity["FailureCode"] = summary.FailureCode;
            if (!string.IsNullOrEmpty(summary.FailureMessage))
                entity["FailureMessage"] = summary.FailureMessage;

            return entity;
        }
    }
}
