using System.Text.Json;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: F1 time-attribution persistence (insights spec §F1, PR2).
    /// SessionTimeBreakdowns: PK=TenantId, RK=SessionId — one row per terminal session,
    /// written once at the terminal transition and self-healed by the 30-day maintenance
    /// sweep; deleted with its session (deletion manifest step + offboarding partition wipe).
    /// TimeAttributionAggregates: PK=TenantId ("global" for the cross-tenant row),
    /// RK="{yyyy-MM-dd}|{enrollmentClass}" — daily fleet rollups, recomputed idempotently.
    /// </summary>
    public partial class TableStorageService
    {
        // ===== SESSION TIME BREAKDOWNS =====

        /// <summary>
        /// Loads the session facts + events and computes/stores the breakdown via
        /// <see cref="TimeAttributionCalculator"/>. Returns the stored breakdown, or null when
        /// the session is not computable (non-terminal, Incomplete — which deliberately has no
        /// DurationSeconds — or missing) or on any storage failure. Fail-soft by design: this
        /// runs on the terminal-transition path and the maintenance sweep, and must never fail
        /// either. Idempotent — recomputing from the same events yields the same row (the
        /// calculator is deterministic and Replace-mode overwrites).
        /// </summary>
        public async Task<SessionTimeBreakdown?> ComputeAndStoreSessionTimeBreakdownAsync(string tenantId, string sessionId)
        {
            try
            {
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
                SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

                var session = await GetSessionAsync(tenantId, sessionId);
                if (session == null) return null;
                if (session.Status != SessionStatus.Succeeded && session.Status != SessionStatus.Failed) return null;
                if (!session.DurationSeconds.HasValue || session.DurationSeconds.Value <= 0) return null;

                var events = await GetSessionEventsAsync(tenantId, sessionId);
                if (events.Count == 0) return null; // events aged out (retention) — no evidence, no row

                var breakdown = TimeAttributionCalculator.Compute(new TimeAttributionInput
                {
                    TenantId = tenantId,
                    SessionId = sessionId,
                    Status = session.Status.ToString(),
                    StartedAt = session.StartedAt,
                    CompletedAt = session.CompletedAt,
                    DurationSeconds = session.DurationSeconds,
                    IsPreProvisioned = session.IsPreProvisioned,
                    ResumedAt = session.ResumedAt,
                    Events = events,
                });
                if (breakdown == null) return null;

                // Change signal for the sweep: a session whose EventCount moved after this
                // write (late/replayed batches) gets recomputed from the fuller stream.
                breakdown.EventCountAtCompute = session.EventCount;

                return await StoreSessionTimeBreakdownAsync(breakdown) ? breakdown : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Time-attribution compute failed for session {SessionId} (fail-soft)", sessionId);
                return null;
            }
        }

        /// <summary>Upserts the breakdown row (Replace — a recompute owns the whole row).</summary>
        public async Task<bool> StoreSessionTimeBreakdownAsync(SessionTimeBreakdown breakdown)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionTimeBreakdowns);
                await tableClient.UpsertEntityAsync(
                    BuildSessionTimeBreakdownEntity(breakdown), TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store time breakdown for session {SessionId}", breakdown.SessionId);
                return false;
            }
        }

        /// <summary>Point-reads one breakdown row; null when the session has none.</summary>
        public async Task<SessionTimeBreakdown?> GetSessionTimeBreakdownAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionTimeBreakdowns);
                var result = await tableClient.GetEntityIfExistsAsync<TableEntity>(tenantId, sessionId);
                return result.HasValue ? MapToSessionTimeBreakdown(result.Value!) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get time breakdown for session {SessionId}", sessionId);
                return null;
            }
        }

        // internal static: entity builder + mapper are pinned by round-trip unit tests
        // (table-storage-serialization rule — every property must survive Store→Map).
        internal static TableEntity BuildSessionTimeBreakdownEntity(SessionTimeBreakdown b)
        {
            var entity = new TableEntity(b.TenantId, b.SessionId)
            {
                ["AttributionVersion"] = b.AttributionVersion,
                ["EventCountAtCompute"] = b.EventCountAtCompute,
                ["WallClockSeconds"] = b.WallClockSeconds,
                ["UnattributedSeconds"] = b.UnattributedSeconds,
                ["RebootSeconds"] = b.RebootSeconds,
                ["BlockingAppCount"] = b.BlockingAppCount,
                ["QualityFlags"] = (int)b.QualityFlags,
                ["SegmentsJson"] = JsonSerializer.Serialize(b.Segments),
                ["RebootSpansJson"] = JsonSerializer.Serialize(b.RebootSpans),
                ["BlockingAppsJson"] = JsonSerializer.Serialize(b.BlockingApps),
                ["ComputedAt"] = DateTime.UtcNow,
            };
            // Tri-state: absent column = occupancy unknown (blocking set never observed) — the
            // mapper must yield null, never 0 (truthfulness rule 1).
            if (b.EspAppsOccupancySeconds.HasValue)
                entity["EspAppsOccupancySeconds"] = b.EspAppsOccupancySeconds.Value;
            return entity;
        }

        internal static SessionTimeBreakdown MapToSessionTimeBreakdown(TableEntity entity)
        {
            return new SessionTimeBreakdown
            {
                TenantId = entity.PartitionKey,
                SessionId = entity.RowKey,
                AttributionVersion = entity.GetInt32("AttributionVersion") ?? 0,
                EventCountAtCompute = entity.GetInt32("EventCountAtCompute") ?? 0,
                WallClockSeconds = entity.GetInt32("WallClockSeconds") ?? 0,
                UnattributedSeconds = entity.GetInt32("UnattributedSeconds") ?? 0,
                RebootSeconds = entity.GetInt32("RebootSeconds") ?? 0,
                BlockingAppCount = entity.GetInt32("BlockingAppCount") ?? 0,
                EspAppsOccupancySeconds = entity.GetInt32("EspAppsOccupancySeconds"),
                QualityFlags = (TimeAttributionFlags)(entity.GetInt32("QualityFlags") ?? 0),
                Segments = DeserializeJsonColumn<TimeAttributionSpan>(entity.GetString("SegmentsJson")),
                RebootSpans = DeserializeJsonColumn<RebootSpan>(entity.GetString("RebootSpansJson")),
                BlockingApps = DeserializeJsonColumn<BlockingAppInterval>(entity.GetString("BlockingAppsJson")),
            };
        }

        private static List<T> DeserializeJsonColumn<T>(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new List<T>();
            try
            {
                return JsonSerializer.Deserialize<List<T>>(json!) ?? new List<T>();
            }
            catch (JsonException)
            {
                return new List<T>(); // corrupt/truncated column — degrade to empty, never throw on read
            }
        }

        // ===== TIME ATTRIBUTION DAILY AGGREGATES =====

        /// <summary>Upserts one daily aggregate row (Replace — the sweep recomputes rows whole).</summary>
        public async Task<bool> SaveTimeAttributionAggregateAsync(TimeAttributionDailyAggregate aggregate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TimeAttributionAggregates);
                await tableClient.UpsertEntityAsync(
                    BuildTimeAttributionAggregateEntity(aggregate), TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save time-attribution aggregate {Date}/{Class} for tenant {TenantId}",
                    aggregate.Date, aggregate.EnrollmentClass, aggregate.TenantId);
                return false;
            }
        }

        // internal static: pinned by round-trip tests together with MapToTimeAttributionAggregate.
        internal static TableEntity BuildTimeAttributionAggregateEntity(TimeAttributionDailyAggregate aggregate)
        {
            return new TableEntity(aggregate.TenantId, $"{aggregate.Date}|{aggregate.EnrollmentClass}")
            {
                ["Date"] = aggregate.Date,
                ["EnrollmentClass"] = aggregate.EnrollmentClass,
                ["AttributionVersion"] = aggregate.AttributionVersion,
                ["CleanSessionCount"] = aggregate.CleanSessionCount,
                ["FlaggedExcludedCount"] = aggregate.FlaggedExcludedCount,
                ["MissingBreakdownCount"] = aggregate.MissingBreakdownCount,
                ["SegmentStatsJson"] = JsonSerializer.Serialize(aggregate.SegmentStats),
                ["TopBlockingAppsJson"] = JsonSerializer.Serialize(aggregate.TopBlockingApps),
                ["ComputedAt"] = EnsureUtc(aggregate.ComputedAt),
            };
        }

        /// <summary>
        /// Reads the aggregate rows of one tenant partition ("global" allowed) for an inclusive
        /// date range. RowKeys are "{yyyy-MM-dd}|{class}", so a string range filter covers the
        /// window; dates are server-formatted (injection-safe).
        /// </summary>
        public async Task<List<TimeAttributionDailyAggregate>> GetTimeAttributionAggregatesAsync(
            string tenantId, DateTime startDate, DateTime endDate)
        {
            if (tenantId != "global")
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TimeAttributionAggregates);
                // '~' (0x7E) sorts after '|' (0x7C): "{end}~" upper-bounds every "{end}|{class}" key.
                var filter = $"PartitionKey eq '{tenantId}' and RowKey ge '{startDate:yyyy-MM-dd}' and RowKey lt '{endDate:yyyy-MM-dd}~'";
                var results = new List<TimeAttributionDailyAggregate>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToTimeAttributionAggregate(entity));
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query time-attribution aggregates for tenant {TenantId}", tenantId);
                return new List<TimeAttributionDailyAggregate>();
            }
        }

        /// <summary>
        /// Reads the rolling-window aggregate rows (RK "rolling30|{class}") of one tenant
        /// partition ("global" allowed) — the range statistics the fleet panel renders. The
        /// date-range getter never returns these (its digit-prefixed upper bound sorts below
        /// "rolling…"), and vice versa.
        /// </summary>
        public async Task<List<TimeAttributionDailyAggregate>> GetRollingTimeAttributionAggregatesAsync(string tenantId)
        {
            if (tenantId != "global")
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TimeAttributionAggregates);
                // '}' (0x7D) sorts after '|' (0x7C) — prefix range over "rolling30|…".
                var filter = $"PartitionKey eq '{tenantId}' and RowKey ge 'rolling30|' and RowKey lt 'rolling30}}'";
                var results = new List<TimeAttributionDailyAggregate>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToTimeAttributionAggregate(entity));
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query rolling time-attribution aggregates for tenant {TenantId}", tenantId);
                return new List<TimeAttributionDailyAggregate>();
            }
        }

        internal static TimeAttributionDailyAggregate MapToTimeAttributionAggregate(TableEntity entity)
        {
            return new TimeAttributionDailyAggregate
            {
                TenantId = entity.PartitionKey,
                Date = entity.GetString("Date") ?? string.Empty,
                EnrollmentClass = entity.GetString("EnrollmentClass") ?? string.Empty,
                AttributionVersion = entity.GetInt32("AttributionVersion") ?? 0,
                CleanSessionCount = entity.GetInt32("CleanSessionCount") ?? 0,
                FlaggedExcludedCount = entity.GetInt32("FlaggedExcludedCount") ?? 0,
                MissingBreakdownCount = entity.GetInt32("MissingBreakdownCount") ?? 0,
                SegmentStats = DeserializeJsonColumn<TimeAttributionSegmentStat>(entity.GetString("SegmentStatsJson")),
                TopBlockingApps = DeserializeJsonColumn<TimeAttributionBlockingAppStat>(entity.GetString("TopBlockingAppsJson")),
                ComputedAt = entity.GetDateTimeOffset("ComputedAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        /// <summary>
        /// Deletes one aggregate row (daily "{date}|{class}" or rolling "rolling30|{class}").
        /// Used by the sweep's stale-bucket reconcile: a bucket that was not regenerated this
        /// run (its sessions were deleted or its class left the window) must not keep serving
        /// old numbers. Missing rows are a no-op.
        /// </summary>
        public async Task DeleteTimeAttributionAggregateAsync(string tenantId, string dateKey, string enrollmentClass)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TimeAttributionAggregates);
                await tableClient.DeleteEntityAsync(tenantId, $"{dateKey}|{enrollmentClass}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete stale time-attribution aggregate {Date}/{Class} for tenant {TenantId}",
                    dateKey, enrollmentClass, tenantId);
            }
        }

        /// <summary>
        /// Retention: deletes aggregate rows older than the cutoff (RowKey starts with the date,
        /// so a string compare works across partitions). Mirrors the UsageMetrics 180d policy.
        /// </summary>
        public async Task<int> DeleteTimeAttributionAggregatesOlderThanAsync(DateTime cutoffDate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TimeAttributionAggregates);
                var filter = $"RowKey lt '{cutoffDate:yyyy-MM-dd}'";
                var deleted = 0;
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                    filter: filter, select: new[] { "PartitionKey", "RowKey" }))
                {
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                    deleted++;
                }
                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old time-attribution aggregates");
                return 0;
            }
        }
    }
}
