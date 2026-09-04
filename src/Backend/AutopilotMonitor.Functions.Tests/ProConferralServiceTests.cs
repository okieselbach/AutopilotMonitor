using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the write side of conferred Pro (<see cref="ProConferralService"/>): the ONLY thing ever written is
/// the retention grace anchor (<see cref="TenantConfiguration.ProDowngradedUtc"/>) on the managed tenant —
/// never a plan tier — and only when the managing tenant was a permanent-Pro tenant (a trial MSP conferred
/// nothing). Group-wide loss stamps every member except the home tenant, and every path drops the index.
/// </summary>
public sealed class ProConferralServiceTests
{
    private const string Home = "11111111-1111-1111-1111-111111111111";
    private const string CustomerA = "33333333-3333-3333-3333-333333333333";
    private const string CustomerB = "44444444-4444-4444-4444-444444444444";
    private static readonly DateTime Now = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public required ProConferralService Svc { get; init; }
        public required Dictionary<string, TenantConfiguration> Rows { get; init; }
        public required List<(TenantConfiguration Config, string? Source, string? Reason)> Saves { get; init; }
        public required StubManagedTenantProIndex Index { get; init; }
    }

    private static Harness Build(string homeTier, params string[] members)
    {
        var rows = new Dictionary<string, TenantConfiguration>(StringComparer.OrdinalIgnoreCase)
        {
            [Home] = new() { TenantId = Home, DomainName = "partner.example", PlanTier = homeTier },
            [CustomerA] = new() { TenantId = CustomerA, DomainName = "a.example", PlanTier = "community", DataRetentionDays = 365 },
            [CustomerB] = new() { TenantId = CustomerB, DomainName = "b.example", PlanTier = "pro" },
        };
        var saves = new List<(TenantConfiguration, string?, string?)>();

        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(c => c.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => rows.TryGetValue(id, out var cfg) ? cfg : null);
        configRepo.Setup(c => c.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((TenantConfiguration cfg, string? source, string? reason) => { saves.Add((cfg, source, reason)); return true; });

        var adminRepo = new Mock<IAdminRepository>();
        adminRepo.Setup(a => a.GetGroupTenantsAsync(Constants.TenantGroupIds.ForHomeTenant(Home))).ReturnsAsync(members.ToList());

        var index = new StubManagedTenantProIndex(_ => null);
        var configs = new TenantConfigurationService(configRepo.Object, NullLogger<TenantConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()), index);
        var svc = new ProConferralService(adminRepo.Object, configs, index, NullLogger<ProConferralService>.Instance, new TestTimeProvider(Now));
        return new Harness { Svc = svc, Rows = rows, Saves = saves, Index = index };
    }

    [Fact]
    public async Task RecordLoss_PermanentProHome_StampsAnchor_NeverTouchesPlanTier()
    {
        var h = Build("pro");

        var stamped = await h.Svc.RecordLossAsync(CustomerA, Home, "customer-revoked");

        Assert.True(stamped);
        var save = Assert.Single(h.Saves);
        Assert.Equal(CustomerA, save.Config.TenantId);
        Assert.Equal(Now, save.Config.ProDowngradedUtc);
        Assert.Equal("community", save.Config.PlanTier);
        Assert.Equal("delegation", save.Source);
        Assert.Equal("customer-revoked", save.Reason);
        Assert.Equal($"delegation:{Home}", save.Config.UpdatedBy);
        Assert.Equal(1, h.Index.Invalidations);
    }

    [Fact]
    public async Task RecordLoss_TrialHome_StampsNothing_StillDropsCaches()
    {
        var h = Build("community");
        h.Rows[Home].TrialExpiresUtc = Now.AddDays(5);

        var stamped = await h.Svc.RecordLossAsync(CustomerA, Home, "customer-revoked");

        Assert.False(stamped);
        Assert.Empty(h.Saves);
        Assert.Equal(1, h.Index.Invalidations);
    }

    [Fact]
    public async Task RecordLoss_UnknownCustomerRow_IsNoOp()
    {
        var h = Build("pro");

        Assert.False(await h.Svc.RecordLossAsync("99999999-9999-9999-9999-999999999999", Home, "customer-revoked"));
        Assert.Empty(h.Saves);
    }

    [Fact]
    public async Task RecordLossForOwnedGroup_StampsEveryMember_ExceptHome()
    {
        var h = Build("community", CustomerA, CustomerB, Home);

        var count = await h.Svc.RecordLossForOwnedGroupAsync(Home, "manager-lost-permanent-pro");

        Assert.Equal(2, count);
        Assert.Equal(new[] { CustomerA, CustomerB }, h.Saves.Select(s => s.Config.TenantId).OrderBy(t => t).ToArray());
        Assert.All(h.Saves, s => Assert.Equal(Now, s.Config.ProDowngradedUtc));
        Assert.All(h.Saves, s => Assert.Equal("manager-lost-permanent-pro", s.Reason));
        Assert.Equal(1, h.Index.Invalidations);
    }

    [Fact]
    public async Task NotifyDelegationChanged_DropsIndex()
    {
        var h = Build("pro");

        await h.Svc.NotifyDelegationChangedAsync(CustomerA);

        Assert.Equal(1, h.Index.Invalidations);
        Assert.Empty(h.Saves);
    }
}
