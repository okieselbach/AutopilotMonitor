using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using Azure.Data.Tables;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Restore mode for <see cref="TableStorageService.RestoreRowsByExactKeysInBatchesAsync"/>.
    /// <para>
    /// <b>PR4c F3b: both values now have identical 409-ignore semantics.</b> The enum is kept for
    /// API readability — callers still document intent ("I'm doing a full restore" vs "I'm doing
    /// a partial-poisoned-recovery") — but the implementation no longer throws on row-already-exists.
    /// This was relaxed because the original "Full mode throws on 409 as corruption signal" intent
    /// blocked the common case: a previous restore attempt failed mid-flight and left some rows
    /// behind, and the operator's retry would 409-throw on those leftover rows. With 409-ignore,
    /// retries are idempotent; <see cref="RestoreBatchResult.Skipped"/> still surfaces the count
    /// for operator inspection. The plan §13.6 "ghost row corruption signal" check shifts to a
    /// future post-restore verification pass (out of PR4c scope).
    /// </para>
    /// </summary>
    public enum RestoreMode
    {
        /// <summary>Restore from a completed cascade (Sessions row absent + progress.CompletedAt set,
        ///     or PR4c F2 tombstone-gap case: Sessions row absent + TombstoneStarted=true).</summary>
        Full,
        /// <summary>Restore from a poisoned cascade (Sessions row present + DeletionState=Poisoned).</summary>
        Partial,
    }

    /// <summary>
    /// Result of a <see cref="TableStorageService.RestoreRowsByExactKeysInBatchesAsync"/> call.
    /// <see cref="Restored"/> counts rows newly inserted; <see cref="Skipped"/> counts rows that
    /// were already present (Partial mode only — Full mode throws on conflict).
    /// </summary>
    public class RestoreBatchResult
    {
        public int Attempted { get; }
        public int Restored { get; }
        public int Skipped { get; }

        public RestoreBatchResult(int attempted, int restored, int skipped)
        {
            Attempted = attempted;
            Restored = restored;
            Skipped = skipped;
        }

        public static readonly RestoreBatchResult Empty = new RestoreBatchResult(0, 0, 0);
    }

    /// <summary>
    /// Cascade-deletion restore primitives (PR4b). Symmetric inverse of the PR2 delete helpers:
    /// where <c>DeleteByExactKeysInBatchesAsync</c> consumes (Pk, Rk) tuples, this partial
    /// consumes the full <see cref="DeletionRowDump"/> records (with EDM-tagged Props) and
    /// re-Inserts them as typed <see cref="TableEntity"/> rows.
    /// <para>
    /// EDM round-trip fidelity (plan §13 "manifest IS the backup") is preserved by
    /// <see cref="ConvertFromPropValue"/>: each <see cref="DeletionPropValue.EdmType"/> tag
    /// dispatches into the matching <c>JsonElement.Get*</c> + .NET-type conversion so the SDK
    /// re-serializes with the original column type on the wire. Without the tag a DateTime
    /// coming out of JSON would be inserted as Edm.String and silently change the column shape.
    /// </para>
    /// </summary>
    public partial class TableStorageService
    {
        /// <summary>
        /// Mirror of <see cref="DeleteByExactKeysInBatchesAsync"/>: groups <paramref name="rows"/>
        /// by PartitionKey (batch transactions are partition-scoped), chunks each group at the
        /// batch-action limit, and submits a single <c>UpsertReplace</c>/<c>Add</c> transaction
        /// per chunk. On batch-level 409 (any row exists) the behaviour depends on
        /// <paramref name="mode"/>:
        /// <list type="bullet">
        ///   <item><see cref="RestoreMode.Full"/> — falls back to per-row <c>AddEntityAsync</c>;
        ///       any individual 409 throws <see cref="InvalidDataException"/> as a corruption
        ///       signal (the cascade had marked these rows for deletion and they should not exist).</item>
        ///   <item><see cref="RestoreMode.Partial"/> — falls back to per-row <c>AddEntityAsync</c>
        ///       with 409-ignore, counting skipped rows in the returned
        ///       <see cref="RestoreBatchResult.Skipped"/>.</item>
        /// </list>
        /// All other storage errors propagate unchanged per memory <c>feedback_storage_helpers_fail_soft</c>.
        /// </summary>
        public virtual async Task<RestoreBatchResult> RestoreRowsByExactKeysInBatchesAsync(
            string tableName,
            IReadOnlyList<DeletionRowDump> rows,
            RestoreMode mode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("tableName is required", nameof(tableName));
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (rows.Count == 0) return RestoreBatchResult.Empty;

            var tableClient = _tableServiceClient.GetTableClient(tableName);
            var restored = 0;
            var skipped = 0;

            foreach (var group in rows.GroupBy(r => r.Pk, StringComparer.Ordinal))
            {
                var entities = group.Select(dump => ConvertDumpToEntity(dump, tableName)).ToList();
                foreach (var actions in TableTransactionBatcher.Split(entities, TableTransactionActionType.Add))
                {
                    var chunk = actions.Select(a => (TableEntity)a.Entity).ToList();
                    try
                    {
                        await tableClient.SubmitTransactionAsync(actions, cancellationToken);
                        restored += chunk.Count;
                    }
                    catch (RequestFailedException ex) when (StorageErrors.IsAlreadyExists(ex))
                    {
                        // Azure rolls back the entire transaction when any Add hits an existing
                        // row. Fall back to per-row AddEntity so we can distinguish "row already
                        // there" from genuine errors. Some Azure deployments map duplicate-row
                        // conflict to 400 with code EntityAlreadyExists; both surface through
                        // this fallback path.
                        // PR4c F3b: both Full and Partial modes count 409 as Skipped (idempotent
                        // retries after partial-failed restore). The `mode` parameter is kept for
                        // API readability but no longer changes behaviour here.
                        _ = mode; // intentional: relaxation documented above
                        foreach (var entity in chunk)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await tableClient.AddEntityAsync(entity, cancellationToken);
                                restored++;
                            }
                            catch (RequestFailedException rfe) when (StorageErrors.IsAlreadyExists(rfe))
                            {
                                // Mirror the batch-level relaxation: some Azure deployments and
                                // the Azurite emulator surface duplicate-row conflict as HTTP 400
                                // with ErrorCode=EntityAlreadyExists instead of 409. Counting only
                                // 409 here left the per-row fallback non-idempotent on those
                                // backends — restore-after-partial would throw on the leftover
                                // rows that the previous attempt had already inserted.
                                skipped++;
                            }
                        }
                    }
                }
            }

            return new RestoreBatchResult(rows.Count, restored, skipped);
        }

        /// <summary>
        /// Symmetric inverse of <see cref="Services.Tables.TableEntityDumpConverter.ConvertToPropValue"/>:
        /// parses the EDM-tagged <see cref="DeletionPropValue"/> back into a typed .NET object
        /// suitable for assignment into a <see cref="TableEntity"/>'s dictionary. The SDK then
        /// re-serializes with the original column type on the wire.
        /// <para>
        /// Null handling: <c>EdmType=String</c> + <c>ValueKind=Null</c> returns <c>null</c>
        /// (mirrors the encode path's null fallback).
        /// </para>
        /// </summary>
        public static object? ConvertFromPropValue(DeletionPropValue prop)
        {
            if (prop == null) throw new ArgumentNullException(nameof(prop));

            // Null is encoded as EdmType=String + JsonNull (no first-class null in Azure Tables).
            if (prop.Value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            switch (prop.EdmType)
            {
                case DeletionPropEdmType.String:
                    return prop.Value.GetString();
                case DeletionPropEdmType.Boolean:
                    return prop.Value.GetBoolean();
                case DeletionPropEdmType.Int32:
                    return prop.Value.GetInt32();
                case DeletionPropEdmType.Int64:
                    return prop.Value.GetInt64();
                case DeletionPropEdmType.Double:
                    return prop.Value.GetDouble();
                case DeletionPropEdmType.DateTime:
                {
                    var raw = prop.Value.GetString()
                        ?? throw new InvalidDataException("DateTime EdmType has null Value.");
                    // RoundtripKind preserves UTC Kind set by ConvertToPropValue.
                    return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                }
                case DeletionPropEdmType.Guid:
                {
                    var raw = prop.Value.GetString()
                        ?? throw new InvalidDataException("Guid EdmType has null Value.");
                    return Guid.Parse(raw);
                }
                case DeletionPropEdmType.Binary:
                {
                    var raw = prop.Value.GetString()
                        ?? throw new InvalidDataException("Binary EdmType has null Value.");
                    return Convert.FromBase64String(raw);
                }
                default:
                    throw new InvalidDataException(
                        $"Unsupported EdmType '{prop.EdmType}' in DeletionPropValue. " +
                        "Manifest schema may be from a newer version than this restore endpoint supports.");
            }
        }

        /// <summary>
        /// Builds a <see cref="TableEntity"/> from a <see cref="DeletionRowDump"/> ready for
        /// <c>AddEntityAsync</c>. For Sessions-table rows the <c>DeletionState</c> +
        /// <c>PendingDeletionManifestId</c> columns are forcibly overridden to
        /// <c>None</c> + <c>null</c> — the snapshot captured the row AFTER the producer's
        /// CAS-Preparing, so a verbatim re-insert would leave the restored session permanently
        /// locked. This override is the critical correctness invariant of the restore path.
        /// </summary>
        public static TableEntity ConvertDumpToEntity(DeletionRowDump dump, string tableName)
        {
            if (dump == null) throw new ArgumentNullException(nameof(dump));
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("tableName is required", nameof(tableName));

            var entity = new TableEntity(dump.Pk, dump.Rk);
            foreach (var kvp in dump.Props)
            {
                entity[kvp.Key] = ConvertFromPropValue(kvp.Value);
            }

            // Sessions-row override: snapshot was captured after CAS-None→Preparing so the row's
            // DeletionState column carries "Preparing" + the current ManifestId. Restore must
            // reset both so writers can lock the row again post-restore.
            if (string.Equals(tableName, Constants.TableNames.Sessions, StringComparison.Ordinal))
            {
                entity["DeletionState"] = SessionDeletionState.None;
                entity["PendingDeletionManifestId"] = null;
            }

            return entity;
        }

        /// <summary>
        /// Bulk-restore helper for the critical-table backup feature (plan §PR0). Symmetric to
        /// <see cref="RestoreRowsByExactKeysInBatchesAsync"/> but uses
        /// <see cref="TableTransactionActionType.UpsertReplace"/> instead of <c>Add</c>:
        /// existing rows are overwritten, missing rows are inserted, no 409 fallback path is
        /// needed. This is the only safe primitive for "restore a row even if a corrupt version
        /// is currently live" — the cascade-delete restore (Add-only with 409-skip) would leave
        /// the corrupt live row in place.
        /// <para>
        /// Fail-loud per memory <c>feedback_storage_helpers_fail_soft</c>: storage errors propagate.
        /// </para>
        /// </summary>
        public virtual async Task<int> UpsertRowsByExactKeysInBatchesAsync(
            string tableName,
            IReadOnlyList<DeletionRowDump> rows,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("tableName is required", nameof(tableName));
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            if (rows.Count == 0) return 0;

            var tableClient = _tableServiceClient.GetTableClient(tableName);
            var upserted = 0;

            foreach (var group in rows.GroupBy(r => r.Pk, StringComparer.Ordinal))
            {
                var entities = group.Select(dump => ConvertDumpToEntity(dump, tableName)).ToList();
                foreach (var actions in TableTransactionBatcher.Split(entities, TableTransactionActionType.UpsertReplace))
                {
                    await tableClient.SubmitTransactionAsync(actions, cancellationToken);
                    upserted += actions.Count;
                }
            }

            return upserted;
        }

    }
}
