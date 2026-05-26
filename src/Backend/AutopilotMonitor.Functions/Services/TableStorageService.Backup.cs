using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Critical-table backup primitives on <see cref="TableStorageService"/> (plan §PR1).
    /// </summary>
    public partial class TableStorageService
    {
        /// <summary>
        /// Streams every row of <paramref name="tableName"/> as a generic
        /// <see cref="TableEntity"/>. Used by <c>CriticalTableBackupService</c> to feed
        /// the per-table NDJSON dump without loading the whole table into memory.
        /// Fail-loud per <c>feedback_storage_helpers_fail_soft</c>: storage errors
        /// propagate so the caller can decide whether to mark the table Failed and
        /// continue or to bubble.
        /// </summary>
        public virtual async IAsyncEnumerable<TableEntity> EnumerateAllAsync(
            string tableName,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tableName))
                throw new ArgumentException("tableName is required", nameof(tableName));

            var tableClient = _tableServiceClient.GetTableClient(tableName);
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                yield return entity;
            }
        }
    }
}
