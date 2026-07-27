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
    /// Partial: F2 device-history / First-Time-Right persistence (insights spec §F2, PR4).
    /// DeviceHistories: PK=TenantId, RK=encoded normalized serial — one row per device key,
    /// holding the terminal-session chain (cap 20) + derived journey counts. Written inline at
    /// the session-terminal seam and healed by the rolling maintenance sweep (which also drops
    /// refs of deleted sessions and deletes emptied rows). Deliberately NOT part of the
    /// per-session deletion manifest — the row aggregates many sessions; tenant offboarding
    /// wipes the partition. DeviceJourneyAggregates: PK=TenantId ("global" for the cross-tenant
    /// row), RK="{yyyy-MM-dd}" — daily FTR rollups, recomputed idempotently, 180d retention.
    /// </summary>
    public partial class TableStorageService
    {
        // ===== DEVICE HISTORIES =====

        /// <summary>
        /// Inline seam entry (F2 counterpart of <see cref="ComputeAndStoreSessionTimeBreakdownAsync"/>):
        /// upserts the terminal session into its device's history chain so the session-detail
        /// banner is fresh, not a day old. No-ops for non-terminal sessions, junk/placeholder
        /// serials (excluded by design, disclosed in the daily aggregate) and sessions inside a
        /// deletion cascade — unlike the F1 breakdown row, a chain ref does NOT die with its
        /// session automatically; only the sweep's tombstone pass prunes it, so it must never be
        /// added for a session already being deleted. Fail-soft + idempotent; the maintenance
        /// sweep self-heals any miss.
        /// </summary>
        public async Task UpdateDeviceHistoryForSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
                SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

                var session = await GetSessionAsync(tenantId, sessionId);
                if (session == null) return;
                if (!DeviceJourneyCalculator.IsTerminal(session.Status)) return;
                if (!string.IsNullOrEmpty(session.DeletionState) && session.DeletionState != "None") return;

                var serialKey = DeviceJourneyCalculator.NormalizeSerial(session.SerialNumber);
                if (serialKey == null) return;

                var reference = DeviceJourneyCalculator.BuildSessionRef(session);
                if (reference == null) return;

                var existing = await GetDeviceHistoryAsync(tenantId, serialKey);
                var chain = DeviceJourneyCalculator.MergeChain(existing?.Chain, new[] { reference });
                var history = BuildDeviceHistoryRow(tenantId, serialKey, chain, existing, session);
                await UpsertDeviceHistoryAsync(history);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device-history update failed for session {SessionId} (fail-soft)", sessionId);
            }
        }

        /// <summary>
        /// Assembles the row from a merged chain: derived journey counts via the calculator, and
        /// display fields (serial casing, manufacturer, model) taken from the given session only
        /// when it IS the chain's newest entry (or the row has none yet) — a sweep backfilling an
        /// older session must not regress the display data to stale values. internal static so
        /// the display-precedence rule is pinned by unit tests.
        /// </summary>
        internal static DeviceHistory BuildDeviceHistoryRow(
            string tenantId, string serialKey, List<DeviceSessionRef> chain,
            DeviceHistory? existing, SessionSummary session)
        {
            var (journeyCount, currentAttempts) = DeviceJourneyCalculator.Derive(chain);
            var sessionIsNewest = chain.Count > 0 && chain[chain.Count - 1].SessionId == session.SessionId;
            return new DeviceHistory
            {
                TenantId = tenantId,
                SerialKey = serialKey,
                SerialNumber = sessionIsNewest || string.IsNullOrEmpty(existing?.SerialNumber)
                    ? (session.SerialNumber ?? string.Empty).Trim()
                    : existing!.SerialNumber,
                Manufacturer = sessionIsNewest || string.IsNullOrEmpty(existing?.Manufacturer)
                    ? (session.Manufacturer ?? string.Empty).Trim()
                    : existing!.Manufacturer,
                Model = sessionIsNewest || string.IsNullOrEmpty(existing?.Model)
                    ? (session.Model ?? string.Empty).Trim()
                    : existing!.Model,
                Chain = chain,
                CurrentJourneyAttempts = currentAttempts,
                JourneyCount = journeyCount,
                JourneyVersion = DeviceJourneyCalculator.CurrentVersion,
                LastUpdated = DateTime.UtcNow,
            };
        }

        /// <summary>Point-reads one device's history row; null when the device has none. Takes the NORMALIZED serial (RK encoding is internal).</summary>
        public async Task<DeviceHistory?> GetDeviceHistoryAsync(string tenantId, string serialKey)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (string.IsNullOrEmpty(serialKey)) return null;
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceHistories);
                var result = await tableClient.GetEntityIfExistsAsync<TableEntity>(tenantId, SerialRowKey(serialKey));
                return result.HasValue ? MapToDeviceHistory(result.Value!) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get device history for tenant {TenantId}", tenantId);
                return null;
            }
        }

        /// <summary>All device-history rows of one tenant (the sweep's tombstone-cleanup scan; bounded by the tenant's device count).</summary>
        public async Task<List<DeviceHistory>> GetDeviceHistoriesByTenantAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceHistories);
                var results = new List<DeviceHistory>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'"))
                {
                    results.Add(MapToDeviceHistory(entity));
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query device histories for tenant {TenantId}", tenantId);
                return new List<DeviceHistory>();
            }
        }

        /// <summary>Upserts the history row (Replace — chain maintenance is read-modify-write, the caller owns the whole row).</summary>
        public async Task<bool> UpsertDeviceHistoryAsync(DeviceHistory history)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceHistories);
                await tableClient.UpsertEntityAsync(BuildDeviceHistoryEntity(history), TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert device history for tenant {TenantId}", history.TenantId);
                return false;
            }
        }

        /// <summary>Deletes one device's history row (the sweep calls this when every chain ref belonged to deleted sessions).</summary>
        public async Task DeleteDeviceHistoryAsync(string tenantId, string serialKey)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (string.IsNullOrEmpty(serialKey)) return;
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceHistories);
                await tableClient.DeleteEntityAsync(tenantId, SerialRowKey(serialKey));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete device history for tenant {TenantId}", tenantId);
            }
        }

        /// <summary>
        /// RowKey encoding for the normalized serial: percent-encoding keeps ordinary serials
        /// human-readable while making the characters Table Storage forbids in keys
        /// ('/', '\', '#', '?', controls) safe. Deterministic and reversible — the mapper
        /// restores the exact normalized serial.
        /// </summary>
        internal static string SerialRowKey(string serialKey) => Uri.EscapeDataString(serialKey);

        // internal static: entity builder + mapper are pinned by round-trip unit tests
        // (table-storage-serialization rule — every property must survive Store→Map).
        internal static TableEntity BuildDeviceHistoryEntity(DeviceHistory history)
        {
            return new TableEntity(history.TenantId, SerialRowKey(history.SerialKey))
            {
                ["SerialNumber"] = history.SerialNumber,
                ["Manufacturer"] = history.Manufacturer,
                ["Model"] = history.Model,
                ["ChainJson"] = JsonSerializer.Serialize(history.Chain),
                ["CurrentJourneyAttempts"] = history.CurrentJourneyAttempts,
                ["JourneyCount"] = history.JourneyCount,
                ["JourneyVersion"] = history.JourneyVersion,
                ["LastUpdated"] = EnsureUtc(history.LastUpdated),
            };
        }

        internal static DeviceHistory MapToDeviceHistory(TableEntity entity)
        {
            return new DeviceHistory
            {
                TenantId = entity.PartitionKey,
                SerialKey = Uri.UnescapeDataString(entity.RowKey),
                SerialNumber = entity.GetString("SerialNumber") ?? string.Empty,
                Manufacturer = entity.GetString("Manufacturer") ?? string.Empty,
                Model = entity.GetString("Model") ?? string.Empty,
                Chain = DeserializeJsonColumn<DeviceSessionRef>(entity.GetString("ChainJson")),
                CurrentJourneyAttempts = entity.GetInt32("CurrentJourneyAttempts") ?? 0,
                JourneyCount = entity.GetInt32("JourneyCount") ?? 0,
                JourneyVersion = entity.GetInt32("JourneyVersion") ?? 0,
                LastUpdated = entity.GetDateTimeOffset("LastUpdated")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        // ===== DEVICE JOURNEY (FTR) DAILY AGGREGATES =====

        /// <summary>Upserts one daily FTR aggregate row (Replace — the sweep recomputes rows whole).</summary>
        public async Task<bool> SaveDeviceJourneyAggregateAsync(DeviceJourneyDailyAggregate aggregate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceJourneyAggregates);
                await tableClient.UpsertEntityAsync(BuildDeviceJourneyAggregateEntity(aggregate), TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save device-journey aggregate {Date} for tenant {TenantId}",
                    aggregate.Date, aggregate.TenantId);
                return false;
            }
        }

        /// <summary>
        /// Reads the FTR aggregate rows of one tenant partition ("global" allowed) for an
        /// inclusive date range. RowKeys are exactly "{yyyy-MM-dd}", so a string range filter
        /// covers the window; dates are server-formatted (injection-safe). Counts are additive —
        /// a window rate is the sum over these rows.
        /// </summary>
        public async Task<List<DeviceJourneyDailyAggregate>> GetDeviceJourneyAggregatesAsync(
            string tenantId, DateTime startDate, DateTime endDate)
        {
            if (tenantId != "global")
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceJourneyAggregates);
                var filter = $"PartitionKey eq '{tenantId}' and RowKey ge '{startDate:yyyy-MM-dd}' and RowKey le '{endDate:yyyy-MM-dd}'";
                var results = new List<DeviceJourneyDailyAggregate>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToDeviceJourneyAggregate(entity));
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query device-journey aggregates for tenant {TenantId}", tenantId);
                return new List<DeviceJourneyDailyAggregate>();
            }
        }

        // internal static: pinned by round-trip tests together with MapToDeviceJourneyAggregate.
        internal static TableEntity BuildDeviceJourneyAggregateEntity(DeviceJourneyDailyAggregate aggregate)
        {
            return new TableEntity(aggregate.TenantId, aggregate.Date)
            {
                ["Date"] = aggregate.Date,
                ["JourneyVersion"] = aggregate.JourneyVersion,
                ["CompletedJourneyCount"] = aggregate.CompletedJourneyCount,
                ["FirstTimeRightCount"] = aggregate.FirstTimeRightCount,
                ["AttemptHistogramJson"] = JsonSerializer.Serialize(aggregate.AttemptHistogram),
                ["ExcludedSessionCount"] = aggregate.ExcludedSessionCount,
                ["ComputedAt"] = EnsureUtc(aggregate.ComputedAt),
            };
        }

        internal static DeviceJourneyDailyAggregate MapToDeviceJourneyAggregate(TableEntity entity)
        {
            return new DeviceJourneyDailyAggregate
            {
                TenantId = entity.PartitionKey,
                Date = entity.GetString("Date") ?? entity.RowKey,
                JourneyVersion = entity.GetInt32("JourneyVersion") ?? 0,
                CompletedJourneyCount = entity.GetInt32("CompletedJourneyCount") ?? 0,
                FirstTimeRightCount = entity.GetInt32("FirstTimeRightCount") ?? 0,
                AttemptHistogram = DeserializeJsonColumn<DeviceJourneyAttemptBucket>(entity.GetString("AttemptHistogramJson")),
                ExcludedSessionCount = entity.GetInt32("ExcludedSessionCount") ?? 0,
                ComputedAt = entity.GetDateTimeOffset("ComputedAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        /// <summary>
        /// Retention: deletes FTR aggregate rows older than the cutoff (RowKey IS the date, so a
        /// string compare works across partitions). Mirrors the UsageMetrics 180d policy.
        /// DeviceHistories needs no age sweep — refs are pruned tombstone-driven and the rows
        /// die with tenant offboarding.
        /// </summary>
        public async Task<int> DeleteDeviceJourneyAggregatesOlderThanAsync(DateTime cutoffDate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceJourneyAggregates);
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
                _logger.LogError(ex, "Failed to delete old device-journey aggregates");
                return 0;
            }
        }
    }
}
