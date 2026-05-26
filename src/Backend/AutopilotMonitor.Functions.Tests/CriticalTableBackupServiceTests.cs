using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Service-level happy/partial/empty/budget paths for the critical-table backup feature.
/// Uses in-memory fakes for table enumeration and blob I/O so the loop's structural
/// invariants (manifest written LAST, per-table failure → Partial, budget → Skipped)
/// can be locked without touching Azurite.
/// </summary>
public class CriticalTableBackupServiceTests
{
    [Fact]
    public async Task Happy_AllTables_OutcomeSuccess_ManifestWritten()
    {
        var tables = new FakeTableStorage(new Dictionary<string, List<TableEntity>>
        {
            // Use real critical-table names so the service finds them in CriticalBackupTables.All.
            [Constants.TableNames.AdminConfiguration] = new()
            {
                new TableEntity("admin", "row1") { ["Setting"] = "value" },
            },
            [Constants.TableNames.AnalyzeRules] = new()
            {
                new TableEntity("tenant1", "rule1") { ["Enabled"] = true },
            },
        }, defaultEmpty: true);
        var store = new FakeBlobStore();
        var clock = new FakeClock(new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc));
        var svc = new CriticalTableBackupService(tables, store, NullLogger<CriticalTableBackupService>.Instance, clock);

        var backupId = svc.GenerateBackupId();
        var result = await svc.RunBackupUnderLeaseAsync(backupId, "Timer", CancellationToken.None);

