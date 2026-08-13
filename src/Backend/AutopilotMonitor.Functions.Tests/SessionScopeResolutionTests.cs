using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using RequestContext = AutopilotMonitor.Functions.Helpers.RequestContext;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Fragilitätsaudit P5.3/P6.1: <c>RequestContextExtensions.ResolveSessionScopeAsync</c> is the
/// single implementation of the former 15-site copy-paste session-scope fallback, and
/// <c>TableStorageService.ResolveSessionTenantIdAsync</c> replaces the cross-partition
/// SessionsIndex scan with a SessionTenantLookup point-read (legacy scan fallback + self-heal).
/// These tests pin the semantics every migrated endpoint now inherits.
/// </summary>
public class SessionScopeResolutionTests
{
    private const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "44444444-4444-4444-4444-444444444444";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    // ── RequestContextExtensions.ResolveSessionScopeAsync ───────────────────────

    [Fact]
    public async Task NonGlobalCaller_KeepsTargetTenant_WithoutTouchingTheRepo()
    {
        var repo = new Mock<ISessionRepository>(MockBehavior.Strict);
        var ctx = new RequestContext { TenantId = HomeTenant, TargetTenantId = HomeTenant, UserRole = Constants.TenantRoles.Admin };

        var result = await ctx.ResolveSessionScopeAsync(repo.Object, SessionId);

        Assert.Equal(HomeTenant, result);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GlobalScope_CrossTenantSession_ResolvesToOwningTenant()
    {
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.ResolveSessionTenantIdAsync(SessionId)).ReturnsAsync(OtherTenant);
        var ctx = new RequestContext { TenantId = HomeTenant, TargetTenantId = HomeTenant, IsGlobalReader = true };

        var result = await ctx.ResolveSessionScopeAsync(repo.Object, SessionId);

        Assert.Equal(OtherTenant, result);
    }

    [Fact]
    public async Task GlobalScope_OwnTenantSession_KeepsTargetTenant()
    {
        // Case-insensitive comparison: a resolver returning a different casing must not
        // count as "different tenant".
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.ResolveSessionTenantIdAsync(SessionId)).ReturnsAsync(HomeTenant.ToUpperInvariant());
        var ctx = new RequestContext { TenantId = HomeTenant, TargetTenantId = HomeTenant, IsGlobalAdmin = true };

        var result = await ctx.ResolveSessionScopeAsync(repo.Object, SessionId);

