using System;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Telemetry;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// The one compare-and-swap loop for single-row counters. Reads the row, lets the caller
    /// derive a merge patch from what it read, and writes the patch with the read ETag; a 412
    /// (row changed) or 409 (concurrent creator) re-reads and retries with backoff. A missing
    /// row is created with <c>AddEntity</c>, never upsert, so two first writers cannot clobber
    /// each other. Every conflict is reported to <see cref="StorageMetrics"/>.
    /// <para>
    /// Replaces the last-writer-wins read-modify-write that PlatformStats and RuleStats used
    /// (audit 2026-09-05 F06): a lost increment there was permanent for counters without a
    /// recompute source, and the full-entity write-back clobbered fields other writers had
    /// changed in between. Callers keep their own fail-soft wrapper; the helper only returns
    /// <c>false</c> when the row did not need a change or the retries were exhausted.
    /// </para>
    /// </summary>
    internal static class TableCasRetry
    {
        public const int DefaultRetries = 4;

        /// <summary>
        /// <paramref name="patch"/> receives the row as read and returns the properties to merge
        /// (null = nothing to change). <paramref name="createMissing"/> builds the whole row for
        /// the 404 case. Returns true when a write landed.
        /// </summary>
        public static async Task<bool> MutateAsync(
            TableClient table,
            string partitionKey,
            string rowKey,
            Func<TableEntity, TableEntity?> patch,
            Func<TableEntity> createMissing,
            string operation,
            string tableName,
            StorageMetrics? metrics,
            ILogger logger,
            int retries = DefaultRetries)
        {
            for (var attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    TableEntity read;
                    try
                    {
                        read = (await table.GetEntityAsync<TableEntity>(partitionKey, rowKey).ConfigureAwait(false)).Value;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        // AddEntity (not upsert): a concurrent creator surfaces as 409 and the
                        // retry lands in the update branch instead of overwriting the winner.
                        await table.AddEntityAsync(createMissing()).ConfigureAwait(false);
                        return true;
                    }

                    var update = patch(read);
                    if (update == null)
                        return false;

                    await table.UpdateEntityAsync(update, read.ETag, TableUpdateMode.Merge).ConfigureAwait(false);
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status == 412 || StorageErrors.IsAlreadyExists(ex))
                {
                    var exhausted = attempt == retries;
                    metrics?.CasConflict(operation, tableName, exhausted ? CasOutcome.Exhausted : CasOutcome.Retried);
                    if (exhausted)
                    {
                        logger.LogWarning(
                            "{Operation} on {Table} {PartitionKey}/{RowKey} lost the CAS race {Retries} times — giving up (status {Status})",
                            operation, tableName, partitionKey, rowKey, retries, ex.Status);
                        return false;
                    }

                    var delay = 50 * attempt;
                    await Task.Delay(delay + Random.Shared.Next(0, delay)).ConfigureAwait(false);
                }
            }

            return false;
        }
    }
}
