using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins <see cref="AdminIdentityResolver"/> — what makes granting a cross-tenant role "type the UPN":
/// sign-in history first (tid + oid, both from validated tokens), then UPN domain → onboarded tenant
/// (tid only, oid pinned on first sign-in), and NO answer when the history is ambiguous (same UPN under
/// two tenants — exactly the situation the binding exists for) or the domain is unknown.
/// </summary>
public class AdminIdentityResolverTests
{
    private const string Upn = "msp@partner.example";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string OidOld = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";
    private const string OidNew = "bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb";

    private static AdminIdentityResolver Build(
        IEnumerable<UserSignInIdentity>? signIns = null,
        params (string TenantId, string Domain)[] tenants)
    {
        var metrics = new Mock<IMetricsRepository>();
        metrics.Setup(m => m.GetSignInIdentitiesByUpnAsync(It.IsAny<string>()))
            .ReturnsAsync((signIns ?? Array.Empty<UserSignInIdentity>()).ToList());

        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetAllTenantConfigurationsAsync())
            .ReturnsAsync(tenants.Select(t => new TenantConfiguration { TenantId = t.TenantId, DomainName = t.Domain }).ToList());
        var configService = new TenantConfigurationService(
            configRepo.Object, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        return new AdminIdentityResolver(metrics.Object, configService, NullLogger<AdminIdentityResolver>.Instance);
    }

    private static UserSignInIdentity SignIn(string tid, string oid, int daysAgo) => new()
    {
        TenantId = tid, ObjectId = oid, LastLoginAt = DateTime.UtcNow.AddDays(-daysAgo), LoginCount = 1,
    };

    [Fact]
    public async Task SignInHistory_SingleTenant_ResolvesTidAndOid()
    {
        var r = await Build(new[] { SignIn(TenantA, OidOld, 3) }).ResolveAsync("MSP@Partner.Example");
        Assert.NotNull(r);
        Assert.Equal(TenantA, r!.TenantId);
        Assert.Equal(OidOld, r.ObjectId);
        Assert.Equal(ResolvedAdminIdentity.SourceSignIn, r.Source);
    }

    [Fact]
    public async Task SignInHistory_SameTenantTwoOids_TakesMostRecent()
    {
        // Account re-created in the same tenant: the newer oid is the one that will sign in next.
        var r = await Build(new[] { SignIn(TenantA, OidOld, 30), SignIn(TenantA, OidNew, 1) }).ResolveAsync(Upn);
        Assert.Equal(OidNew, r!.ObjectId);
    }

    [Fact]
    public async Task SignInHistory_TwoTenants_IsAmbiguous_NotResolved_EvenIfDomainMatches()
    {
        // The same UPN string seen under two tenants is the attack shape; never auto-pick, even when the
        // domain fallback would have an answer.
        var r = await Build(new[] { SignIn(TenantA, OidOld, 3), SignIn(TenantB, OidNew, 1) }, (TenantA, "partner.example"))
            .ResolveAsync(Upn);
        Assert.Null(r);
    }

    [Fact]
    public async Task NoSignIns_DomainOfOnboardedTenant_ResolvesTidOnly()
    {
        var r = await Build(null, (TenantA, "Partner.Example"), (TenantB, "other.example")).ResolveAsync(Upn);
        Assert.NotNull(r);
        Assert.Equal(TenantA, r!.TenantId);
        Assert.Null(r.ObjectId);
        Assert.Equal(ResolvedAdminIdentity.SourceDomain, r.Source);
    }

    [Fact]
    public async Task NoSignIns_TwoTenantsClaimTheDomain_NotResolved()
    {
        var r = await Build(null, (TenantA, "partner.example"), (TenantB, "partner.example")).ResolveAsync(Upn);
        Assert.Null(r);
    }

    [Fact]
    public async Task NoSignIns_UnknownDomain_NotResolved()
    {
        var r = await Build(null, (TenantA, "other.example")).ResolveAsync(Upn);
        Assert.Null(r);
    }

    [Theory]
    [InlineData("nodomain")]
    [InlineData("trailing@")]
    public async Task MalformedUpn_NotResolved(string upn)
        => Assert.Null(await Build(null, (TenantA, "partner.example")).ResolveAsync(upn));
}