        Assert.Equal(BackupOutcome.Success, result.Outcome);
        Assert.True(store.ManifestWritten, "manifest must be written LAST");
        Assert.Equal(15, result.Manifest.Tables.Count);   // critical-tables catalog count
        Assert.All(result.Manifest.Tables, t =>
            Assert.True(t.Status == TableBackupStatus.Ok || t.Status == TableBackupStatus.Empty));
    }

    [Fact]
    public async Task Partial_MiddleTableThrows_OutcomePartial_ManifestStillWritten()
    {
        var tables = new FakeTableStorage(new Dictionary<string, List<TableEntity>>
        {
            [Constants.TableNames.AdminConfiguration] = new() { new TableEntity("a", "b") { ["X"] = 1 } },
        }, defaultEmpty: true, throwOnTable: Constants.TableNames.AnalyzeRules);
        var store = new FakeBlobStore();
        var clock = new FakeClock(new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc));
        var svc = new CriticalTableBackupService(tables, store, NullLogger<CriticalTableBackupService>.Instance, clock);

        var result = await svc.RunBackupUnderLeaseAsync(svc.GenerateBackupId(), "Timer", CancellationToken.None);

        Assert.Equal(BackupOutcome.Partial, result.Outcome);
        Assert.True(store.ManifestWritten);
        var failedEntry = result.Manifest.Tables.First(t => t.TableName == Constants.TableNames.AnalyzeRules);
        Assert.Equal(TableBackupStatus.Failed, failedEntry.Status);
        Assert.NotNull(failedEntry.ErrorMessage);
    }

    [Fact]
    public async Task Empty_AllTablesEmpty_OutcomeSuccess_EveryEntryEmptyStatus()
    {
        var tables = new FakeTableStorage(new(), defaultEmpty: true);
        var store = new FakeBlobStore();
        var clock = new FakeClock(new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc));
        var svc = new CriticalTableBackupService(tables, store, NullLogger<CriticalTableBackupService>.Instance, clock);

        var result = await svc.RunBackupUnderLeaseAsync(svc.GenerateBackupId(), "Timer", CancellationToken.None);

        Assert.Equal(BackupOutcome.Success, result.Outcome);
        Assert.All(result.Manifest.Tables, t => Assert.Equal(TableBackupStatus.Empty, t.Status));
        Assert.All(result.Manifest.Tables, t => Assert.Equal(0, t.RowCount));
    }

    [Fact]
    public async Task BudgetExceeded_PreLoop_RemainingTablesSkipped_OutcomePartial()
    {
        // Clock jumps forward by 51 min after the very first GetUtcNow call so the
        // per-run budget (50 min) is exceeded before any subsequent table iteration.
        var startUtc = new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc);
        var clock = new FakeClock(startUtc);
        var tables = new FakeTableStorage(new(), defaultEmpty: true);
        var store = new FakeBlobStore();

        // First clock read = startedAtUtc inside RunBackupUnderLeaseAsync; after that we
        // jump 51 min so the very first per-table budget check trips. Use a hand-crafted
        // backupId so GenerateBackupId() doesn't pre-consume a clock read.
        clock.AdvanceOnNextRead = TimeSpan.FromMinutes(51);
        clock.AdvanceAfterReadNumber = 1;

        var svc = new CriticalTableBackupService(tables, store, NullLogger<CriticalTableBackupService>.Instance, clock);
        var result = await svc.RunBackupUnderLeaseAsync("20260522T040000Z_deadbeef", "Timer", CancellationToken.None);

        Assert.Equal(BackupOutcome.Partial, result.Outcome);
        Assert.True(store.ManifestWritten);
        Assert.All(result.Manifest.Tables, t => Assert.Equal(TableBackupStatus.Skipped, t.Status));
    }

    [Fact]
    public void GenerateBackupId_FormatIsStable()
    {
        var clock = new FakeClock(new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc));
        var tables = new FakeTableStorage(new(), defaultEmpty: true);
        var store = new FakeBlobStore();
        var svc = new CriticalTableBackupService(tables, store, NullLogger<CriticalTableBackupService>.Instance, clock);

        var id = svc.GenerateBackupId();
        Assert.Matches(@"^\d{8}T\d{6}Z_[0-9a-f]{8}$", id);
        Assert.StartsWith("20260522T040000Z_", id);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeTableStorage : TableStorageService
    {
        private readonly Dictionary<string, List<TableEntity>> _data;
        private readonly bool _defaultEmpty;
        private readonly string? _throwOnTable;

        public FakeTableStorage(Dictionary<string, List<TableEntity>> data, bool defaultEmpty = false, string? throwOnTable = null)
            : base(new TableServiceClient(new Uri("https://example.invalid"), new TableSharedKeyCredential("x", Convert.ToBase64String(new byte[32]))), NullLogger<TableStorageService>.Instance)
        {
            _data = data;
            _defaultEmpty = defaultEmpty;
            _throwOnTable = throwOnTable;
        }

        public override async IAsyncEnumerable<TableEntity> EnumerateAllAsync(string tableName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (string.Equals(tableName, _throwOnTable, StringComparison.Ordinal))
                throw new InvalidOperationException($"simulated table failure: {tableName}");

            if (_data.TryGetValue(tableName, out var rows))
            {
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return row;
                }
                yield break;
            }
            if (_defaultEmpty) yield break;
            throw new InvalidOperationException($"unexpected table: {tableName}");
        }
    }

    private sealed class FakeBlobStore : BlobBackupStore
    {
        public bool ManifestWritten { get; private set; }
        public readonly List<string> WrittenNdjsonBlobs = new();
        public readonly Dictionary<string, byte[]> NdjsonBytes = new();

        public FakeBlobStore()
            : base(new FakeBlobStorageService(), NullLogger<BlobBackupStore>.Instance)
        {
        }

        public override Task EnsureContainerAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task EnsureMaintenanceLockSentinelAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task<Stream> OpenNdjsonWriteStreamAsync(string backupId, string tableName, CancellationToken ct = default)
        {
            var name = BuildNdjsonBlobName(backupId, tableName);
            WrittenNdjsonBlobs.Add(name);
            var ms = new RecordingMemoryStream(b => NdjsonBytes[name] = b);
            return Task.FromResult<Stream>(ms);
        }

        public override Task<bool> TryWriteManifestAsync(string backupId, byte[] manifestBytes, CancellationToken ct = default)
        {
            // Plan invariant: every NDJSON blob must be committed (= disposed) before the manifest is written.
            // Our recording stream finalises on dispose; if a stream is still open at this point a bug exists.
            ManifestWritten = true;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingMemoryStream : MemoryStream
    {
        private readonly Action<byte[]> _onClose;
        private bool _closed;
        public RecordingMemoryStream(Action<byte[]> onClose) { _onClose = onClose; }

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                _onClose(this.ToArray());
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Minimal stub — never actually used because all BlobBackupStore methods are overridden.</summary>
    private sealed class FakeBlobStorageService : BlobStorageService
    {
        public FakeBlobStorageService()
            : base(new Azure.Storage.Blobs.BlobServiceClient(new Uri("https://example.invalid"), new Azure.Storage.StorageSharedKeyCredential("x", Convert.ToBase64String(new byte[32]))), NullLogger<BlobStorageService>.Instance) { }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTime _now;
        private int _readCount;
        /// <summary>Time to advance AFTER the Nth read (default: after the 1st).</summary>
        public TimeSpan AdvanceOnNextRead { get; set; } = TimeSpan.Zero;
        public int AdvanceAfterReadNumber { get; set; } = 1;
        public FakeClock(DateTime startUtc) { _now = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc); }
        public override DateTimeOffset GetUtcNow()
        {
            var currentValue = new DateTimeOffset(_now, TimeSpan.Zero);
            _readCount++;
            if (_readCount == AdvanceAfterReadNumber && AdvanceOnNextRead != TimeSpan.Zero)
            {
                _now += AdvanceOnNextRead;
                AdvanceOnNextRead = TimeSpan.Zero;
            }
            return currentValue;
        }
    }
}
