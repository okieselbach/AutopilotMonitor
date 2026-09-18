using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Storage side of the platform rollup: the platform row is merged with the ETag it was read
/// with and never carries IssuesDetected, the tenant floors land in one write, and the reads
/// that feed the rollup baseline are scoped to exactly the rows they mean.
/// </summary>
public class PlatformStatsStorageTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private sealed class Harness
    {
        public Mock<TableClient> Table { get; } = new();
        public List<string> Filters { get; } = new();
        public List<List<TableTransactionAction>> Submitted { get; } = new();
        public Func<string, TableEntity[]> RowsFor { get; set; } = _ => Array.Empty<TableEntity>();
        public Queue<Exception> SubmitFailures { get; } = new();
        public TableStorageService Sut { get; }

        public Harness()
        {
            Table.Setup(c => c.QueryAsync<TableEntity>(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns((string filter, int? _, IEnumerable<string> _, CancellationToken _) =>
                {
                    Filters.Add(filter);
                    var page = Page<TableEntity>.FromValues(RowsFor(filter), null, Mock.Of<Response>());
                    return AsyncPageable<TableEntity>.FromPages(new[] { page });
                });
            Table.Setup(c => c.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) =>
                {
                    var snapshot = actions.ToList();
                    Submitted.Add(snapshot);
                    if (SubmitFailures.Count > 0) throw SubmitFailures.Dequeue();
                    var inner = (IReadOnlyList<Response>)snapshot.Select(_ => Mock.Of<Response>()).ToList();
                    return Task.FromResult(Response.FromValue(inner, Mock.Of<Response>()));
                });

            var service = new Mock<TableServiceClient>();
            service.Setup(s => s.GetTableClient(Constants.TableNames.PlatformStats)).Returns(Table.Object);
            Sut = new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance);
        }
    }

    private static TableEntity PlatformRow(long enrollments, long issues, string etag)
    {
        var row = new TableEntity("global", "current") { ["TotalEnrollments"] = enrollments, ["IssuesDetected"] = issues };
        row.ETag = new ETag(etag);
        return row;
    }

    private static TableEntity BaselineRow(long enrollments)
        => new("global", "rollup-baseline") { ["TotalEnrollments"] = enrollments };

    // ---------- rollup write ----------

    [Fact]
    public async Task Rollup_merges_the_platform_row_with_its_read_etag_and_leaves_IssuesDetected_alone()
    {
        var h = new Harness { RowsFor = _ => new[] { PlatformRow(19_312, issues: 35_000, etag: "e1"), BaselineRow(21_000) } };
        PlatformRollupSources? seenBaseline = null;

        var result = await h.Sut.RollupPlatformStatsAsync(
            (existing, baseline) =>
            {
                seenBaseline = baseline;
                return new PlatformStats { TotalEnrollments = existing!.TotalEnrollments + 40, IssuesDetected = existing.IssuesDetected };
            },
            new PlatformRollupSources { TotalEnrollments = 21_040 });

        Assert.Equal(21_000, seenBaseline!.TotalEnrollments);
        Assert.Equal(19_352, result!.TotalEnrollments);
        Assert.Equal("PartitionKey eq 'global'", Assert.Single(h.Filters));

        var batch = Assert.Single(h.Submitted);
        var platform = batch.Single(a => a.Entity.RowKey == "current");
        Assert.Equal(TableTransactionActionType.UpdateMerge, platform.ActionType);
        Assert.Equal(new ETag("e1"), platform.ETag);
        Assert.Equal(19_352L, ((TableEntity)platform.Entity).GetInt64("TotalEnrollments"));
        Assert.False(((TableEntity)platform.Entity).ContainsKey("IssuesDetected"),
            "a write-back of IssuesDetected undoes every increment that landed since the read");

        var baselineWrite = batch.Single(a => a.Entity.RowKey == "rollup-baseline");
        Assert.Equal(TableTransactionActionType.UpsertReplace, baselineWrite.ActionType);
        Assert.Equal(21_040L, ((TableEntity)baselineWrite.Entity).GetInt64("TotalEnrollments"));
    }

    [Fact]
    public async Task Rollup_re_reads_and_re_merges_when_the_platform_row_changed_in_between()
    {
        var reads = 0;
        var h = new Harness
        {
            RowsFor = _ => ++reads == 1
                ? new[] { PlatformRow(19_312, issues: 1, etag: "e1"), BaselineRow(100) }
                : new[] { PlatformRow(21_800, issues: 2, etag: "e2"), BaselineRow(100) } // corrected in between
        };
        h.SubmitFailures.Enqueue(new RequestFailedException(412, "precondition failed"));

        var result = await h.Sut.RollupPlatformStatsAsync(
            (existing, _) => new PlatformStats { TotalEnrollments = existing!.TotalEnrollments + 5 },
            new PlatformRollupSources { TotalEnrollments = 105 });

        Assert.Equal(21_805, result!.TotalEnrollments);
        Assert.Equal(2, h.Submitted.Count);
        Assert.Equal(new ETag("e2"), h.Submitted[1].Single(a => a.Entity.RowKey == "current").ETag);
    }

    [Fact]
    public async Task Rollup_adds_a_missing_platform_row_instead_of_upserting_it()
    {
        var h = new Harness();

        await h.Sut.RollupPlatformStatsAsync(
            (existing, baseline) =>
            {
                Assert.Null(existing);
                Assert.Null(baseline);
                return new PlatformStats { TotalEnrollments = 10 };
            },
            new PlatformRollupSources { TotalEnrollments = 10 });

        var platform = Assert.Single(h.Submitted).Single(a => a.Entity.RowKey == "current");
        Assert.Equal(TableTransactionActionType.Add, platform.ActionType);
        Assert.True(((TableEntity)platform.Entity).ContainsKey("IssuesDetected"));
    }

    // ---------- tenant counters ----------

    private static (Mock<TableClient> Table, TableStorageService Sut, List<(TableEntity Entity, ETag ETag, TableUpdateMode Mode)> Updates) TenantRowHarness(TableEntity row)
    {
        var table = new Mock<TableClient>();
        table.Setup(t => t.GetEntityAsync<TableEntity>(Tenant, "current", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(row, Mock.Of<Response>()));
        var updates = new List<(TableEntity, ETag, TableUpdateMode)>();
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, tag, mode, _) => { updates.Add((e, tag, mode)); return Task.FromResult(Mock.Of<Response>()); });
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(Constants.TableNames.PlatformStats)).Returns(table.Object);
        return (table, new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance), updates);
    }

    [Fact]
    public async Task Floors_raise_only_the_counters_below_their_floor_in_one_merge()
    {
        var row = new TableEntity(Tenant, "current") { ["TotalEnrollments"] = 500L, ["SuccessfulEnrollments"] = 10L };
        row.ETag = new ETag("t1");
        var (_, sut, updates) = TenantRowHarness(row);

        var inPlace = await sut.EnsureTenantStatFloorsAsync(Tenant, new Dictionary<string, long>
        {
            ["TotalEnrollments"] = 120,       // below the counter: retention pruned, must not lower
            ["SuccessfulEnrollments"] = 90,   // above: heal
            ["TotalEventsProcessed"] = 30_000 // field missing on the row: seed
        });

        Assert.True(inPlace);
        var update = Assert.Single(updates);
        Assert.Equal(new ETag("t1"), update.ETag);
        Assert.Equal(TableUpdateMode.Merge, update.Mode);
        Assert.False(update.Entity.ContainsKey("TotalEnrollments"));
        Assert.Equal(90L, update.Entity.GetInt64("SuccessfulEnrollments"));
        Assert.Equal(30_000L, update.Entity.GetInt64("TotalEventsProcessed"));
    }

    [Fact]
    public async Task Floors_already_met_write_nothing_and_still_count_as_in_place()
    {
        var row = new TableEntity(Tenant, "current") { ["TotalEnrollments"] = 500L };
        row.ETag = new ETag("t1");
        var (_, sut, updates) = TenantRowHarness(row);

        var inPlace = await sut.EnsureTenantStatFloorsAsync(Tenant, new Dictionary<string, long> { ["TotalEnrollments"] = 120 });

        Assert.True(inPlace);
        Assert.Empty(updates);
    }

    [Fact]
    public async Task Floors_that_cannot_be_written_report_false()
    {
        var row = new TableEntity(Tenant, "current") { ["TotalEnrollments"] = 1L };
        row.ETag = new ETag("t1");
        var (table, sut, _) = TenantRowHarness(row);
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "storage down"));

        Assert.False(await sut.EnsureTenantStatFloorsAsync(Tenant, new Dictionary<string, long> { ["TotalEnrollments"] = 120 }));
    }

    [Fact]
    public async Task Increment_adds_to_the_one_field()
    {
        var row = new TableEntity(Tenant, "current") { ["TotalEnrollments"] = 500L, ["TotalEventsProcessed"] = 1_000L };
        row.ETag = new ETag("t1");
        var (_, sut, updates) = TenantRowHarness(row);

        await sut.IncrementTenantStatAsync(Tenant, "TotalEventsProcessed", 37);

        var update = Assert.Single(updates);
        Assert.Equal(1_037L, update.Entity.GetInt64("TotalEventsProcessed"));
        Assert.False(update.Entity.ContainsKey("TotalEnrollments"), "the patch must not carry fields it did not change");
    }

    [Fact]
    public async Task All_tenant_stats_read_the_tenant_rows_only()
    {
        var h = new Harness
        {
            RowsFor = _ => new[]
            {
                new TableEntity(Tenant, "current") { ["TotalEnrollments"] = 7L, ["SuccessfulEnrollments"] = 5L, ["TotalEventsProcessed"] = 900L },
            }
        };

        var stats = Assert.Single(await h.Sut.GetAllTenantStatsAsync());

        Assert.Equal("RowKey eq 'current' and PartitionKey ne 'global'", Assert.Single(h.Filters));
        Assert.Equal(7, stats.TotalEnrollments);
        Assert.Equal(5, stats.SuccessfulEnrollments);
        Assert.Equal(900, stats.TotalEventsProcessed);
    }

    [Fact]
    public async Task A_failing_tenant_stats_read_throws_instead_of_reporting_zero()
    {
        // Zero would become the rollup baseline and the next run would count every tenant
        // counter as growth a second time.
        var h = new Harness { RowsFor = _ => throw new RequestFailedException(503, "busy") };

        await Assert.ThrowsAsync<RequestFailedException>(() => h.Sut.GetAllTenantStatsAsync());
    }

    // ---------- seen device models ----------

    [Fact]
    public async Task Seen_models_adds_only_unknown_models_and_returns_the_size_of_the_set()
    {
        var known = TableStorageService.SeenDeviceModelRowKey("LENOVO ThinkPad T14 Gen 6");
        var h = new Harness { RowsFor = _ => new[] { new TableEntity("models", known), new TableEntity("models", "PRUNEDMODELHASH") } };

        var size = await h.Sut.RecordSeenDeviceModelsAsync(new[]
        {
            "lenovo thinkpad t14 gen 6",          // known, different casing
            "HP EliteBook 840 14 inch G10/#1",    // new, with characters a table key cannot hold
            " ",
        });

        Assert.Equal(3, size);
        Assert.Equal("PartitionKey eq 'models'", Assert.Single(h.Filters));
        var added = Assert.Single(Assert.Single(h.Submitted));
        Assert.Equal("models", added.Entity.PartitionKey);
        Assert.Equal("HP EliteBook 840 14 inch G10/#1", ((TableEntity)added.Entity).GetString("Model"));
        Assert.Matches("^[0-9A-F]{64}$", added.Entity.RowKey);
    }
}
