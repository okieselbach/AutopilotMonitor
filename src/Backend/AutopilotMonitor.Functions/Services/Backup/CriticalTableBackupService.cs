using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Tables;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Streams all <see cref="Constants.CriticalBackupTables.All"/> tables into per-table
    /// NDJSON blobs, then writes a <c>manifest.json</c> LAST as the durability anchor.
    /// Plan §PR1.
    /// <para>
    /// LEASE OWNERSHIP boundary: this service neither acquires nor releases the
    /// maintenance lease. It assumes the caller (timer or queue worker) already holds it.
    /// The service also does not write to <c>BackupJobStatus</c> — that responsibility
    /// lives with the caller too, so the service stays Unit-testable without touching
    /// the table-storage layer.
    /// </para>
    /// </summary>
    public sealed class CriticalTableBackupService : ICriticalTableBackupService
    {
        /// <summary>Per-run wall-clock budget. 10 min safety headroom below the 60 min Function timeout.</summary>
        internal static readonly TimeSpan PerRunBudget = TimeSpan.FromMinutes(50);

        private readonly TableStorageService _tables;
        private readonly BlobBackupStore _store;
        private readonly ILogger<CriticalTableBackupService> _logger;
        private readonly TimeProvider _clock;

        public CriticalTableBackupService(
            TableStorageService tables,
            BlobBackupStore store,
            ILogger<CriticalTableBackupService> logger,
            TimeProvider? clock = null)
        {
            _tables = tables;
            _store = store;
            _logger = logger;
            _clock = clock ?? TimeProvider.System;
        }

        public string GenerateBackupId()
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var stamp = now.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
            var guidPart = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"{stamp}_{guidPart}";
        }

        public async Task<BackupRunResult> RunBackupUnderLeaseAsync(string backupId, string triggeredBy, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(backupId)) throw new ArgumentException("backupId required", nameof(backupId));
            if (string.IsNullOrEmpty(triggeredBy)) triggeredBy = "Unknown";

            var startedAtUtc = _clock.GetUtcNow().UtcDateTime;
            var deadlineUtc = startedAtUtc + PerRunBudget;

            var manifest = new CriticalTableBackupManifest
            {
                SchemaVersion = 1,
                BackupId = backupId,
                StartedAtUtc = startedAtUtc,
                TriggeredBy = triggeredBy,
            };

            var degraded = false;

            foreach (var tableName in Constants.CriticalBackupTables.All)
            {
                ct.ThrowIfCancellationRequested();

                if (_clock.GetUtcNow().UtcDateTime >= deadlineUtc)
                {
                    manifest.Tables.Add(new CriticalTableBackupTableEntry
                    {
                        TableName = tableName,
                        Status = TableBackupStatus.Skipped,
                        BlobName = BlobBackupStore.BuildNdjsonBlobName(backupId, tableName),
                        ErrorMessage = "per-run budget exceeded before table started",
                    });
                    degraded = true;
                    continue;
                }

                var entry = await DumpOneTableAsync(backupId, tableName, deadlineUtc, ct).ConfigureAwait(false);
                manifest.Tables.Add(entry);
                if (entry.Status == TableBackupStatus.Failed || entry.Status == TableBackupStatus.Skipped)
                {
                    degraded = true;
                }
            }

            manifest.CompletedAtUtc = _clock.GetUtcNow().UtcDateTime;
            manifest.Outcome = degraded ? BackupOutcome.Partial : BackupOutcome.Success;

            // Manifest LAST. If this throws, caller treats run as Failed (no JobState=Completed,
            // no BackupOutcome). Existence of manifest.json is the durability anchor.
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupManifestJson.SerializerOptions);
            var ok = await _store.TryWriteManifestAsync(backupId, manifestBytes, ct).ConfigureAwait(false);
            if (!ok)
            {
                throw new InvalidOperationException($"Manifest for backupId {backupId} already exists — collision");
            }

            return new BackupRunResult
            {
                Outcome = manifest.Outcome,
                Manifest = manifest,
                ManifestBlobName = BlobBackupStore.BuildManifestBlobName(backupId),
            };
        }

        // ── Per-table loop body ─────────────────────────────────────────────────

        private async Task<CriticalTableBackupTableEntry> DumpOneTableAsync(
            string backupId, string tableName, DateTime deadlineUtc, CancellationToken ct)
        {
            var entry = new CriticalTableBackupTableEntry
            {
                TableName = tableName,
                BlobName = BlobBackupStore.BuildNdjsonBlobName(backupId, tableName),
            };

            long rows = 0;
            long byteSize = 0;
            string sha256Hex;

            try
            {
                using var sha = SHA256.Create();
                // await using ensures the block-blob CommitBlockList runs BEFORE the next
                // table starts and BEFORE the manifest is written (plan Wave14 #3).
                await using var blobStream = await _store.OpenNdjsonWriteStreamAsync(backupId, tableName, ct).ConfigureAwait(false);
                // CountingStream tracks byte count without buffering; CryptoStream feeds the hash;
                // both wrap the actual blob stream so writes pass through each layer once.
                await using var counting = new CountingStream(blobStream);
                await using var crypto = new CryptoStream(counting, sha, CryptoStreamMode.Write, leaveOpen: true);

                await foreach (var entity in _tables.EnumerateAllAsync(tableName, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (_clock.GetUtcNow().UtcDateTime >= deadlineUtc)
                        throw new BudgetExceededException("per-run budget exceeded during table dump");

                    var dump = TableEntityDumpConverter.MapEntityToDump(entity);
                    var line = JsonSerializer.SerializeToUtf8Bytes(dump, BackupManifestJson.SerializerOptions);
                    await crypto.WriteAsync(line, ct).ConfigureAwait(false);
                    await crypto.WriteAsync(NewlineBytes, ct).ConfigureAwait(false);
                    rows++;
                }

                // Final-block must flush BEFORE we read sha.Hash; CryptoStream does this on dispose,
                // but we need the bytes BEFORE the CountingStream/blobStream dispose. Manual flush:
                crypto.FlushFinalBlock();
                sha256Hex = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
                byteSize = counting.BytesWritten;
            }
            catch (OperationCanceledException)
            {
                // Renewal-loss / external cancel — never swallow into a per-table Failed.
                throw;
            }
            catch (BudgetExceededException ex)
            {
                _logger.LogWarning(ex, "CriticalTableBackupService: budget exceeded during {Table}", tableName);
                entry.Status = TableBackupStatus.Skipped;
                entry.ErrorMessage = ex.Message;
                return entry;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CriticalTableBackupService: table {Table} dump failed", tableName);
                entry.Status = TableBackupStatus.Failed;
                entry.ErrorMessage = ex.Message;
                return entry;
            }

            entry.RowCount = rows;
            entry.ByteSize = byteSize;
            entry.Sha256Hex = sha256Hex;
            entry.Status = rows == 0 ? TableBackupStatus.Empty : TableBackupStatus.Ok;
            return entry;
        }

        private static readonly byte[] NewlineBytes = Encoding.UTF8.GetBytes("\n");

        // ── Counting stream wrapper ─────────────────────────────────────────────

        /// <summary>
        /// Tracks the byte-count of writes that flow through it. Wraps the blob stream so
        /// the service can stamp ByteSize on the manifest entry without re-reading the blob.
        /// </summary>
        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;
            private long _bytesWritten;

            public CountingStream(Stream inner) { _inner = inner; }
            public long BytesWritten => _bytesWritten;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _bytesWritten;
            public override long Position { get => _bytesWritten; set => throw new NotSupportedException(); }

            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _bytesWritten += count;
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
                _bytesWritten += count;
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
                _bytesWritten += buffer.Length;
            }

            protected override void Dispose(bool disposing)
            {
                // Don't dispose _inner — the outer await using on the blob stream owns disposal.
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// Thrown when the per-run wall-clock budget is exceeded mid-loop. Routed to
    /// <see cref="TableBackupStatus.Skipped"/> by its own catch — distinct from generic
    /// failures so the Outcome math (Partial) and operator action items stay clear.
    /// </summary>
    public sealed class BudgetExceededException : Exception
    {
        public BudgetExceededException(string message) : base(message) { }
    }
}
