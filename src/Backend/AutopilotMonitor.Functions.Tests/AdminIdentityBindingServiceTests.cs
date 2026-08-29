using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins <see cref="AdminIdentityBindingService"/> — the one place that decides whether a caller IS the
/// identity a cross-tenant-role UPN was granted for. The API accepts tokens from any Entra tenant and
/// upn/preferred_username are mutable, so a UPN string alone must never resolve a platform or delegated
/// role: only the (upn, tid, oid) triple matching the stored binding does. Covers the verdict matrix
/// (no binding / tenant mismatch / first-sign-in pin / pinned mismatch / concurrent-pin loss), the cache
/// invalidation after a pin, and the grant-time conflict rules (a grant never silently re-homes a UPN).
/// </summary>
public class AdminIdentityBindingServiceTests
{
    private const string Upn = "admin@vendor.example";
    private const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string ForeignTenant = "22222222-2222-2222-2222-222222222222";
    private const string Oid = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";
    private const string OtherOid = "bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb";

    private static (AdminIdentityBindingService Svc, Mock<IAdminRepository> Repo) Build(AdminIdentityBinding? stored)
    {
        var repo = new Mock<IAdminRepository>();
        // Stateful stand-in for the row: reads return `stored`, a pin mutates it under the repo's contract
        // (only onto an unpinned row homed in the caller's tenant), upserts replace it.
        repo.Setup(r => r.GetIdentityBindingAsync(It.IsAny<string>())).ReturnsAsync(() => stored);
        repo.Setup(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string tid, string oid) =>
            {
                if (stored != null && !stored.IsObjectIdPinned && stored.TenantId == tid)
                    stored.ObjectId = oid;
                return stored;
            });
        repo.Setup(r => r.UpsertIdentityBindingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ReturnsAsync((string upn, string tid, string? oid, string by) =>
            {
                stored = new AdminIdentityBinding { Upn = upn, TenantId = tid, ObjectId = oid ?? string.Empty, BoundBy = by };
                return true;
            });
        var svc = new AdminIdentityBindingService(
            repo.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<AdminIdentityBindingService>.Instance);
        return (svc, repo);
    }

    private static AdminIdentityBinding Binding(string tenantId, string objectId = "") =>
        new() { Upn = Upn, TenantId = tenantId, ObjectId = objectId };

    private static AdminIdentity Caller(string tenantId = HomeTenant, string oid = Oid) => new(Upn, tenantId, oid);

    // ── Verdict matrix ────────────────────────────────────────────────────────────

