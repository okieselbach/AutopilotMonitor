using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Single-row preview tests for the critical-table restore service. Covers the
/// happy path (row found in backup + missing from live), the diff shape
/// (Added / Removed / Changed / Unchanged), and the RowNotInBackup edge.
/// Auth-table flag propagation is also asserted.
/// <para>
/// Commit-path tests live in <see cref="CriticalTableRestoreServiceCommitTests"/>
/// once we can mock <c>BlobLeaseClient</c> without hitting the real SDK lease
/// API; for now the commit happy/conflict paths are covered indirectly via the
/// validator + preflight tests.
/// </para>
/// </summary>
public class CriticalTableRestoreServiceTests
{
    private const string BackupId = "20260522T040000Z_a1b2c3d4";
    private const string Table = Constants.TableNames.AnalyzeRules;

    [Fact]
    public async Task PreviewRowAsync_returns_full_diff_when_backup_and_live_differ()
    {
        var lineBytes = BuildNdjsonLine("tenant1", "rule1", new Dictionary<string, (string Edm, object? Value)>
        {
            ["Enabled"] = ("Boolean", true),
            ["KillSwitch"] = ("Boolean", false),
            ["Comment"] = ("String", "from backup"),
        });
        var sha = ComputeSha(lineBytes);

        var entry = MakeEntry(Table, sha, lineBytes.Length);
        var manifest = MakeManifest(entry);

        var fakeStore = new FakeStore(manifest, lineBytes, new ETag("\"0xABC\""));
        var liveRow = new TableEntity("tenant1", "rule1")
        {
            ["Enabled"] = false,                 // changed
            ["KillSwitch"] = false,              // unchanged
            ["NewLiveColumn"] = "added-after",   // removed by restore
            ["odata.etag"] = "live-etag",
        };
        liveRow.ETag = new ETag("\"live-etag\"");
        var tables = BuildTableStorageWithLiveRow(liveRow);

        var sut = MakeService(tables, fakeStore);

        var preview = await sut.PreviewRowAsync(BackupId, Table, "tenant1", "rule1", CancellationToken.None);

        Assert.Equal(BackupId, preview.BackupId);
        Assert.Equal(Table, preview.TableName);
        Assert.Equal("tenant1", preview.PartitionKey);
        Assert.Equal("rule1", preview.RowKey);
        Assert.Equal(sha, preview.RowSha256);
        Assert.Equal("\"live-etag\"", preview.CurrentETag);
        Assert.False(preview.IsAuthTable);

        // Diff sanity
        var changed = preview.Diff.Single(d => d.Name == "Enabled");
        Assert.Equal(RestoreRowDiffKind.Changed, changed.Kind);

        var unchanged = preview.Diff.Single(d => d.Name == "KillSwitch");
        Assert.Equal(RestoreRowDiffKind.Unchanged, unchanged.Kind);

        var removed = preview.Diff.Single(d => d.Name == "NewLiveColumn");
        Assert.Equal(RestoreRowDiffKind.Removed, removed.Kind);
        // "Comment" exists in backup, not in live → Added on restore.
        var added = preview.Diff.Single(d => d.Name == "Comment");
        Assert.Equal(RestoreRowDiffKind.Added, added.Kind);
    }

    [Fact]
    public async Task PreviewRowAsync_marks_currentETag_null_when_live_row_missing()
    {
        var lineBytes = BuildNdjsonLine("p", "r", new Dictionary<string, (string Edm, object? Value)>
        {
            ["Setting"] = ("String", "value"),
        });
        var sha = ComputeSha(lineBytes);
        var manifest = MakeManifest(MakeEntry(Table, sha, lineBytes.Length));

        var fakeStore = new FakeStore(manifest, lineBytes, new ETag("\"0xABC\""));
        var tables = BuildTableStorageWithLiveRow(liveRow: null);

        var sut = MakeService(tables, fakeStore);

        var preview = await sut.PreviewRowAsync(BackupId, Table, "p", "r", CancellationToken.None);

        Assert.Null(preview.CurrentETag);
        Assert.Null(preview.CurrentProperties);
        Assert.Single(preview.Diff);
        Assert.Equal(RestoreRowDiffKind.Added, preview.Diff[0].Kind);
    }

    [Fact]
    public async Task PreviewRowAsync_throws_RowNotInBackup_when_keys_dont_match_any_line()
    {
        var lineBytes = BuildNdjsonLine("p", "r", new Dictionary<string, (string Edm, object? Value)>
        {
            ["Setting"] = ("String", "value"),
        });
        var sha = ComputeSha(lineBytes);
        var manifest = MakeManifest(MakeEntry(Table, sha, lineBytes.Length));

        var fakeStore = new FakeStore(manifest, lineBytes, new ETag("\"0xABC\""));
        var tables = BuildTableStorageWithLiveRow(liveRow: null);

        var sut = MakeService(tables, fakeStore);

        var ex = await Assert.ThrowsAsync<BackupTerminalException>(() =>
            sut.PreviewRowAsync(BackupId, Table, "p-OTHER", "r", CancellationToken.None));
        Assert.Equal("RowNotInBackup", ex.Code);
    }

