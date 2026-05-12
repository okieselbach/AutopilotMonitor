using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// SDK-mocked behaviour tests for the SoftwareInventory side-row + counter ETag-CAS helpers
/// in <see cref="TableStorageService"/>. These are the writer-side correctness primitives that
/// the cascade decrement step (PR4) and the new VulnerabilityCorrelationService increment path
/// (PR2) depend on. Tests exercise the actual retry loops + clamp + 404 idempotency without
/// hitting Azure, matching repo convention.
/// </summary>
public class TableStorageInventoryCasTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    // ============================================================ IncrementSoftwareInventoryEntryAsync ====

    [Fact]
    public async Task Increment_creates_row_with_full_metadata_when_no_row_exists()
    {
        // First encounter for a (tenant, key) pair → AddEntity with all the display + normalization
        // metadata, SessionCount=1, FirstSeenAt/LastSeenAt populated.
        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "ResourceNotFound"));

        TableEntity? addedEntity = null;
        harness.SoftwareInventory.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, CancellationToken>((e, _) =>
            {
                addedEntity = e;
                return Task.FromResult(new Mock<Response>().Object);
            });

        var item = new SoftwareInventoryItem
        {
            NormalizedVendor = "contoso", NormalizedName = "widget", NormalizedVersion = "1.0",
            DisplayName = "Contoso Widget", Publisher = "Contoso Ltd.",
            RegistrySource = "x64", NormalizationConfidence = "high",
            CpeUri = "cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*",
        };

        await harness.Sut.IncrementSoftwareInventoryEntryAsync(TenantId, item, SessionId);

        Assert.NotNull(addedEntity);
        Assert.Equal(TenantId, addedEntity!.PartitionKey);
        Assert.Equal(1, addedEntity.GetInt32("SessionCount"));
        Assert.Equal("Contoso Widget", addedEntity.GetString("DisplayName"));
        Assert.Equal("widget", addedEntity.GetString("NormalizedName"));
        Assert.Equal("contoso", addedEntity.GetString("NormalizedVendor"));
        Assert.Equal("1.0", addedEntity.GetString("NormalizedVersion"));
        Assert.Equal("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", addedEntity.GetString("CpeUri"));
        Assert.Equal(SessionId, addedEntity.GetString("FirstSessionId"));
        Assert.Equal(SessionId, addedEntity.GetString("LastSessionId"));

        harness.SoftwareInventory.Verify(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.SoftwareInventory.Verify(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Increment_does_etag_cas_plus_one_when_row_exists()
    {
        var existing = new TableEntity(TenantId, "contoso:widget:1.0")
        {
            ["DisplayName"]   = "Contoso Widget",
            ["NormalizedName"] = "widget",
            ["SessionCount"]  = 3,
            ["LastSeenAt"]    = DateTime.UtcNow.AddDays(-1).ToString("o"),
            ["CpeUri"]        = "cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*",
        };
        existing.ETag = new ETag("0xABC");

        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));

        TableEntity? written = null;
        harness.SoftwareInventory.Setup(t => t.UpdateEntityAsync(
                It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, etag, _, _) =>
            {
                written = e;
                Assert.Equal(new ETag("0xABC"), etag);
                return Task.FromResult(new Mock<Response>().Object);
            });

        var item = new SoftwareInventoryItem
        {
            NormalizedVendor = "contoso", NormalizedName = "widget", NormalizedVersion = "1.0",
        };
        await harness.Sut.IncrementSoftwareInventoryEntryAsync(TenantId, item, SessionId);

        Assert.NotNull(written);
        Assert.Equal(4, written!.GetInt32("SessionCount"));
        Assert.Equal(SessionId, written.GetString("LastSessionId"));
        harness.SoftwareInventory.Verify(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Increment_retries_on_412_etag_conflict_and_succeeds_on_second_attempt()
    {
        var existing = new TableEntity(TenantId, "contoso:widget:1.0")
        {
            ["SessionCount"] = 2,
        };
        existing.ETag = new ETag("0xSTALE");

        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));

        var updateCalls = 0;
        harness.SoftwareInventory.Setup(t => t.UpdateEntityAsync(
                It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
            {
                updateCalls++;
                if (updateCalls == 1)
                {
                    throw new RequestFailedException(412, "Precondition Failed");
                }
                return Task.FromResult(new Mock<Response>().Object);
            });

        var item = new SoftwareInventoryItem
        {
            NormalizedVendor = "contoso", NormalizedName = "widget", NormalizedVersion = "1.0",
        };

        await harness.Sut.IncrementSoftwareInventoryEntryAsync(TenantId, item, SessionId);

        Assert.Equal(2, updateCalls);
    }

    [Fact]
    public async Task Increment_retries_on_first_create_race_409_and_falls_back_to_update_path()
    {
        // Race: two callers see no row, both try AddEntity. First wins (no 409). Second gets 409,
        // re-reads on next attempt, finds the row, and goes through the Update path.
        var harness = new Harness();
        var getCalls = 0;
        var existingAfterRace = new TableEntity(TenantId, "contoso:widget:1.0")
        {
            ["SessionCount"] = 1,
        };
        existingAfterRace.ETag = new ETag("0xPOSTRACE");

        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                getCalls++;
                if (getCalls == 1)
                {
                    return Task.FromException<Response<TableEntity>>(new RequestFailedException(404, "ResourceNotFound"));
                }
                return Task.FromResult(Response.FromValue(existingAfterRace, new Mock<Response>().Object));
            });

        harness.SoftwareInventory.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(409, "EntityAlreadyExists"));

        var updateCalls = 0;
        harness.SoftwareInventory.Setup(t => t.UpdateEntityAsync(
                It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
            {
                updateCalls++;
                return Task.FromResult(new Mock<Response>().Object);
            });

        var item = new SoftwareInventoryItem
        {
            NormalizedVendor = "contoso", NormalizedName = "widget", NormalizedVersion = "1.0",
        };
        await harness.Sut.IncrementSoftwareInventoryEntryAsync(TenantId, item, SessionId);

        Assert.Equal(2, getCalls);
        Assert.Equal(1, updateCalls);
    }

    [Fact]
    public async Task Increment_skips_empty_normalized_triple_silently()
    {
        var harness = new Harness();
        var item = new SoftwareInventoryItem(); // all empty
        await harness.Sut.IncrementSoftwareInventoryEntryAsync(TenantId, item, SessionId);

        harness.SoftwareInventory.Verify(t =>
            t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================ DecrementSoftwareInventoryEntryAsync ====

    [Fact]
    public async Task Decrement_is_no_op_when_row_missing_404()
    {
        // Idempotent: re-running the cascade on a session whose counters are already drained
        // must not throw. The helper returns silently.
        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "ResourceNotFound"));

        await harness.Sut.DecrementSoftwareInventoryEntryAsync(TenantId, "contoso", "widget", "1.0");

        harness.SoftwareInventory.Verify(t => t.UpdateEntityAsync(
            It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.SoftwareInventory.Verify(t =>
            t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Decrement_clamps_at_zero_and_deletes_the_row_when_count_drops_to_zero()
    {
        var existing = new TableEntity(TenantId, "contoso:widget:1.0")
        {
            ["SessionCount"] = 1,
        };
        existing.ETag = new ETag("0xLASTONE");

        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));

        harness.SoftwareInventory.Setup(t =>
            t.DeleteEntityAsync(TenantId, It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<Response>().Object);

        await harness.Sut.DecrementSoftwareInventoryEntryAsync(TenantId, "contoso", "widget", "1.0");

        // Row is deleted (count→0), NOT updated to a row with SessionCount=0.
        harness.SoftwareInventory.Verify(t =>
            t.DeleteEntityAsync(TenantId, It.IsAny<string>(), new ETag("0xLASTONE"), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.SoftwareInventory.Verify(t => t.UpdateEntityAsync(
            It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Decrement_subtracts_one_when_count_is_greater_than_one()
    {
        var existing = new TableEntity(TenantId, "contoso:widget:1.0")
        {
            ["SessionCount"] = 4,
        };
        existing.ETag = new ETag("0xSEVERAL");

        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));

        TableEntity? written = null;
        harness.SoftwareInventory.Setup(t => t.UpdateEntityAsync(
                It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
            {
                written = e;
                return Task.FromResult(new Mock<Response>().Object);
            });

        await harness.Sut.DecrementSoftwareInventoryEntryAsync(TenantId, "contoso", "widget", "1.0");

        Assert.NotNull(written);
        Assert.Equal(3, written!.GetInt32("SessionCount"));
        harness.SoftwareInventory.Verify(t =>
            t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Decrement_clamps_at_zero_even_when_existing_count_is_zero()
    {
        // Drift case: side-row promises a decrement but the counter is already at zero. Math.Max
        // keeps us safe; the row is deleted (count→0 already).
        var existing = new TableEntity(TenantId, "contoso:widget:1.0")
        {
            ["SessionCount"] = 0,
        };
        existing.ETag = new ETag("0xZERO");

        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));

        harness.SoftwareInventory.Setup(t =>
            t.DeleteEntityAsync(TenantId, It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<Response>().Object);

        await harness.Sut.DecrementSoftwareInventoryEntryAsync(TenantId, "contoso", "widget", "1.0");

        // Clamped → row deleted, never updated to a negative count.
        harness.SoftwareInventory.Verify(t =>
            t.DeleteEntityAsync(TenantId, It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Decrement_retries_on_412_and_succeeds_on_second_attempt()
    {
        var existing = new TableEntity(TenantId, "contoso:widget:1.0")
        {
            ["SessionCount"] = 5,
        };
        existing.ETag = new ETag("0xSTALE");

        var harness = new Harness();
        harness.SoftwareInventory.Setup(t =>
            t.GetEntityAsync<TableEntity>(TenantId, It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existing, new Mock<Response>().Object));

        var updateCalls = 0;
        harness.SoftwareInventory.Setup(t => t.UpdateEntityAsync(
                It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, _, _, _) =>
            {
                updateCalls++;
                if (updateCalls == 1)
                {
                    throw new RequestFailedException(412, "Precondition Failed");
                }
                return Task.FromResult(new Mock<Response>().Object);
            });

        await harness.Sut.DecrementSoftwareInventoryEntryAsync(TenantId, "contoso", "widget", "1.0");

        Assert.Equal(2, updateCalls);
    }

    // ============================================================ Harness ====

    /// <summary>
    /// SDK-mock harness for SoftwareInventory CAS helpers. Per-test setups configure
    /// <see cref="SoftwareInventory"/> for the (Get / Add / Update / Delete) call sequence
    /// the inventory partial drives.
    /// </summary>
    private sealed class Harness
    {
        public Mock<TableClient> SoftwareInventory { get; }
        public TableStorageService Sut { get; }

        public Harness()
        {
            SoftwareInventory = new Mock<TableClient>();
            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient
                .Setup(s => s.GetTableClient(Constants.TableNames.SoftwareInventory))
                .Returns(SoftwareInventory.Object);
            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
