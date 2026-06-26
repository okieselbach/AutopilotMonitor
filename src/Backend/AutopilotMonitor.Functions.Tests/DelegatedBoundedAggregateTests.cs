using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Security guard for the delegated ("MSP") bounded aggregate. GetAllSessionsPageAsync with a non-null
/// allowedTenantIds must NEVER fall back to the unbounded primary-table scan when the managed set resolves
/// to no in-config tenants — otherwise /api/global/sessions (no tenantId), now admitted for a delegated
/// caller, could leak ALL tenants' sessions when a managed tenant has no config row, config is momentarily
/// empty, or a casing mismatch yields no overlap.
/// </summary>
public class DelegatedBoundedAggregateTests
{
    private const string ConfigTenant   = "11111111-1111-1111-1111-111111111111";
    private const string ManagedTenant  = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task BoundedAggregate_EmptyManagedSet_ReturnsEmpty_NeverUnboundedScan()
    {
        // Config knows only ConfigTenant; the delegated caller manages a DIFFERENT tenant → bounded set is empty.
        var configClient = MockTableClientReturning(new TableEntity(ConfigTenant, ""));
        // Sessions table backs the UNBOUNDED fallback — it must never be queried for a bounded request.
        var sessionsClient = MockTableClientReturning(new TableEntity("99999999-9999-9999-9999-999999999999", "leak"));
        var indexClient = new Mock<TableClient>();

        var storage = BuildStorage(configClient, sessionsClient, indexClient);

        var page = await storage.GetAllSessionsPageAsync(
            tenantIdFilter: null, days: null, pageSize: 10, continuation: null,
            allowedTenantIds: new[] { ManagedTenant });

        Assert.Empty(page.Items);
        AssertNeverQueried(sessionsClient);
    }

    [Fact]
    public async Task UnboundedAggregate_EmptyConfig_StillUsesPrimaryTableFallback()
    {
        // Sanity: with allowedTenantIds == null (GA all-tenants) and empty config, the legacy fallback DOES run —
        // proving it is the bounded guard, not some unrelated gate, that suppresses the scan above.
        var configClient = MockTableClientReturning();
        var sessionsClient = MockTableClientReturning();
        var indexClient = new Mock<TableClient>();

        var storage = BuildStorage(configClient, sessionsClient, indexClient);

        var page = await storage.GetAllSessionsPageAsync(
            tenantIdFilter: null, days: null, pageSize: 10, continuation: null, allowedTenantIds: null);

        Assert.Empty(page.Items);
        sessionsClient.Verify(c => c.QueryAsync<TableEntity>(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static TableStorageService BuildStorage(
        Mock<TableClient> config, Mock<TableClient> sessions, Mock<TableClient> index)
    {
        var serviceClient = new Mock<TableServiceClient>();
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.TenantConfiguration)).Returns(config.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(sessions.Object);
        serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(index.Object);
        return new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
    }

    /// <summary>Mocks both QueryAsync overloads (string + expression filter) to return the given rows.</summary>
    private static Mock<TableClient> MockTableClientReturning(params TableEntity[] rows)
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

    private static void AssertNeverQueried(Mock<TableClient> client)
    {
        client.Verify(c => c.QueryAsync<TableEntity>(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never());
        client.Verify(c => c.QueryAsync<TableEntity>(
            It.IsAny<Expression<Func<TableEntity, bool>>>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    private static AsyncPageable<TableEntity> AsAsyncPageable(TableEntity[] entities)
    {
        var page = Page<TableEntity>.FromValues(entities, continuationToken: null, new Mock<Response>().Object);
        return AsyncPageable<TableEntity>.FromPages(new[] { page });
    }
}
