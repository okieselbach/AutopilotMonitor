using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// TableConfigBackupRepository: Store↔Map roundtrip (project rule "table-serialization"),
/// reverse-ticks RowKey ordering (newest-first), and prune-beyond-keep semantics.
/// </summary>
public class ConfigBackupRepositoryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private sealed class Harness
    {
        public TableConfigBackupRepository Sut { get; }
        public Mock<TableClient> Table { get; } = new();
        public List<TableEntity> Upserts { get; } = new();
        public List<(string Pk, string Rk)> Deletes { get; } = new();

        public Harness()
        {
            Table.Setup(c => c.UpsertEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, TableUpdateMode, CancellationToken>((e, _, _) =>
                {
                    Upserts.Add(e);
                    return Task.FromResult(Mock.Of<Response>());
                });
            Table.Setup(c => c.DeleteEntityAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, ETag, CancellationToken>((pk, rk, _, _) =>
                {
                    Deletes.Add((pk, rk));
                    return Task.FromResult(Mock.Of<Response>());
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(c => c.GetTableClient(It.IsAny<string>())).Returns(Table.Object);
            Sut = new TableConfigBackupRepository(
                new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance),
                NullLogger<TableConfigBackupRepository>.Instance);
        }

        public void SetupQuery(params TableEntity[] rows)
        {
            var page = Page<TableEntity>.FromValues(rows, continuationToken: null, Mock.Of<Response>());
            Table.Setup(c => c.QueryAsync<TableEntity>(
                    It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(AsyncPageable<TableEntity>.FromPages(new[] { page }));
        }
    }

    private static ConfigBackupEntry Entry(string rowKey = "0001_abc") => new()
    {
        PartitionKey = TenantId,
        RowKey = rowKey,
        TenantId = TenantId,
        EntityJson = "{\"DomainName\":\"contoso.com\"}",
        ChangedBy = "ga@operator.example",
        Source = "mcp-patch",
        Reason = "retention bump",
        DiffJson = "{\"DataRetentionDays\":\"30 \\u2192 90\"}",
        BackupTakenAt = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task Roundtrip_StoreAndMap_PreservesEveryField()
    {
        var harness = new Harness();
        var entry = Entry();

        await harness.Sut.UpsertAsync(entry);
        var stored = Assert.Single(harness.Upserts);

        // Feed the stored entity back through the read path (TryGetAsync → Map).
        harness.Table.Setup(c => c.GetEntityAsync<TableEntity>(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(stored, Mock.Of<Response>()));
        var mapped = await harness.Sut.TryGetAsync(TenantId, entry.RowKey);

        Assert.NotNull(mapped);
        Assert.Equal(entry.PartitionKey, mapped!.PartitionKey);
        Assert.Equal(entry.RowKey, mapped.RowKey);
        Assert.Equal(entry.TenantId, mapped.TenantId);
        Assert.Equal(entry.EntityJson, mapped.EntityJson);
        Assert.Equal(entry.ChangedBy, mapped.ChangedBy);
        Assert.Equal(entry.Source, mapped.Source);
        Assert.Equal(entry.Reason, mapped.Reason);
        Assert.Equal(entry.DiffJson, mapped.DiffJson);
        Assert.Equal(entry.BackupTakenAt, mapped.BackupTakenAt);
    }

    [Fact]
    public void BuildRowKey_LaterTimestamps_SortFirst()
    {
        var earlier = TableConfigBackupRepository.BuildRowKey(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc));
        var later = TableConfigBackupRepository.BuildRowKey(new DateTime(2026, 8, 3, 12, 0, 1, DateTimeKind.Utc));

        // Reverse ticks: the LATER backup gets the LEXICOGRAPHICALLY SMALLER RowKey, so
        // Azure's RowKey-ascending partition scan returns newest-first.
        Assert.True(string.CompareOrdinal(later, earlier) < 0);
    }

    [Fact]
    public void BuildRowKey_SameTick_DistinctViaGuidSuffix()
    {
        var at = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.NotEqual(
            TableConfigBackupRepository.BuildRowKey(at),
            TableConfigBackupRepository.BuildRowKey(at));
    }

    [Fact]
    public async Task Prune_DeletesEverythingBeyondKeep_InScanOrder()
    {
        var harness = new Harness();
        // Scan order = RowKey ascending = newest first (reverse ticks). keep=2 must
        // delete rows 3 and 4, never the two newest.
        harness.SetupQuery(
            new TableEntity(TenantId, "0001_newest"),
            new TableEntity(TenantId, "0002_second"),
            new TableEntity(TenantId, "0003_old"),
            new TableEntity(TenantId, "0004_oldest"));

        var deleted = await harness.Sut.PruneAsync(TenantId, keep: 2);

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { "0003_old", "0004_oldest" }, harness.Deletes.Select(d => d.Rk).ToArray());
    }

    [Fact]
    public async Task Prune_WithinKeep_DeletesNothing()
    {
        var harness = new Harness();
        harness.SetupQuery(
            new TableEntity(TenantId, "0001_newest"),
            new TableEntity(TenantId, "0002_second"));

        var deleted = await harness.Sut.PruneAsync(TenantId, keep: 2);

        Assert.Equal(0, deleted);
        Assert.Empty(harness.Deletes);
    }

    [Fact]
    public async Task ListByPartition_CapsAtMax()
    {
        var harness = new Harness();
        harness.SetupQuery(
            new TableEntity(TenantId, "0001"),
            new TableEntity(TenantId, "0002"),
            new TableEntity(TenantId, "0003"));

        var listed = await harness.Sut.ListByPartitionAsync(TenantId, max: 2);

        Assert.Equal(2, listed.Count);
        Assert.Equal("0001", listed[0].RowKey);
    }
}
