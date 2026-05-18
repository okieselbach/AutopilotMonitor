using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Four-step verified wipe pattern used by the tenant-offboarding worker.
    /// <list type="number">
    /// <item><b>Fetch</b> rows matching a tenant-anchored filter (PK or TenantId-Property).</item>
    /// <item><b>Verify</b> each fetched row matches the expected anchor server-side AND client-side.</item>
    /// <item><b>Delete</b> via batched <c>SubmitTransactionAsync</c> with <c>ETag.All</c>; per-row
    ///     404-fallback so the helper stays idempotent across crash/resume.</item>
    /// <item><b>Audit</b> the deleted/already-missing counts to the caller's logger.</item>
    /// </list>
    /// Verify mismatch → <see cref="SafeWipeVerificationException"/> before any delete fires.
    /// </summary>
    public class SafeWipeService
    {
        private const int BatchActionLimit = 100;

        private readonly TableStorageService _tables;
        private readonly BlobStorageService _blobs;
        private readonly ILogger<SafeWipeService> _logger;

        public SafeWipeService(TableStorageService tables, BlobStorageService blobs, ILogger<SafeWipeService> logger)
        {
            _tables = tables;
            _blobs = blobs;
            _logger = logger;
        }

        /// <summary>
        /// Variant A (exact tenant PartitionKey). Used for tables where rows live under
        /// <c>PartitionKey == normalizedTenantId</c> (AuditLogs, UsageMetrics, …).
        /// </summary>
        public virtual Task<int> WipeByExactPartitionAsync(
            string tableName, string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            var filter = OffboardingFilters.ExactPartition(normalizedTenantId);

            return RunFetchVerifyDeleteAsync(
                tableName,
                filter,
                expectedAnchor: normalizedTenantId,
                verifyRow: e => string.Equals(e.PartitionKey, normalizedTenantId, StringComparison.Ordinal),
                ct);
        }

        /// <summary>
        /// Variant A range (composite tenant PartitionKey). Used for tables where rows live
        /// under <c>{tenantId}_{sessionId|discriminator}</c>.
        /// </summary>
        public virtual Task<int> WipeByCompositePartitionRangeAsync(
            string tableName, string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            var prefix = normalizedTenantId + "_";
            var filter = OffboardingFilters.CompositePartitionRange(normalizedTenantId);

            return RunFetchVerifyDeleteAsync(
                tableName,
                filter,
                expectedAnchor: prefix,
                verifyRow: e => e.PartitionKey.StartsWith(prefix, StringComparison.Ordinal),
                ct);
        }

        /// <summary>
        /// Variant B (discriminator-PK + TenantId-Property). Used for tables where rows share
        /// a fixed string PartitionKey (e.g. "CodeLookup", "reports", "Feedback") and only the
        /// <c>TenantId</c> property identifies tenant ownership. Verify-step MUST check the
        /// property because the PK is non-anchored.
        /// </summary>
        public virtual Task<int> WipeByDiscriminatorAndTenantPropertyAsync(
            string tableName, string discriminator, string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            if (string.IsNullOrEmpty(discriminator)) throw new ArgumentException("Discriminator required", nameof(discriminator));

            var filter = OffboardingFilters.DiscriminatorWithTenantProp(discriminator, normalizedTenantId);
            var expectedAnchor = $"{discriminator}+TenantId={normalizedTenantId}";

            return RunFetchVerifyDeleteAsync(
                tableName,
                filter,
                expectedAnchor: expectedAnchor,
                verifyRow: e => string.Equals(e.PartitionKey, discriminator, StringComparison.Ordinal)
                                && string.Equals(e.GetString("TenantId"), normalizedTenantId, StringComparison.Ordinal),
                ct);
        }

        /// <summary>
        /// Variant C (Property-only full-table filter). Reserved for low-volume tables where
        /// PartitionKey is not tenant-anchored (e.g. <c>UserUsageLog</c> keyed by userOid).
        /// Full-table scan — DO NOT use on high-volume tables without consideration.
        /// </summary>
        public virtual Task<int> WipeByTenantIdPropertyAsync(
            string tableName, string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            var filter = OffboardingFilters.TenantIdProperty(normalizedTenantId);

            return RunFetchVerifyDeleteAsync(
                tableName,
                filter,
                expectedAnchor: $"TenantId={normalizedTenantId}",
                verifyRow: e => string.Equals(e.GetString("TenantId"), normalizedTenantId, StringComparison.Ordinal),
                ct);
        }

        /// <summary>
        /// Blob-prefix wipe. Lists blobs under <c>{normalizedTenantId}/</c> in the named
        /// container, verifies each returned name starts with the prefix, then deletes
        /// per-blob (DeleteIfExistsAsync) so the helper stays idempotent across crash/resume.
        /// Mismatch → abort before any delete. Returns the number of blobs deleted.
        /// </summary>
        public virtual async Task<int> WipeBlobsByTenantPrefixAsync(
            string containerName, string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));
            if (string.IsNullOrEmpty(containerName)) throw new ArgumentException("Container required", nameof(containerName));

            var prefix = normalizedTenantId + "/";
            var container = _blobs.GetContainerClient(containerName);

            var collected = new List<string>();
            await foreach (var blob in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None,
                Azure.Storage.Blobs.Models.BlobStates.None,
                prefix: prefix,
                cancellationToken: ct))
            {
                collected.Add(blob.Name);
            }

            if (collected.Count == 0)
            {
                _logger.LogInformation(
                    "SafeWipe blobs '{Container}' tenant={Tenant}: no blobs found under '{Prefix}'",
                    containerName, normalizedTenantId, prefix);
                return 0;
            }

            var mismatches = collected
                .Where(name => !name.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            if (mismatches.Count > 0)
            {
                _logger.LogCritical(
                    "SafeWipe blobs '{Container}' tenant={Tenant}: aborting — {Total} blob(s) listed but {Bad} do not start with '{Prefix}'. First bad: '{Sample}'",
                    containerName, normalizedTenantId, collected.Count, mismatches.Count, prefix, mismatches[0]);
                throw new SafeWipeVerificationException(containerName, prefix, mismatches.Count);
            }

            int deleted = 0, missing = 0;
            foreach (var name in collected)
            {
                ct.ThrowIfCancellationRequested();
                var blobClient = container.GetBlobClient(name);
                var resp = await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
                if (resp.Value) deleted++;
                else missing++;
            }

            _logger.LogInformation(
                "SafeWipe blobs '{Container}' tenant={Tenant}: deleted {Deleted}, already-missing {Missing}",
                containerName, normalizedTenantId, deleted, missing);
            return deleted;
        }

        // ── Internal pipeline ───────────────────────────────────────────────────

        private async Task<int> RunFetchVerifyDeleteAsync(
            string tableName,
            string filter,
            string expectedAnchor,
            Func<TableEntity, bool> verifyRow,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("tableName required", nameof(tableName));

            var tableClient = _tables.GetTableClient(tableName);

            // STEP 1 — Fetch keys + the TenantId property if any verify-rule needs it.
            // We pull "PartitionKey", "RowKey", "TenantId" so Variant B/C verify works.
            var fetched = new List<TableEntity>();
            await foreach (var e in tableClient.QueryAsync<TableEntity>(
                filter,
                select: new[] { "PartitionKey", "RowKey", "TenantId" },
                cancellationToken: ct))
            {
                fetched.Add(e);
            }

            if (fetched.Count == 0)
            {
                _logger.LogDebug(
                    "SafeWipe '{Table}' anchor={Anchor}: 0 rows matched filter",
                    tableName, expectedAnchor);
                return 0;
            }

            // STEP 2 — Verify every row matches the expected anchor.
            var mismatches = fetched.Where(e => !verifyRow(e)).ToList();
            if (mismatches.Count > 0)
            {
                var sample = mismatches.First();
                _logger.LogCritical(
                    "SafeWipe '{Table}' anchor={Anchor}: aborting — {Total} fetched, {Bad} fail verify. " +
                    "Sample bad row: PK='{Pk}' RK='{Rk}' TenantId='{Tid}'",
                    tableName, expectedAnchor, fetched.Count, mismatches.Count,
                    sample.PartitionKey, sample.RowKey, sample.GetString("TenantId") ?? "<null>");
                throw new SafeWipeVerificationException(tableName, expectedAnchor, mismatches.Count);
            }

            // STEP 3 — Delete in batches, grouped by PartitionKey (transaction requirement).
            int deleted = 0, alreadyMissing = 0;
            var groupedByPk = fetched
                .GroupBy(e => e.PartitionKey, StringComparer.Ordinal)
                .ToList();

            foreach (var group in groupedByPk)
            {
                var rows = group.ToList();
                for (int i = 0; i < rows.Count; i += BatchActionLimit)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = rows.Skip(i).Take(BatchActionLimit).ToList();
                    var actions = chunk
                        .Select(e => new TableTransactionAction(
                            TableTransactionActionType.Delete,
                            new TableEntity(e.PartitionKey, e.RowKey) { ETag = ETag.All }))
                        .ToList();

                    try
                    {
                        await tableClient.SubmitTransactionAsync(actions, ct);
                        deleted += chunk.Count;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        // Azure rolls the whole transaction back on a single 404; degrade to per-row.
                        foreach (var entity in chunk)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                await tableClient.DeleteEntityAsync(
                                    entity.PartitionKey, entity.RowKey, ETag.All, ct);
                                deleted++;
                            }
                            catch (RequestFailedException rfe) when (rfe.Status == 404)
                            {
                                alreadyMissing++;
                            }
                        }
                    }
                }
            }

            // STEP 4 — Audit.
            _logger.LogInformation(
                "SafeWipe '{Table}' anchor={Anchor}: deleted {Deleted}, already-missing {Missing}",
                tableName, expectedAnchor, deleted, alreadyMissing);

            return deleted;
        }
    }
}
