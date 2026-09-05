using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

public class TableCasRetryTests
{
    private sealed class Harness
    {
        public Mock<TableClient> Table { get; } = new();
        public List<TableEntity> Added { get; } = new();
        public List<(TableEntity Entity, ETag ETag, TableUpdateMode Mode)> Updates { get; } = new();
        public Queue<Func<Response<TableEntity>>> Reads { get; } = new();
        public Queue<Func<Response>> UpdateBehaviors { get; } = new();
        public Queue<Func<Response>> AddBehaviors { get; } = new();

        public Harness()
        {
            Table.Setup(t => t.GetEntityAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Reads.Dequeue()()));
            Table.Setup(t => t.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((e, _) =>
                {
                    Added.Add(e);
                    return Task.FromResult(AddBehaviors.Count > 0 ? AddBehaviors.Dequeue()() : Mock.Of<Response>());
                });
            Table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, tag, mode, _) =>
                {
                    Updates.Add((e, tag, mode));
                    return Task.FromResult(UpdateBehaviors.Count > 0 ? UpdateBehaviors.Dequeue()() : Mock.Of<Response>());
                });
        }

        public static Response<TableEntity> Row(long value, string etag)
        {
            var e = new TableEntity("pk", "rk") { ["Counter"] = value };
            e.ETag = new ETag(etag);
            return Response.FromValue(e, Mock.Of<Response>());
        }

        public static Func<Response<TableEntity>> Missing => () => throw new RequestFailedException(404, "not found");
    }

    private static Task<bool> Increment(Harness h, int retries = TableCasRetry.DefaultRetries)
        => TableCasRetry.MutateAsync(
            h.Table.Object, "pk", "rk",
            patch: read => new TableEntity("pk", "rk") { ["Counter"] = (read.GetInt64("Counter") ?? 0) + 1 },
            createMissing: () => new TableEntity("pk", "rk") { ["Counter"] = 1L },
            operation: "test", tableName: "T", metrics: null, logger: NullLogger.Instance, retries: retries);

    [Fact]
    public async Task Existing_row_is_patched_with_its_etag_as_a_merge()
    {
        var h = new Harness();
        h.Reads.Enqueue(() => Harness.Row(5, "e1"));

        Assert.True(await Increment(h));

        var (entity, etag, mode) = Assert.Single(h.Updates);
        Assert.Equal(6L, entity.GetInt64("Counter"));
        Assert.Equal(new ETag("e1"), etag);
        Assert.Equal(TableUpdateMode.Merge, mode);
        Assert.Empty(h.Added);
    }

    [Fact]
    public async Task Missing_row_is_created_with_add_not_upsert()
    {
        var h = new Harness();
        h.Reads.Enqueue(Harness.Missing);

        Assert.True(await Increment(h));

        Assert.Equal(1L, Assert.Single(h.Added).GetInt64("Counter"));
        Assert.Empty(h.Updates);
    }

    [Fact]
    public async Task Concurrent_creator_turns_the_add_into_a_retry_that_lands_in_the_update_branch()
    {
        var h = new Harness();
        h.Reads.Enqueue(Harness.Missing);
        h.AddBehaviors.Enqueue(() => throw new RequestFailedException(409, "exists", "EntityAlreadyExists", null));
        h.Reads.Enqueue(() => Harness.Row(1, "e2"));

        Assert.True(await Increment(h));

        Assert.Single(h.Added);
        Assert.Equal(2L, Assert.Single(h.Updates).Entity.GetInt64("Counter"));
    }

    [Fact]
    public async Task Etag_conflict_re_reads_and_retries()
    {
        var h = new Harness();
        h.Reads.Enqueue(() => Harness.Row(5, "e1"));
        h.UpdateBehaviors.Enqueue(() => throw new RequestFailedException(412, "precondition failed"));
        h.Reads.Enqueue(() => Harness.Row(9, "e2"));

        Assert.True(await Increment(h));

        Assert.Equal(2, h.Updates.Count);
        Assert.Equal(10L, h.Updates[1].Entity.GetInt64("Counter"));
        Assert.Equal(new ETag("e2"), h.Updates[1].ETag);
    }

    [Fact]
    public async Task Retries_are_bounded()
    {
        var h = new Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Reads.Enqueue(() => Harness.Row(1, "e"));
            h.UpdateBehaviors.Enqueue(() => throw new RequestFailedException(412, "precondition failed"));
        }

        Assert.False(await Increment(h, retries: 3));
        Assert.Equal(3, h.Updates.Count);
    }

    [Fact]
    public async Task No_op_patch_writes_nothing()
    {
        var h = new Harness();
        h.Reads.Enqueue(() => Harness.Row(5, "e1"));

        var landed = await TableCasRetry.MutateAsync(
            h.Table.Object, "pk", "rk",
            patch: _ => null,
            createMissing: () => new TableEntity("pk", "rk"),
            operation: "test", tableName: "T", metrics: null, logger: NullLogger.Instance);

        Assert.False(landed);
        Assert.Empty(h.Updates);
        Assert.Empty(h.Added);
    }
}
