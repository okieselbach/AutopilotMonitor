using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="IDistressReportRepository"/>.
    /// PartitionKey = tenantId (lowercase), RowKey = reverse-tick for newest-first ordering.
    /// </summary>
    public class TableDistressReportRepository : IDistressReportRepository
    {
        private readonly TableClient _table;
        private readonly ILogger<TableDistressReportRepository> _logger;

        public TableDistressReportRepository(
            TableStorageService storage,
            ILogger<TableDistressReportRepository> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.DistressReports);
        }

        public async Task SaveDistressReportAsync(string tenantId, DistressReportEntry entry)
        {
            var entity = MapToEntity(tenantId, RowKeyCodec.InvertedTicks(DateTime.UtcNow), entry);
            await _table.UpsertEntityAsync(entity);
        }

        /// <summary>The stored row of a report; <see cref="MapToEntry"/> reads it back.</summary>
        internal static TableEntity MapToEntity(string tenantId, string rowKey, DistressReportEntry entry)
        {
            return new TableEntity(tenantId.ToLowerInvariant(), rowKey)
            {
                ["ErrorType"]       = Truncate(entry.ErrorType, 64),
                ["Manufacturer"]    = Truncate(entry.Manufacturer, 64),
                ["Model"]           = Truncate(entry.Model, 64),
                ["SerialNumber"]    = Truncate(entry.SerialNumber, 64),
                ["AgentVersion"]    = Truncate(entry.AgentVersion, 32),
                ["HttpStatusCode"]  = entry.HttpStatusCode,
                ["Message"]         = Truncate(entry.Message, 256),
                ["AgentTimestamp"]  = entry.AgentTimestamp,
                ["IngestedAt"]      = entry.IngestedAt,
                ["SourceIp"]        = Truncate(entry.SourceIp, 45), // max IPv6 length
                ["IsCloudPc"]       = entry.IsCloudPc,
                ["CertSourceState"] = Truncate(entry.CertSourceState, 32),
                ["CertThumbprint"]  = Truncate(entry.CertThumbprint, 40),
                ["CertSubject"]     = Truncate(entry.CertSubject, 96),
                ["CertIssuer"]      = Truncate(entry.CertIssuer, 96),
                ["CertNotBefore"]   = entry.CertNotBefore,
                ["CertNotAfter"]    = entry.CertNotAfter,
                ["OsBuild"]         = Truncate(entry.OsBuild, 32),
                ["OobeState"]       = Truncate(entry.OobeState, 16),
                ["TpmVersion"]      = Truncate(entry.TpmVersion, 48),
                ["EspPhase"]        = Truncate(entry.EspPhase, 32),
                ["DeviceBootUtc"]   = entry.DeviceBootUtc,
            };
        }

        public async Task<List<DistressReportEntry>> GetDistressReportsAsync(string tenantId, DistressErrorType errorType, int maxResults = 100)
        {
            var pk = tenantId.ToLowerInvariant();
            var result = new List<DistressReportEntry>();
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {pk} and ErrorType eq {errorType.ToString()}");

            await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter, maxPerPage: AzureTablesPaginator.ServerPageSize(maxResults)))
            {
                result.Add(MapToEntry(entity));
                if (result.Count >= maxResults)
                    break;
            }

            return result;
        }

        public Task<(List<DistressReportEntry> Reports, bool Truncated)> GetNewestDistressReportsAsync(int maxResults)
        {
            // A cross-partition query returns rows in tenant order, so the first N rows are not the
            // newest N: the whole table is read (bounded by the 14-day cleanup) and ranked here.
            return KeepNewestAsync(MapAll(_table.QueryAsync<TableEntity>(maxPerPage: 1000)), maxResults);
        }

        private static async IAsyncEnumerable<DistressReportEntry> MapAll(IAsyncEnumerable<TableEntity> entities)
        {
            await foreach (var entity in entities)
                yield return MapToEntry(entity);
        }

        /// <summary>
        /// The newest <paramref name="maxResults"/> entries by IngestedAt, newest first. A min-heap keeps
        /// memory at <paramref name="maxResults"/> however many rows the source yields.
        /// </summary>
        internal static async Task<(List<DistressReportEntry> Reports, bool Truncated)> KeepNewestAsync(
            IAsyncEnumerable<DistressReportEntry> entries, int maxResults)
        {
            var newest = new PriorityQueue<DistressReportEntry, DateTime>();
            var seen = 0;

            await foreach (var entry in entries)
            {
                seen++;
                if (newest.Count < maxResults)
                    newest.Enqueue(entry, entry.IngestedAt);
                else
                    newest.EnqueueDequeue(entry, entry.IngestedAt);
            }

            var reports = new List<DistressReportEntry>(newest.Count);
            while (newest.TryDequeue(out var entry, out _))
                reports.Add(entry);
            reports.Reverse();

            return (reports, seen > maxResults);
        }

        public async Task<int> DeleteDistressReportsOlderThanAsync(string tenantId, DateTime cutoff)
        {
            var pk = tenantId.ToLowerInvariant();
            var deleted = 0;

            // Reverse-tick RowKey: older entries have higher ticks (lower reverse-tick values... actually higher).
            // Simplest approach: query all for this partition and filter by IngestedAt.
            await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == pk))
            {
                var ingestedAt = entity.GetDateTimeOffset("IngestedAt")?.UtcDateTime ?? DateTime.MinValue;
                if (ingestedAt < cutoff)
                {
                    try
                    {
                        await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete distress report {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }
            }

            return deleted;
        }

        internal static DistressReportEntry MapToEntry(TableEntity entity)
        {
            return new DistressReportEntry
            {
                TenantId        = entity.PartitionKey,
                ErrorType       = entity.GetString("ErrorType") ?? string.Empty,
                Manufacturer    = entity.GetString("Manufacturer"),
                Model           = entity.GetString("Model"),
                SerialNumber    = entity.GetString("SerialNumber"),
                AgentVersion    = entity.GetString("AgentVersion"),
                HttpStatusCode  = entity.GetInt32("HttpStatusCode"),
                Message         = entity.GetString("Message"),
                AgentTimestamp  = entity.GetDateTimeOffset("AgentTimestamp")?.UtcDateTime ?? DateTime.MinValue,
                IngestedAt      = entity.GetDateTimeOffset("IngestedAt")?.UtcDateTime ?? DateTime.MinValue,
                SourceIp        = entity.GetString("SourceIp"),
                IsCloudPc       = entity.GetBoolean("IsCloudPc") ?? false,
                CertSourceState = entity.GetString("CertSourceState"),
                CertThumbprint  = entity.GetString("CertThumbprint"),
                CertSubject     = entity.GetString("CertSubject"),
                CertIssuer      = entity.GetString("CertIssuer"),
                CertNotBefore   = entity.GetDateTimeOffset("CertNotBefore")?.UtcDateTime,
                CertNotAfter    = entity.GetDateTimeOffset("CertNotAfter")?.UtcDateTime,
                OsBuild         = entity.GetString("OsBuild"),
                OobeState       = entity.GetString("OobeState"),
                TpmVersion      = entity.GetString("TpmVersion"),
                EspPhase        = entity.GetString("EspPhase"),
                DeviceBootUtc   = entity.GetDateTimeOffset("DeviceBootUtc")?.UtcDateTime,
            };
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (value == null) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
