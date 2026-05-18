using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-trip + shape-guard coverage for the three POCO mappings hosted in
/// <c>OffboardingAudit</c>. Memory: <c>feedback_table_storage_serialization</c> — new
/// fields MUST be exercised in Store+Map so silent drops surface here, not in production.
/// </summary>
public class TableOffboardingAuditRepositoryTests
{
    private const string TenantId = "88888888-8888-8888-8888-888888888888";
    private const string HistoryRowKey = "20260518091523123_88888888-8888-8888-8888-888888888888";

    [Fact]
    public async Task Marker_RoundTrips_AllFields()
    {
        var harness = new Harness();
        var marker = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = HistoryRowKey,
            InitiatedAt = new DateTime(2026, 5, 18, 9, 15, 23, DateTimeKind.Utc),
            InitiatedBy = "alice@contoso.invalid",
            Status = "InProgress",
            CompletedAt = null,
            FailedAt = null,
            FailedPhase = null,
        };

        await harness.Sut.UpsertMarkerAsync(marker);

        var fetched = await harness.Sut.TryGetMarkerAsync(TenantId);
        Assert.NotNull(fetched);
        Assert.Equal(marker.PartitionKey, fetched!.PartitionKey);
        Assert.Equal(marker.RowKey, fetched.RowKey);
        Assert.Equal(marker.TenantId, fetched.TenantId);
        Assert.Equal(marker.OffboardingHistoryRowKey, fetched.OffboardingHistoryRowKey);
        Assert.Equal(marker.InitiatedAt, fetched.InitiatedAt);
        Assert.Equal(marker.InitiatedBy, fetched.InitiatedBy);
        Assert.Equal("InProgress", fetched.Status);
    }

    [Fact]
    public async Task Marker_CompletedTransition_PreservesTimestamp()
    {
        var harness = new Harness();
        var completed = new OffboardingMarkerEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.Marker,
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = HistoryRowKey,
            InitiatedAt = DateTime.UtcNow.AddMinutes(-10),
            InitiatedBy = "bob@contoso.invalid",
            Status = "Completed",
            CompletedAt = DateTime.UtcNow,
        };

        await harness.Sut.UpsertMarkerAsync(completed);
        var fetched = await harness.Sut.TryGetMarkerAsync(TenantId);

        Assert.Equal("Completed", fetched!.Status);
        Assert.NotNull(fetched.CompletedAt);
    }

    [Fact]
    public async Task Marker_TryGet_ReturnsNullOn404()
    {
        var harness = new Harness(throwOnGet: new RequestFailedException(404, "NotFound", "ResourceNotFound", null));
        Assert.Null(await harness.Sut.TryGetMarkerAsync(TenantId));
    }

    [Fact]
    public async Task Marker_DeleteSwallows404_Idempotent()
    {
        var harness = new Harness(throwOnDelete: new RequestFailedException(404, "NotFound", "ResourceNotFound", null));
        await harness.Sut.DeleteMarkerAsync(TenantId);
        // No exception → idempotent.
    }

    [Fact]
    public async Task History_RoundTrips_RetainCountersAndDrainCompletedAt()
    {
        var harness = new Harness();
        var now = new DateTime(2026, 5, 18, 9, 15, 23, DateTimeKind.Utc);
        var history = new OffboardingHistoryEntry
        {
            PartitionKey = Constants.OffboardingPartitionKeys.History,
            RowKey = HistoryRowKey,
            TenantId = TenantId,
            DomainName = "contoso.invalid",
            InitiatedBy = "alice@contoso.invalid",
            OffboardedAt = now,
            Status = "Completed",
            CompletedAt = now.AddMinutes(5),
            DeletedRowCountsJson = "{\"AuditLogs\":42}",
            TotalRowsDeleted = 42,
            DeletedBlobCount = 7,
            CascadeSessionsEnqueued = 3,
            RetryCount = 0,
            DrainCompletedAt = now.AddMinutes(3),  // Rev-9-F1
            CustomGatherRulesArchived = 5,
            CustomAnalyzeRulesArchived = 3,
            ImeLogPatternOverridesArchived = 2,
        };

        await harness.Sut.UpsertHistoryAsync(history);
        var fetched = await harness.Sut.TryGetHistoryAsync(HistoryRowKey);

        Assert.NotNull(fetched);
        Assert.Equal(history.Status, fetched!.Status);
        Assert.Equal(history.TotalRowsDeleted, fetched.TotalRowsDeleted);
        Assert.Equal(history.DeletedBlobCount, fetched.DeletedBlobCount);
        Assert.Equal(history.CascadeSessionsEnqueued, fetched.CascadeSessionsEnqueued);
        Assert.Equal(history.DrainCompletedAt, fetched.DrainCompletedAt);
        Assert.Equal(5, fetched.CustomGatherRulesArchived);
        Assert.Equal(3, fetched.CustomAnalyzeRulesArchived);
        Assert.Equal(2, fetched.ImeLogPatternOverridesArchived);
    }

    [Fact]
    public async Task Pointer_TryGet_OnEmpty_ReturnsNullPointerAndNullEtag()
    {
        var harness = new Harness();
        var (pointer, etag) = await harness.Sut.TryGetByTenantPointerAsync(TenantId);
        Assert.Null(pointer);
        Assert.Null(etag);
    }

    [Fact]
    public async Task Pointer_Insert_RoundTrips_LatestHistoryRowKeyAndOffboardCount()
    {
        var harness = new Harness();
        var pointer = new OffboardingByTenantPointer
        {
            PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
            RowKey = TenantId,
            TenantId = TenantId,
            LatestHistoryRowKey = HistoryRowKey,
            LatestStatus = "Initiated",
            LatestUpdatedAt = DateTime.UtcNow,
            OffboardCount = 1,
        };

        await harness.Sut.InsertByTenantPointerAsync(pointer);

        var (fetched, etag) = await harness.Sut.TryGetByTenantPointerAsync(TenantId);
        Assert.NotNull(fetched);
        Assert.NotNull(etag);
        Assert.Equal(pointer.LatestHistoryRowKey, fetched!.LatestHistoryRowKey);
        Assert.Equal("Initiated", fetched.LatestStatus);
        Assert.Equal(1, fetched.OffboardCount);
    }

    [Fact]
    public async Task Pointer_Insert_OnDuplicate_Throws409()
    {
        var harness = new Harness();
        var pointer = BuildPointer(latestStatus: "Initiated", offboardCount: 1);
        await harness.Sut.InsertByTenantPointerAsync(pointer);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => harness.Sut.InsertByTenantPointerAsync(pointer));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Pointer_UpdateWithEtag_MatchingEtag_Replaces()
    {
        var harness = new Harness();
        await harness.Sut.InsertByTenantPointerAsync(BuildPointer(latestStatus: "Initiated", offboardCount: 1));

        var (current, etag) = await harness.Sut.TryGetByTenantPointerAsync(TenantId);
        Assert.NotNull(current);
        current!.LatestStatus = "InProgress";
        current.OffboardCount = 2;
        current.LatestUpdatedAt = DateTime.UtcNow;

        await harness.Sut.UpdateByTenantPointerWithEtagAsync(current, etag!);

        var (refetched, _) = await harness.Sut.TryGetByTenantPointerAsync(TenantId);
        Assert.Equal("InProgress", refetched!.LatestStatus);
        Assert.Equal(2, refetched.OffboardCount);
    }

    [Fact]
    public async Task Pointer_UpdateWithEtag_StaleEtag_Throws412()
    {
        // Plan §4.4 contract: stale ETag → 412 so the caller's bounded retry loop fires.
        // The blind Upsert path was removed because it could clobber a concurrent worker.
        var harness = new Harness();
        await harness.Sut.InsertByTenantPointerAsync(BuildPointer(latestStatus: "Initiated"));

        var stale = BuildPointer(latestStatus: "InProgress", offboardCount: 99);
        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => harness.Sut.UpdateByTenantPointerWithEtagAsync(stale, ifMatchEtag: "\"0xSTALE\""));
        Assert.Equal(412, ex.Status);
    }

    [Fact]
    public async Task Pointer_UpdateWithEtag_RequiresIfMatchEtag()
    {
        var harness = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.UpdateByTenantPointerWithEtagAsync(BuildPointer(), ""));
    }

    private static OffboardingByTenantPointer BuildPointer(
        string latestStatus = "Initiated",
        int offboardCount = 1) => new()
    {
        PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
        RowKey = TenantId,
        TenantId = TenantId,
        LatestHistoryRowKey = HistoryRowKey,
        LatestStatus = latestStatus,
        LatestUpdatedAt = DateTime.UtcNow,
        OffboardCount = offboardCount,
    };

    [Fact]
    public async Task Marker_RejectsWrongPartitionKey()
    {
        var harness = new Harness();
        var bad = new OffboardingMarkerEntry
        {
            PartitionKey = "WrongPK",
            RowKey = TenantId,
            TenantId = TenantId,
            OffboardingHistoryRowKey = HistoryRowKey,
            InitiatedAt = DateTime.UtcNow,
            InitiatedBy = "alice@contoso.invalid",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Sut.UpsertMarkerAsync(bad));
    }

    [Fact]
    public async Task History_RejectsWrongPartitionKey()
    {
        var harness = new Harness();
        var bad = new OffboardingHistoryEntry
        {
            PartitionKey = "WrongPK",
            RowKey = HistoryRowKey,
            TenantId = TenantId,
            InitiatedBy = "alice@contoso.invalid",
            OffboardedAt = DateTime.UtcNow,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Sut.UpsertHistoryAsync(bad));
    }

    [Fact]
    public async Task Pointer_RejectsWrongPartitionKey()
    {
        var harness = new Harness();
        var bad = new OffboardingByTenantPointer
        {
            PartitionKey = "WrongPK",
            RowKey = TenantId,
            TenantId = TenantId,
            LatestHistoryRowKey = HistoryRowKey,
            LatestStatus = "Initiated",
            LatestUpdatedAt = DateTime.UtcNow,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Sut.InsertByTenantPointerAsync(bad));
    }

    // ── Harness — in-memory dictionary of (PK,RK) → TableEntity backing the mock ──
    //
    // ETag-aware: every successful insert/upsert/update stamps a fresh ETag onto the stored
    // entity so Get reads return it and Update can enforce If-Match. Mirrors enough of Azure
    // Table Storage's behaviour to exercise the §4.4 Pointer CAS-Loop honestly.

    private sealed class Harness
    {
        public TableOffboardingAuditRepository Sut { get; }

        private readonly Dictionary<(string Pk, string Rk), TableEntity> _store = new();
        private int _etagCounter;

        public Harness(
            RequestFailedException? throwOnGet = null,
            RequestFailedException? throwOnDelete = null)
        {
            var mockTableClient = new Mock<TableClient>();

            mockTableClient
                .Setup(c => c.UpsertEntityAsync(
                    It.IsAny<TableEntity>(),
                    It.IsAny<TableUpdateMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TableEntity, TableUpdateMode, CancellationToken>((e, _, _) =>
                {
                    e.ETag = StampEtag();
                    _store[(e.PartitionKey, e.RowKey)] = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            mockTableClient
                .Setup(c => c.AddEntityAsync(
                    It.IsAny<TableEntity>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((e, _) =>
                {
                    if (_store.ContainsKey((e.PartitionKey, e.RowKey)))
                        throw new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null);
                    e.ETag = StampEtag();
                    _store[(e.PartitionKey, e.RowKey)] = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            mockTableClient
                .Setup(c => c.UpdateEntityAsync(
                    It.IsAny<TableEntity>(),
                    It.IsAny<ETag>(),
                    It.IsAny<TableUpdateMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, ifMatch, _, _) =>
                {
                    if (!_store.TryGetValue((e.PartitionKey, e.RowKey), out var stored))
                        throw new RequestFailedException(404, "NotFound", "ResourceNotFound", null);
                    if (ifMatch != ETag.All && ifMatch != stored.ETag)
                        throw new RequestFailedException(412, "ConditionNotMet", "UpdateConditionNotSatisfied", null);
                    e.ETag = StampEtag();
                    _store[(e.PartitionKey, e.RowKey)] = e;
                    return Task.FromResult(new Mock<Response>().Object);
                });

            mockTableClient
                .Setup(c => c.GetEntityAsync<TableEntity>(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, string, IEnumerable<string>, CancellationToken>((pk, rk, _, _) =>
                {
                    if (throwOnGet != null) throw throwOnGet;
                    if (!_store.TryGetValue((pk, rk), out var entity))
                        throw new RequestFailedException(404, "NotFound", "ResourceNotFound", null);
                    return Task.FromResult(Response.FromValue(entity, new Mock<Response>().Object));
                });

            mockTableClient
                .Setup(c => c.DeleteEntityAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<ETag>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, string, ETag, CancellationToken>((pk, rk, _, _) =>
                {
                    if (throwOnDelete != null) throw throwOnDelete;
                    _store.Remove((pk, rk));
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(mockTableClient.Object);

            var storage = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
            Sut = new TableOffboardingAuditRepository(storage, NullLogger<TableOffboardingAuditRepository>.Instance);
        }

        private ETag StampEtag() => new($"\"0xFAKE_{++_etagCounter}\"");
    }
}
