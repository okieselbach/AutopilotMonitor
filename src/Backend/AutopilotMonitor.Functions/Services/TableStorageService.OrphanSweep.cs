using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Storage side of the orphan-session sweep (<see cref="Maintenance.OrphanSessionSweeper"/>):
    /// the two key drains the diff runs on, the EventSessionIndex handle as the sweep's progress
    /// row, and the per-table delete by the writer's key shape.
    /// </summary>
    public partial class TableStorageService
    {
        private const string HandleInventoryDecrementedColumn = "InventoryDecremented";

        // Timestamp must be named explicitly in a projection; ETag always travels with the row.
        private static readonly string[] HandleColumns =
            { "PartitionKey", "RowKey", "Timestamp", "LastIngestAt", HandleInventoryDecrementedColumn };

        public async Task<HashSet<(string TenantId, string SessionId)>> GetSessionKeysAsync(CancellationToken ct = default)
        {
            var client = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
            var keys = new HashSet<(string TenantId, string SessionId)>();
            await foreach (var row in client.QueryAsync<TableEntity>(
                maxPerPage: 1000, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
            {
                keys.Add((row.PartitionKey, row.RowKey));
            }
            return keys;
        }

        public async Task<IReadOnlyList<OrphanSessionHandle>> GetEventSessionIndexHandlesAsync(CancellationToken ct = default)
        {
            var client = _tableServiceClient.GetTableClient(Constants.TableNames.EventSessionIndex);
            var handles = new List<OrphanSessionHandle>();
            await foreach (var row in client.QueryAsync<TableEntity>(maxPerPage: 1000, select: HandleColumns, cancellationToken: ct))
            {
                handles.Add(ToHandle(row));
            }
            return handles;
        }

        public async Task<OrphanSessionHandle?> GetEventSessionIndexHandleAsync(string tenantId, string sessionId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            var client = _tableServiceClient.GetTableClient(Constants.TableNames.EventSessionIndex);
            var row = await client.GetEntityIfExistsAsync<TableEntity>(tenantId, sessionId, select: HandleColumns, cancellationToken: ct);
            return row.HasValue ? ToHandle(row.Value!) : null;
        }

        public async Task<string?> StampHandleInventoryDecrementedAsync(string tenantId, string sessionId, string etag, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            var client = _tableServiceClient.GetTableClient(Constants.TableNames.EventSessionIndex);
            var stamp = new TableEntity(tenantId, sessionId) { [HandleInventoryDecrementedColumn] = true };
            try
            {
                var response = await client.UpdateEntityAsync(stamp, new ETag(etag), TableUpdateMode.Merge, ct);
                return response.Headers.ETag?.ToString();
            }
            catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<bool> DeleteEventSessionIndexHandleAsync(string tenantId, string sessionId, string etag, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            var client = _tableServiceClient.GetTableClient(Constants.TableNames.EventSessionIndex);
            try
            {
                await client.DeleteEntityAsync(tenantId, sessionId, new ETag(etag), ct);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<int> DeleteOrphanSessionRowsAsync(string table, string tenantId, string sessionId, CancellationToken ct = default)
        {
            var filter = Maintenance.OrphanSessionRowFilters.For(table, tenantId, sessionId);
            var client = _tableServiceClient.GetTableClient(table);
            var keys = new List<(string Pk, string Rk)>();
            await foreach (var row in client.QueryAsync<TableEntity>(
                filter: filter, maxPerPage: 1000, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
            {
                keys.Add((row.PartitionKey, row.RowKey));
            }
            if (keys.Count == 0) return 0;

            // Groups by partition and falls back to per-row deletes on a 404 inside a batch, so a
            // rerun after a partial failure is idempotent; any other failure propagates.
            await DeleteByExactKeysInBatchesAsync(table, keys, ct);
            return keys.Count;
        }

        public async Task<HashSet<(string TenantId, string SessionId)>> GetEventTypeIndexSessionKeysAsync(CancellationToken ct = default)
        {
            var client = _tableServiceClient.GetTableClient(Constants.TableNames.EventTypeIndex);
            var keys = new HashSet<(string TenantId, string SessionId)>();
            await foreach (var row in client.QueryAsync<TableEntity>(
                maxPerPage: 1000, select: new[] { "PartitionKey", "SessionId", "TenantId" }, cancellationToken: ct))
            {
                var sessionId = row.GetString("SessionId");
                if (string.IsNullOrEmpty(sessionId)) continue; // the readers skip such rows as well

                var tenantId = row.GetString("TenantId");
                if (string.IsNullOrEmpty(tenantId))
                {
                    // Rows older than the TenantId column: PartitionKey is {tenantId}_{eventType} and a
                    // tenant id (a GUID) never contains an underscore.
                    var cut = row.PartitionKey.IndexOf('_');
                    if (cut <= 0) continue;
                    tenantId = row.PartitionKey.Substring(0, cut);
                }
                keys.Add((tenantId!, sessionId!));
            }
            return keys;
        }

        private static OrphanSessionHandle ToHandle(TableEntity row) => new OrphanSessionHandle
        {
            TenantId = row.PartitionKey,
            SessionId = row.RowKey,
            LastIngestAt = row.GetDateTimeOffset("LastIngestAt")?.UtcDateTime ?? DateTime.MinValue,
            WrittenAt = row.Timestamp?.UtcDateTime ?? DateTime.MinValue,
            ETag = row.ETag.ToString(),
            InventoryDecremented = row.GetBoolean(HandleInventoryDecrementedColumn) ?? false,
        };
    }
}
