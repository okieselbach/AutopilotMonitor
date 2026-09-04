using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: verdict-calibration daily aggregate persistence (internal/docs/backend/verdict-calibration.md).
    /// VerdictCalibrationAggregates: PK=TenantId ("global" for the cross-tenant row),
    /// RK="{yyyy-MM-dd}" — one row per (tenant, StartedAt date) holding the per-verdict-path
    /// buckets as a JSON column. Recomputed whole (Replace) by the maintenance sweep, 180d
    /// retention, regenerable from Sessions + DeviceHistories.
    /// </summary>
    public partial class TableStorageService
    {
        public async Task<bool> SaveVerdictCalibrationAggregateAsync(VerdictCalibrationDailyAggregate aggregate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VerdictCalibrationAggregates);
                await tableClient.UpsertEntityAsync(BuildVerdictCalibrationAggregateEntity(aggregate), TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save verdict-calibration aggregate {Date} for tenant {TenantId}",
                    aggregate.Date, aggregate.TenantId);
                return false;
            }
        }

        public async Task<List<VerdictCalibrationDailyAggregate>> GetVerdictCalibrationAggregatesAsync(
            string tenantId, DateTime startDate, DateTime endDate)
        {
            if (tenantId != "global")
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VerdictCalibrationAggregates);
                var filter = $"PartitionKey eq '{tenantId}' and RowKey ge '{startDate:yyyy-MM-dd}' and RowKey le '{endDate:yyyy-MM-dd}'";
                var results = new List<VerdictCalibrationDailyAggregate>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToVerdictCalibrationAggregate(entity));
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query verdict-calibration aggregates for tenant {TenantId}", tenantId);
                return new List<VerdictCalibrationDailyAggregate>();
            }
        }

        // internal static: pinned by round-trip tests together with MapToVerdictCalibrationAggregate.
        internal static TableEntity BuildVerdictCalibrationAggregateEntity(VerdictCalibrationDailyAggregate aggregate)
        {
            return new TableEntity(aggregate.TenantId, aggregate.Date)
            {
                ["Date"] = aggregate.Date,
                ["Version"] = aggregate.Version,
                ["SessionCount"] = aggregate.SessionCount,
                ["TerminalSessionCount"] = aggregate.TerminalSessionCount,
                ["BucketsJson"] = JsonSerializer.Serialize(aggregate.Buckets),
                ["ComputedAt"] = EnsureUtc(aggregate.ComputedAt),
            };
        }

        internal static VerdictCalibrationDailyAggregate MapToVerdictCalibrationAggregate(TableEntity entity)
        {
            return new VerdictCalibrationDailyAggregate
            {
                TenantId = entity.PartitionKey,
                Date = entity.GetString("Date") ?? entity.RowKey,
                Version = entity.GetInt32("Version") ?? 0,
                SessionCount = entity.GetInt32("SessionCount") ?? 0,
                TerminalSessionCount = entity.GetInt32("TerminalSessionCount") ?? 0,
                Buckets = DeserializeJsonColumn<VerdictCalibrationBucket>(entity.GetString("BucketsJson")),
                ComputedAt = entity.GetDateTimeOffset("ComputedAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        public async Task DeleteVerdictCalibrationAggregateAsync(string tenantId, string dateKey)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VerdictCalibrationAggregates);
                await tableClient.DeleteEntityAsync(tenantId, dateKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete stale verdict-calibration aggregate {Date} for tenant {TenantId}",
                    dateKey, tenantId);
            }
        }

        public async Task<int> DeleteVerdictCalibrationAggregatesOlderThanAsync(DateTime cutoffDate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.VerdictCalibrationAggregates);
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
                _logger.LogError(ex, "Failed to delete old verdict-calibration aggregates");
                return 0;
            }
        }
    }
}
