using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using System.Threading;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Telemetry;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Caching;
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
                    ReconcileAppInstallSummaryWithExisting(summary, existingResult.Value!);
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
        /// Reconciles an incoming per-batch <see cref="AppInstallSummary"/> against the stored row
        /// before the Merge-mode upsert. Extracted as an internal static seam so the cross-batch
        /// contract (earliest StartedAt wins, terminal status is sticky, out-of-order duration
        /// recompute, prior-batch metadata preservation) is pinned by unit tests.
        /// </summary>
        internal static void ReconcileAppInstallSummaryWithExisting(AppInstallSummary summary, TableEntity existing)
        {
                    // Terminal status is sticky (PR0 hardening, same out-of-order family as Q4):
                    // a late or replayed batch that only observed pre-terminal events carries
                    // Status "InProgress" — a real (non-sentinel) value that Merge-mode would
                    // happily write over a row the terminal batch already closed. Only another
                    // terminal observation may change a terminal status (Failed→Succeeded on a
                    // successful retry stays legal).
                    var existingStatus = existing.GetString("Status");
                    if ((existingStatus == "Succeeded" || existingStatus == "Failed") &&
                        summary.Status != "Succeeded" && summary.Status != "Failed")
                    {
                        summary.Status = existingStatus;
                    }

                    var existingStartedAt = existing.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    if (existingStartedAt.HasValue && existingStartedAt.Value != DateTime.MinValue)
                    {
                        // Keep the earlier StartedAt — it is the window/bucket filter column and
                        // the radar's version-ordering key, never re-anchored to a later attempt.
                        // Duration recompute happens attempt-aware further down.
                        if (summary.StartedAt == DateTime.MinValue || existingStartedAt.Value < summary.StartedAt)
                            summary.StartedAt = existingStartedAt.Value;
                    }

                    // Attempt anchor: the LATEST start wins across batches (mirror of the
                    // in-batch max-fold — a replayed older batch can never regress it), and the
                    // pass counter only grows when the batch carries genuinely newer starts
                    // (replay guard: a batch whose newest start is not newer than the stored
                    // anchor was already counted).
                    var existingLastAttempt = existing.GetDateTimeOffset("LastAttemptStartedAt")?.UtcDateTime;
                    var existingPassCount = existing.GetInt32("InstallPassCount") ?? 0;
                    if (summary.LastAttemptStartedAt.HasValue &&
                        (!existingLastAttempt.HasValue || summary.LastAttemptStartedAt.Value > existingLastAttempt.Value))
                    {
                        summary.InstallPassCount += existingPassCount;
                    }
                    else
                    {
                        summary.InstallPassCount = Math.Max(summary.InstallPassCount, existingPassCount);
                        if (existingLastAttempt.HasValue)
                            summary.LastAttemptStartedAt = existingLastAttempt;
                    }

                    // Cross-batch mirror of the weaker-terminal guard: a batch that only saw a
                    // Skipped/Postponed re-evaluation pass never overrides a stored
                    // Installed/Error, and its CompletedAt/DurationSeconds must not re-describe
                    // the row (nulling them omits the columns → Merge preserves the stored
                    // values of the surviving attempt).
                    var existingTerminalStateForGuard = existing.GetString("TerminalState");
                    if ((existingTerminalStateForGuard == "Installed" || existingTerminalStateForGuard == "Error") &&
                        (summary.TerminalState == "Skipped" || summary.TerminalState == "Postponed"))
                    {
                        summary.TerminalState = existingTerminalStateForGuard!;
                        if (existingStatus == "Succeeded" || existingStatus == "Failed")
                            summary.Status = existingStatus!;
                        summary.CompletedAt = null;
                        summary.DurationSeconds = 0;
                    }

                    // Q4 (source-data audit 2026-07-26): out-of-order arrival — the terminal batch
                    // landed FIRST (row already carries CompletedAt), the started batch arrives now.
                    // Neither batch alone could compute the duration; adopt the stored CompletedAt
                    // so the attempt-aware recompute below can pair both endpoints.
                    if (!summary.CompletedAt.HasValue && summary.StartedAt != DateTime.MinValue)
                    {
                        var existingCompletedAt = existing.GetDateTimeOffset("CompletedAt")?.UtcDateTime;
                        if (existingCompletedAt.HasValue && existingCompletedAt.Value >= summary.StartedAt)
                            summary.CompletedAt = existingCompletedAt.Value;
                    }

                    // Attempt-aware duration recompute — single site for both endpoints-known
                    // paths (in-order and Q4 out-of-order). Anchor = latest attempt start at or
                    // before CompletedAt; rows without an observed start keep the historical
                    // StartedAt (span) fallback. A LastAttemptStartedAt NEWER than CompletedAt
                    // means a fresh pass is in flight — the stored duration of the completed
                    // attempt stands (DurationSeconds 0 omits the column, Merge preserves it).
                    if (summary.CompletedAt.HasValue)
                    {
                        if (summary.LastAttemptStartedAt.HasValue)
                        {
                            if (summary.LastAttemptStartedAt.Value <= summary.CompletedAt.Value)
                                summary.DurationSeconds = (int)(summary.CompletedAt.Value - summary.LastAttemptStartedAt.Value).TotalSeconds;
                        }
                        else if (summary.StartedAt != DateTime.MinValue && summary.CompletedAt.Value >= summary.StartedAt)
                        {
                            summary.DurationSeconds = (int)(summary.CompletedAt.Value - summary.StartedAt).TotalSeconds;
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

                    // F1 PR1 — AppId identity across batches (audit Q3). First-seen appId wins
                    // (mirrors the in-batch fold rule); a later batch carrying a DIFFERENT appId
                    // under the same name proves the name-keyed row merges two apps → collision.
                    var existingAppId = existing.GetString("AppId");
                    if (!string.IsNullOrEmpty(existingAppId))
                    {
                        if (string.IsNullOrEmpty(summary.AppId))
                        {
                            summary.AppId = existingAppId!;
                        }
                        else if (!string.Equals(summary.AppId, existingAppId, StringComparison.OrdinalIgnoreCase))
                        {
                            summary.AppIdCollision = true;
                            summary.AppId = existingAppId!;
                        }
                    }
                    // Collision is one-way sticky: once any batch proved the identity mix, no
                    // later single-identity batch can unprove it.
                    if (!summary.AppIdCollision && (existing.GetBoolean("AppIdCollision") ?? false))
                        summary.AppIdCollision = true;

                    // EspBlocking is written only by the session-terminal resolution (positive
                    // evidence). A late app-event batch carries null → adopt the stored verdict
                    // so the in-memory summary matches what Merge-mode preserves on disk.
                    if (!summary.EspBlocking.HasValue)
                    {
                        var existingEspBlocking = existing.GetBoolean("EspBlocking");
                        if (existingEspBlocking.HasValue)
                            summary.EspBlocking = existingEspBlocking;
                    }
        }

        /// <summary>
        /// Resolves <see cref="AppInstallSummary.EspBlocking"/> for every app row of a session by
        /// joining the rows' <c>AppId</c> against the session's LATEST <c>esp_config_detected</c>
        /// tracking lists (F1 PR1; source-data audit Q2). Positive evidence only: listed rows are
        /// merge-stamped <c>EspBlocking=true</c>; absent rows keep null (unknown) — never false.
        /// Runs once per session at the terminal transition (TableSessionRepository seam — the
        /// same funnel as the counter reconcile, covering ingest, admin marks, maintenance sweep
        /// and rule-engine fails alike). Idempotent and fail-soft: any error is logged and
        /// swallowed; app rows created by late batches after this ran simply stay unknown.
        /// </summary>
        /// <returns>Number of rows stamped (0 on no lists / nothing to stamp / error).</returns>
        public async Task<int> ResolveEspBlockingForSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
                SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

                // Latest emission that actually carries lists wins: the registry lists grow
                // progressively, so later reads are supersets — but a later emission whose probe
                // found no Diagnostics key (no espTracked* keys at all) must not erase earlier
                // positive evidence.
                var espEvents = await GetSessionEventsByTypeAsync(
                    tenantId, sessionId, Constants.EventTypes.EspConfigDetected, maxResults: 50);
                EspBlockingSets? sets = null;
                for (var i = espEvents.Count - 1; i >= 0 && sets == null; i--)
                    sets = EspBlockingSets.FromEventData(espEvents[i].Data);
                if (sets == null || sets.ListedCount == 0)
                    return 0;

                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var filter = $"PartitionKey eq '{tenantId}' and RowKey ge '{sessionId}_' and RowKey lt '{sessionId}`'";
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: filter,
                    select: new[] { "PartitionKey", "RowKey", "AppId", "EspBlocking", "AppIdCollision" });

                var stamped = 0;
                await foreach (var row in query)
                {
                    if (!ShouldStampEspBlocking(row, sets)) continue;

                    var update = new TableEntity(row.PartitionKey, row.RowKey)
                    {
                        ["EspBlocking"] = true
                    };
                    await tableClient.UpsertEntityAsync(update, TableUpdateMode.Merge);
                    stamped++;
                }

                if (stamped > 0)
                    _logger.LogInformation(
                        "Session {SessionId}: stamped EspBlocking=true on {Count} app rows ({Listed} ids in blocking set)",
                        sessionId, stamped, sets.ListedCount);
                return stamped;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EspBlocking resolution failed for session {SessionId} (fail-soft)", sessionId);
                return 0;
            }
        }

        /// <summary>
        /// Pure per-row stamping predicate behind <see cref="ResolveEspBlockingForSessionAsync"/> —
        /// internal static so the join contract is pinned by unit tests: rows without an AppId and
        /// collision rows (identity ambiguous) stay unknown; only positive membership stamps; an
        /// already-stamped row is skipped (idempotency).
        /// </summary>
        internal static bool ShouldStampEspBlocking(TableEntity row, EspBlockingSets sets)
        {
            var appId = row.GetString("AppId");
            if (string.IsNullOrEmpty(appId)) return false;
            if (row.GetBoolean("AppIdCollision") ?? false) return false;
            if (row.GetBoolean("EspBlocking") == true) return false;
            return sets.Contains(appId);
        }

        /// <summary>
        /// Gets app install summaries for a tenant (fleet-level metrics). When <paramref name="sinceUtc"/>
        /// is supplied, a server-side <c>StartedAt ge</c> filter is applied so a windowed view (e.g. the
        /// app dashboard's days=30) does not dematerialize the tenant's entire StartedAt history. The
        /// filter still scans the partition (no secondary index on StartedAt), but only the in-window rows
        /// are deserialized and returned over the wire. <paramref name="sinceUtc"/> is a server-derived
        /// DateTime (never caller-supplied text), so interpolating it into the OData filter is injection-safe.
        /// </summary>
        public Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId, DateTime? sinceUtc = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            return QueryAppInstallSummariesAsync(tenantId, sinceUtc, select: null);
        }

        /// <summary>
        /// Shared core for every AppInstallSummaries scan: optional tenant partition, optional
        /// server-side <c>StartedAt ge</c> window, optional column projection. All public variants
        /// (full, geo, app-metrics, apps-dashboard) delegate here so filter semantics stay in
        /// lockstep. sinceUtc is server-derived, so interpolating it is injection-safe.
        /// </summary>
        private async Task<List<AppInstallSummary>> QueryAppInstallSummariesAsync(string? tenantId, DateTime? sinceUtc, string[]? select)
        {
            if (!string.IsNullOrEmpty(tenantId))
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                // Cross-tenant + windowed + projected = the aggregate endpoints' shape → shared snapshot.
                if (string.IsNullOrEmpty(tenantId) && sinceUtc.HasValue && select != null)
                    return await GetCrossTenantAppSummarySnapshotAsync(sinceUtc.Value);

                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var filters = new List<string>();
                if (!string.IsNullOrEmpty(tenantId))
                    filters.Add($"PartitionKey eq '{tenantId}'");
                if (sinceUtc.HasValue)
                    filters.Add(AppSummaryWindowFilter(sinceUtc.Value));
                var filter = filters.Count > 0 ? string.Join(" and ", filters) : null;

                return await ScanAppInstallSummariesAsync(tableClient, filter, select, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query app install summaries (tenant={TenantId})", tenantId ?? "(all)");
                return new List<AppInstallSummary>();
            }
        }

        private static string AppSummaryWindowFilter(DateTime sinceUtc)
            => $"StartedAt ge datetime'{sinceUtc:yyyy-MM-ddTHH:mm:ss}Z'";

        private async Task<List<AppInstallSummary>> ScanAppInstallSummariesAsync(
            TableClient tableClient, string? filter, string[]? select, CancellationToken cancellationToken)
        {
            var summaries = new List<AppInstallSummary>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter, select: select, cancellationToken: cancellationToken))
            {
                summaries.Add(MapToAppInstallSummary(entity));
            }
            return summaries;
        }

        // ===== CROSS-TENANT APP SUMMARY SNAPSHOT =====
        //
        // AppInstallSummaries is keyed (tenantId, "{sessionId}_{appName}") — the RowKey carries no
        // time, so a window can only ever be a StartedAt PROPERTY filter. Cross-tenant that used to
        // be ONE whole-table scan: 7-8 s p50 for every global App/Geo endpoint in production (perf
        // audit 2026-09-02). Now it is one partition query per tenant (PK eq + StartedAt ge), fanned
        // out with bounded concurrency — partition-server-parallel, finishing in the time of the
        // largest tenant — over the UNION of every consumer's projection, cached per instance with
        // single-flight for AppSummarySnapshotTtl: the Installs tab fires two of these endpoints at
        // once and the app-detail page one per filter change, and they now share one scan.
        //
        // The window start is floored to the minute and used AS the filter, so every caller inside
        // the same minute bucket reads a superset and applies its own exact in-memory cutoff (they
        // all do — the OData filter is second-granular anyway). Single slot: a snapshot is hundreds
        // of thousands of rows for a 30-day window, so at most one window's rows stay resident.
        // Callers must not mutate the shared objects; each call receives its own List over them.
        internal static readonly TimeSpan AppSummarySnapshotTtl = TimeSpan.FromSeconds(60);
        private readonly SingleFlightCache<List<AppInstallSummary>> _appSummarySnapshotCache = new(maxEntries: 1);

        private async Task<List<AppInstallSummary>> GetCrossTenantAppSummarySnapshotAsync(DateTime sinceUtc)
        {
            var bucketStart = new DateTime(sinceUtc.Ticks - sinceUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var snapshot = await _appSummarySnapshotCache.GetOrAddAsync(
                bucketStart.ToString("yyyyMMddHHmm"), AppSummarySnapshotTtl, ct => ScanCrossTenantAppSummariesAsync(bucketStart, ct));
            return new List<AppInstallSummary>(snapshot);
        }

        private async Task<List<AppInstallSummary>> ScanCrossTenantAppSummariesAsync(DateTime sinceUtc, CancellationToken cancellationToken)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
            var windowFilter = AppSummaryWindowFilter(sinceUtc);
            var select = CrossTenantAppScanProjection.Value;

            var tenantIds = await GetTenantIdsCachedAsync();
            if (tenantIds.Count == 0)
            {
                // Empty config table (fresh install): the legacy cross-partition scan is the safety net.
                return await ScanAppInstallSummariesAsync(tableClient, windowFilter, select, cancellationToken);
            }

            return await BoundedFanOut.RunAsync(tenantIds, BoundedFanOut.CrossTenantConcurrency,
                (tenantId, ct) => ScanAppInstallSummariesAsync(tableClient, $"PartitionKey eq '{tenantId}' and {windowFilter}", select, ct),
                cancellationToken);
        }

        /// <summary>
        /// Gets all app install summaries across all tenants (for global admin mode). When
        /// <paramref name="sinceUtc"/> is supplied, a server-side <c>StartedAt ge</c> filter scopes the
        /// (otherwise full-table) scan to the window so only in-window rows are deserialized.
        /// <paramref name="sinceUtc"/> is server-derived, so interpolating it is injection-safe.
        /// </summary>
        public Task<List<AppInstallSummary>> GetAllAppInstallSummariesAsync(DateTime? sinceUtc = null)
            => QueryAppInstallSummariesAsync(tenantId: null, sinceUtc, select: null);

        /// <summary>
        /// Columns MetricsMath.BuildAppMetricsPayload consumes (slowest/failing ranking + DO rollup):
        /// grouping key, status + terminal state (PR0 skip classification — dropping TerminalState
        /// silently zeroed totalSkipped in production, found 2026-07-27), duration/bytes, failure
        /// code and every counter DoAggregator sums.
        /// internal so AppsProjectionEquivalenceTests derives its keep-set from this array.
        /// </summary>
        internal static readonly string[] AppMetricsProjection =
        {
            "PartitionKey", "RowKey", "AppName", "Status", "TerminalState", "StartedAt",
            "DurationSeconds", "DownloadBytes", "FailureCode",
            "DoDownloadMode", "DoTotalBytesDownloaded", "DoBytesFromPeers", "DoBytesFromHttp",
            "DoBytesFromLanPeers", "DoBytesFromGroupPeers", "DoBytesFromInternetPeers",
            "DoBytesFromLinkLocalPeers", "DoBytesFromCacheServer", "AppIdCollision"
        };

        /// <summary>
        /// Column-projected windowed scan for the app-metrics endpoints (metrics/app +
        /// global/metrics/app). Returned objects carry ONLY the <see cref="AppMetricsProjection"/>
        /// fields — everything else is defaults and must not be read.
        /// </summary>
        public Task<List<AppInstallSummary>> GetAppMetricsSummariesAsync(DateTime sinceUtc, string? tenantId = null)
            => QueryAppInstallSummariesAsync(tenantId, sinceUtc, AppMetricsProjection);

        /// <summary>
        /// Columns the App Dashboard endpoints (AppsAnalyticsHelper: list / analytics / sessions)
        /// consume — union of all three Build* methods. Drops the Delivery Optimization telemetry
        /// block and DownloadDurationSeconds, which the dashboard never reads. internal so
        /// AppsProjectionEquivalenceTests derives its keep-set from this array.
        /// </summary>
        internal static readonly string[] AppsDashboardProjection =
        {
            "PartitionKey", "RowKey", "TenantId", "SessionId", "AppName", "AppType", "AppVersion",
            "Status", "TerminalState", "StartedAt", "CompletedAt", "DurationSeconds", "DownloadBytes",
            "AttemptNumber", "InstallerPhase", "FailureCode", "FailureMessage", "ExitCode", "DetectionResult",
            "AppId", "EspBlocking", "AppIdCollision",
            // Attempt-duration columns (2026-08): the radar's cutover gate reads the anchor,
            // the dashboard sessions table shows the pass count.
            "LastAttemptStartedAt", "InstallPassCount"
        };

        /// <summary>
        /// Column-projected windowed scan for the App Dashboard endpoints. Returned objects carry
        /// ONLY the <see cref="AppsDashboardProjection"/> fields — everything else is defaults and
        /// must not be read.
        /// </summary>
        public Task<List<AppInstallSummary>> GetAppsDashboardSummariesAsync(DateTime sinceUtc, string? tenantId = null)
            => QueryAppInstallSummariesAsync(tenantId, sinceUtc, AppsDashboardProjection);

        /// <summary>
        /// Columns the (SessionId, AppName) pair projection transfers. The usage-metrics compute
        /// only groups installs per session and counts distinct app names, so the full summary row
        /// (DO telemetry, failure text, timestamps, …) is dead weight on this scan.
        /// </summary>
        internal static readonly string[] AppInstallRefProjection = { "PartitionKey", "RowKey", "SessionId", "AppName" };

        /// <summary>
        /// Lean windowed (SessionId, AppName) scan over AppInstallSummaries. Same filter semantics
        /// as the summary getters (server-side <c>StartedAt ge</c> + optional tenant partition), but
        /// column-projected to <see cref="AppInstallRefProjection"/>. <paramref name="sinceUtc"/> is
        /// server-derived, so interpolating it is injection-safe.
        /// </summary>
        public async Task<List<SessionAppRef>> GetAppInstallRefsAsync(DateTime sinceUtc, string? tenantId = null)
        {
            if (!string.IsNullOrEmpty(tenantId))
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                // Cross-tenant: project the shared snapshot (its union projection carries both columns)
                // instead of running a second whole-table scan for the same window.
                if (string.IsNullOrEmpty(tenantId))
                {
                    var snapshot = await GetCrossTenantAppSummarySnapshotAsync(sinceUtc);
                    return snapshot.ConvertAll(s => new SessionAppRef { SessionId = s.SessionId, AppName = s.AppName });
                }

                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var filter = $"PartitionKey eq '{tenantId}' and {AppSummaryWindowFilter(sinceUtc)}";

                var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: AppInstallRefProjection);

                var refs = new List<SessionAppRef>();
                await foreach (var entity in query)
                {
                    refs.Add(new SessionAppRef
                    {
                        SessionId = entity.GetString("SessionId") ?? string.Empty,
                        AppName = entity.GetString("AppName") ?? string.Empty
                    });
                }

                return refs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get app install refs");
                return new List<SessionAppRef>();
            }
        }

        /// <summary>
        /// Columns the geographic aggregations consume: the session join key, the window filter
        /// column, download throughput inputs and every field <see cref="DoAggregator"/> sums.
        /// internal so GeoMetricsProjectionEquivalenceTests derives its keep-set from this array.
        /// </summary>
        internal static readonly string[] GeoAppInstallProjection =
        {
            "PartitionKey", "RowKey", "SessionId", "StartedAt",
            "DownloadBytes", "DownloadDurationSeconds",
            "DoDownloadMode", "DoTotalBytesDownloaded", "DoBytesFromPeers", "DoBytesFromHttp",
            "DoBytesFromLanPeers", "DoBytesFromGroupPeers", "DoBytesFromInternetPeers",
            "DoBytesFromLinkLocalPeers", "DoBytesFromCacheServer"
        };

        /// <summary>
        /// Union of every projected consumer's column set (app-metrics, apps dashboard, geo, refs) —
        /// the projection of the shared cross-tenant snapshot (see CROSS-TENANT APP SUMMARY
        /// SNAPSHOT). Declared after its inputs (static initializers run in textual order) and Lazy
        /// so a future reordering can never observe a half-initialized array.
        /// </summary>
        internal static readonly Lazy<string[]> CrossTenantAppScanProjection = new(() =>
            AppMetricsProjection
                .Concat(AppsDashboardProjection)
                .Concat(GeoAppInstallProjection)
                .Concat(AppInstallRefProjection)
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        /// <summary>
        /// Column-projected windowed AppInstallSummaries scan for the geographic endpoints. Returns
        /// <see cref="AppInstallSummary"/> objects with ONLY the <see cref="GeoAppInstallProjection"/>
        /// fields populated (everything else is defaults) — callers must not read fields outside the
        /// projection. Same filter semantics as the full getters (server-side <c>StartedAt ge</c> +
        /// optional tenant partition). <paramref name="sinceUtc"/> is server-derived, so
        /// interpolating it is injection-safe.
        /// </summary>
        public Task<List<AppInstallSummary>> GetGeoAppInstallSummariesAsync(DateTime sinceUtc, string? tenantId = null)
            => QueryAppInstallSummariesAsync(tenantId, sinceUtc, GeoAppInstallProjection);

        // internal (not private) so GeoMetricsProjectionEquivalenceTests can pin that a row
        // carrying only GeoAppInstallProjection maps to the same geo-relevant fields as a full row.
        internal AppInstallSummary MapToAppInstallSummary(TableEntity entity)
        {
            return new AppInstallSummary
            {
                AppName = entity.GetString("AppName") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                Status = entity.GetString("Status") ?? "InProgress",
                TerminalState = entity.GetString("TerminalState") ?? string.Empty,
                DurationSeconds = entity.GetInt32("DurationSeconds") ?? 0,
                DownloadBytes = entity.GetInt64("DownloadBytes") ?? 0,
                DownloadDurationSeconds = entity.GetInt32("DownloadDurationSeconds") ?? 0,
                FailureCode = entity.GetString("FailureCode") ?? string.Empty,
                FailureMessage = entity.GetString("FailureMessage") ?? string.Empty,
                StartedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MinValue,
                CompletedAt = entity.GetDateTimeOffset("CompletedAt")?.UtcDateTime,
                LastAttemptStartedAt = entity.GetDateTimeOffset("LastAttemptStartedAt")?.UtcDateTime,
                InstallPassCount = entity.GetInt32("InstallPassCount") ?? 0,
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
                DetectionResult = entity.GetString("DetectionResult") ?? string.Empty,
                // F1 PR1 identity/blocking columns (absent on legacy rows → sentinels)
                AppId = entity.GetString("AppId") ?? string.Empty,
                EspBlocking = entity.GetBoolean("EspBlocking"),
                AppIdCollision = entity.GetBoolean("AppIdCollision") ?? false
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
        /// Increments one platform counter with ETag CAS and a field-only merge. Only
        /// <c>IssuesDetected</c> still goes through here (D-198): the enrollment and event
        /// counters are recomputed every two hours from live data and no longer incremented on
        /// the hot path — one global row cannot absorb an increment per ingest batch.
        /// </summary>
        public async Task IncrementPlatformStatAsync(string field, long amount = 1)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);
                await TableCasRetry.MutateAsync(
                    tableClient, "global", "current",
                    patch: read => new TableEntity("global", "current")
                    {
                        [field] = (read.GetInt64(field) ?? 0) + amount,
                        ["LastUpdated"] = DateTime.UtcNow,
                    },
                    createMissing: () => new TableEntity("global", "current")
                    {
                        ["TotalEnrollments"] = 0L,
                        ["TotalUsers"] = 0L,
                        ["TotalTenants"] = 0L,
                        ["TotalSignedUpTenants"] = 0L,
                        ["UniqueDeviceModels"] = 0L,
                        ["TotalEventsProcessed"] = 0L,
                        ["SuccessfulEnrollments"] = 0L,
                        ["IssuesDetected"] = 0L,
                        [field] = amount,
                        ["LastFullCompute"] = DateTime.MinValue,
                        ["LastUpdated"] = DateTime.UtcNow,
                    },
                    operation: "IncrementPlatformStat",
                    tableName: Constants.TableNames.PlatformStats,
                    metrics: _metrics,
                    logger: _logger).ConfigureAwait(false);
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
                await TableCasRetry.MutateAsync(
                    tableClient, tenantId, TenantStatsRowKey,
                    patch: read =>
                    {
                        var current = read.GetInt64(field) ?? 0;
                        var next = mutate(current);
                        if (next == current)
                            return null;
                        return new TableEntity(tenantId, TenantStatsRowKey)
                        {
                            [field] = next,
                            ["LastUpdated"] = DateTime.UtcNow,
                        };
                    },
                    createMissing: () => new TableEntity(tenantId, TenantStatsRowKey)
                    {
                        [field] = missingRowValue,
                        ["LastUpdated"] = DateTime.UtcNow,
                    },
                    operation: "MutateTenantStat",
                    tableName: Constants.TableNames.PlatformStats,
                    metrics: _metrics,
                    logger: _logger,
                    retries: TenantStatsCasRetries).ConfigureAwait(false);
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
                var invertedTicks = RowKeyCodec.InvertedTicksD20(now);

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
        /// Every distinct (tenant, object id) pair a UPN has signed in with — see IMetricsRepository. The Upn
        /// column is not a key, so this is a filtered table scan; acceptable because it runs once per grant.
        /// </summary>
        public async Task<List<UserSignInIdentity>> GetSignInIdentitiesByUpnAsync(string upn)
        {
            var result = new Dictionary<(string Tid, string Oid), UserSignInIdentity>();
            if (string.IsNullOrWhiteSpace(upn))
                return new List<UserSignInIdentity>();

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
            // Rows store the UPN as the token carried it; compare lowercase in memory and pre-filter on the
            // exact lowercase string (the common case) OR the raw input so a mixed-case row is not missed.
            var normalized = upn.ToLowerInvariant().Replace("'", "''");
            var filter = $"Upn eq '{normalized}'";
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "Upn", "ObjectId", "LoginAt" }))
            {
                var tid = entity.PartitionKey.ToLowerInvariant();
                var oid = (entity.GetString("ObjectId") ?? string.Empty).ToLowerInvariant();
                var loginAt = entity.GetDateTime("LoginAt") ?? DateTime.MinValue;
                if (!result.TryGetValue((tid, oid), out var entry))
                {
                    entry = new UserSignInIdentity { TenantId = tid, ObjectId = oid };
                    result[(tid, oid)] = entry;
                }
                entry.LoginCount++;
                if (loginAt > entry.LastLoginAt)
                    entry.LastLoginAt = loginAt;
            }
            return result.Values.ToList();
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
        // Layout D-199 (see RuleStatsKeys): PartitionKey "{scope}_{yyyy-MM-dd}", RowKey = ruleId.
        // Writer: one partition read + one transaction per scope and analysis, CAS as a unit.
        // Readers: PartitionKey ranges. Legacy rows (PartitionKey = date) are consulted until
        // RuleStatsKeys.LegacyLayoutUntil.

        private const int RuleStatsCasRetries = 4;

        /// <summary>
        /// Folds a session's rule evaluations into the daily counters of one scope. Increments
        /// for the same rule are merged first; the partition is read once, every touched row is
        /// written back with its ETag in one transaction (new rows as Add), and a 412/409 on the
        /// transaction re-reads and retries. Fail-soft: a caller never breaks on a stats write.
        /// </summary>
        public async Task RecordRuleStatsAsync(string date, string scope, IReadOnlyList<RuleStatIncrement> increments)
        {
            if (increments == null || increments.Count == 0) return;

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);
                var partitionKey = RuleStatsKeys.PartitionKey(scope, date);
                var deltas = RuleStatDelta.Merge(increments);

                // A partition rarely holds more than the rule catalog (~60 rows); chunks only
                // matter for huge custom catalogs. Each chunk retries on its own so a conflict in
                // a later chunk never re-applies a chunk that already committed.
                foreach (var chunk in deltas.Chunk(TableTransactionBatcher.MaxActionsPerTransaction))
                    await RecordRuleStatsChunkAsync(tableClient, partitionKey, chunk).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record rule stats for {Scope} / {Date}", scope, date);
            }
        }

        private async Task RecordRuleStatsChunkAsync(TableClient tableClient, string partitionKey, RuleStatDelta[] chunk)
        {
            var partitionFilter = $"PartitionKey eq '{ODataSanitizer.EscapeValue(partitionKey)}'";

            for (var attempt = 1; attempt <= RuleStatsCasRetries; attempt++)
            {
                var existing = new Dictionary<string, TableEntity>(StringComparer.Ordinal);
                await foreach (var row in tableClient.QueryAsync<TableEntity>(filter: partitionFilter).ConfigureAwait(false))
                    existing[row.RowKey] = row;

                var now = DateTime.UtcNow;
                var actions = new List<TableTransactionAction>(chunk.Length);
                foreach (var delta in chunk)
                {
                    var rowKey = RuleStatsKeys.RowKey(delta.RuleId);
                    if (existing.TryGetValue(rowKey, out var row))
                    {
                        ApplyRuleStatDelta(row, delta, now);
                        actions.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, row, row.ETag));
                    }
                    else
                    {
                        var fresh = new TableEntity(partitionKey, rowKey)
                        {
                            ["RuleId"] = delta.RuleId,
                            ["RuleType"] = delta.RuleType,
                            ["FireCount"] = 0,
                            ["EvaluationCount"] = 0,
                            ["SessionsEvaluated"] = 0,
                            ["ConfidenceScoreSum"] = 0L,
                            ["AvgConfidenceScore"] = 0.0,
                        };
                        ApplyRuleStatDelta(fresh, delta, now);
                        actions.Add(new TableTransactionAction(TableTransactionActionType.Add, fresh));
                    }
                }

                try
                {
                    await tableClient.SubmitTransactionAsync(actions).ConfigureAwait(false);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 412 || StorageErrors.IsAlreadyExists(ex))
                {
                    var exhausted = attempt == RuleStatsCasRetries;
                    _metrics?.CasConflict("RecordRuleStats", Constants.TableNames.RuleStats,
                        exhausted ? CasOutcome.Exhausted : CasOutcome.Retried);
                    if (exhausted)
                    {
                        _logger.LogWarning(
                            "Rule stats for {PartitionKey} lost the CAS race {Retries} times — giving up (status {Status})",
                            partitionKey, RuleStatsCasRetries, ex.Status);
                        return;
                    }

                    var delay = 50 * attempt;
                    await Task.Delay(delay + Random.Shared.Next(0, delay)).ConfigureAwait(false);
                }
            }
        }

        private static void ApplyRuleStatDelta(TableEntity entity, RuleStatDelta delta, DateTime now)
        {
            entity["EvaluationCount"] = (entity.GetInt32("EvaluationCount") ?? 0) + delta.Evaluations;
            entity["SessionsEvaluated"] = (entity.GetInt32("SessionsEvaluated") ?? 0) + delta.Evaluations;

            if (delta.Fires > 0)
            {
                var fireCount = (entity.GetInt32("FireCount") ?? 0) + delta.Fires;
                entity["FireCount"] = fireCount;
                if (delta.ConfidenceScoreSum != 0)
                {
                    var sum = (entity.GetInt64("ConfidenceScoreSum") ?? 0) + delta.ConfidenceScoreSum;
                    entity["ConfidenceScoreSum"] = sum;
                    entity["AvgConfidenceScore"] = fireCount > 0 ? (double)sum / fireCount : 0.0;
                }
            }

            // Keep metadata fresh
            entity["RuleTitle"] = delta.RuleTitle;
            entity["Category"] = delta.Category;
            entity["Severity"] = delta.Severity;
            entity["UpdatedAt"] = now;
        }

        /// <summary>
        /// Saves a fully computed rule stats entry (used by daily aggregation).
        /// </summary>
        public async Task<bool> SaveRuleStatsEntryAsync(RuleStatsEntry entry)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);
                var entity = new TableEntity(RuleStatsKeys.PartitionKey(entry.TenantId, entry.Date), RuleStatsKeys.RowKey(entry.RuleId))
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
        /// Rule stats of ONE scope (a tenant id or "global") for a date range, optionally by rule
        /// type. A key-range read on the D-199 layout plus, while legacy rows can still exist,
        /// the legacy cross-partition query. Cross-tenant reads go through
        /// <see cref="GetRuleStatsForTenantsAsync"/>.
        /// </summary>
        public async Task<List<RuleStatsEntry>> GetRuleStatsAsync(
            string tenantId, string? startDate = null, string? endDate = null,
            string? ruleType = null, int maxResults = 10000)
        {
            if (string.IsNullOrEmpty(tenantId))
                throw new ArgumentException("A tenant id or \"global\" is required; cross-tenant reads use GetRuleStatsForTenantsAsync", nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);
                var results = new List<RuleStatsEntry>();

                await CollectRuleStatsAsync(tableClient, BuildRuleStatsFilter(tenantId, startDate, endDate, ruleType), results, maxResults, tenantId).ConfigureAwait(false);

                var legacyFilter = BuildLegacyRuleStatsFilter(tenantId, startDate, endDate, ruleType, DateTime.UtcNow);
                if (legacyFilter != null && results.Count < maxResults)
                    await CollectRuleStatsAsync(tableClient, legacyFilter, results, maxResults, tenantId).ConfigureAwait(false);

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get rule stats for {Scope}", tenantId);
                return new List<RuleStatsEntry>();
            }
        }

        private async Task CollectRuleStatsAsync(TableClient tableClient, string filter, List<RuleStatsEntry> results, int maxResults, string scope)
        {
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter).ConfigureAwait(false))
            {
                results.Add(MapToRuleStatsEntry(entity));
                if (results.Count >= maxResults)
                {
                    _logger.LogWarning(
                        "Rule stats query hit the {MaxResults}-row cap for {Scope} — result truncated",
                        maxResults, scope);
                    return;
                }
            }
        }

        /// <summary>
        /// Rule stats of many tenants: one partition-range read per tenant with bounded
        /// concurrency (maintenance aggregation, regression radar). Never includes "global".
        /// </summary>
        public Task<List<RuleStatsEntry>> GetRuleStatsForTenantsAsync(
            IReadOnlyCollection<string> tenantIds, string? startDate = null, string? endDate = null,
            string? ruleType = null, int maxResultsPerTenant = 10000)
        {
            if (tenantIds == null || tenantIds.Count == 0)
                return Task.FromResult(new List<RuleStatsEntry>());

            return BoundedFanOut.RunAsync(
                tenantIds.Where(t => !string.Equals(t, RuleStatsKeys.GlobalScope, StringComparison.OrdinalIgnoreCase)),
                BoundedFanOut.CrossTenantConcurrency,
                (tenantId, _) => GetRuleStatsAsync(tenantId, startDate, endDate, ruleType, maxResultsPerTenant),
                CancellationToken.None);
        }

        /// <summary>
        /// OData filter on the D-199 layout for one scope: a PartitionKey range from
        /// <c>{scope}_{start}</c> to <c>{scope}_{end}</c> (open ends fall back to the scope
        /// prefix bounds), plus the rule type. Every value is caller-supplied and escaped via
        /// <see cref="ODataSanitizer.EscapeValue"/>; the scope is part of the key, so an
        /// injected quote cannot widen the read to another tenant.
        /// </summary>
        internal static string BuildRuleStatsFilter(string scope, string? startDate, string? endDate, string? ruleType)
        {
            var safeScope = ODataSanitizer.EscapeValue(scope);
            var lower = string.IsNullOrEmpty(startDate)
                ? $"PartitionKey ge '{safeScope}_'"
                : $"PartitionKey ge '{safeScope}_{ODataSanitizer.EscapeValue(startDate)}'";
            var upper = string.IsNullOrEmpty(endDate)
                ? $"PartitionKey lt '{safeScope}_~'"  // ~ sorts after every digit
                : $"PartitionKey le '{safeScope}_{ODataSanitizer.EscapeValue(endDate)}'";

            var filter = $"{lower} and {upper}";
            if (!string.IsNullOrEmpty(ruleType))
                filter += $" and RuleType eq '{ODataSanitizer.EscapeValue(ruleType)}'";
            return filter;
        }

        /// <summary>
        /// The pre-D-199 filter (PartitionKey = date range, RowKey prefix = scope), or null once
        /// no legacy row can exist any more. The RowKey prefix keeps D-199 rows (RowKey = ruleId)
        /// out of the legacy result even where a tenant GUID sorts inside the date range.
        /// </summary>
        internal static string? BuildLegacyRuleStatsFilter(string scope, string? startDate, string? endDate, string? ruleType, DateTime utcNow)
        {
            if (!RuleStatsKeys.LegacyLayoutActive(utcNow))
                return null;

            var filters = new List<string>();
            if (!string.IsNullOrEmpty(startDate))
                filters.Add($"PartitionKey ge '{ODataSanitizer.EscapeValue(startDate)}'");
            if (!string.IsNullOrEmpty(endDate))
                filters.Add($"PartitionKey le '{ODataSanitizer.EscapeValue(endDate)}'");

            var safeScope = ODataSanitizer.EscapeValue(scope);
            filters.Add($"RowKey ge '{safeScope}_'");
            filters.Add($"RowKey lt '{safeScope}_~'");

            if (!string.IsNullOrEmpty(ruleType))
                filters.Add($"RuleType eq '{ODataSanitizer.EscapeValue(ruleType)}'");

            return string.Join(" and ", filters);
        }

        /// <summary>PartitionKey range that selects one scope's rows older than the cutoff date.</summary>
        internal static string BuildRuleStatsCleanupFilter(string scope, string cutoffDate)
        {
            var safeScope = ODataSanitizer.EscapeValue(scope);
            return $"PartitionKey ge '{safeScope}_' and PartitionKey lt '{safeScope}_{ODataSanitizer.EscapeValue(cutoffDate)}'";
        }

        /// <summary>
        /// Retention cleanup: per scope (every tenant plus "global") a PartitionKey range delete;
        /// while legacy rows can still exist, additionally the legacy date-keyed rows — checked
        /// row by row, because a bare date range would also match tenant GUIDs that sort inside
        /// it. Per-row failures are logged and skipped so one 404 does not abort the sweep.
        /// </summary>
        public async Task<int> DeleteRuleStatsOlderThanAsync(DateTime cutoffDate, IReadOnlyCollection<string> tenantIds)
        {
            var cutoffStr = cutoffDate.ToString("yyyy-MM-dd");
            var deleted = 0;

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStats);
                var scopes = (tenantIds ?? Array.Empty<string>()).Append(RuleStatsKeys.GlobalScope).Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var scope in scopes)
                    deleted += await DeleteRuleStatsRowsAsync(tableClient, BuildRuleStatsCleanupFilter(scope, cutoffStr), requireLegacyKey: false).ConfigureAwait(false);

                if (RuleStatsKeys.LegacyLayoutActive(DateTime.UtcNow))
                    deleted += await DeleteRuleStatsRowsAsync(tableClient, $"PartitionKey ge '2000-01-01' and PartitionKey lt '{cutoffStr}'", requireLegacyKey: true).ConfigureAwait(false);

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} rule stats entries older than {Cutoff}", deleted, cutoffStr);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old rule stats entries (deleted {Count} before the failure)", deleted);
            }

            return deleted;
        }

        private async Task<int> DeleteRuleStatsRowsAsync(TableClient tableClient, string filter, bool requireLegacyKey)
        {
            var deleted = 0;
            var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });
            await foreach (var entity in query.ConfigureAwait(false))
            {
                if (requireLegacyKey && !RuleStatsKeys.IsLegacyPartitionKey(entity.PartitionKey))
                    continue;

                try
                {
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey).ConfigureAwait(false);
                    deleted++;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Already gone (concurrent sweep) — counts as done.
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogWarning(ex, "Rule stats cleanup could not delete {PartitionKey}/{RowKey} (status {Status})",
                        entity.PartitionKey, entity.RowKey, ex.Status);
                }
            }
            return deleted;
        }

        private static RuleStatsEntry MapToRuleStatsEntry(TableEntity entity)
        {
            var (scope, date, ruleId) = RuleStatsKeys.Parse(entity.PartitionKey ?? string.Empty, entity.RowKey ?? string.Empty);

            return new RuleStatsEntry
            {
                Date = date,
                TenantId = scope,
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
            if (!string.IsNullOrEmpty(summary.TerminalState))
                entity["TerminalState"] = summary.TerminalState;
            // F1 PR1 identity/blocking columns — all sentinel-gated so a batch that observed
            // nothing about them cannot clobber a prior value under Merge-mode:
            //   AppId        empty  = not observed in this batch
            //   EspBlocking  null   = unknown (only the session-terminal resolution ever sets it,
            //                        and only to true — positive evidence, never false)
            //   AppIdCollision false = never written; the flag is one-way sticky, so only true
            //                        is persisted (Merge cannot un-set an absent column).
            if (!string.IsNullOrEmpty(summary.AppId))
                entity["AppId"] = summary.AppId;
            if (summary.EspBlocking.HasValue)
                entity["EspBlocking"] = summary.EspBlocking.Value;
            if (summary.AppIdCollision)
                entity["AppIdCollision"] = true;
            if (summary.DurationSeconds > 0)
                entity["DurationSeconds"] = summary.DurationSeconds;
            if (summary.CompletedAt.HasValue)
                entity["CompletedAt"] = EnsureUtc(summary.CompletedAt.Value);
            // Attempt-duration columns (2026-08): both sentinel-gated — a batch that observed
            // no install start must not clobber the stored anchor/counter under Merge-mode.
            if (summary.LastAttemptStartedAt.HasValue)
                entity["LastAttemptStartedAt"] = EnsureUtc(summary.LastAttemptStartedAt.Value);
            if (summary.InstallPassCount > 0)
                entity["InstallPassCount"] = summary.InstallPassCount;
            if (!string.IsNullOrEmpty(summary.FailureCode))
                entity["FailureCode"] = summary.FailureCode;
            if (!string.IsNullOrEmpty(summary.FailureMessage))
                entity["FailureMessage"] = summary.FailureMessage;

            return entity;
        }
    }
}
