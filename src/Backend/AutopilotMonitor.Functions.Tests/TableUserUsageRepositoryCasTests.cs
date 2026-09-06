using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The two MCP usage counters go through <see cref="TableCasRetry"/> with a retry budget sized for
/// parallel tool calls of one user (2026-09-06: the previous three-attempt loop without backoff lost
/// 3 of 6 concurrent increments). These tests pin the budget, the merge-patch shape and the
/// create-with-AddEntity path against a mocked table surface.
/// </summary>
public class TableUserUsageRepositoryCasTests
{
    private sealed class TableHarness
    {
        public Mock<TableClient> Table { get; } = new();
        public List<TableEntity> Added { get; } = new();
        public List<(TableEntity Entity, ETag ETag, TableUpdateMode Mode)> Updates { get; } = new();
        public Func<Response<TableEntity>> Read { get; set; } = () => throw new RequestFailedException(404, "not found");
        public Queue<Func<Response>> UpdateBehaviors { get; } = new();

        public TableHarness()
        {
            Table.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Read()));
            Table.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((e, _) =>
                {
                    Added.Add(e);
                    return Task.FromResult(Mock.Of<Response>());
                });
            Table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, tag, mode, _) =>
                {
                    Updates.Add((e, tag, mode));
                    return Task.FromResult(UpdateBehaviors.Count > 0 ? UpdateBehaviors.Dequeue()() : Mock.Of<Response>());
                });
        }

        public static Response<TableEntity> Row(string pk, string rk, long count, string etag)
        {
            var e = new TableEntity(pk, rk) { ["RequestCount"] = count };
            e.ETag = new ETag(etag);
            return Response.FromValue(e, Mock.Of<Response>());
        }

        public static Func<Response> Conflict => () => throw new RequestFailedException(412, "precondition failed");
    }

    private sealed class Harness
    {
        public TableHarness Users { get; } = new();
        public TableHarness Tenants { get; } = new();
        public TableUserUsageRepository Repo { get; }

        public Harness()
        {
            var service = new Mock<TableServiceClient>();
            service.Setup(s => s.GetTableClient(Constants.TableNames.UserUsageLog)).Returns(Users.Table.Object);
            service.Setup(s => s.GetTableClient(Constants.TableNames.McpTenantUsage)).Returns(Tenants.Table.Object);
            var storage = new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance);
            Repo = new TableUserUsageRepository(storage, NullLogger<TableUserUsageRepository>.Instance);
        }
    }

    [Fact]
    public void Retry_budget_is_sized_for_parallel_tool_calls()
    {
        Assert.Equal(8, TableUserUsageRepository.UsageCounterRetries);
        Assert.True(TableUserUsageRepository.UsageCounterRetries > TableCasRetry.DefaultRetries);
    }

    [Fact]
    public async Task Four_consecutive_etag_conflicts_still_land_the_user_increment()
    {
        var h = new Harness();
        h.Users.Read = () => TableHarness.Row("oid-1", "20260906_get_session_events:sessions_{id}_events", 5, "e1");
        for (var i = 0; i < 4; i++)
            h.Users.UpdateBehaviors.Enqueue(TableHarness.Conflict);

        await h.Repo.IncrementUsageAsync("oid-1", "user@example.test", "tid-home", "get_session_events:sessions_{id}_events");

        // The old loop gave up after three attempts; four conflicts plus the landing write = five updates.
        Assert.Equal(5, h.Users.Updates.Count);
        var (entity, etag, mode) = h.Users.Updates.Last();
        Assert.Equal(6L, entity.GetInt64("RequestCount"));
        Assert.Equal(new ETag("e1"), etag);
        Assert.Equal(TableUpdateMode.Merge, mode);
        Assert.Empty(h.Users.Added);
    }

    [Fact]
    public async Task Missing_user_row_is_created_with_add_and_full_attribution()
    {
        var h = new Harness();

        await h.Repo.IncrementUsageAsync("oid-1", "user@example.test", "tid-home", "get_metrics:metrics");

        var row = Assert.Single(h.Users.Added);
        Assert.Equal("oid-1", row.PartitionKey);
        Assert.EndsWith("_get_metrics:metrics", row.RowKey);
        Assert.Equal(1L, row.GetInt64("RequestCount"));
        Assert.Equal("user@example.test", row.GetString("UserPrincipalName"));
        Assert.Equal("tid-home", row.GetString("TenantId"));
        Assert.Equal("get_metrics:metrics", row.GetString("Endpoint"));
        Assert.Empty(h.Users.Updates);
    }

    [Fact]
    public async Task Tenant_increment_merges_only_the_counter_and_attribution_columns()
    {
        var h = new Harness();
        h.Tenants.Read = () => TableHarness.Row("tid-customer", "20260906_oid-1", 41, "t1");

        await h.Repo.IncrementTenantUsageAsync("tid-customer", "oid-1", "msp@example.test", "tid-home");

        var (entity, etag, mode) = Assert.Single(h.Tenants.Updates);
        Assert.Equal(TableUpdateMode.Merge, mode);
        Assert.Equal(new ETag("t1"), etag);
        Assert.Equal(42L, entity.GetInt64("RequestCount"));
        Assert.Equal("msp@example.test", entity.GetString("UserPrincipalName"));
        Assert.Equal("tid-home", entity.GetString("HomeTenantId"));
        Assert.NotNull(entity["LastRequestAt"]);
        // A merge patch never re-sends the identity columns of the row.
        Assert.False(entity.ContainsKey("Date"));
        Assert.False(entity.ContainsKey("UserId"));
    }

    [Fact]
    public async Task Tenant_increment_is_skipped_without_a_tenant_or_user()
    {
        var h = new Harness();

        await h.Repo.IncrementTenantUsageAsync("", "oid-1", null, null);
        await h.Repo.IncrementTenantUsageAsync("tid-customer", " ", null, null);

        Assert.Empty(h.Tenants.Updates);
        Assert.Empty(h.Tenants.Added);
    }
}
