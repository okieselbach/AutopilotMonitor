using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// Startup table initialization is gated by a schema sentinel (hash over TableNames.All)
    /// stored in AdminConfiguration: a matching sentinel means zero CreateTableIfNotExists
    /// calls on a cold start; anything else runs the full idempotent pass and rewrites it.
    /// </summary>
    public class TableSchemaSentinelTests
    {
        private sealed class Harness
        {
            public Mock<TableServiceClient> Service { get; } = new();
            public Mock<TableClient> AdminConfig { get; } = new();
            public TableEntity? UpsertedSentinel { get; private set; }
            public int CreateCalls;

            public Harness()
            {
                Service.Setup(s => s.GetTableClient(Constants.TableNames.AdminConfiguration)).Returns(AdminConfig.Object);
                Service.Setup(s => s.CreateTableIfNotExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns<string, CancellationToken>((_, _) =>
                    {
                        Interlocked.Increment(ref CreateCalls);
                        return Task.FromResult(Response.FromValue(new TableItem("x"), Mock.Of<Response>()));
                    });
                AdminConfig.Setup(c => c.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                    .Callback<ITableEntity, TableUpdateMode, CancellationToken>((e, _, _) => UpsertedSentinel = (TableEntity)e)
                    .ReturnsAsync(Mock.Of<Response>());
            }

            public void SentinelReturns(string? hash)
            {
                var response = new Mock<NullableResponse<TableEntity>>();
                response.SetupGet(r => r.HasValue).Returns(hash != null);
                if (hash != null)
                {
                    response.SetupGet(r => r.Value).Returns(new TableEntity(
                        TableStorageService.SchemaSentinelPartitionKey, TableStorageService.SchemaSentinelRowKey)
                    {
                        [TableStorageService.SchemaSentinelHashProperty] = hash
                    });
                }
                AdminConfig.Setup(c => c.GetEntityIfExistsAsync<TableEntity>(
                        TableStorageService.SchemaSentinelPartitionKey, TableStorageService.SchemaSentinelRowKey,
                        It.IsAny<System.Collections.Generic.IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(response.Object);
            }

            public void SentinelThrows()
            {
                AdminConfig.Setup(c => c.GetEntityIfExistsAsync<TableEntity>(
                        It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<System.Collections.Generic.IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new RequestFailedException(404, "TableNotFound"));
            }

            public TableStorageService Build() => new(Service.Object, NullLogger<TableStorageService>.Instance);
        }

        private static string CurrentHash => TableStorageService.ComputeTableSchemaHash(Constants.TableNames.All);

        [Fact]
        public void Hash_IsOrderIndependent_AndChangesWhenTableAdded()
        {
            var a = TableStorageService.ComputeTableSchemaHash(new[] { "B", "A" });
            var b = TableStorageService.ComputeTableSchemaHash(new[] { "A", "B" });
            var c = TableStorageService.ComputeTableSchemaHash(new[] { "A", "B", "C" });

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(64, a.Length);
        }

        [Fact]
        public async Task MatchingSentinel_SkipsTableCreation()
        {
            var h = new Harness();
            h.SentinelReturns(CurrentHash);

            var fullPass = await h.Build().InitializeTablesAsync();

            Assert.False(fullPass);
            Assert.Equal(0, h.CreateCalls);
            Assert.Null(h.UpsertedSentinel);
        }

        [Fact]
        public async Task StaleSentinel_RunsFullPass_AndRewritesSentinel()
        {
            var h = new Harness();
            h.SentinelReturns("STALE");

            var fullPass = await h.Build().InitializeTablesAsync();

            Assert.True(fullPass);
            Assert.Equal(Constants.TableNames.All.Length, h.CreateCalls);
            Assert.NotNull(h.UpsertedSentinel);
            Assert.Equal(CurrentHash, h.UpsertedSentinel!.GetString(TableStorageService.SchemaSentinelHashProperty));
        }

        [Fact]
        public async Task MissingSentinel_RunsFullPass()
        {
            var h = new Harness();
            h.SentinelReturns(null);

            Assert.True(await h.Build().InitializeTablesAsync());
            Assert.Equal(Constants.TableNames.All.Length, h.CreateCalls);
        }

        [Fact]
        public async Task UnreadableSentinel_FreshStorage_RunsFullPass()
        {
            var h = new Harness();
            h.SentinelThrows();

            Assert.True(await h.Build().InitializeTablesAsync());
            Assert.Equal(Constants.TableNames.All.Length, h.CreateCalls);
            Assert.NotNull(h.UpsertedSentinel);
        }

        [Fact]
        public async Task FailedTableCreate_DoesNotWriteSentinel_AndRetriesNextCall()
        {
            var h = new Harness();
            h.SentinelReturns(null);
            var failing = Constants.TableNames.All.First();
            h.Service.Setup(s => s.CreateTableIfNotExistsAsync(failing, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(500, "boom"));
            var svc = h.Build();

            Assert.True(await svc.InitializeTablesAsync());
            Assert.Null(h.UpsertedSentinel);

            // Not marked initialized → a second call runs the pass again instead of short-circuiting.
            var before = h.CreateCalls;
            Assert.True(await svc.InitializeTablesAsync());
            Assert.True(h.CreateCalls > before);
        }

        [Fact]
        public async Task SecondCallAfterSuccess_IsNoOp()
        {
            var h = new Harness();
            h.SentinelReturns(null);
            var svc = h.Build();

            await svc.InitializeTablesAsync();
            var calls = h.CreateCalls;
            Assert.False(await svc.InitializeTablesAsync());
            Assert.Equal(calls, h.CreateCalls);
        }

        [Fact]
        public async Task BackfillClaim_WinnerTrue_LoserFalse()
        {
            var h = new Harness();
            h.AdminConfig.SetupSequence(c => c.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>())
                .ThrowsAsync(new RequestFailedException(409, "EntityAlreadyExists"));
            var svc = h.Build();

            Assert.True(await svc.TryClaimSessionIndexBackfillAsync());
            Assert.False(await svc.TryClaimSessionIndexBackfillAsync());
        }
    }
}
