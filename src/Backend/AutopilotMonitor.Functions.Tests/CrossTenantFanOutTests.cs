using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
/// The cross-tenant aggregate primitives (perf audit 2026-09-02): the tenant list is one cached
/// snapshot, every aggregate is one PartitionKey-anchored query per tenant instead of a
/// cross-partition scan, the session window is a RowKey key range on SessionsIndex, and the
/// app-summary snapshot is shared single-flight across the endpoints that fire together.
/// </summary>
public class CrossTenantFanOutTests
{
    private const string TenantA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string TenantB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string TenantC = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

    [Fact]
    public async Task SessionStats_DrainsIndexPerTenant_WithRowKeyBound_AndIsCached()
    {
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 120), IndexRow(TenantB, "s2", SessionStatus.Failed, null));
        var sessions = MockTableClientReturning();
        var config = MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB));
        var storage = BuildStorage(config, sessions, index, apps: new Mock<TableClient>(), counters: Counters(TenantA, TenantB));

        var stats = await storage.GetAllSessionStatsAsync(tenantIdFilter: null, days: 7, allowedTenantIds: null);
        var again = await storage.GetAllSessionStatsAsync(tenantIdFilter: null, days: 7, allowedTenantIds: null);

        // Two tenants → two partition-anchored, RowKey-bounded, projected queries; the second call is a cache hit.
        var filters = index.Filters;
        Assert.Equal(2, filters.Count);
        Assert.Contains(filters, f => f.StartsWith($"PartitionKey eq '{TenantA}'") && f.Contains(" and RowKey lt '"));
        Assert.Contains(filters, f => f.StartsWith($"PartitionKey eq '{TenantB}'") && f.Contains(" and RowKey lt '"));
        Assert.All(index.Selects, s => Assert.Equal(TableStorageService.SessionStatsProjection, s));
        Assert.Same(stats, again);
        AssertNeverQueried(sessions);
        // The tenant list was read once and shared.
        config.Verify(c => c.QueryAsync<TableEntity>(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once());
        Assert.Equal(2, stats.TotalLastNDays);
    }

    [Fact]
    public async Task SessionStats_BoundedCaller_FansOutOnlyManagedTenants()
    {
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 60));
        var config = MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB));
        var storage = BuildStorage(config, MockTableClientReturning(), index, apps: new Mock<TableClient>(), counters: Counters(TenantA, TenantB));

        await storage.GetAllSessionStatsAsync(tenantIdFilter: null, days: 30, allowedTenantIds: new[] { TenantB.ToUpperInvariant() });

        var filter = Assert.Single(index.Filters);
        Assert.StartsWith($"PartitionKey eq '{TenantB}'", filter);
    }

    [Fact]
    public async Task GeoWindow_CrossTenant_ReadsIndexKeyRange_NeverTheSessionsTable()
    {
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 60));
        var sessions = MockTableClientReturning();
        var storage = BuildStorage(MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB)), sessions, index, apps: new Mock<TableClient>(), counters: Counters(TenantA, TenantB));

        var start = DateTime.UtcNow.AddDays(-30);
        var end = DateTime.UtcNow.AddDays(1);
        var rows = await storage.GetGeoWindowSessionsAsync(start, end, tenantId: null);

        Assert.Single(rows);
        Assert.Equal(2, index.Filters.Count);
        Assert.All(index.Filters, f => Assert.Matches("^PartitionKey eq '[0-9a-f-]+' and RowKey ge '\\d{19}' and RowKey lt '\\d{19}'$", f));
        Assert.All(index.Selects, s => Assert.Equal(TableStorageService.GeoMetricsSessionProjection, s));
        AssertNeverQueried(sessions);
    }

    [Fact]
    public async Task GeoWindow_EmptyConfig_FallsBackToSessionsScan()
    {
        var sessions = MockTableClientReturning();
        var index = MockTableClientReturning();
        var storage = BuildStorage(MockTableClientReturning(), sessions, index, apps: new Mock<TableClient>());

        await storage.GetGeoWindowSessionsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, tenantId: null);

        Assert.Single(sessions.Filters);
        AssertNeverQueried(index);
    }

    [Fact]
    public async Task AppSummaries_CrossTenant_OnePartitionQueryPerTenant_SharedAcrossConcurrentCallers()
    {
        var apps = MockTableClientReturning(AppRow(TenantA, "s1", "Teams"), AppRow(TenantB, "s2", "Office"));
        var storage = BuildStorage(MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB)), MockTableClientReturning(), MockTableClientReturning(), apps, counters: Counters(TenantA, TenantB));

        var since = DateTime.UtcNow.AddDays(-30);
        // The Installs tab fires these two at once; the usage compute adds the refs projection.
        var metricsTask = storage.GetAppMetricsSummariesAsync(since, tenantId: null);
        var dashboardTask = storage.GetAppsDashboardSummariesAsync(since, tenantId: null);
        var refsTask = storage.GetAppInstallRefsAsync(since, tenantId: null);
        await Task.WhenAll(metricsTask, dashboardTask, refsTask);

        Assert.Equal(2, apps.Filters.Count);
        Assert.All(apps.Filters, f => Assert.Matches("^PartitionKey eq '[0-9a-f-]+' and StartedAt ge datetime'", f));
        Assert.All(apps.Selects, s => Assert.Equal(TableStorageService.CrossTenantAppScanProjection.Value, s));
        Assert.Equal(2, (await metricsTask).Count);
        Assert.Equal(2, (await refsTask).Count);
        // Each caller gets its own list over the shared rows.
        Assert.NotSame(await metricsTask, await dashboardTask);
    }

    [Fact]
    public async Task AppSummaries_TenantScoped_StaysASinglePartitionQuery_Uncached()
    {
        var apps = MockTableClientReturning(AppRow(TenantA, "s1", "Teams"));
        var storage = BuildStorage(MockTableClientReturning(ConfigRow(TenantA)), MockTableClientReturning(), MockTableClientReturning(), apps);

        var since = DateTime.UtcNow.AddDays(-7);
        await storage.GetAppMetricsSummariesAsync(since, TenantA);
        await storage.GetAppMetricsSummariesAsync(since, TenantA);

        Assert.Equal(2, apps.Filters.Count);
        Assert.All(apps.Filters, f => Assert.StartsWith($"PartitionKey eq '{TenantA}' and StartedAt ge", f));
        Assert.All(apps.Selects, s => Assert.Equal(TableStorageService.AppMetricsProjection, s));
    }

    // ── enrolled-tenant snapshot: the fan-out width is the set of tenants with an enrollment on record ──

    [Fact]
    public async Task SessionList_FansOutOnlyOverEnrolledTenants_AndScansCountersOncePerTtl()
    {
        // Config knows three tenants: A enrolled, B's counter row stands at 0, C has no counter row at all.
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 60));
        var counters = MockTableClientReturning(CounterRow(TenantA, 5), CounterRow(TenantB, 0));
        var storage = BuildStorage(
            MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB), ConfigRow(TenantC)),
            MockTableClientReturning(), index, apps: new Mock<TableClient>(), counters: counters);

        var page = await storage.GetAllSessionsPageAsync(tenantIdFilter: null, days: null, pageSize: 10, continuation: null);
        await storage.GetAllSessionsPageAsync(tenantIdFilter: null, days: null, pageSize: 10, continuation: null);

        Assert.Single(page.Items);
        // Two pages, one partition query each — only the enrolled tenant is ever asked.
        Assert.Equal(2, index.Filters.Count);
        Assert.All(index.Filters, f => Assert.StartsWith($"PartitionKey eq '{TenantA}'", f));
        // The marker is one projected property-filter read per TTL, shared by both pages.
        Assert.Equal("RowKey eq 'current'", Assert.Single(counters.Filters));
        Assert.Equal(new[] { "PartitionKey", "TotalEnrollments" }, Assert.Single(counters.Selects));
    }

    [Fact]
    public async Task SessionList_CounterScanFailure_FailsOpenToEveryConfiguredTenant()
    {
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 60));
        var counters = new RecordingTableClient();
        counters.Setup(c => c.QueryAsync<TableEntity>(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException(503, "PlatformStats unavailable"));
        var storage = BuildStorage(
            MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB)),
            MockTableClientReturning(), index, apps: new Mock<TableClient>(), counters: counters);

        var page = await storage.GetAllSessionsPageAsync(tenantIdFilter: null, days: null, pageSize: 10, continuation: null);

        // A failed marker read must never narrow the fan-out: every configured tenant is queried.
        Assert.Single(page.Items);
        Assert.Equal(2, index.Filters.Count);
    }

    [Fact]
    public async Task SessionList_CounterRowOfAnUnconfiguredTenant_IsNeverQueried()
    {
        // An offboarded tenant may keep its counter row and even index rows; without a config row it is out.
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 60), IndexRow(TenantB, "s2", SessionStatus.Failed, null));
        var storage = BuildStorage(
            MockTableClientReturning(ConfigRow(TenantA)),
            MockTableClientReturning(), index, apps: new Mock<TableClient>(), counters: Counters(TenantA, TenantB));

        var page = await storage.GetAllSessionsPageAsync(tenantIdFilter: null, days: null, pageSize: 10, continuation: null);

        Assert.Single(page.Items);
        Assert.StartsWith($"PartitionKey eq '{TenantA}'", Assert.Single(index.Filters));
    }

    [Fact]
    public async Task SessionList_ConfiguredButNobodyEnrolled_IsAnEmptyPage_NeverTheLegacyScan()
    {
        // The primary-table scan is the EMPTY-CONFIG safety net only; "configured, nobody enrolled" is simply empty.
        var sessions = MockTableClientReturning(new TableEntity(TenantA, "leak"));
        var index = MockTableClientReturning();
        var storage = BuildStorage(
            MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB)),
            sessions, index, apps: new Mock<TableClient>(), counters: MockTableClientReturning());

        var page = await storage.GetAllSessionsPageAsync(tenantIdFilter: null, days: null, pageSize: 10, continuation: null);
        var stats = await storage.GetAllSessionStatsAsync(tenantIdFilter: null, days: 7, allowedTenantIds: null);

        Assert.Empty(page.Items);
        Assert.Null(page.NextRawToken);
        Assert.Equal(0, stats.TotalLastNDays);
        AssertNeverQueried(sessions);
        AssertNeverQueried(index);
    }

    [Fact]
    public async Task SessionStats_BoundedCaller_IsCutToEnrolledTenants_AfterTheBound()
    {
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 60), IndexRow(TenantB, "s2", SessionStatus.Succeeded, 60));
        var storage = BuildStorage(
            MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB)),
            MockTableClientReturning(), index, apps: new Mock<TableClient>(), counters: Counters(TenantA));

        // The delegated caller manages both tenants; only A ever enrolled.
        await storage.GetAllSessionStatsAsync(tenantIdFilter: null, days: 30, allowedTenantIds: new[] { TenantA, TenantB });

        Assert.StartsWith($"PartitionKey eq '{TenantA}'", Assert.Single(index.Filters));
    }

    [Fact]
    public async Task GeoWindowAndAppSummaries_CrossTenant_SkipTenantsWithoutEnrollment()
    {
        var index = MockTableClientReturning(IndexRow(TenantA, "s1", SessionStatus.Succeeded, 60));
        var apps = MockTableClientReturning(AppRow(TenantA, "s1", "Teams"));
        var storage = BuildStorage(
            MockTableClientReturning(ConfigRow(TenantA), ConfigRow(TenantB)),
            MockTableClientReturning(), index, apps, counters: Counters(TenantA));

        await storage.GetGeoWindowSessionsAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(1), tenantId: null);
        await storage.GetAppMetricsSummariesAsync(DateTime.UtcNow.AddDays(-30), tenantId: null);

        Assert.StartsWith($"PartitionKey eq '{TenantA}'", Assert.Single(index.Filters));
        Assert.StartsWith($"PartitionKey eq '{TenantA}'", Assert.Single(apps.Filters));
    }

    [Fact]
    public void GeoProjection_IsFullyMirroredOnSessionsIndex()
    {
        // GetGeoWindowSessionsAsync now reads SessionsIndex; every projected column must exist there.
        var mirrored = new HashSet<string>(SessionIndexFieldManifest.All, StringComparer.Ordinal) { "PartitionKey", "RowKey" };
        Assert.All(TableStorageService.GeoMetricsSessionProjection, c => Assert.Contains(c, mirrored));
        Assert.All(TableStorageService.SessionStatsProjection, c => Assert.Contains(c, mirrored));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static TableEntity ConfigRow(string tenantId) => new(tenantId, "config");

    private static TableEntity IndexRow(string tenantId, string sessionId, SessionStatus status, int? durationSeconds)
    {
        var startedAt = DateTime.UtcNow.AddHours(-1);
        var row = new TableEntity(tenantId, $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}")
        {
            ["SessionId"] = sessionId,
            ["Status"] = status.ToString(),
            ["StartedAt"] = startedAt,
        };
        if (durationSeconds.HasValue) row["DurationSeconds"] = durationSeconds.Value;
        return row;
    }

    private static TableEntity AppRow(string tenantId, string sessionId, string appName)
        => new(tenantId, $"{sessionId}_{appName}") { ["SessionId"] = sessionId, ["AppName"] = appName, ["StartedAt"] = DateTime.UtcNow.AddHours(-2) };

    /// <param name="counters">PlatformStats counter rows — the enrolled-tenant marker; null = nobody has enrolled.</param>
    private static TableStorageService BuildStorage(RecordingTableClient config, RecordingTableClient sessions, RecordingTableClient index, Mock<TableClient> apps, RecordingTableClient? counters = null)
    {
        var serviceClient = new Mock<TableServiceClient>();
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.TenantConfiguration)).Returns(config.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(sessions.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(index.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.AppInstallSummaries)).Returns(apps.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.PlatformStats)).Returns((counters ?? MockTableClientReturning()).Object);
        return new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
    }

    /// <summary>A per-tenant counter row (PlatformStats, RowKey "current") — the enrolled-tenant marker.</summary>
    private static TableEntity CounterRow(string tenantId, long totalEnrollments)
        => new(tenantId, "current") { ["TotalEnrollments"] = totalEnrollments };

    /// <summary>Counter rows marking every given tenant as enrolled.</summary>
    private static RecordingTableClient Counters(params string[] tenantIds)
        => MockTableClientReturning(tenantIds.Select(t => CounterRow(t, 1)).ToArray());

    /// <summary>Mock TableClient that records the filter + select of every string-filter QueryAsync call.</summary>
    private sealed class RecordingTableClient : Mock<TableClient>
    {
        public ConcurrentBag<string> Filters { get; } = new();
        public ConcurrentBag<string[]> Selects { get; } = new();
    }

    private static RecordingTableClient MockTableClientReturning(params TableEntity[] rows)
    {
        var m = new RecordingTableClient();
        m.Setup(c => c.QueryAsync<TableEntity>(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns((string filter, int? _, IEnumerable<string> select, CancellationToken _) =>
            {
                m.Filters.Add(filter ?? string.Empty);
                m.Selects.Add(select?.ToArray() ?? Array.Empty<string>());
                // Honour the partition anchor so per-tenant fan-outs see only their own rows.
                var partition = filter != null && filter.StartsWith("PartitionKey eq '")
                    ? filter.Substring("PartitionKey eq '".Length, filter.IndexOf('\'', "PartitionKey eq '".Length) - "PartitionKey eq '".Length)
                    : null;
                var visible = partition == null ? rows : rows.Where(r => r.PartitionKey == partition).ToArray();
                return AsAsyncPageable(visible);
            });
        m.Setup(c => c.QueryAsync<TableEntity>(
                It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => AsAsyncPageable(rows));
        return m;
    }

    private static void AssertNeverQueried(Mock<TableClient> client)
        => client.Verify(c => c.QueryAsync<TableEntity>(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never());

    private static AsyncPageable<TableEntity> AsAsyncPageable(TableEntity[] entities)
    {
        var page = Page<TableEntity>.FromValues(entities, continuationToken: null, new Mock<Response>().Object);
        return AsyncPageable<TableEntity>.FromPages(new[] { page });
    }
}
