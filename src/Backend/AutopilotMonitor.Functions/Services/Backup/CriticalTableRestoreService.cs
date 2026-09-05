using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using AutopilotMonitor.Shared.Models.Deletion;
using Azure;
using AutopilotMonitor.Functions.Helpers;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Single-row preview + commit primitives for the critical-table backup restore
    /// path (plan §PR2). Two phases:
    /// <list type="bullet">
    ///   <item><b>PreviewRowAsync</b> — heavy <see cref="BackupRestoreInputValidator"/>
    ///       (manifest + SHA + ETag-pin), then line-by-line scan of the same ETag-pinned
    ///       NDJSON blob for the requested (pk, rk). Reads the live row via
    ///       <c>GetEntityIfExistsAsync</c>, returns backup+current+diff+rowHash+currentETag.
    ///       <b>No write</b>, no lease — purely read-only.</item>
    ///   <item><b>CommitRowAsync</b> — acquires the maintenance lease (short-lived but
    ///       with the standard 45 s renewal loop so a slow blob fetch cannot drop it),
    ///       re-runs the validator, recomputes the row hash, re-reads the live row,
    ///       verifies <c>ifSha256</c> and <c>ifCurrentETag</c> echoed by the client, then
    ///       writes <c>UpdateEntityAsync(IfMatch=etag, Replace)</c> for existing rows or
    ///       <c>AddEntityAsync</c> for missing rows. 412/404/409 → <c>CurrentRowChanged</c>.</item>
    /// </list>
    /// <para>
    /// This service is intentionally NOT used for full-table restores — the bulk
    /// path lives in <c>UpsertRowsByExactKeysInBatchesAsync</c> and has no per-row
    /// ETag-CAS. PR2 covers only single-row; PR3/PR4 wire the bulk path.
    /// </para>
    /// </summary>
    public sealed class CriticalTableRestoreService
    {
        private readonly TableStorageService _tables;
        private readonly BlobBackupStore _store;
        private readonly BackupRestoreInputValidator _validator;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<CriticalTableRestoreService> _logger;

        public CriticalTableRestoreService(
            TableStorageService tables,
            BlobBackupStore store,
            BackupRestoreInputValidator validator,
            ILoggerFactory loggerFactory,
            ILogger<CriticalTableRestoreService> logger)
        {
            _tables = tables;
            _store = store;
            _validator = validator;
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        // ── Preview ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Heavy validate + scan + diff. No write, no lease.
        /// </summary>
        public async Task<RestoreRowPreviewResponse> PreviewRowAsync(
            string backupId,
            string tableName,
            string partitionKey,
            string rowKey,
            CancellationToken ct = default)
        {
            // 1. Heavy validate (manifest, SHA, ETag-pin).
            var validation = await _validator.ValidateAsync(backupId, tableName, ct).ConfigureAwait(false);

            // 2. Scan the SAME ETag-pinned blob version for the requested (pk, rk).
            //    We re-open the blob with IfMatch=validation.BlobETag so the scan sees
            //    byte-identical bytes to the hash. This is the central correctness
            //    invariant — without re-pinning, a concurrent overwrite could change
            //    the bytes between hash and scan.
            var openResult = await OpenAtPinnedETagAsync(backupId, tableName, validation.BlobETag, ct).ConfigureAwait(false);

            var (dump, ndjsonLineBytes) = await FindLineAsync(openResult, partitionKey, rowKey, ct).ConfigureAwait(false);
            if (dump == null)
            {
                throw new BackupTerminalException(
                    "RowNotInBackup",
                    $"row (pk='{partitionKey}', rk='{rowKey}') was not found in backup {backupId}/{tableName}");
            }

            // 3. Read the live row.
            var tableClient = _tables.GetTableClient(tableName);
            var liveResponse = await tableClient
                .GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct)
                .ConfigureAwait(false);

            TableEntity? liveEntity = liveResponse.HasValue ? liveResponse.Value : null;
            string? currentETag = liveEntity != null ? liveEntity.ETag.ToString() : null;

            // 4. Build the diff + snapshots.
            var backupSnap = ToSnapshotMap(dump);
            var currentSnap = liveEntity != null ? LiveEntityToSnapshotMap(liveEntity) : null;
            var diff = BuildDiff(backupSnap, currentSnap);

            // 5. Hash the exact NDJSON line (without trailing \n). See plan §Wave15 #5
            //    — no canonicalisation; the original NDJSON bytes are the canonical form.
            var rowSha = ComputeSha256Hex(ndjsonLineBytes);

            return new RestoreRowPreviewResponse
            {
                BackupId = backupId,
                TableName = tableName,
                PartitionKey = partitionKey,
                RowKey = rowKey,
                BackupProperties = backupSnap,
                CurrentProperties = currentSnap,
                Diff = diff,
                RowSha256 = rowSha,
                CurrentETag = currentETag,
                IsAuthTable = Constants.CriticalBackupTables.AuthTablesFullRestoreForbidden
                    .Contains(tableName, StringComparer.Ordinal),
            };
        }

        // ── Commit ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Acquires the maintenance lease (briefly), re-validates, recomputes the
        /// row hash, then runs a conditional write keyed on <paramref name="ifCurrentETag"/>.
        /// All five CurrentRowChanged paths from the plan are covered:
        /// <list type="bullet">
        ///   <item>Row existed in preview + same ETag → <c>UpdateEntity(Replace)</c></item>
        ///   <item>Row existed in preview + different ETag (412) → 409 <c>CurrentRowChanged</c></item>
        ///   <item>Row deleted between preview + commit (404) → 409 <c>CurrentRowChanged</c></item>
        ///   <item>Row missing in preview → <c>AddEntity</c></item>
        ///   <item>Row missing in preview + meanwhile created (409) → 409 <c>CurrentRowChanged</c></item>
        /// </list>
        /// </summary>
        public async Task<RestoreRowCommitResponse> CommitRowAsync(
            string backupId,
            string tableName,
            string partitionKey,
            string rowKey,
            string ifSha256,
            string? ifCurrentETag,
            CancellationToken ct = default)
        {
            // Acquire the maintenance lease BEFORE re-reading anything that could
            // race with a parallel full-table restore. Without the lease a Single-Row
            // commit could be overwritten by an in-flight bulk replace-all started
            // microseconds later (plan §Wave15 #4).
            Azure.Storage.Blobs.Specialized.BlobLeaseClient leaseClient;
            try
            {
                leaseClient = await _store.AcquireMaintenanceLeaseAsync(leaseDuration: null, ct: ct).ConfigureAwait(false);
            }
            catch (LeaseHeldException ex)
            {
                throw new BackupTerminalException(
                    "MaintenanceInProgress",
                    "another maintenance operation is in progress — please retry shortly",
                    ex);
            }

            using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var holder = new MaintenanceLeaseHolder(
                leaseClient,
                handlerCts,
                _loggerFactory.CreateLogger<MaintenanceLeaseHolder>());

            try
            {
                // Re-validate from scratch under the lease. The validator's ETag-pin
                // returns a NEW ETag for the current blob version — we use that pin
                // for the scan below (NOT the preview's, which may now be stale if a
                // parallel write+restore landed in between).
                var validation = await _validator.ValidateAsync(backupId, tableName, handlerCts.Token).ConfigureAwait(false);

                var openResult = await OpenAtPinnedETagAsync(backupId, tableName, validation.BlobETag, handlerCts.Token).ConfigureAwait(false);
                var (dump, ndjsonLineBytes) = await FindLineAsync(openResult, partitionKey, rowKey, handlerCts.Token).ConfigureAwait(false);
                if (dump == null)
                {
                    throw new BackupTerminalException(
                        "RowNotInBackup",
                        $"row (pk='{partitionKey}', rk='{rowKey}') was not found in backup {backupId}/{tableName}");
                }

                // Re-compute SHA on the fresh line bytes. If the operator's echoed
                // ifSha256 does not match, the backup has changed (or the operator
                // typed by hand) — refuse.
                var freshSha = ComputeSha256Hex(ndjsonLineBytes);
                if (!string.Equals(freshSha, (ifSha256 ?? string.Empty).ToLowerInvariant(), StringComparison.Ordinal))
                {
                    throw new BackupTerminalException(
                        "RowChangedSinceValidation",
                        $"backup row SHA-256 changed between preview and commit (preview echoed {ifSha256}, fresh {freshSha}) — re-open the preview to refresh");
                }

                // Build the entity from the dump. ConvertDumpToEntity also applies
                // the Sessions-specific reset (no-op for other tables).
                var entity = TableStorageService.ConvertDumpToEntity(dump, tableName);

                var tableClient = _tables.GetTableClient(tableName);

                if (!string.IsNullOrEmpty(ifCurrentETag))
                {
                    // Preview saw a live row; commit requires the live ETag still matches.
                    var etag = new ETag(ifCurrentETag);
                    try
                    {
                        await tableClient.UpdateEntityAsync(entity, etag, TableUpdateMode.Replace, handlerCts.Token).ConfigureAwait(false);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
                    {
                        throw new BackupTerminalException(
                            "CurrentRowChanged",
                            $"live row was modified or deleted since preview (status={ex.Status}) — re-open the preview",
                            ex);
                    }

                    return new RestoreRowCommitResponse
                    {
                        BackupId = backupId,
                        TableName = tableName,
                        PartitionKey = partitionKey,
                        RowKey = rowKey,
                        Outcome = RestoreRowCommitOutcome.Replaced,
                    };
                }
                else
                {
                    // Preview saw NO live row; commit requires the row is still absent.
                    try
                    {
                        await tableClient.AddEntityAsync(entity, handlerCts.Token).ConfigureAwait(false);
                    }
                    catch (RequestFailedException ex) when (StorageErrors.IsAlreadyExists(ex))
                    {
                        throw new BackupTerminalException(
                            "CurrentRowChanged",
                            "live row was created since preview — re-open the preview",
                            ex);
                    }

                    return new RestoreRowCommitResponse
                    {
                        BackupId = backupId,
                        TableName = tableName,
                        PartitionKey = partitionKey,
                        RowKey = rowKey,
                        Outcome = RestoreRowCommitOutcome.Inserted,
                    };
                }
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // The renewal loop cancelled handlerCts after a failed RenewAsync — the
                // external client did NOT go away (ct still alive), so this is a server-side
                // lease loss, not a client abort. Map to a stable 409 so the UI can prompt
                // the operator to retry (the row-level write may not even have started).
                // Without this guard the storage SDK's OperationCanceledException bubbles
                // up to the HTTP layer's generic catch and renders as a 500.
                var reason = holder.RenewalFailureReason ?? "handler cancelled without explicit renewal failure";
                throw new BackupTerminalException(
                    "MaintenanceLeaseLost",
                    $"maintenance lease lost during commit — retry shortly ({reason})",
                    ex);
            }
            finally
            {
                // DisposeAsync stops the renewal loop deterministically before releasing.
                try { await holder.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "CriticalTableRestoreService: lease holder dispose threw"); }
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private async Task<(Stream Stream, ETag ETag)> OpenAtPinnedETagAsync(
            string backupId, string tableName, ETag expectedETag, CancellationToken ct)
        {
            var open = await _store.OpenNdjsonReadAsync(backupId, tableName, ct).ConfigureAwait(false);
            if (open == null)
            {
                throw new BackupTerminalException(
                    "BackupNotFound",
                    $"NDJSON blob for {backupId}/{tableName}.ndjson disappeared between validation and scan");
            }
            if (!open.Value.ETag.Equals(expectedETag))
            {
                // The validator already opened+read; if the ETag changed between
                // validator and scan, the blob was overwritten.
                try { open.Value.Stream.Dispose(); } catch { /* best effort */ }
                throw new BackupTerminalException(
                    "BlobChangedSinceValidation",
                    "NDJSON blob ETag changed between validation hash and scan — refusing to read mismatching bytes");
            }
            return open.Value;
        }

        /// <summary>
        /// Scans an NDJSON stream line-by-line for the (pk, rk) match. Returns the
        /// parsed dump plus the raw line bytes (without trailing <c>\n</c>) so the
        /// caller can SHA-256 the same bytes that get echoed via <c>ifSha256</c>.
        /// </summary>
        private static async Task<(DeletionRowDump? Dump, byte[] LineBytes)> FindLineAsync(
            (Stream Stream, ETag ETag) open,
            string partitionKey,
            string rowKey,
            CancellationToken ct)
        {
            using var stream = open.Stream;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);

            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;

                // Quick reject on PK/RK substring miss before deserialising —
                // a worthwhile micro-opt because most lines won't match.
                DeletionRowDump? dump;
                try
                {
                    dump = JsonSerializer.Deserialize<DeletionRowDump>(line, BackupManifestJson.SerializerOptions);
                }
                catch (JsonException ex)
                {
                    throw new BackupTerminalException(
                        "ManifestCorrupt",
                        $"NDJSON line failed to parse as DeletionRowDump: {ex.Message}",
                        ex);
                }

                if (dump == null) continue;
                if (!string.Equals(dump.Pk, partitionKey, StringComparison.Ordinal)) continue;
                if (!string.Equals(dump.Rk, rowKey, StringComparison.Ordinal)) continue;

                var lineBytes = Encoding.UTF8.GetBytes(line);
                return (dump, lineBytes);
            }

            return (null, Array.Empty<byte>());
        }

        private static Dictionary<string, RestoreRowPropertySnapshot> ToSnapshotMap(DeletionRowDump dump)
        {
            var map = new Dictionary<string, RestoreRowPropertySnapshot>(StringComparer.Ordinal);
            foreach (var kvp in dump.Props)
            {
                map[kvp.Key] = new RestoreRowPropertySnapshot
                {
                    EdmType = kvp.Value.EdmType,
                    Value = kvp.Value.Value,
                };
            }
            return map;
        }

        private static Dictionary<string, RestoreRowPropertySnapshot> LiveEntityToSnapshotMap(TableEntity entity)
        {
            var map = new Dictionary<string, RestoreRowPropertySnapshot>(StringComparer.Ordinal);
            foreach (var kvp in entity)
            {
                // Skip Azure-system columns; PartitionKey and RowKey are top-level on
                // the dump, Timestamp + odata.etag are SDK-managed.
                if (kvp.Key == "PartitionKey" || kvp.Key == "RowKey" || kvp.Key == "Timestamp" || kvp.Key == "odata.etag")
                    continue;

                map[kvp.Key] = ConvertLiveValueToSnapshot(kvp.Value);
            }
            return map;
        }

        private static RestoreRowPropertySnapshot ConvertLiveValueToSnapshot(object? value)
        {
            // Mirror the dump's EDM tagging — we don't have full SDK type info on
            // the live row beyond CLR types, but the round-trip rules from
            // TableEntityDumpConverter give us enough fidelity for a diff UI.
            //
            // For property comparison we only need a deterministic JSON
            // representation of the value, so we hand-encode each EDM type
            // identically to how MapEntityToDump would have encoded it. That keeps
            // the diff stable: backup-vs-current of a long stays "long==long" even
            // though the live SDK already typed it as Int64.
            switch (value)
            {
                case null:
                    return MakeSnapshot(DeletionPropEdmType.String, JsonValueKind.Null, null);
                case string s:
                    return MakeSnapshot(DeletionPropEdmType.String, JsonValueKind.String, s);
                case bool b:
                    return MakeSnapshot(DeletionPropEdmType.Boolean, b ? JsonValueKind.True : JsonValueKind.False, b);
                case int i:
                    return MakeSnapshot(DeletionPropEdmType.Int32, JsonValueKind.Number, i);
                case long l:
                    return MakeSnapshot(DeletionPropEdmType.Int64, JsonValueKind.Number, l);
                case double d:
                    return MakeSnapshot(DeletionPropEdmType.Double, JsonValueKind.Number, d);
                case DateTime dt:
                    return MakeSnapshot(DeletionPropEdmType.DateTime, JsonValueKind.String,
                        dt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                case DateTimeOffset dto:
                    return MakeSnapshot(DeletionPropEdmType.DateTime, JsonValueKind.String,
                        dto.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                case Guid g:
                    return MakeSnapshot(DeletionPropEdmType.Guid, JsonValueKind.String, g.ToString());
                case byte[] bytes:
                    return MakeSnapshot(DeletionPropEdmType.Binary, JsonValueKind.String, Convert.ToBase64String(bytes));
                default:
                    return MakeSnapshot(DeletionPropEdmType.String, JsonValueKind.String, value.ToString());
            }
        }

        private static RestoreRowPropertySnapshot MakeSnapshot(string edmType, JsonValueKind kind, object? value)
        {
            // Cheap round-trip via JsonSerializer to get a normalized JsonElement —
            // the diff serializer (BackupManifestJson.SerializerOptions) re-emits it
            // verbatim, and JsonElement equality is byte-based, so this keeps the
            // diff stable across processes.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BackupManifestJson.SerializerOptions);
            using var doc = JsonDocument.Parse(bytes);
            return new RestoreRowPropertySnapshot
            {
                EdmType = edmType,
                Value = doc.RootElement.Clone(),
            };
            // 'kind' parameter retained for self-documentation at call sites.
        }

        private static List<RestoreRowPropertyDiff> BuildDiff(
            Dictionary<string, RestoreRowPropertySnapshot> backup,
            Dictionary<string, RestoreRowPropertySnapshot>? current)
        {
            var diff = new List<RestoreRowPropertyDiff>();
            var keys = new HashSet<string>(backup.Keys, StringComparer.Ordinal);
            if (current != null) keys.UnionWith(current.Keys);

            foreach (var name in keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                backup.TryGetValue(name, out var b);
                RestoreRowPropertySnapshot? c = null;
                current?.TryGetValue(name, out c);

                RestoreRowDiffKind kind;
                if (b != null && c == null) kind = current == null ? RestoreRowDiffKind.Added : RestoreRowDiffKind.Added;
                else if (b == null && c != null) kind = RestoreRowDiffKind.Removed;
                else if (b != null && c != null)
                {
                    kind = SnapshotsEqual(b, c) ? RestoreRowDiffKind.Unchanged : RestoreRowDiffKind.Changed;
                }
                else continue;

                diff.Add(new RestoreRowPropertyDiff
                {
                    Name = name,
                    Kind = kind,
                    Backup = b,
                    Current = c,
                });
            }

            return diff;
        }

        private static bool SnapshotsEqual(RestoreRowPropertySnapshot a, RestoreRowPropertySnapshot b)
        {
            if (!string.Equals(a.EdmType, b.EdmType, StringComparison.Ordinal)) return false;
            // JsonElement equality is by raw text — re-serialise both for a
            // canonical byte-compare. Both go through the same SerializerOptions so
            // ordering is stable.
            var aBytes = JsonSerializer.SerializeToUtf8Bytes(a.Value, BackupManifestJson.SerializerOptions);
            var bBytes = JsonSerializer.SerializeToUtf8Bytes(b.Value, BackupManifestJson.SerializerOptions);
            return aBytes.AsSpan().SequenceEqual(bBytes);
        }

        private static string ComputeSha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

    }
}
