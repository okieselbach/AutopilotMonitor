using System;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Telemetry;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// The one compare-and-swap loop: single rows through <see cref="MutateAsync"/>, multi-row
    /// transactions through <see cref="RunAsync"/>. An attempt reads, derives its write from
    /// what it read and writes with the read ETags; a 412 (row changed) or 409 (concurrent
    /// creator) runs the attempt again after a jittered backoff. Every conflict is reported to
    /// <see cref="StorageMetrics"/>.
    /// <para>
    /// Replaces the last-writer-wins read-modify-write that PlatformStats and RuleStats used
    /// (audit 2026-09-05 F06): a lost increment there was permanent for counters without a
    /// recompute source, and the full-entity write-back clobbered fields other writers had
    /// changed in between. Callers keep their own fail-soft wrapper; the helper only returns
    /// <c>false</c> when there was nothing to change or the retries were exhausted.
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
        public static Task<bool> MutateAsync(
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
            => RunAsync(
                async () =>
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
                },
                operation, tableName, $"{partitionKey}/{rowKey}", metrics, logger, retries);

        /// <summary>
        /// Runs <paramref name="attempt"/> until it completes without a write conflict. The
        /// attempt does one full read-derive-write pass and returns true when a write landed,
        /// false when there was nothing to write. A 412 or 409-already-exists from the attempt
        /// is a conflict; any other exception propagates. <paramref name="target"/> names the
        /// row or partition in the warning when the retries are exhausted.
        /// </summary>
        public static async Task<bool> RunAsync(
            Func<Task<bool>> attempt,
            string operation,
            string tableName,
            string target,
            StorageMetrics? metrics,
            ILogger logger,
            int retries = DefaultRetries)
        {
            for (var round = 1; round <= retries; round++)
            {
                try
                {
                    return await attempt().ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (ex.Status == 412 || StorageErrors.IsAlreadyExists(ex))
                {
                    var exhausted = round == retries;
                    metrics?.CasConflict(operation, tableName, exhausted ? CasOutcome.Exhausted : CasOutcome.Retried);
                    if (exhausted)
                    {
                        logger.LogWarning(
                            "{Operation} on {Table} {Target} lost the CAS race {Retries} times — giving up (status {Status})",
                            operation, tableName, target, retries, ex.Status);
                        return false;
                    }

                    var delay = 50 * round;
                    await Task.Delay(delay + Random.Shared.Next(0, delay)).ConfigureAwait(false);
                }
            }

            return false;
        }
    }
}
