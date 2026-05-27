using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IUserUsageRepository.
    /// PartitionKey: userId (oid claim), RowKey: {yyyyMMdd}_{normalizedEndpoint}
    /// </summary>
    public class TableUserUsageRepository : IUserUsageRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableUserUsageRepository> _logger;

        public TableUserUsageRepository(
            TableStorageService storage,
            ILogger<TableUserUsageRepository> logger)
        {
            _logger = logger;
            _tableClient = storage.GetTableClient(Constants.TableNames.UserUsageLog);
        }

        public async Task IncrementUsageAsync(string userId, string userPrincipalName, string tenantId, string endpoint)
        {
            var date = DateTime.UtcNow.ToString("yyyyMMdd");
            var rowKey = $"{date}_{endpoint}";

            const int maxRetries = 3;
            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var result = await _tableClient.GetEntityAsync<TableEntity>(userId, rowKey);
                    var entity = result.Value;
                    var count = entity.TryGetValue("RequestCount", out var c) ? Convert.ToInt64(c) : 0L;
                    entity["RequestCount"] = count + 1;
                    entity["LastRequestAt"] = DateTimeOffset.UtcNow;
                    await _tableClient.UpdateEntityAsync(entity, entity.ETag);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    var entity = new TableEntity(userId, rowKey)
                    {
                        ["Date"] = date,
                        ["Endpoint"] = endpoint,
                        ["UserId"] = userId,
                        ["UserPrincipalName"] = userPrincipalName,
                        ["TenantId"] = tenantId,
                        ["RequestCount"] = 1L,
                        ["LastRequestAt"] = DateTimeOffset.UtcNow,
                    };
                    try
                    {
                        await _tableClient.AddEntityAsync(entity);
                        return;
                    }
                    catch (RequestFailedException addEx) when (addEx.Status == 409)
                    {
                        continue;
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    continue;
                }
            }

            _logger.LogWarning("Failed to increment user usage after {MaxRetries} retries: user={UserId}, endpoint={Endpoint}",
                maxRetries, userId, endpoint);
        }

        public async Task<List<UserUsageRecord>> GetUsageByUserAsync(string userId, string? dateFrom = null, string? dateTo = null)
        {
            var filter = BuildUserUsageFilter(userId, dateFrom, dateTo);

            var records = new List<UserUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }
            return records;
        }

        public async Task<List<UserUsageRecord>> GetUsageByTenantAsync(string tenantId, string? dateFrom = null, string? dateTo = null)
        {
            var filter = BuildTenantUsageFilter(tenantId, dateFrom, dateTo);

            var records = new List<UserUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }
            return records;
        }

        public async Task<List<UserUsageDailySummary>> GetDailySummaryAsync(string? tenantId = null, string? dateFrom = null, string? dateTo = null)
        {
            var filter = BuildTenantUsageFilter(tenantId, dateFrom, dateTo);

            var records = new List<UserUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }

            var grouped = records
                .GroupBy(r => r.Date)
                .Select(g => new UserUsageDailySummary
                {
                    Date = g.Key,
                    TenantId = tenantId,
                    TotalRequests = g.Sum(r => r.RequestCount),
                    UniqueUsers = g.Select(r => r.UserId).Distinct().Count(),
                    UniqueEndpoints = g.Select(r => r.Endpoint).Distinct().Count(),
                })
                .OrderByDescending(s => s.Date)
                .ToList();

            return grouped;
        }

        /// <summary>
        /// Builds the OData filter for a per-user usage query. <paramref name="userId"/> is a route
        /// segment (AAD oid) and the dates are query params, so all values MUST be OData-escaped before
        /// interpolation — otherwise a single quote can inject an OR clause that broadens the query past
        /// the intended PartitionKey/Date scope. Extracted as a pure function so the escaping is testable.
        /// </summary>
        internal static string BuildUserUsageFilter(string userId, string? dateFrom, string? dateTo)
        {
            var filter = $"PartitionKey eq '{ODataSanitizer.EscapeValue(userId)}'";
            return AppendDateFilter(filter, dateFrom, dateTo);
        }

        /// <summary>
        /// Builds the OData filter for a per-tenant usage query. Same escaping requirement as
        /// <see cref="BuildUserUsageFilter"/>; returns null when no tenant/date scope is supplied.
        /// </summary>
        internal static string? BuildTenantUsageFilter(string? tenantId, string? dateFrom, string? dateTo)
        {
            string? filter = !string.IsNullOrEmpty(tenantId)
                ? $"TenantId eq '{ODataSanitizer.EscapeValue(tenantId)}'"
                : null;
            return AppendDateFilter(filter, dateFrom, dateTo);
        }

        private static string AppendDateFilter(string? existingFilter, string? dateFrom, string? dateTo)
        {
            var filter = existingFilter ?? "";

            if (!string.IsNullOrEmpty(dateFrom))
            {
                // .Replace("-","") only normalizes the date shape; it does NOT neutralize single quotes,
                // so the value must still be OData-escaped before interpolation.
                var dateVal = ODataSanitizer.EscapeValue(dateFrom.Replace("-", ""));
                var clause = $"Date ge '{dateVal}'";
                filter = string.IsNullOrEmpty(filter) ? clause : $"{filter} and {clause}";
            }

            if (!string.IsNullOrEmpty(dateTo))
            {
                var dateVal = ODataSanitizer.EscapeValue(dateTo.Replace("-", ""));
                var clause = $"Date le '{dateVal}'";
                filter = string.IsNullOrEmpty(filter) ? clause : $"{filter} and {clause}";
            }

            return string.IsNullOrEmpty(filter) ? null! : filter;
        }

        public async Task<int> DeleteRecordsOlderThanAsync(string dateCutoff)
        {
            var filter = $"Date lt '{ODataSanitizer.EscapeValue(dateCutoff)}'";
            int deleted = 0;

            // Collect entities in batches to avoid modifying collection during enumeration
            var toDelete = new List<(string partitionKey, string rowKey)>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
                filter: filter, select: new[] { "PartitionKey", "RowKey" }))
            {
                toDelete.Add((entity.PartitionKey, entity.RowKey));
            }

            foreach (var (pk, rk) in toDelete)
            {
                try
                {
                    await _tableClient.DeleteEntityAsync(pk, rk);
                    deleted++;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Already deleted, skip
                }
            }

            return deleted;
        }

        private static UserUsageRecord MapToRecord(TableEntity entity)
        {
            return new UserUsageRecord
            {
                UserId = entity.GetString("UserId") ?? entity.PartitionKey,
                UserPrincipalName = entity.GetString("UserPrincipalName") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? string.Empty,
                Endpoint = entity.GetString("Endpoint") ?? string.Empty,
                Date = entity.GetString("Date") ?? string.Empty,
                RequestCount = entity.TryGetValue("RequestCount", out var rc) ? Convert.ToInt64(rc) : 0L,
                LastRequestAt = entity.GetDateTimeOffset("LastRequestAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }
    }
}
