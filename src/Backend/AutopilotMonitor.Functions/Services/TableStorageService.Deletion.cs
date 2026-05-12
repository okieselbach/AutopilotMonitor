using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services.Deletion;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Cascade-deletion read surface. Implements <see cref="ISessionDeletionInventoryReader"/>
    /// against the existing <c>_tableServiceClient</c>. Pure delegation; fail-loud on every
    /// non-404 storage error per memory <c>feedback_storage_helpers_fail_soft</c>.
    /// </summary>
    public partial class TableStorageService : ISessionDeletionInventoryReader
    {
        public async Task<TableEntity?> GetSessionRowAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.Sessions);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: cancellationToken);
                return response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<TableEntity?> GetSessionsIndexRowAsync(string tenantId, string indexRowKey, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.SessionsIndex);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, indexRowKey, cancellationToken: cancellationToken);
                return response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async IAsyncEnumerable<TableEntity> QueryAsync(
            string tableName,
            string filter,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(tableName);
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                yield return entity;
            }
        }

        public async Task<TableEntity?> GetEntityOrNullAsync(string tableName, string partitionKey, string rowKey, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(tableName);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: cancellationToken);
                return response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }
    }
}
