using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// SoftwareInventory side-row + counter helpers (Plan §17). All methods are fail-loud per
    /// memory <c>feedback_storage_helpers_fail_soft</c> — non-404 storage errors propagate;
    /// bounded retry exhaustion (10 attempts / 60s wall-clock per §12-Q10) throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public partial class TableStorageService
    {
        // Bounded-retry budget for every ETag-CAS / AddEntity-race loop in this file.
        // §12-Q10: 10 attempts OR 60s wall-clock, whichever first → poison.
        private const int InventoryCasMaxAttempts = 10;
        private static readonly TimeSpan InventoryCasMaxWallClock = TimeSpan.FromSeconds(60);

        // ============================================================ SessionInventoryContributions ====

        /// <summary>
        /// Reads the side-row by (tenantId, sessionId). Returns null on 404 (pre-side-row session
        /// or never written). Fail-loud on any other storage error.
        /// </summary>
        public async Task<IReadOnlyList<DeletionDecrementKey>?> GetSessionInventoryContributionsAsync(
            string tenantId, string sessionId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionInventoryContributions);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: ct);
                var encoded = response.Value.GetString("SoftwareKeysJson") ?? string.Empty;
                var compressed = response.Value.GetBoolean("IsCompressed") ?? false;
                return SoftwareKeysJsonCodec.Decode(encoded, compressed);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        /// <summary>
        /// Direct delete with 404-ignore. Cascade tombstone calls this after consuming the
        /// side-row's keys for the decrement step (Plan §17.5).
        /// </summary>
        public async Task DeleteSessionInventoryContributionsAsync(
            string tenantId, string sessionId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionInventoryContributions);
            try
            {
                await tableClient.DeleteEntityAsync(tenantId, sessionId, ETag.All, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already gone — idempotent.
            }
        }

        /// <summary>
        /// At-most-once-per-session, delta-update upsert. Plan §17.3 algorithm:
        /// <list type="number">
        ///   <item>Read existing side-row.</item>
        ///   <item>Compute newKeys = items.Select(ToDecrementKey).</item>
        ///   <item>If no side-row: AddEntity (full payload). On 409 → race lost, restart loop.</item>
        ///   <item>If side-row exists: decode old keys, compute (added, removed). If both empty: no-op return.
        ///         Otherwise UpdateEntity with ETag-Replace. On 412 → restart loop with fresh read.</item>
        /// </list>
        /// Bounded retry exhausted → throw <see cref="InvalidOperationException"/>.
        /// </summary>
        public async Task<InventoryContributionsDelta> UpsertSessionInventoryContributionsAsync(
            string tenantId, string sessionId,
            IReadOnlyList<SoftwareInventoryItem> newItems,
            CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            if (newItems == null) throw new ArgumentNullException(nameof(newItems));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionInventoryContributions);

            // Deduplicate by composite key — upstream may pass the same normalization twice.
            var newKeys = new Dictionary<string, SoftwareInventoryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in newItems)
            {
                if (string.IsNullOrEmpty(item.NormalizedVendor)
                    && string.IsNullOrEmpty(item.NormalizedName)
                    && string.IsNullOrEmpty(item.NormalizedVersion))
                {
                    continue; // skip empty keys; matches existing UpsertSoftwareInventory dedup
                }
                var composite = SoftwareKeysJsonCodec.CompositeKey(item.NormalizedVendor, item.NormalizedName, item.NormalizedVersion);
                newKeys[composite] = item;
            }
            var newKeyList = newKeys.Values.Select(i => i.ToDecrementKey()).ToList();

            var deadline = DateTime.UtcNow + InventoryCasMaxWallClock;
            for (var attempt = 0; attempt < InventoryCasMaxAttempts && DateTime.UtcNow < deadline; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                TableEntity? existing = null;
                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: ct);
                    existing = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // First-time write path.
                }

                var encoded = SoftwareKeysJsonCodec.Encode(newKeyList);
                var now = DateTime.UtcNow;

                if (existing == null)
                {
                    var row = new TableEntity(tenantId, sessionId)
                    {
                        ["SoftwareKeysJson"] = encoded.Encoded,
                        ["IsCompressed"]     = encoded.IsCompressed,
                        ["CountedAt"]        = now,
                        ["LastUpdatedAt"]    = now,
                        ["KeyCount"]         = encoded.KeyCount,
                    };
                    try
                    {
                        await tableClient.AddEntityAsync(row, ct);
                        return new InventoryContributionsDelta
                        {
                            AddedItems = newKeys.Values.ToList(),
                            RemovedKeys = new List<DeletionDecrementKey>(),
                            FirstTime = true,
                        };
                    }
                    catch (RequestFailedException ex) when (ex.Status == 409)
                    {
                        continue; // race lost — another writer beat us to first-insert
                    }
                }
                else
                {
                    var oldEncoded = existing.GetString("SoftwareKeysJson") ?? string.Empty;
                    var oldCompressed = existing.GetBoolean("IsCompressed") ?? false;
                    var oldKeys = SoftwareKeysJsonCodec.Decode(oldEncoded, oldCompressed);

                    var (added, removed) = SoftwareKeysJsonCodec.ComputeDelta(oldKeys, newKeyList);
                    if (added.Count == 0 && removed.Count == 0)
                    {
                        return new InventoryContributionsDelta
                        {
                            AddedItems = new List<SoftwareInventoryItem>(),
                            RemovedKeys = new List<DeletionDecrementKey>(),
                            FirstTime = false,
                        };
                    }

                    existing["SoftwareKeysJson"] = encoded.Encoded;
                    existing["IsCompressed"]     = encoded.IsCompressed;
                    existing["LastUpdatedAt"]    = now;
                    existing["KeyCount"]         = encoded.KeyCount;

                    try
                    {
                        await tableClient.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace, ct);
                        var addedItems = newKeys
                            .Where(kv => added.Contains(kv.Key))
                            .Select(kv => kv.Value)
                            .ToList();
                        var removedKeys = oldKeys
                            .Where(k => removed.Contains(SoftwareKeysJsonCodec.CompositeKey(k)))
                            .ToList();
                        return new InventoryContributionsDelta
                        {
                            AddedItems = addedItems,
                            RemovedKeys = removedKeys,
                            FirstTime = false,
                        };
                    }
                    catch (RequestFailedException ex) when (ex.Status == 412)
                    {
                        continue; // ETag conflict — re-read on next attempt
                    }
                }
            }

            throw new InvalidOperationException(
                $"SessionInventoryContributions ETag-CAS exhausted after {InventoryCasMaxAttempts} attempts or "
                + $"{InventoryCasMaxWallClock.TotalSeconds:0}s wall-clock. tenant={tenantId} session={sessionId}");
        }

        // ============================================================ SoftwareInventory counters ====

        /// <summary>
        /// ETag-CAS <c>SessionCount += 1</c> for the (tenantId, normalizedKey) row, creating the
        /// row with full <paramref name="item"/> metadata on first encounter. Bounded retry per
        /// <see cref="InventoryCasMaxAttempts"/> / <see cref="InventoryCasMaxWallClock"/>.
        /// </summary>
        public async Task IncrementSoftwareInventoryEntryAsync(
            string tenantId, SoftwareInventoryItem item, string sessionId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (item == null) throw new ArgumentNullException(nameof(item));

            var rowKey = BuildSoftwareInventoryRowKey(item.NormalizedVendor, item.NormalizedName, item.NormalizedVersion);
            if (string.IsNullOrWhiteSpace(rowKey) || rowKey == "::") return; // skip empty triples — matches existing UpsertSoftwareInventory dedup

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SoftwareInventory);
            var deadline = DateTime.UtcNow + InventoryCasMaxWallClock;

            for (var attempt = 0; attempt < InventoryCasMaxAttempts && DateTime.UtcNow < deadline; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var nowIso = DateTime.UtcNow.ToString("o");

                TableEntity? existing = null;
                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, rowKey, cancellationToken: ct);
                    existing = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // First-time create path.
                }

                if (existing == null)
                {
                    var row = new TableEntity(tenantId, rowKey)
                    {
                        ["DisplayName"]            = item.DisplayName,
                        ["NormalizedName"]         = item.NormalizedName,
                        ["NormalizedVendor"]       = item.NormalizedVendor,
                        ["NormalizedVersion"]      = item.NormalizedVersion,
                        ["Publisher"]              = item.Publisher,
                        ["RegistrySource"]         = item.RegistrySource,
                        ["NormalizationConfidence"] = item.NormalizationConfidence,
                        ["FirstSeenAt"]            = nowIso,
                        ["LastSeenAt"]             = nowIso,
                        ["FirstSessionId"]         = sessionId,
                        ["LastSessionId"]          = sessionId,
                        ["SessionCount"]           = 1,
                        ["CpeUri"]                 = item.CpeUri ?? string.Empty,
                    };
                    try
                    {
                        await tableClient.AddEntityAsync(row, ct);
                        return;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 409)
                    {
                        continue; // race lost — another writer created it; reread + increment
                    }
                }
                else
                {
                    existing["SessionCount"]  = (existing.GetInt32("SessionCount") ?? 0) + 1;
                    existing["LastSeenAt"]    = nowIso;
                    existing["LastSessionId"] = sessionId;
                    // Stamp a CpeUri if we just got one and the row didn't have one yet — preserves
                    // existing behaviour from UpsertSoftwareInventoryAsync.
                    if (!string.IsNullOrEmpty(item.CpeUri) && string.IsNullOrEmpty(existing.GetString("CpeUri")))
                    {
                        existing["CpeUri"] = item.CpeUri;
                    }

                    try
                    {
                        await tableClient.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace, ct);
                        return;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 412)
                    {
                        continue;
                    }
                }
            }

            throw new InvalidOperationException(
                $"SoftwareInventory increment ETag-CAS exhausted after {InventoryCasMaxAttempts} attempts or "
                + $"{InventoryCasMaxWallClock.TotalSeconds:0}s wall-clock. tenant={tenantId} key={rowKey}");
        }

        /// <summary>
        /// ETag-CAS <c>SessionCount -= 1</c> with clamp at zero. Deletes the row when count hits
        /// zero. 404 = idempotent no-op. Bounded retry per <see cref="InventoryCasMaxAttempts"/> /
        /// <see cref="InventoryCasMaxWallClock"/>; throws on exhaustion.
        /// </summary>
        public virtual async Task DecrementSoftwareInventoryEntryAsync(
            string tenantId, string normalizedVendor, string normalizedName, string normalizedVersion,
            CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            var rowKey = BuildSoftwareInventoryRowKey(normalizedVendor, normalizedName, normalizedVersion);
            if (string.IsNullOrWhiteSpace(rowKey) || rowKey == "::") return;

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SoftwareInventory);
            var deadline = DateTime.UtcNow + InventoryCasMaxWallClock;

            for (var attempt = 0; attempt < InventoryCasMaxAttempts && DateTime.UtcNow < deadline; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                TableEntity existing;
                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, rowKey, cancellationToken: ct);
                    existing = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return; // already gone — decrement is idempotent
                }

                var newCount = Math.Max(0, (existing.GetInt32("SessionCount") ?? 0) - 1);
                if (newCount == 0)
                {
                    try
                    {
                        await tableClient.DeleteEntityAsync(tenantId, rowKey, existing.ETag, ct);
                        return;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 412)
                    {
                        continue;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        return;
                    }
                }

                existing["SessionCount"] = newCount;
                existing["LastSeenAt"]   = DateTime.UtcNow.ToString("o");
                try
                {
                    await tableClient.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace, ct);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    continue;
                }
            }

            throw new InvalidOperationException(
                $"SoftwareInventory decrement ETag-CAS exhausted after {InventoryCasMaxAttempts} attempts or "
                + $"{InventoryCasMaxWallClock.TotalSeconds:0}s wall-clock. tenant={tenantId} key={rowKey}");
        }

        /// <summary>
        /// Re-increments a SoftwareInventory counter from a <see cref="DeletionDecrementKey"/>
        /// (vendor, name, version only — no DisplayName/Publisher). Symmetric inverse of
        /// <see cref="DecrementSoftwareInventoryEntryAsync"/>; used by the cascade-delete restore
        /// path (PR4b plan §13.6) where the manifest's side-row only carries the decrement keys.
        /// <para>
        /// If the inventory row exists → ETag-CAS <c>SessionCount += 1</c>, preserving DisplayName,
        /// Publisher, and every other column untouched. If the row is missing (count had dropped
        /// to zero and the cascade deleted it) → AddEntity with vendor/name/version + count=1 +
        /// FirstSeenAt + LastSeenAt + empty DisplayName/Publisher. The fidelity loss on display
        /// metadata is accepted per plan §13.6 ("re-creates the inventory row with SessionCount=1");
        /// the next live inventory-correlation will fill in DisplayName + Publisher.
        /// </para>
        /// Bounded retry per <see cref="InventoryCasMaxAttempts"/> / <see cref="InventoryCasMaxWallClock"/>;
        /// throws on exhaustion per memory <c>feedback_storage_helpers_fail_soft</c>.
        /// </summary>
        public virtual async Task RestoreSoftwareInventoryEntryByKeyAsync(
            string tenantId, DeletionDecrementKey key, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (key == null) throw new ArgumentNullException(nameof(key));
            var rowKey = BuildSoftwareInventoryRowKey(key.Vendor, key.Name, key.Version);
            if (string.IsNullOrWhiteSpace(rowKey) || rowKey == "::")
            {
                throw new ArgumentException(
                    "DeletionDecrementKey has empty vendor/name/version — cannot restore a SoftwareInventory entry with no identity.",
                    nameof(key));
            }

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SoftwareInventory);
            var deadline = DateTime.UtcNow + InventoryCasMaxWallClock;

            for (var attempt = 0; attempt < InventoryCasMaxAttempts && DateTime.UtcNow < deadline; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                TableEntity existing;
                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, rowKey, cancellationToken: ct);
                    existing = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Row was deleted by cascade (count reached zero) — re-create with minimal data.
                    var now = DateTime.UtcNow;
                    var fresh = new TableEntity(tenantId, rowKey)
                    {
                        ["NormalizedVendor"] = key.Vendor ?? string.Empty,
                        ["NormalizedName"] = key.Name ?? string.Empty,
                        ["NormalizedVersion"] = key.Version ?? string.Empty,
                        ["SessionCount"] = 1,
                        ["FirstSeenAt"] = now.ToString("o"),
                        ["LastSeenAt"] = now.ToString("o"),
                    };
                    try
                    {
                        await tableClient.AddEntityAsync(fresh, ct);
                        return;
                    }
                    catch (RequestFailedException rfe) when (rfe.Status == 409)
                    {
                        // Race: another writer created the row between our Get and our Add — retry.
                        continue;
                    }
                }

                existing["SessionCount"] = (existing.GetInt32("SessionCount") ?? 0) + 1;
                existing["LastSeenAt"] = DateTime.UtcNow.ToString("o");
                try
                {
                    await tableClient.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Replace, ct);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    continue;
                }
            }

            throw new InvalidOperationException(
                $"SoftwareInventory restore ETag-CAS exhausted after {InventoryCasMaxAttempts} attempts or "
                + $"{InventoryCasMaxWallClock.TotalSeconds:0}s wall-clock. tenant={tenantId} key={rowKey}");
        }

        // ============================================================ Helpers ====

        /// <summary>
        /// SoftwareInventory RowKey format: <c>vendor:name:version</c> through
        /// <see cref="TableKeySanitizer"/>, capped at 512 characters.
        /// </summary>
        private static string BuildSoftwareInventoryRowKey(string vendor, string name, string version)
        {
            var raw = $"{vendor ?? string.Empty}:{name ?? string.Empty}:{version ?? string.Empty}";
            var sanitized = TableKeySanitizer.Sanitize(raw);
            return sanitized.Length > 512 ? sanitized.Substring(0, 512) : sanitized;
        }
    }
}
