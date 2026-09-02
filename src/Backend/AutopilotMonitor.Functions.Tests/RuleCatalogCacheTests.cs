using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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
/// The rule catalog read caches in TableStorageService.Rules.cs: a partition is queried once per
/// TTL, every write path invalidates it, hand-outs are clones, and a storage failure is served
/// as the usual empty result but never cached.
/// </summary>
public class RuleCatalogCacheTests
{
    private const string Tenant = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public async Task GatherRules_QueriedOncePerTtl_HandOutsAreClones()
    {
        var (storage, gather, _, _) = BuildStorage(gatherRows: new[] { GatherRow("global", "GATHER-001") });

        var first = await storage.GetGatherRulesAsync("global");
        var second = await storage.GetGatherRulesAsync("global");

        VerifyQueries(gather, Times.Once());
        Assert.Single(first);
        Assert.NotSame(first[0], second[0]);

        // Mutating a hand-out (what GatherRuleService does for per-tenant overrides) never
        // reaches the shared snapshot.
        first[0].Enabled = false;
        first[0].Parameters["x"] = "y";
        var third = await storage.GetGatherRulesAsync("global");
        Assert.True(third[0].Enabled);
        Assert.Empty(third[0].Parameters);
    }

    [Fact]
    public async Task GatherRules_StoreAndDelete_InvalidateThePartition()
    {
        var (storage, gather, _, _) = BuildStorage(gatherRows: new[] { GatherRow("global", "GATHER-001") });

        await storage.GetGatherRulesAsync("global");
        await storage.StoreGatherRuleAsync(new GatherRule { RuleId = "GATHER-002", Title = "t" }, "global");
        await storage.GetGatherRulesAsync("global");
        VerifyQueries(gather, Times.Exactly(2));

        await storage.DeleteGatherRuleAsync("global", "GATHER-002");
        await storage.GetGatherRulesAsync("global");
        VerifyQueries(gather, Times.Exactly(3));

        // A different partition is unaffected by the global writes.
        await storage.GetGatherRulesAsync(Tenant);
        await storage.GetGatherRulesAsync(Tenant);
        VerifyQueries(gather, Times.Exactly(4));
    }

    [Fact]
    public async Task RuleStates_CachedPerTenant_ValuesAreClones_WritesInvalidate()
    {
        var stateRow = new TableEntity(Tenant, "GATHER-001") { ["Enabled"] = false };
        var (storage, _, states, _) = BuildStorage(stateRows: new[] { stateRow });

        var first = await storage.GetRuleStatesAsync(Tenant);
        var second = await storage.GetRuleStatesAsync(Tenant);
        VerifyQueries(states, Times.Once());
        Assert.False(first["GATHER-001"].Enabled);
        Assert.NotSame(first["GATHER-001"], second["GATHER-001"]);

        await storage.StoreRuleStateAsync(Tenant, "GATHER-001", new RuleState { Enabled = true });
        await storage.GetRuleStatesAsync(Tenant);
        VerifyQueries(states, Times.Exactly(2));

        await storage.DeleteRuleStateAsync(Tenant, "GATHER-001");
        await storage.GetRuleStatesAsync(Tenant);
        VerifyQueries(states, Times.Exactly(3));
    }

    [Fact]
    public async Task ImePatterns_CachedPerPartition_DeleteInvalidates()
    {
        var patternRow = new TableEntity("global", "IME-ESP-PHASE") { ["Pattern"] = "^x$", ["Action"] = "espPhaseDetected", ["Enabled"] = true };
        var (storage, _, _, patterns) = BuildStorage(patternRows: new[] { patternRow });

        var first = await storage.GetImeLogPatternsAsync("global");
        var second = await storage.GetImeLogPatternsAsync("global");
        VerifyQueries(patterns, Times.Once());
        Assert.NotSame(first[0], second[0]);

        await storage.DeleteImeLogPatternAsync("global", "IME-ESP-PHASE");
        await storage.GetImeLogPatternsAsync("global");
        VerifyQueries(patterns, Times.Exactly(2));
    }

    [Fact]
    public async Task StorageFailure_YieldsEmpty_ButIsNeverCached()
    {
        var gather = new Mock<TableClient>();
        var calls = 0;
        gather.Setup(c => c.QueryAsync<TableEntity>(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1
                ? throw new RequestFailedException(503, "throttled")
                : AsAsyncPageable(new[] { GatherRow("global", "GATHER-001") }));
        var storage = BuildStorageWith(gather, new Mock<TableClient>(), new Mock<TableClient>());

        Assert.Empty(await storage.GetGatherRulesAsync("global"));
        Assert.Single(await storage.GetGatherRulesAsync("global"));
        Assert.Equal(2, calls);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static TableEntity GatherRow(string partition, string ruleId)
        => new(partition, ruleId) { ["Title"] = "Collect something", ["Enabled"] = true, ["IsBuiltIn"] = true };

    private static (TableStorageService storage, Mock<TableClient> gather, Mock<TableClient> states, Mock<TableClient> patterns) BuildStorage(
        TableEntity[]? gatherRows = null, TableEntity[]? stateRows = null, TableEntity[]? patternRows = null)
    {
        var gather = MockTableClientReturning(gatherRows ?? Array.Empty<TableEntity>());
        var states = MockTableClientReturning(stateRows ?? Array.Empty<TableEntity>());
        var patterns = MockTableClientReturning(patternRows ?? Array.Empty<TableEntity>());
        return (BuildStorageWith(gather, states, patterns), gather, states, patterns);
    }

    private static TableStorageService BuildStorageWith(Mock<TableClient> gather, Mock<TableClient> states, Mock<TableClient> patterns)
    {
        foreach (var m in new[] { gather, states, patterns })
        {
            m.Setup(c => c.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());
            m.Setup(c => c.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());
        }
        var serviceClient = new Mock<TableServiceClient>();
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.GatherRules)).Returns(gather.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.RuleStates)).Returns(states.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.ImeLogPatterns)).Returns(patterns.Object);
        return new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
    }

    private static Mock<TableClient> MockTableClientReturning(TableEntity[] rows)
    {
        var m = new Mock<TableClient>();
        m.Setup(c => c.QueryAsync<TableEntity>(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsAsyncPageable(rows));
        m.Setup(c => c.QueryAsync<TableEntity>(
                It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsAsyncPageable(rows));
        return m;
    }

    private static void VerifyQueries(Mock<TableClient> client, Times times)
        => client.Verify(c => c.QueryAsync<TableEntity>(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), times);

    private static AsyncPageable<TableEntity> AsAsyncPageable(TableEntity[] entities)
    {
        var page = Page<TableEntity>.FromValues(entities, continuationToken: null, new Mock<Response>().Object);
        return AsyncPageable<TableEntity>.FromPages(new[] { page });
    }
}
