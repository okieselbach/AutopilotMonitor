using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Azure;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Heavy, I/O-bound validation that has to run before any restore write touches
    /// the live table. Performs:
    /// <list type="number">
    ///   <item>Manifest exists + parses (plan §Wave11 #4)</item>
    ///   <item>Manifest schema version is supported</item>
    ///   <item>Entry for the requested <c>tableName</c> exists with <c>Status ∈ {Ok, Empty}</c></item>
    ///   <item>Entry's <c>BlobName</c> matches the canonical <c>{backupId}/{tableName}.ndjson</c></item>
    ///   <item><b>ETag-pin + SHA in a single stream read</b> (plan §Wave12 #3):
    ///     GetProperties → pin ETag → OpenReadAsync(IfMatch=etag) → SHA-256 over that stream.
    ///     Mismatch → <c>IntegrityCheckFailed</c>. Race on the blob → <c>BlobChangedSinceValidation</c>.</item>
    /// </list>
    /// On success returns the parsed manifest, the matching entry, and the pinned
    /// ETag — the restore service uses the SAME ETag for the line-scan read so the
    /// hash and the scan see byte-identical bytes.
    /// </summary>
    public sealed class BackupRestoreInputValidator
    {
        private const int SupportedSchemaVersion = 1;

        private readonly BlobBackupStore _store;
        private readonly ILogger<BackupRestoreInputValidator> _logger;

        public BackupRestoreInputValidator(BlobBackupStore store, ILogger<BackupRestoreInputValidator> logger)
        {
            _store = store;
            _logger = logger;
        }

        /// <summary>
        /// Result of a successful validation. The pinned ETag is returned so the
        /// restore service can OpenReadAsync the SAME blob version it was just
        /// hashed against.
        /// </summary>
        public sealed class ValidationResult
        {
            public CriticalTableBackupManifest Manifest { get; init; } = default!;
            public CriticalTableBackupTableEntry Entry { get; init; } = default!;
            public ETag BlobETag { get; init; }
        }

        /// <summary>
        /// Runs all checks. Throws <see cref="BackupTerminalException"/> on the
        /// first failure with a stable error code suitable for the HTTP envelope.
        /// </summary>
        public async Task<ValidationResult> ValidateAsync(string backupId, string tableName, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(backupId)) throw new BackupTerminalException(Constants.BackupErrorCodes.InvalidBackupId, "backupId is required");
            if (string.IsNullOrEmpty(tableName)) throw new BackupTerminalException(Constants.BackupErrorCodes.InvalidTable, "tableName is required");

            // 1. Manifest exists + parseable
            var (payload, _) = await _store.ReadManifestAsync(backupId, ct).ConfigureAwait(false);
            if (payload == null)
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.BackupNotFound, $"manifest for backupId '{backupId}' was not found");
            }

            CriticalTableBackupManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<CriticalTableBackupManifest>(payload, BackupManifestJson.SerializerOptions)
                    ?? throw new BackupTerminalException(Constants.BackupErrorCodes.ManifestCorrupt, "manifest deserialised to null");
            }
            catch (BackupTerminalException) { throw; }
            catch (Exception ex)
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.ManifestCorrupt, $"manifest JSON parse failed: {ex.Message}", ex);
            }

            if (manifest.SchemaVersion != SupportedSchemaVersion)
            {
                throw new BackupTerminalException(
                    Constants.BackupErrorCodes.ManifestSchemaUnsupported,
                    $"manifest schemaVersion={manifest.SchemaVersion} not supported by this restore endpoint (expected {SupportedSchemaVersion})");
            }

            // 2. Per-table entry exists + Status is restorable
            var entry = manifest.Tables.FirstOrDefault(t => string.Equals(t.TableName, tableName, StringComparison.Ordinal));
            if (entry == null)
            {
                throw new BackupTerminalException(
                    Constants.BackupErrorCodes.TableNotInBackup,
                    $"no entry for tableName '{tableName}' in manifest {backupId}");
            }
            if (entry.Status != TableBackupStatus.Ok && entry.Status != TableBackupStatus.Empty)
            {
                throw new BackupTerminalException(
                    Constants.BackupErrorCodes.TableNotInBackup,
                    $"manifest entry for '{tableName}' has Status={entry.Status} — restore is only allowed from Ok or Empty entries");
            }

            // 3. Canonical BlobName invariant
            var expectedBlobName = BlobBackupStore.BuildNdjsonBlobName(backupId, tableName);
            if (!string.Equals(entry.BlobName, expectedBlobName, StringComparison.Ordinal))
            {
                throw new BackupTerminalException(
                    Constants.BackupErrorCodes.ManifestCorrupt,
                    $"manifest entry BlobName='{entry.BlobName}' does not match canonical '{expectedBlobName}'");
            }

            // 4. ETag-pin + SHA in a single stream read
            var openResult = await _store.OpenNdjsonReadAsync(backupId, tableName, ct).ConfigureAwait(false);
            if (openResult == null)
            {
                throw new BackupTerminalException(
                    Constants.BackupErrorCodes.BackupNotFound,
                    $"NDJSON blob '{expectedBlobName}' is missing despite manifest entry present — orphan manifest");
            }

            var (stream, blobETag) = openResult.Value;
            string actualSha;
            try
            {
                using (stream)
                using (var sha = SHA256.Create())
                {
                    var hashBytes = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
                    actualSha = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // The IfMatch precondition can also fire mid-stream if the blob was
                // overwritten between OpenRead and the final byte. Same recovery as
                // OpenNdjsonReadAsync's 412 path.
                throw new BackupTerminalException(Constants.BackupErrorCodes.BlobChangedSinceValidation, "NDJSON blob changed during SHA verification", ex);
            }

            var expectedSha = (entry.Sha256Hex ?? string.Empty).ToLowerInvariant();
            if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "BackupRestoreInputValidator: SHA mismatch for backupId={BackupId} table={Table} (expected={Expected}, actual={Actual}) — refusing restore",
                    backupId, tableName, expectedSha, actualSha);
                throw new BackupTerminalException(
                    Constants.BackupErrorCodes.IntegrityCheckFailed,
                    $"SHA-256 mismatch for table '{tableName}' (expected {expectedSha}, got {actualSha}) — backup may be tampered or partially written");
            }

            return new ValidationResult
            {
                Manifest = manifest,
                Entry = entry,
                BlobETag = blobETag,
            };
        }
    }
}
