using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== AUDIT LOG METHODS =====

        /// <summary>
        /// RowKey prefix marking entries written under the new reverse-tick scheme.
        /// '!' (0x21) sorts before all hex digits ('0'-'9','a'-'f'), so new rows always
        /// land in front of legacy bare-GUID rows during the migration window — letting
        /// us drop in-memory re-sorts on paged reads while keeping legacy entries
        /// reachable at the tail until they age out.
        /// </summary>
        internal const string AuditLogRowKeyPrefix = "!";

        /// <summary>
        /// Builds the RowKey for a new audit entry: prefix + zero-padded reverse-tick
        /// + GUID suffix (same-tick collision protection). Exposed for unit testing
        /// of the ordering invariants.
        /// </summary>
        internal static string BuildAuditLogRowKey(DateTime timestampUtc, Guid collisionSuffix)
        {
            var revTick = DateTime.MaxValue.Ticks - timestampUtc.Ticks;
            return $"{AuditLogRowKeyPrefix}{revTick:D19}_{collisionSuffix:N}";
        }

        /// <summary>
        /// Logs an audit entry. RowKey uses a fixed prefix + reverse-tick (newest-first
        /// natural ordering) + GUID suffix (same-tick collision protection). Paged reads
        /// then need no in-memory re-sorting because Azure Tables returns rows in
        /// (PK asc, RK asc) order, which here is (tenant, newest-first).
        /// </summary>
        public async Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType, string entityId, string performedBy, Dictionary<string, string>? details = null)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);
                var timestamp = DateTime.UtcNow;
                var rowKey = BuildAuditLogRowKey(timestamp, Guid.NewGuid());

                var entity = new TableEntity(tenantId, rowKey)
                {
                    { "Action", action },
                    { "EntityType", entityType },
                    { "EntityId", entityId },
                    { "PerformedBy", performedBy },
                    { "Timestamp", timestamp },
                    { "Details", details != null ? JsonConvert.SerializeObject(details) : string.Empty }
                };

                await tableClient.AddEntityAsync(entity);
                _logger.LogInformation($"Audit log created: {action} on {entityType} {entityId} by {performedBy}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create audit log entry");
                return false;
            }
        }

        /// <summary>
        /// Retention cleanup: deletes audit log entries whose Timestamp is older than the cutoff.
        /// The AuditLogs table is append-only (one row per admin action) and is otherwise only
        /// wiped on tenant offboarding, so without this it grows unbounded. Filters server-side on
        /// the per-row "Timestamp" property and selects only PK/RK to keep the scan cheap.
        /// </summary>
        public async Task<int> DeleteAuditLogsOlderThanAsync(DateTime cutoffUtc)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);
                var filter = $"Timestamp lt datetime'{cutoffUtc:yyyy-MM-ddTHH:mm:ss}Z'";
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
                        _logger.LogWarning(ex, "Failed to delete audit log {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} audit log entries older than {Cutoff:yyyy-MM-dd}", deleted, cutoffUtc);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old audit log entries");
                return 0;
            }
        }

        /// <summary>
        /// Gets audit log entries for a tenant within an optional UTC date window.
        /// No row cap — returns the full filtered set, sorted newest-first.
        /// </summary>
        public async Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, DateTime? dateFrom = null, DateTime? dateTo = null,
            AuditLogQueryFilters? filters = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);
                var filter = BuildAuditLogFilter(tenantId, dateFrom, dateTo, excludeDeletions: false, filters);
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var logs = new List<AuditLogEntry>();
                await foreach (var entity in query)
                {
                    logs.Add(MapToAuditLogEntry(entity));
                }
                logs.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                return logs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get audit logs for tenant {tenantId}");
                return new List<AuditLogEntry>();
            }
        }

        /// <summary>
        /// Gets audit log entries across all tenants (Global Admin Mode), optional UTC window.
        /// </summary>
        public async Task<List<AuditLogEntry>> GetAllAuditLogsAsync(DateTime? dateFrom = null, DateTime? dateTo = null,
            AuditLogQueryFilters? filters = null)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);
                var filter = BuildAuditLogFilter(tenantId: null, dateFrom, dateTo, excludeDeletions: false, filters);
                var query = string.IsNullOrEmpty(filter)
                    ? tableClient.QueryAsync<TableEntity>()
                    : tableClient.QueryAsync<TableEntity>(filter: filter);

                var logs = new List<AuditLogEntry>();
                await foreach (var entity in query)
                {
                    logs.Add(MapToAuditLogEntry(entity));
                }
                logs.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                return logs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all audit logs");
                return new List<AuditLogEntry>();
            }
        }

        public Task<RawPage<AuditLogEntry>> GetAuditLogsPageAsync(
            string tenantId, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            bool excludeDeletions = false, AuditLogQueryFilters? filters = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            return FetchAuditLogPageInternalAsync(
                BuildAuditLogFilter(tenantId, dateFrom, dateTo, excludeDeletions, filters), pageSize, continuation);
        }

        public Task<RawPage<AuditLogEntry>> GetAllAuditLogsPageAsync(
            DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            bool excludeDeletions = false, AuditLogQueryFilters? filters = null)
        {
            // Cross-tenant: per-tenant fan-out + merge by RowKey. Without this
            // fan-out Azure pages by (PK asc, RK asc) cross-partition, surfacing
            // tenants alphabetically rather than newest-first globally.
            return FetchAllAuditLogsPageAsync(dateFrom, dateTo, pageSize, continuation, excludeDeletions, filters);
        }

        // Upper bound on Azure round-trips per page when back-filling past
        // excluded rows. With server-side filtering each round-trip advances
        // the scan by up to ~1000 rows, so this tolerates a window of ~tens of
        // thousands of consecutive deletion entries before a page can come back
        // short (still non-empty progress — the caller just clicks Next).
        private const int AuditLogMaxFillRoundTrips = 20;

        private async Task<RawPage<AuditLogEntry>> FetchAuditLogPageInternalAsync(
            string? filter, int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);

                // RowKey scheme is `!{revtick}_{guid}` so Azure-native (PK asc, RK asc)
                // already yields newest-first within the partition. No re-sort needed.
                // Legacy entries (bare GUID RowKey) sort *after* all new entries because
                // '!' (0x21) precedes hex digits — they appear at the tail of the result
                // in undefined order until they age out of the date window.
                //
                // Back-fill loop: a server-side exclusion (e.g. "exclude
                // deletions") can make a single Azure page return far fewer than
                // pageSize matches — during a cleanup sweep the first scanned
                // rows may all be deletion bookkeeping. We follow the opaque
                // continuation until we have at least pageSize matches or the
                // partition is exhausted, then return the last consumed page's
                // token. Accumulating whole Azure pages (rather than trimming to
                // pageSize) keeps that token gap-free, so we may overshoot by up
                // to one Azure page — harmless for the caller. Without an
                // exclusion the first page already fills, so the loop exits
                // immediately and behaviour is unchanged.
                var logs = new List<AuditLogEntry>(pageSize);
                string? nextRawToken = continuation;
                var roundTrips = 0;
                do
                {
                    var (entities, token) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                        client: tableClient,
                        filter: filter,
                        pageSize: pageSize,
                        continuation: nextRawToken);
                    foreach (var entity in entities) logs.Add(MapToAuditLogEntry(entity));
                    nextRawToken = token;
                    roundTrips++;
                }
                while (logs.Count < pageSize
                    && !string.IsNullOrEmpty(nextRawToken)
                    && roundTrips < AuditLogMaxFillRoundTrips);

                return new RawPage<AuditLogEntry>(logs, nextRawToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get audit logs page");
                return RawPage<AuditLogEntry>.Empty;
            }
        }

        private async Task<RawPage<AuditLogEntry>> FetchAllAuditLogsPageAsync(
            DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation, bool excludeDeletions,
            AuditLogQueryFilters? filters = null)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
            try
            {
                var continuations = AutopilotMonitor.Functions.DataAccess.TableStorage.PerPartitionFanOutMerge
                    .DecodeMultiContinuation(continuation);
                // First-page request (no continuation) fans out across every
                // tenant in the catalog. Subsequent pages restrict to tenants
                // still in the continuation map — anything dropped by
                // MergeAndAdvance (because that partition exhausted on a
                // prior page) stays dropped, which is what shrinks the
                // wire-format token from 30+ KB to a few hundred bytes.
                var isFirstPage = string.IsNullOrEmpty(continuation);

                // Tenants come from TenantConfiguration (1 row per tenant — cheap).
                // PLUS the synthetic global-tenant partition (Constants.AuditGlobalTenantId)
                // where platform-action audits are written from TenantOffboardFunction,
                // VersionBlockFunction, UpdateAdminConfigurationFunction, … TenantConfiguration
                // has no row for it because it's a virtual partition, so it'd be
                // silently skipped without this explicit add.
                var configTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TenantConfiguration);
                var tenantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Constants.AuditGlobalTenantId,
                };
                await foreach (var entity in configTableClient.QueryAsync<TableEntity>(
                    select: new[] { "PartitionKey" }, maxPerPage: 1000))
                {
                    tenantIds.Add(entity.PartitionKey);
                }

                var activeTenantIds = tenantIds
                    .Where(t => isFirstPage || continuations.ContainsKey(t))
                    .ToList();
                if (activeTenantIds.Count == 0)
                    return new RawPage<AuditLogEntry>(new List<AuditLogEntry>(), null);

                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);

                // Per-tenant fetch in parallel: filter `PartitionKey eq tenantId` plus
                // `RowKey gt LastRowKey` (revtick scheme → strictly older than what
                // we already returned for this tenant), plus the optional date window.
                var fetchTasks = activeTenantIds.Select(async tid =>
                {
                    continuations.TryGetValue(tid, out var prior);
                    var tenantFilter = BuildAuditLogFilterWithRowKeyBound(tid, dateFrom, dateTo, prior?.LastRowKey, excludeDeletions, filters);
                    var fetched = new List<(string RowKey, AuditLogEntry Item)>();
                    await foreach (var e in tableClient.QueryAsync<TableEntity>(filter: tenantFilter, maxPerPage: pageSize))
                    {
                        fetched.Add((e.RowKey, MapToAuditLogEntry(e)));
                        if (fetched.Count >= pageSize) break;
                    }
                    return new AutopilotMonitor.Functions.DataAccess.TableStorage.PerPartitionFanOutMerge
                        .PartitionFetchResult<AuditLogEntry>(tid, fetched);
                }).ToList();

                var results = await Task.WhenAll(fetchTasks);

                var (items, nextContinuations) = AutopilotMonitor.Functions.DataAccess.TableStorage.PerPartitionFanOutMerge
                    .MergeAndAdvance(results, continuations, pageSize, e => e.Timestamp);

                // MergeAndAdvance only puts active partitions into the map now
                // (exhausted ones are dropped, see PerPartitionFanOutMerge.cs).
                // An empty map therefore means "every partition is done" and we
                // emit no continuation, ending the pagination cleanly.
                string? nextRawToken = nextContinuations.Count > 0
                    ? AutopilotMonitor.Functions.DataAccess.TableStorage.PerPartitionFanOutMerge
                        .EncodeMultiContinuation(nextContinuations)
                    : null;
                return new RawPage<AuditLogEntry>(items, nextRawToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all audit logs page");
                return RawPage<AuditLogEntry>.Empty;
            }
        }

        // Suppress automated maintenance entries (SessionTimeout, ExcessiveDataBlock,
        // DataRetentionCleanup, …) from the human-facing audit list. They are written
        // by MaintenanceService for traceability but duplicate the OpsEvents stream
        // and are not actionable as user-attributable audits. Rows stay in storage
        // and remain reachable via raw table queries.
        private const string AuditLogSuppressedPerformer = "System.Maintenance";

        // Per-session deletion bookkeeping. A single cleanup run emits one
        // `deletion_started` + one `deletion_completed` audit row per session,
        // so a retention sweep can flood the table with thousands of these.
        // They carry the triggering operator's UPN (not System.Maintenance), so
        // the performer exclusion above does not catch them. The "exclude
        // deletions" audit view drops them server-side so they never consume a
        // page slot. Keep in sync with the actions written by
        // SessionDeletionProducer.cs.
        private const string AuditLogDeletionStartedAction = "deletion_started";
        private const string AuditLogDeletionCompletedAction = "deletion_completed";

        internal static string DeletionExclusionClause()
            => $"Action ne '{AuditLogDeletionStartedAction}' and Action ne '{AuditLogDeletionCompletedAction}'";

        // Escapes a string literal for an OData filter (single quotes are doubled).
        private static string EscapeOData(string value) => value.Replace("'", "''");

        // Appends optional exact-match field-filter clauses (action / performedBy /
        // entityType / entityId). Shared by the tenant and global filter builders so
        // both audit views filter identically — a drift would silently scope the two
        // paths differently. Each non-empty value becomes a server-side `eq` clause.
        private static void AppendAuditFieldFilters(List<string> clauses, AuditLogQueryFilters? filters)
        {
            if (filters == null) return;
            if (!string.IsNullOrEmpty(filters.Action))
                clauses.Add($"Action eq '{EscapeOData(filters.Action!)}'");
            if (!string.IsNullOrEmpty(filters.PerformedBy))
                clauses.Add($"PerformedBy eq '{EscapeOData(filters.PerformedBy!)}'");
            if (!string.IsNullOrEmpty(filters.EntityType))
                clauses.Add($"EntityType eq '{EscapeOData(filters.EntityType!)}'");
            if (!string.IsNullOrEmpty(filters.EntityId))
                clauses.Add($"EntityId eq '{EscapeOData(filters.EntityId!)}'");
        }

        internal static string BuildAuditLogFilterWithRowKeyBound(
            string tenantId, DateTime? dateFrom, DateTime? dateTo, string? lastRowKey, bool excludeDeletions,
            AuditLogQueryFilters? filters = null)
        {
            var clauses = new List<string>
            {
                $"PartitionKey eq '{tenantId}'",
                $"PerformedBy ne '{AuditLogSuppressedPerformer}'",
            };
            if (excludeDeletions)
                clauses.Add(DeletionExclusionClause());
            AppendAuditFieldFilters(clauses, filters);
            if (!string.IsNullOrEmpty(lastRowKey))
                clauses.Add($"RowKey gt '{lastRowKey!.Replace("'", "''")}'");
            if (dateFrom.HasValue)
                clauses.Add($"Timestamp ge datetime'{ToUtc(dateFrom.Value):o}'");
            if (dateTo.HasValue)
                clauses.Add($"Timestamp le datetime'{ToUtc(dateTo.Value):o}'");
            return string.Join(" and ", clauses);
        }

        private static AuditLogEntry MapToAuditLogEntry(TableEntity entity) => new AuditLogEntry
        {
            Id = entity.RowKey,
            TenantId = entity.PartitionKey,
            Action = entity.GetString("Action") ?? string.Empty,
            EntityType = entity.GetString("EntityType") ?? string.Empty,
            EntityId = entity.GetString("EntityId") ?? string.Empty,
            PerformedBy = entity.GetString("PerformedBy") ?? string.Empty,
            Timestamp = entity.GetDateTimeOffset("Timestamp")?.UtcDateTime ?? DateTime.UtcNow,
            Details = entity.GetString("Details") ?? string.Empty,
        };

        // The system Timestamp is auto-managed by Azure on insert and reliably
        // sortable; the user-defined "Timestamp" property is set by LogAuditEntry
        // for parity with the model. We filter on the system Timestamp since it
        // is always indexed.
        internal static string? BuildAuditLogFilter(string? tenantId, DateTime? dateFrom, DateTime? dateTo,
            bool excludeDeletions = false, AuditLogQueryFilters? filters = null)
        {
            var clauses = new List<string>
            {
                $"PerformedBy ne '{AuditLogSuppressedPerformer}'",
            };
            if (excludeDeletions)
            {
                clauses.Add(DeletionExclusionClause());
            }
            AppendAuditFieldFilters(clauses, filters);
            if (!string.IsNullOrEmpty(tenantId))
            {
                clauses.Add($"PartitionKey eq '{tenantId}'");
            }
            if (dateFrom.HasValue)
            {
                clauses.Add($"Timestamp ge datetime'{ToUtc(dateFrom.Value):o}'");
            }
            if (dateTo.HasValue)
            {
                clauses.Add($"Timestamp le datetime'{ToUtc(dateTo.Value):o}'");
            }
            return string.Join(" and ", clauses);
        }

        private static DateTime ToUtc(DateTime dt)
            => dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();

        // ===== DATA RETENTION METHODS =====

        /// <summary>
        /// Gets sessions older than a specific date for a tenant, capped at <paramref name="maxResults"/>.
        /// The retention fanout only advances a fixed number of sessions per run, so the read is
        /// server-bounded to that cap instead of materializing the whole backlog every run.
        /// <para>
        /// <paramref name="excludeInFlightDeletions"/>: skip rows whose <c>DeletionState</c> is a
        /// lock state (Preparing/Queued/Running/Poisoned) WITHOUT counting them toward the cap.
        /// Azure Tables returns rows in RowKey order, so without this a head of ≥cap permanently
        /// stuck sessions (Poisoned / stranded Queued are never auto-cleared) would fill every
        /// capped read and starve the tail forever. Cannot be a server-side filter: rows written
        /// before the deletion feature have no <c>DeletionState</c> column and a property
        /// comparison would silently drop them.
        /// </para>
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate, int maxResults = int.MaxValue, bool excludeInFlightDeletions = false)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (maxResults <= 0) return new List<SessionSummary>();

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // Query sessions for this tenant older than cutoff date.
                var filter = $"PartitionKey eq '{tenantId}' and StartedAt lt datetime'{cutoffDate:yyyy-MM-ddTHH:mm:ss}Z'";

                // Bound the server read: cap page size to maxResults and stop enumerating once we
                // have that many. Azure Tables has no server-side OrderBy, so this returns the first
                // maxResults rows in RowKey order — the same ordering as the previous full scan, it
                // just stops reading once the cap is reached. Unbounded callers pass int.MaxValue.
                // With excludeInFlightDeletions the scan continues past locked rows, so it may read
                // more pages than maxResults — bounded by the tenant's eligible backlog.
                int? maxPerPage = maxResults == int.MaxValue ? (int?)null : maxResults;
                var query = tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: maxPerPage);

                var sessions = new List<SessionSummary>();
                int skippedInFlight = 0;
                await foreach (var entity in query)
                {
                    if (excludeInFlightDeletions &&
                        AutopilotMonitor.Shared.Models.Deletion.SessionDeletionState.IsLocked(entity.GetString("DeletionState")))
                    {
                        skippedInFlight++;
                        continue;
                    }

                    sessions.Add(MapToSessionSummary(entity));
                    if (sessions.Count >= maxResults)
                        break;
                }

                if (sessions.Count == 0 && skippedInFlight > 0)
                {
                    // Warning (not Information) so it reaches App Insights: the entire eligible
                    // backlog is locked in deletion states — likely Poisoned/stranded rows that
                    // need operator attention (POST /restore), not healthy throughput.
                    _logger.LogWarning(
                        "Tenant {TenantId}: all {Skipped} sessions older than {Cutoff} are locked in deletion states — retention cannot progress until they are restored/cleared",
                        tenantId, skippedInFlight, cutoffDate.ToString("yyyy-MM-dd"));
                }
                else
                {
                    _logger.LogInformation($"Found {sessions.Count} sessions older than {cutoffDate:yyyy-MM-dd} for tenant {tenantId} (cap={maxResults}, skippedInFlight={skippedInFlight})");
                }
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get old sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets all sessions within a date range, optionally filtered by tenant.
        /// Uses server-side filtering to avoid loading all sessions into memory.
        /// </summary>
        public Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null)
            => QuerySessionsByDateRangeAsync(startDate, endDate, tenantId, select: null);

        /// <summary>
        /// Columns the usage-metrics compute (UsageMetricsService) actually consumes, plus the
        /// structural PartitionKey (TenantId) / RowKey (SessionId). CompletedAt / IsPreProvisioned /
        /// ResumedAt must stay in the set for ComputeEffectiveDuration parity (WhiteGlove Part-2
        /// branch and the completed-at fallback). Everything else on the wide Sessions row — most
        /// notably FailureSnapshotJson — is never read by the compute, so the projected scan skips
        /// it. Non-projected columns map to null/default via the Safe*/`?? ` getters in
        /// MapToSessionSummary. internal so UsageMetricsProjectionEquivalenceTests derives its
        /// keep-set from this exact array.
        /// </summary>
        internal static readonly string[] UsageMetricsSessionProjection =
        {
            "PartitionKey", "RowKey", "StartedAt", "CompletedAt", "Status", "DurationSeconds",
            "Manufacturer", "Model", "IsUserDriven", "IsPreProvisioned", "ResumedAt",
            "PlatformScriptCount", "RemediationScriptCount"
        };

        /// <summary>
        /// Column-projected date-range query for the usage-metrics compute. Identical filter and
        /// result semantics to <see cref="GetSessionsByDateRangeAsync"/>; only the transferred
        /// column set differs (see <see cref="UsageMetricsSessionProjection"/>).
        /// </summary>
        public Task<List<SessionSummary>> GetUsageWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null)
            => QuerySessionsByDateRangeAsync(startDate, endDate, tenantId, UsageMetricsSessionProjection);

        /// <summary>
        /// Columns the geographic-metrics aggregation (ComputeGeographicMetrics + location grouping)
        /// consumes, plus the structural PartitionKey (TenantId) / RowKey (SessionId). CompletedAt /
        /// IsPreProvisioned / ResumedAt stay in the set for ComputeEffectiveDuration parity (see
        /// <see cref="UsageMetricsSessionProjection"/>). internal so
        /// GeoMetricsProjectionEquivalenceTests derives its keep-set from this exact array.
        /// </summary>
        internal static readonly string[] GeoMetricsSessionProjection =
        {
            "PartitionKey", "RowKey", "StartedAt", "CompletedAt", "Status", "DurationSeconds",
            "IsPreProvisioned", "ResumedAt", "GeoCountry", "GeoRegion", "GeoCity", "GeoLoc"
        };

        /// <summary>
        /// Column-projected date-range query for the geographic-metrics aggregation (map view).
        /// The drilldown endpoints keep the full-row query — their response shape returns nearly
        /// every session column.
        /// </summary>
        public Task<List<SessionSummary>> GetGeoWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null)
            => QuerySessionsByDateRangeAsync(startDate, endDate, tenantId, GeoMetricsSessionProjection);

        private async Task<List<SessionSummary>> QuerySessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId, string[]? select)
        {
            if (!string.IsNullOrEmpty(tenantId))
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                var filter = !string.IsNullOrEmpty(tenantId)
                    ? $"PartitionKey eq '{tenantId}' and StartedAt ge datetime'{startDate:yyyy-MM-ddTHH:mm:ss}Z' and StartedAt lt datetime'{endDate:yyyy-MM-ddTHH:mm:ss}Z'"
                    : $"StartedAt ge datetime'{startDate:yyyy-MM-ddTHH:mm:ss}Z' and StartedAt lt datetime'{endDate:yyyy-MM-ddTHH:mm:ss}Z'";

                var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: select);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                _logger.LogInformation($"Found {sessions.Count} sessions between {startDate:yyyy-MM-dd} and {endDate:yyyy-MM-dd}" +
                    (tenantId != null ? $" for tenant {tenantId}" : ""));
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get sessions by date range");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets stalled sessions (InProgress status, started before cutoff time) for a tenant.
        /// Uses server-side filtering to avoid loading all sessions into memory.
        /// </summary>
        public async Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // Eligible for the 5h-timeout sweep: InProgress or Stalled sessions (Stalled is a
                // non-terminal intermediate state set earlier by either the agent or the 2h sweep).
                // At the 5h mark the session is reclassified rather than blindly failed — see
                // EnrollmentTimeoutClassifier. AwaitingUser is included too so it keeps being
                // re-evaluated each pass and graduates to Incomplete once SessionGraceHours elapses
                // (tasks/enrollment-status-reclassification.md).
                // WhiteGlove: sealed devices are protected by STATUS (Pending is not in this set),
                // not by IsPreProvisioned — a resumed Part-2 session is InProgress and must be
                // sweepable like any other run, otherwise it lingers as InProgress forever when the
                // agent dies mid-Part-2 (misclassification audit 2026-07-16). The caller anchors the
                // timeout window to ResumedAt for these rows so a freshly resumed Part 2 with a
                // weeks-old StartedAt is not terminalized while it is still live.
                var filter = $"PartitionKey eq '{tenantId}' " +
                             $"and (Status eq 'InProgress' or Status eq 'Stalled' or Status eq 'AwaitingUser') " +
                             $"and StartedAt lt datetime'{cutoffTime:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get stalled sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets sessions where the agent has gone completely silent for longer than the configured
        /// agent-silence window (default 2h). These are candidates for the intermediate Stalled status.
        /// Used by the 2h maintenance sweep as a backstop for agents that cannot send session_stalled
        /// themselves (bluescreen, network loss, power off).
        ///
        /// Filter criteria:
        /// - Status eq 'InProgress' — do not re-mark already-Stalled sessions; sealed WhiteGlove
        ///   sessions are Pending and therefore never match (the former extra
        ///   "IsPreProvisioned ne true" clause also excluded resumed Part-2 runs, which then could
        ///   never be marked Stalled — misclassification audit 2026-07-16)
        /// - LastEventAt &lt; silenceCutoff — at least the configured window of agent silence
        /// - StartedAt ge hardCutoff OR ResumedAt ge hardCutoff — do not catch sessions that will
        ///   be picked up by the 5h timeout sweep. The ResumedAt alternative keeps a WhiteGlove
        ///   Part-2 run (weeks-old StartedAt, fresh ResumedAt) in THIS stage instead of stage 2;
        ///   rows without ResumedAt fail that comparison server-side and fall back to StartedAt.
        /// </summary>
        public async Task<List<SessionSummary>> GetAgentSilentSessionsAsync(string tenantId, DateTime silenceCutoff, DateTime hardCutoff)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                var silenceCutoffStr = silenceCutoff.ToString("yyyy-MM-ddTHH:mm:ss");
                var hardCutoffStr = hardCutoff.ToString("yyyy-MM-ddTHH:mm:ss");
                var filter = $"PartitionKey eq '{tenantId}' " +
                             $"and Status eq 'InProgress' " +
                             $"and LastEventAt lt datetime'{silenceCutoffStr}Z' " +
                             $"and (StartedAt ge datetime'{hardCutoffStr}Z' or ResumedAt ge datetime'{hardCutoffStr}Z')";

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get agent-silent sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Failed sessions carrying the pre-classifier blanket "Session timed out after ..."
        /// FailureReason — candidates for the one-time admin retro-reconcile
        /// (misclassification audit 2026-07-16). The prefix is matched server-side via an
        /// OData string range (ge prefix, lt prefix-with-last-char-incremented).
        /// </summary>
        public async Task<List<SessionSummary>> GetLegacyTimeoutFailedSessionsAsync(string tenantId, int maxResults)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                // Prefix range for "Session timed out after": upper bound increments the final
                // 'r' to 's'. Matches exactly the legacy sweep's reason format
                // "Session timed out after {N} hours (started at ... UTC)".
                var filter = $"PartitionKey eq '{tenantId}' " +
                             $"and Status eq 'Failed' " +
                             $"and FailureReason ge 'Session timed out after' " +
                             $"and FailureReason lt 'Session timed out aftes'";

                var sessions = new List<SessionSummary>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter))
                {
                    sessions.Add(MapToSessionSummary(entity));
                    if (sessions.Count >= maxResults)
                        break;
                }
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get legacy timeout-failed sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Narrow projection of every session row in the tenant partition — one scan feeding the
        /// in-memory Pending-orphan matching (misclassification audit 2026-07-16). Fields outside
        /// the projection come back as defaults; callers must not read them.
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsLeanAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var select = new[]
                {
                    "PartitionKey", "RowKey", "Status", "StartedAt", "ResumedAt", "LastEventAt",
                    "SerialNumber", "IsPreProvisioned"
                };

                var sessions = new List<SessionSummary>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{tenantId}'", select: select))
                {
                    sessions.Add(MapToSessionSummary(entity));
                }
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get lean session projection for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets sessions where the device has been actively sending data for longer than
        /// <paramref name="maxSessionWindowHours"/>.
        /// Status-independent: detects excessive data senders regardless of session status.
        /// Uses LastEventAt (written on every event batch) for the "still active" check.
        /// Sessions without LastEventAt (predating this field) are not returned.
        ///
        /// The OData pre-filter narrows candidates to sessions that straddle the cutoff boundary,
        /// then a post-filter verifies the actual session duration (LastEventAt − StartedAt)
        /// exceeds the allowed window. This prevents false positives from short sessions that
        /// merely happen to straddle the cutoff time.
        /// </summary>
        public async Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff, int maxSessionWindowHours)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // OData pre-filter: narrow to sessions that straddle the cutoff boundary.
                // Status eq 'InProgress' → only block devices whose session is still actively
                //   sending data. Completed sessions (Succeeded/Failed) must NOT be re-blocked
                //   even if their LastEventAt is still within the window — they cannot continue
                //   to abuse data transfer. This also defends against ghost-blocks caused by
                //   devices with bad clocks: an agent that submits events with timestamps from
                //   weeks in the past pushes StartedAt back, making the session look long-lived;
                //   but once the session is Succeeded/Failed, no further data flows from it.
                // IsPreProvisioned ne true → exclude WhiteGlove sessions: a pre-provisioned device
                //   that resumes after weeks in storage looks like an excessive sender (StartedAt old,
                //   LastEventAt recent) but is a legitimate resumption, not abuse.
                var cutoffStr = windowCutoff.ToString("yyyy-MM-ddTHH:mm:ss");
                var filter = $"PartitionKey eq '{tenantId}' " +
                             $"and Status eq 'InProgress' " +
                             $"and LastEventAt gt datetime'{cutoffStr}Z' " +
                             $"and StartedAt lt datetime'{cutoffStr}Z' " +
                             $"and IsPreProvisioned ne true";

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);
                var maxDuration = TimeSpan.FromHours(maxSessionWindowHours);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    // Post-filter: verify actual session duration exceeds the window.
                    // OData cannot compute date differences, so we check in code.
                    var startedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    var lastEventAt = entity.GetDateTimeOffset("LastEventAt")?.UtcDateTime;

                    if (startedAt.HasValue && lastEventAt.HasValue
                        && (lastEventAt.Value - startedAt.Value) < maxDuration)
                    {
                        continue;
                    }

                    sessions.Add(MapToSessionSummary(entity));
                }

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get excessive data sender sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets all known tenant IDs from the TenantConfiguration table (one "config" row per
        /// tenant). This replaces an O(corpus) scan of the entire Sessions table — the maintenance
        /// pass only needs the tenant set, not every session, and the tenant set grows with the
        /// number of onboarded tenants, not the session history.
        ///
        /// Trade-off: a tenant whose config row was deleted (full offboarding) but whose old
        /// sessions still linger will no longer be visited by maintenance sweeps. That is
        /// acceptable — the offboarding cascade removes those sessions anyway, and a merely
        /// disabled tenant keeps its config row and is still processed.
        /// </summary>
        public async Task<List<string>> GetAllTenantIdsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TenantConfiguration);
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: "RowKey eq 'config'",
                    select: new[] { "PartitionKey" });

                var tenantIds = new HashSet<string>();
                await foreach (var entity in query)
                {
                    tenantIds.Add(entity.PartitionKey);
                }

                _logger.LogInformation($"Found {tenantIds.Count} unique tenants");
                return tenantIds.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tenant IDs");
                return new List<string>();
            }
        }

        // ===== DELETION HELPERS =====

        /// <summary>
        /// Deletes all events for a session from storage
        /// </summary>
        public async Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
            var partitionKey = $"{tenantId}_{sessionId}";
            var filter = $"PartitionKey eq '{partitionKey}'";
            var deleted = await DeleteByFilterInBatchesAsync(tableClient, filter, $"events for session {sessionId}");
            if (deleted > 0)
                _logger.LogInformation($"Deleted {deleted} events for session {sessionId}");
            return deleted;
        }

        /// <summary>
        /// Deletes all rule results for a session
        /// </summary>
        public async Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleResults);
            var partitionKey = $"{tenantId}_{sessionId}";
            var filter = $"PartitionKey eq '{partitionKey}'";
            return await DeleteByFilterInBatchesAsync(tableClient, filter, $"rule results for session {sessionId}");
        }

        /// <summary>
        /// Deletes all entities matching the filter from the given table.
        /// Uses projected query (PK/RK only) to minimize payload, and submits 100-entity
        /// batch transactions in parallel (up to 4 in flight) for faster bulk delete.
        /// REQUIRES all matched rows to share the same PartitionKey (Table Storage batch constraint).
        /// Fail-loud: throws on query/submit errors (already-submitted batches stay deleted —
        /// deletes are idempotent, callers retry). Returns the number of CONFIRMED deletes.
        /// </summary>
        private async Task<int> DeleteByFilterInBatchesAsync(TableClient tableClient, string filter, string contextForLogs)
        {
            const int maxParallelBatches = 4;
            const int batchSize = 100;

            try
            {
                // Project to PK/RK only — drastically reduces query bytes for large sessions.
                var query = tableClient.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                // Counts CONFIRMED deletes only (incremented after a successful batch submit),
                // so the return value never over-reports on partial failure.
                int deletedCount = 0;
                var batch = new List<TableTransactionAction>(batchSize);
                var gate = new SemaphoreSlim(maxParallelBatches);
                var inFlight = new List<Task>();

                async Task SubmitAsync(List<TableTransactionAction> snapshot)
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await tableClient.SubmitTransactionAsync(snapshot).ConfigureAwait(false);
                        Interlocked.Add(ref deletedCount, snapshot.Count);
                    }
                    finally { gate.Release(); }
                }

                await foreach (var entity in query)
                {
                    // ETag.All → unconditional delete (safe for maintenance cleanup; no optimistic concurrency needed).
                    var stub = new TableEntity(entity.PartitionKey, entity.RowKey) { ETag = ETag.All };
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, stub));
                    if (batch.Count >= batchSize)
                    {
                        var snapshot = batch;
                        batch = new List<TableTransactionAction>(batchSize);
                        inFlight.Add(SubmitAsync(snapshot));
                    }
                }
                if (batch.Count > 0)
                {
                    inFlight.Add(SubmitAsync(batch));
                }

                if (inFlight.Count > 0)
                    await Task.WhenAll(inFlight).ConfigureAwait(false);

                return deletedCount;
            }
            catch (Exception ex)
            {
                // Fail-loud: callers must be able to distinguish "0 rows matched" from "delete
                // failed". Orphan cleanup keeps the EventSessionIndex entry (retries next run)
                // and the reanalyze path surfaces the error instead of merging stale results.
                _logger.LogError(ex, $"Failed to delete {contextForLogs}");
                throw;
            }
        }

        // ===== SESSION INDEX BACKFILL =====

        /// <summary>
        /// Backfills the SessionsIndex table from the Sessions table.
        /// Finds sessions that don't have an IndexRowKey property and creates the corresponding
        /// index entry. Idempotent — safe to run repeatedly.
        /// Returns the number of sessions backfilled.
        /// </summary>
        public async Task<int> BackfillSessionIndexAsync()
        {
            try
            {
                var sessionsTable = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var query = sessionsTable.QueryAsync<TableEntity>();

                int backfilledCount = 0;

                await foreach (var entity in query)
                {
                    var existingIndexRowKey = entity.GetString("IndexRowKey");
                    if (!string.IsNullOrEmpty(existingIndexRowKey))
                        continue; // Already indexed

                    var startedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.UtcNow;

                    try
                    {
                        await UpsertSessionIndexAsync(entity, startedAt);
                        backfilledCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to backfill session index for {TenantId}/{SessionId}",
                            entity.PartitionKey, entity.RowKey);
                    }
                }

                if (backfilledCount > 0)
                {
                    _logger.LogInformation("Session index backfill completed: {Count} sessions indexed", backfilledCount);
                }

                return backfilledCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session index backfill failed");
                return 0;
            }
        }

        /// <summary>
        /// One-time cleanup: removes ghost entries from SessionsIndex where a SessionId has
        /// multiple index rows (caused by a bug where StoreSessionAsync's Replace mode deleted
        /// IndexRowKey, preventing old index entries from being cleaned up when StartedAt shifted).
        /// Fixed in the same release — this cleanup can be removed once all environments have run it.
        /// TODO: Remove after 2026-06-01 (3 months grace period).
        /// </summary>
        public async Task<int> CleanupGhostSessionIndexEntriesAsync()
        {
            try
            {
                var indexTable = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                var sessionsTable = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // Build a map of SessionId → list of index RowKeys (across all tenants)
                var sessionIndexEntries = new Dictionary<string, List<(string PartitionKey, string RowKey, int EventCount)>>();

                var query = indexTable.QueryAsync<TableEntity>(
                    select: new[] { "PartitionKey", "RowKey", "SessionId", "EventCount" });

                await foreach (var entity in query)
                {
                    var sessionId = entity.GetString("SessionId") ?? ExtractSessionIdFromIndexRowKey(entity.RowKey);
                    var key = $"{entity.PartitionKey}_{sessionId}";
                    var eventCount = entity.GetInt32("EventCount") ?? 0;

                    if (!sessionIndexEntries.ContainsKey(key))
                        sessionIndexEntries[key] = new List<(string, string, int)>();

                    sessionIndexEntries[key].Add((entity.PartitionKey, entity.RowKey, eventCount));
                }

                int deletedCount = 0;

                foreach (var (key, entries) in sessionIndexEntries)
                {
                    if (entries.Count <= 1)
                        continue; // No duplicates

                    // Keep the entry with the highest EventCount (most up-to-date);
                    // delete all others as ghosts.
                    var sorted = entries.OrderByDescending(e => e.EventCount).ToList();
                    var keep = sorted[0];

                    for (int i = 1; i < sorted.Count; i++)
                    {
                        var ghost = sorted[i];
                        try
                        {
                            await indexTable.DeleteEntityAsync(ghost.PartitionKey, ghost.RowKey);
                            deletedCount++;
                            _logger.LogInformation(
                                "Deleted ghost SessionsIndex entry: {PartitionKey}/{RowKey} (EventCount={GhostCount}, kept entry has EventCount={KeptCount})",
                                ghost.PartitionKey, ghost.RowKey, ghost.EventCount, keep.EventCount);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete ghost index entry {PartitionKey}/{RowKey}",
                                ghost.PartitionKey, ghost.RowKey);
                        }
                    }
                }

                if (deletedCount > 0)
                {
                    _logger.LogInformation("Ghost SessionsIndex cleanup completed: {Count} ghost entries removed", deletedCount);
                }

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ghost SessionsIndex cleanup failed");
                return 0;
            }
        }

        /// <summary>
        /// Checks if the SessionsIndex table is empty.
        /// Used by startup backfill to determine if a full migration is needed.
        /// </summary>
        public async Task<bool> IsSessionIndexEmptyAsync()
        {
            try
            {
                var indexTable = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                var query = indexTable.QueryAsync<TableEntity>(maxPerPage: 1, select: new[] { "PartitionKey" });

                await foreach (var _ in query)
                {
                    return false; // At least one entity exists
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check if session index is empty");
                return true; // Assume empty on error → trigger backfill
            }
        }

        // ===== ORPHAN EVENT DETECTION =====

        /// <summary>
        /// Scans EventSessionIndex, checks each entry against the Sessions table,
        /// and returns entries where no session exists and LastIngestAt is older than the grace period.
        /// </summary>
        public async Task<List<OrphanedEventSession>> GetOrphanedEventSessionsAsync(TimeSpan gracePeriod)
        {
            var orphans = new List<OrphanedEventSession>();
            var cutoff = DateTime.UtcNow - gracePeriod;

            try
            {
                var indexClient = _tableServiceClient.GetTableClient(Constants.TableNames.EventSessionIndex);
                var sessionsClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                await foreach (var entity in indexClient.QueryAsync<TableEntity>())
                {
                    var tenantId = entity.PartitionKey;
                    var sessionId = entity.RowKey;
                    var lastIngestAt = entity.GetDateTimeOffset("LastIngestAt")?.UtcDateTime ?? DateTime.MinValue;
                    var eventCount = entity.GetInt32("EventCount") ?? 0;

                    // Grace period: skip recent entries (race condition protection)
                    if (lastIngestAt > cutoff)
                        continue;

                    // Check if session exists
                    try
                    {
                        var session = await sessionsClient.GetEntityIfExistsAsync<TableEntity>(tenantId, sessionId, select: new[] { "PartitionKey" });
                        if (!session.HasValue)
                        {
                            orphans.Add(new OrphanedEventSession
                            {
                                TenantId = tenantId,
                                SessionId = sessionId,
                                LastIngestAt = lastIngestAt,
                                EventCount = eventCount
                            });
                        }
                    }
                    catch (RequestFailedException ex)
                    {
                        // GetEntityIfExistsAsync does NOT throw on 404 (handled via HasValue above),
                        // so this only fires on transient errors (429/500/503). Treating those as
                        // "orphan" would delete events of live sessions — skip the entry instead.
                        _logger.LogWarning(ex, "Transient error checking session existence for {TenantId}/{SessionId} (status {Status}); skipping orphan classification this run", tenantId, sessionId, ex.Status);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan EventSessionIndex for orphans");
            }

            return orphans;
        }

        public async Task DeleteEventSessionIndexEntryAsync(string tenantId, string sessionId)
        {
            try
            {
                var indexClient = _tableServiceClient.GetTableClient(Constants.TableNames.EventSessionIndex);
                await indexClient.DeleteEntityAsync(tenantId, sessionId);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted, ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete EventSessionIndex entry for {TenantId}/{SessionId}", tenantId, sessionId);
            }
        }
    }
}
