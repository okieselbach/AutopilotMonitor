using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DelegatedAdminService"/> scope resolution — the "scoped global" (MSP) tier.
/// Locks in the fail-closed rules: only Active + enabled + recognized-role rows confer scope; empty role
/// defaults to the least-privileged DelegatedReader; unknown roles are dropped. Also covers the 5-minute
/// cache and its invalidation on mutation.
/// </summary>
public class DelegatedAdminServiceTests
{
    private const string Upn = "msp-admin@partner.example";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private static (DelegatedAdminService Svc, Mock<IAdminRepository> Repo) Build()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<DelegatedAdminEntry>());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new DelegatedAdminService(repo.Object, cache, NullLogger<DelegatedAdminService>.Instance);
        return (svc, repo);
    }

    private static DelegatedAdminEntry Row(
        string tenantId,
        string role = Constants.DelegatedRoles.DelegatedReader,
        bool enabled = true,
        string status = Constants.DelegatedStatus.Active) => new()
        {
            Upn = Upn,
            TenantId = tenantId,
            Role = role,
            IsEnabled = enabled,
            Status = status,
            Source = Constants.DelegatedSource.OperatorGranted,
        };

    private static void Returns(Mock<IAdminRepository> repo, params DelegatedAdminEntry[] rows) =>
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(rows.ToList());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetScope_BlankUpn_ReturnsEmpty(string? upn)
    {
        var (svc, repo) = Build();
        var scope = await svc.GetScopeAsync(upn);
        Assert.True(scope.IsEmpty);
        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetScope_NoRows_ReturnsEmpty()
    {
        var (svc, _) = Build();
        var scope = await svc.GetScopeAsync(Upn);
        Assert.True(scope.IsEmpty);
        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_ActiveEnabledRow_IsCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedReader));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.Covers(TenantA));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
        Assert.False(scope.CanWrite(TenantA));
        Assert.Single(scope.TenantIds);
    }

    [Fact]
    public async Task GetScope_DelegatedAdminRole_CanWrite()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedAdmin));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.CanWrite(TenantA));
    }

    [Theory]
    [InlineData(Constants.DelegatedStatus.PendingApproval)]
    [InlineData(Constants.DelegatedStatus.Revoked)]
    public async Task GetScope_NonActiveStatus_NotCovered(string status)
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, status: status));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_DisabledRow_NotCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, enabled: false));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_EmptyRole_DefaultsToReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: ""));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
    }

    [Fact]
    public async Task GetScope_UnknownRole_IsDropped()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: "SuperRoot"));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_MultipleTenants_AllResolved()
    {
        var (svc, repo) = Build();
        Returns(repo,
            Row(TenantA, Constants.DelegatedRoles.DelegatedReader),
            Row(TenantB, Constants.DelegatedRoles.DelegatedAdmin));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.False(scope.CanWrite(TenantA));
        Assert.True(scope.CanWrite(TenantB));
    }

    [Fact]
    public async Task GetScope_TenantIdLookup_IsCaseInsensitive()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA.ToLowerInvariant()));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.Covers(TenantA.ToUpperInvariant()));
    }

    [Fact]
    public async Task GetScope_SecondCall_IsCached()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));

        await svc.GetScopeAsync(Upn);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Upsert_InvalidatesCache()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.UpsertDelegatedAdminAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Upn); // primes cache
        await svc.UpsertAsync(Upn, TenantB, Constants.DelegatedRoles.DelegatedReader,
            Constants.DelegatedStatus.Active, Constants.DelegatedSource.OperatorGranted, "ga@vendor.example");
        await svc.GetScopeAsync(Upn); // must re-query after invalidation

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Revoke_InvalidatesCache()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.SetDelegatedAdminStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Upn);
        await svc.SetStatusAsync(Upn, TenantA, Constants.DelegatedStatus.Revoked);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }
}