    [Fact]
    public async Task PreviewRowAsync_sets_IsAuthTable_true_for_GlobalAdmins()
    {
        var lineBytes = BuildNdjsonLine("admins", "alice@contoso.test",
            new Dictionary<string, (string Edm, object? Value)>
            {
                ["IsEnabled"] = ("Boolean", true),
            });
        var sha = ComputeSha(lineBytes);
        var manifest = MakeManifestForTable(MakeEntry(Constants.TableNames.GlobalAdmins, sha, lineBytes.Length));

        var fakeStore = new FakeStore(manifest, lineBytes, new ETag("\"0xABC\""));
        var tables = BuildTableStorageWithLiveRow(liveRow: null);

        var sut = MakeService(tables, fakeStore);

        var preview = await sut.PreviewRowAsync(
            BackupId, Constants.TableNames.GlobalAdmins, "admins", "alice@contoso.test",
            CancellationToken.None);

        Assert.True(preview.IsAuthTable);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CriticalTableRestoreService MakeService(TableStorageService tables, FakeStore store)
    {
        var validator = new BackupRestoreInputValidator(store, NullLogger<BackupRestoreInputValidator>.Instance);
        return new CriticalTableRestoreService(
            tables,
            store,
            validator,
            NullLoggerFactory.Instance,
            NullLogger<CriticalTableRestoreService>.Instance);
    }

    private static CriticalTableBackupTableEntry MakeEntry(string tableName, string sha, long byteSize) => new()
    {
        TableName = tableName,
        Status = TableBackupStatus.Ok,
        RowCount = 1,
        ByteSize = byteSize,
        Sha256Hex = sha,
        BlobName = $"{BackupId}/{tableName}.ndjson",
    };

    private static CriticalTableBackupManifest MakeManifest(CriticalTableBackupTableEntry entry) =>
        MakeManifestForTable(entry);

    private static CriticalTableBackupManifest MakeManifestForTable(CriticalTableBackupTableEntry entry) => new()
    {
        SchemaVersion = 1,
        BackupId = BackupId,
        StartedAtUtc = new DateTime(2026, 5, 22, 4, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 5, 22, 4, 0, 1, DateTimeKind.Utc),
        TriggeredBy = "Timer",
        Outcome = BackupOutcome.Success,
        Tables = { entry },
    };

    private static byte[] BuildNdjsonLine(string pk, string rk, Dictionary<string, (string Edm, object? Value)> props)
    {
        var dump = new DeletionRowDump
        {
            Pk = pk,
            Rk = rk,
            Etag = null,
            Props = new Dictionary<string, DeletionPropValue>(StringComparer.Ordinal),
        };
        foreach (var kvp in props)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(kvp.Value.Value, BackupManifestJson.SerializerOptions);
            using var doc = JsonDocument.Parse(bytes);
            dump.Props[kvp.Key] = new DeletionPropValue
            {
                EdmType = kvp.Value.Edm,
                Value = doc.RootElement.Clone(),
            };
        }
        return JsonSerializer.SerializeToUtf8Bytes(dump, BackupManifestJson.SerializerOptions);
    }

    private static string ComputeSha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static TableStorageService BuildTableStorageWithLiveRow(TableEntity? liveRow)
    {
        var mockTableClient = new Mock<TableClient>();

        if (liveRow != null)
        {
            var response = Response.FromValue(liveRow, new Mock<Response>().Object);
            mockTableClient
                .Setup(c => c.GetEntityIfExistsAsync<TableEntity>(
                    liveRow.PartitionKey, liveRow.RowKey,
                    It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((NullableResponse<TableEntity>)response);
        }

        // For any other (pk, rk) — return a "not found" NullableResponse.
        mockTableClient
            .Setup(c => c.GetEntityIfExistsAsync<TableEntity>(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IEnumerable<string>?, CancellationToken>((pk, rk, _, _) =>
            {
                if (liveRow != null
                    && string.Equals(pk, liveRow.PartitionKey, StringComparison.Ordinal)
                    && string.Equals(rk, liveRow.RowKey, StringComparison.Ordinal))
                {
                    return Task.FromResult((NullableResponse<TableEntity>)
                        Response.FromValue(liveRow, new Mock<Response>().Object));
                }
                var notFound = new Mock<NullableResponse<TableEntity>>();
                notFound.SetupGet(r => r.HasValue).Returns(false);
                return Task.FromResult(notFound.Object);
            });

        var mockServiceClient = new Mock<TableServiceClient>();
        mockServiceClient.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(mockTableClient.Object);

        return new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
    }

    private sealed class FakeStore : BlobBackupStore
    {
        private readonly byte[]? _manifestBytes;
        private readonly byte[] _ndjsonBytes;
        private readonly ETag _etag;

        public FakeStore(CriticalTableBackupManifest? manifest, byte[] ndjson, ETag etag)
            : base(new FakeBlobStorageService(), NullLogger<BlobBackupStore>.Instance)
        {
            _manifestBytes = manifest is null ? null : JsonSerializer.SerializeToUtf8Bytes(manifest, BackupManifestJson.SerializerOptions);
            _ndjsonBytes = ndjson;
            _etag = etag;
        }

        public override Task EnsureContainerAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task<(byte[]? Payload, ETag? ETag)> ReadManifestAsync(string backupId, CancellationToken ct = default)
            => Task.FromResult<(byte[]?, ETag?)>((_manifestBytes, _manifestBytes is null ? null : _etag));

        public override Task<(Stream Stream, ETag ETag)?> OpenNdjsonReadAsync(string backupId, string tableName, CancellationToken ct = default)
        {
            // Return a fresh stream every call — the validator and the scan
            // open the blob independently.
            Stream stream = new MemoryStream(_ndjsonBytes, writable: false);
            return Task.FromResult<(Stream, ETag)?>((stream, _etag));
        }
    }

    private sealed class FakeBlobStorageService : BlobStorageService
    {
        public FakeBlobStorageService()
            : base(new Azure.Storage.Blobs.BlobServiceClient(new Uri("https://example.invalid"),
                new Azure.Storage.StorageSharedKeyCredential("x", Convert.ToBase64String(new byte[32]))),
                NullLogger<BlobStorageService>.Instance) { }
    }
}