        Assert.Equal(HomeTenant, result);
    }

    [Fact]
    public async Task GlobalScope_UnknownSession_KeepsTargetTenant_SoEndpointNotFoundHandlingStaysInCharge()
    {
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.ResolveSessionTenantIdAsync(SessionId)).ReturnsAsync((string?)null);
        var ctx = new RequestContext { TenantId = HomeTenant, TargetTenantId = HomeTenant, IsGlobalAdmin = true };

        var result = await ctx.ResolveSessionScopeAsync(repo.Object, SessionId);

        Assert.Equal(HomeTenant, result);
    }

    [Fact]
    public async Task RequireGlobalAdmin_ExcludesGlobalReader_FromCrossTenantWrites()
    {
        // Write paths (annotation upsert) gate the resolve on IsGlobalAdmin: a read-only
        // Global Reader keeps the middleware-validated tenant and can never steer a write
        // into a foreign tenant.
        var repo = new Mock<ISessionRepository>(MockBehavior.Strict);
        var ctx = new RequestContext { TenantId = HomeTenant, TargetTenantId = HomeTenant, IsGlobalReader = true };

        var result = await ctx.ResolveSessionScopeAsync(repo.Object, SessionId, requireGlobalAdmin: true);

        Assert.Equal(HomeTenant, result);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RequireGlobalAdmin_StillResolves_ForGlobalAdmin()
    {
        var repo = new Mock<ISessionRepository>();
        repo.Setup(r => r.ResolveSessionTenantIdAsync(SessionId)).ReturnsAsync(OtherTenant);
        var ctx = new RequestContext { TenantId = HomeTenant, TargetTenantId = HomeTenant, IsGlobalAdmin = true };

        var result = await ctx.ResolveSessionScopeAsync(repo.Object, SessionId, requireGlobalAdmin: true);

        Assert.Equal(OtherTenant, result);
    }

    [Fact]
    public async Task DelegatedCaller_GetsNoFallback_ByDesign()
    {
        // Deliberate: delegated (MSP) callers are NOT global scope — extending the fallback
        // to AllowedTenantIds would widen the authz surface and is a separate user decision.
        var repo = new Mock<ISessionRepository>(MockBehavior.Strict);
        var ctx = new RequestContext
        {
            TenantId = HomeTenant,
            TargetTenantId = OtherTenant,
            IsDelegatedReader = true,
            AllowedTenantIds = new[] { OtherTenant },
        };

        var result = await ctx.ResolveSessionScopeAsync(repo.Object, SessionId);

        Assert.Equal(OtherTenant, result);
        repo.VerifyNoOtherCalls();
    }

    // ── TableStorageService.ResolveSessionTenantIdAsync ─────────────────────────

    [Fact]
    public async Task PointReadHit_ReturnsTenant_WithoutScanningTheIndex()
    {
        var harness = new ResolveHarness(lookupRow: LookupRow(OtherTenant));

        var result = await harness.Sut.ResolveSessionTenantIdAsync(SessionId);

        Assert.Equal(OtherTenant, result);
        harness.Index.Verify(t => t.QueryAsync<TableEntity>(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PointReadMiss_FallsBackToScan_AndHealsTheLookupRow()
    {
        var indexRow = new TableEntity(OtherTenant, "0000000000000000001_" + SessionId) { ["SessionId"] = SessionId };
        var harness = new ResolveHarness(lookupRow: null, indexRows: new[] { indexRow });

        var result = await harness.Sut.ResolveSessionTenantIdAsync(SessionId);

        Assert.Equal(OtherTenant, result);
        // Self-heal: the lookup row is written so the next resolve is a point-read.
        harness.Lookup.Verify(t => t.UpsertEntityAsync(
            It.Is<TableEntity>(e => e.PartitionKey == SessionId
                                    && e.RowKey == "tenant"
                                    && e.GetString("TenantId") == OtherTenant),
            It.IsAny<TableUpdateMode>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UnknownSession_ReturnsNull_AndWritesNoHealRow()
    {
        var harness = new ResolveHarness(lookupRow: null, indexRows: Array.Empty<TableEntity>());

        var result = await harness.Sut.ResolveSessionTenantIdAsync(SessionId);

        Assert.Null(result);
        harness.Lookup.Verify(t => t.UpsertEntityAsync(
            It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvalidSessionId_Throws_BeforeAnyStorageCall()
    {
        var harness = new ResolveHarness(lookupRow: null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Sut.ResolveSessionTenantIdAsync("not-a-guid"));
    }

    // ── RecomputeTriggerGate ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Operator", true)]
    [InlineData("Viewer", false)]
    [InlineData("", false)]
    public void OwnTenantRoles_GateRecompute(string role, bool expected)
    {
        var ctx = new RequestContext { TenantId = HomeTenant, TargetTenantId = HomeTenant, UserRole = role };
        Assert.Equal(expected, RecomputeTriggerGate.CanTriggerRecompute(ctx, HomeTenant));
    }

    [Fact]
    public void GlobalAdmin_MayRecompute_CrossTenant()
    {
        var ctx = new RequestContext { TenantId = HomeTenant, IsGlobalAdmin = true };
        Assert.True(RecomputeTriggerGate.CanTriggerRecompute(ctx, OtherTenant));
    }

    [Fact]
    public void GlobalReader_MayNotRecompute_Anywhere()
    {
        var ctx = new RequestContext { TenantId = HomeTenant, IsGlobalReader = true };
        Assert.False(RecomputeTriggerGate.CanTriggerRecompute(ctx, HomeTenant));
        Assert.False(RecomputeTriggerGate.CanTriggerRecompute(ctx, OtherTenant));
    }

    [Fact]
    public void AdminRole_DoesNotCarryCrossTenant()
    {
        // A tenant Admin viewing a FOREIGN tenant's session (delegated read) must not be able
        // to trigger recomputes there — the role binds to the caller's own tenant.
        var ctx = new RequestContext { TenantId = HomeTenant, UserRole = Constants.TenantRoles.Admin };
        Assert.False(RecomputeTriggerGate.CanTriggerRecompute(ctx, OtherTenant));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static TableEntity LookupRow(string tenantId)
        => new(SessionId, "tenant") { ["TenantId"] = tenantId };

    private sealed class ResolveHarness
    {
        public Mock<TableClient> Lookup { get; } = new();
        public Mock<TableClient> Index { get; } = new();
        public TableStorageService Sut { get; }

        public ResolveHarness(TableEntity? lookupRow, TableEntity[]? indexRows = null)
        {
            if (lookupRow != null)
            {
                Lookup
                    .Setup(t => t.GetEntityAsync<TableEntity>(SessionId, "tenant", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(lookupRow, Mock.Of<Response>()));
            }
            else
            {
                Lookup
                    .Setup(t => t.GetEntityAsync<TableEntity>(SessionId, "tenant", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new RequestFailedException(404, "Not Found"));
            }
            Lookup
                .Setup(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());

            var page = Page<TableEntity>.FromValues(indexRows ?? Array.Empty<TableEntity>(), null, Mock.Of<Response>());
            Index
                .Setup(t => t.QueryAsync<TableEntity>(
                    It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(AsyncPageable<TableEntity>.FromPages(new[] { page }));

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionTenantLookup)).Returns(Lookup.Object);
            serviceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(Index.Object);
            Sut = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