    [Fact]
    public async Task NullIdentity_IsNeverBound_AndReadsNothing()
    {
        var (svc, repo) = Build(Binding(HomeTenant, Oid));
        Assert.False(await svc.IsBoundAsync(null));
        repo.Verify(r => r.GetIdentityBindingAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NoBindingRow_IsNotBound()
    {
        // A legacy role row without a binding is inert — fail-closed, never "bind whoever shows up first".
        var (svc, _) = Build(stored: null);
        Assert.False(await svc.IsBoundAsync(Caller()));
    }

    [Fact]
    public async Task ForeignTenant_WithMatchingUpn_IsNotBound()
    {
        // The finding's primary exploit: same UPN string minted by another Entra tenant (domain re-registered,
        // or a lookalike verified elsewhere). The tid is the immutable discriminator.
        var (svc, repo) = Build(Binding(HomeTenant, Oid));
        Assert.False(await svc.IsBoundAsync(Caller(tenantId: ForeignTenant)));
        // And it must not pin anything onto the real admin's binding.
        repo.Verify(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ForeignTenant_AgainstUnpinnedBinding_DoesNotPin()
    {
        // Unpinned binding + foreign-tenant token: tenant mismatch is checked BEFORE the pin, so the
        // attacker cannot "claim" the object-id slot of an admin who has not signed in yet.
        var (svc, repo) = Build(Binding(HomeTenant));
        Assert.False(await svc.IsBoundAsync(Caller(tenantId: ForeignTenant)));
        repo.Verify(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PinnedBinding_MatchingTidAndOid_IsBound()
    {
        var (svc, _) = Build(Binding(HomeTenant, Oid));
        Assert.True(await svc.IsBoundAsync(Caller()));
    }

    [Fact]
    public async Task PinnedBinding_SameTenantDifferentOid_IsNotBound()
    {
        // UPN recycling inside the home tenant: a User Administrator re-assigns the UPN to another account.
        // Same tid, new oid — refused until an operator explicitly rebinds.
        var (svc, _) = Build(Binding(HomeTenant, Oid));
        Assert.False(await svc.IsBoundAsync(Caller(oid: OtherOid)));
    }

    [Fact]
    public async Task UnpinnedBinding_FirstSignInFromHomeTenant_PinsAndIsBound_ThenEnforced()
    {
        var (svc, repo) = Build(Binding(HomeTenant));

        Assert.True(await svc.IsBoundAsync(Caller()));
        repo.Verify(r => r.TryPinIdentityObjectIdAsync(Upn, HomeTenant, Oid), Times.Once);

        // Pinned now: the same account stays bound, any other account in the same tenant is refused.
        Assert.True(await svc.IsBoundAsync(Caller()));
        Assert.False(await svc.IsBoundAsync(Caller(oid: OtherOid)));
        // The pin happened exactly once — later calls verify against the stored (cached) binding.
        repo.Verify(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ConcurrentPin_LostRace_VerifiesAgainstStoredWinner()
    {
        // Two accounts in the home tenant race the first sign-in; the repository's conditional update lets
        // exactly one win. The loser must be judged against what was STORED, never against its own intent.
        var (svc, repo) = Build(Binding(HomeTenant));
        repo.Setup(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(Binding(HomeTenant, OtherOid)); // someone else won

        Assert.False(await svc.IsBoundAsync(Caller()));
    }

    [Fact]
    public async Task PinResult_Null_MeansBindingVanished_IsNotBound()
    {
        // Binding removed between the cached read and the pin (operator DELETE racing the sign-in).
        var (svc, repo) = Build(Binding(HomeTenant));
        repo.Setup(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((AdminIdentityBinding?)null);

        Assert.False(await svc.IsBoundAsync(Caller()));
    }

    [Fact]
    public async Task Verdict_IsCached_ButPinInvalidates()
    {
        var (svc, repo) = Build(Binding(HomeTenant, Oid));
        Assert.True(await svc.IsBoundAsync(Caller()));
        Assert.True(await svc.IsBoundAsync(Caller()));
        repo.Verify(r => r.GetIdentityBindingAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Rebind_InvalidatesCache_SoNewTenantTakesEffectImmediately()
    {
        var (svc, _) = Build(Binding(HomeTenant, Oid));
        Assert.True(await svc.IsBoundAsync(Caller()));

        await svc.RebindAsync(Upn, ForeignTenant, OtherOid, "ga@vendor.example");

        Assert.False(await svc.IsBoundAsync(Caller()));
        Assert.True(await svc.IsBoundAsync(Caller(tenantId: ForeignTenant, oid: OtherOid)));
    }

    // ── Grant-time binding (EnsureBoundAsync) ─────────────────────────────────────

    [Fact]
    public async Task Ensure_NoExistingBinding_CreatesIt()
    {
        var (svc, repo) = Build(stored: null);
        var b = await svc.EnsureBoundAsync(Upn, HomeTenant, null, "ga@vendor.example");
        Assert.Equal(HomeTenant, b.TenantId);
        Assert.False(b.IsObjectIdPinned);
        repo.Verify(r => r.UpsertIdentityBindingAsync(Upn, HomeTenant, null, "ga@vendor.example"), Times.Once);
    }

    [Fact]
    public async Task Ensure_SameTenant_KeepsExistingPin_NoWrite()
    {
        // A second grant (another managed tenant / a group) for an already-bound UPN is a no-op on the binding.
        var (svc, repo) = Build(Binding(HomeTenant, Oid));
        var b = await svc.EnsureBoundAsync(Upn, HomeTenant, null, "ga@vendor.example");
        Assert.Equal(Oid, b.ObjectId);
        repo.Verify(r => r.UpsertIdentityBindingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_SameTenant_UnpinnedThenObjectIdSupplied_Upgrades()
    {
        var (svc, repo) = Build(Binding(HomeTenant));
        var b = await svc.EnsureBoundAsync(Upn, HomeTenant, Oid, "ga@vendor.example");
        Assert.Equal(Oid, b.ObjectId);
        repo.Verify(r => r.UpsertIdentityBindingAsync(Upn, HomeTenant, Oid, "ga@vendor.example"), Times.Once);
    }

    [Fact]
    public async Task Ensure_DifferentTenant_Conflicts_NeverRehomes()
    {
        // A grant must not be a covert rebind: moving a UPN to another tenant is an explicit PUT.
        var (svc, repo) = Build(Binding(HomeTenant, Oid));
        await Assert.ThrowsAsync<IdentityBindingConflictException>(
            () => svc.EnsureBoundAsync(Upn, ForeignTenant, null, "ga@vendor.example"));
        repo.Verify(r => r.UpsertIdentityBindingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_DifferentPinnedObjectId_Conflicts()
    {
        var (svc, _) = Build(Binding(HomeTenant, Oid));
        await Assert.ThrowsAsync<IdentityBindingConflictException>(
            () => svc.EnsureBoundAsync(Upn, HomeTenant, OtherOid, "ga@vendor.example"));
    }

    [Fact]
    public async Task Ensure_NormalizesCase()
    {
        var (svc, repo) = Build(stored: null);
        await svc.EnsureBoundAsync("Admin@Vendor.Example", HomeTenant.ToUpperInvariant(), Oid.ToUpperInvariant(), "ga@vendor.example");
        repo.Verify(r => r.UpsertIdentityBindingAsync(Upn, HomeTenant, Oid, "ga@vendor.example"), Times.Once);
    }

    // ── AdminIdentity construction ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, HomeTenant, Oid)]
    [InlineData(Upn, "", Oid)]
    [InlineData(Upn, HomeTenant, "  ")]
    public void AdminIdentity_Create_RequiresAllThree(string? upn, string? tid, string? oid)
        => Assert.Null(AdminIdentity.Create(upn, tid, oid));

    [Fact]
    public void AdminIdentity_Create_Lowercases()
    {
        var id = AdminIdentity.Create("Admin@Vendor.Example", HomeTenant.ToUpperInvariant(), Oid.ToUpperInvariant());
        Assert.Equal(new AdminIdentity(Upn, HomeTenant, Oid), id);
    }
}
