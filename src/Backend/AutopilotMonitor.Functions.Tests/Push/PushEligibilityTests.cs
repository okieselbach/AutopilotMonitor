using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The real <see cref="PushEligibility"/> over mocked role and configuration reads: the channel
/// switch per scope (K4), the table-backed role predicate for tenant members (Admin/Operator,
/// never Viewer) and for Global Admins (GlobalAdmin with a matching identity binding, never a
/// Global Reader), and the owner-freshness window (K3).
/// </summary>
public class PushEligibilityTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string HomeTenantId = "33333333-3333-3333-3333-333333333333";
    private const string OtherTenantId = "44444444-4444-4444-4444-444444444444";
    private const string Upn = "admin@contoso.invalid";
    private const string ObjectId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string OtherObjectId = "aaaaaaaa-0000-0000-0000-000000000002";
    private static readonly NotificationScope Tenant = NotificationScope.Tenant(TenantId);
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    // ── HasPushChannelAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task Unset_scope_has_no_push_channel()
    {
        var h = new Harness();
        h.OpsChannels(Channel(WebhookProviderType.Push));
        h.TenantChannels(exists: true, Channel(WebhookProviderType.Push));

        Assert.False(await h.Sut.HasPushChannelAsync(default));
        h.AdminConfig.Verify(a => a.GetConfigurationAsync(), Times.Never);
        h.TenantConfig.Verify(t => t.TryGetConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Platform_scope_has_a_push_channel_with_an_enabled_push_ops_channel()
    {
        var h = new Harness();
        h.OpsChannels(Channel(WebhookProviderType.Slack), Channel(WebhookProviderType.Push));

        Assert.True(await h.Sut.HasPushChannelAsync(NotificationScope.Platform));
        h.TenantConfig.Verify(t => t.TryGetConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Platform_scope_ignores_a_disabled_push_ops_channel()
    {
        var h = new Harness();
        h.OpsChannels(Channel(WebhookProviderType.Push, enabled: false));

        Assert.False(await h.Sut.HasPushChannelAsync(NotificationScope.Platform));
    }

    [Fact]
    public async Task Platform_scope_with_only_webhook_ops_channels_has_no_push_channel()
    {
        var h = new Harness();
        h.OpsChannels(Channel(WebhookProviderType.Slack));

        Assert.False(await h.Sut.HasPushChannelAsync(NotificationScope.Platform));
    }

    [Fact]
    public async Task Platform_scope_without_any_ops_channel_has_no_push_channel()
    {
        var h = new Harness();   // default admin configuration: no channel list, no legacy slots

        Assert.False(await h.Sut.HasPushChannelAsync(NotificationScope.Platform));
    }

    [Fact]
    public async Task Tenant_scope_without_a_stored_configuration_has_no_push_channel()
    {
        var h = new Harness();
        // The default row handed back for a missing tenant never counts, whatever it carries.
        h.TenantChannels(exists: false, Channel(WebhookProviderType.Push));

        Assert.False(await h.Sut.HasPushChannelAsync(Tenant));
    }

    [Fact]
    public async Task Tenant_scope_has_a_push_channel_with_an_enabled_tenant_push_channel()
    {
        var h = new Harness();
        h.TenantChannels(exists: true, Channel(WebhookProviderType.Slack), Channel(WebhookProviderType.Push));

        Assert.True(await h.Sut.HasPushChannelAsync(Tenant));
        h.AdminConfig.Verify(a => a.GetConfigurationAsync(), Times.Never);
    }

    [Fact]
    public async Task Tenant_scope_ignores_a_disabled_tenant_push_channel()
    {
        var h = new Harness();
        h.TenantChannels(exists: true, Channel(WebhookProviderType.Push, enabled: false));

        Assert.False(await h.Sut.HasPushChannelAsync(Tenant));
    }

    // ── IsEligibleAsync — tenant scope ───────────────────────────────────────

    [Theory]
    [InlineData(Constants.TenantRoles.Admin)]
    [InlineData(Constants.TenantRoles.Operator)]
    public async Task Enabled_tenant_member_with_an_alerting_role_is_eligible(string role)
    {
        var h = new Harness();
        h.TenantMember(role);

        Assert.True(await h.Sut.IsEligibleAsync(Tenant, Upn, ObjectId, HomeTenantId));
    }

    [Fact]
    public async Task Tenant_viewer_is_not_eligible()
    {
        var h = new Harness();
        h.TenantMember(Constants.TenantRoles.Viewer);

        Assert.False(await h.Sut.IsEligibleAsync(Tenant, Upn, ObjectId, HomeTenantId));
    }

    [Fact]
    public async Task Tenant_member_without_a_role_counts_as_admin()
    {
        var h = new Harness();
        h.TenantMember(role: string.Empty);   // legacy row written before roles existed

        Assert.True(await h.Sut.IsEligibleAsync(Tenant, Upn, ObjectId, HomeTenantId));
    }

    [Fact]
    public async Task Disabled_tenant_member_is_not_eligible()
    {
        var h = new Harness();
        h.TenantMember(Constants.TenantRoles.Admin, enabled: false);

        Assert.False(await h.Sut.IsEligibleAsync(Tenant, Upn, ObjectId, HomeTenantId));
    }

    [Fact]
    public async Task Unknown_tenant_member_is_not_eligible()
    {
        var h = new Harness();   // no TenantAdmins row for the upn

        Assert.False(await h.Sut.IsEligibleAsync(Tenant, Upn, ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(TenantId, Upn), Times.Once);
    }

    [Fact]
    public async Task Tenant_lookup_lower_cases_the_upn()
    {
        var h = new Harness();
        h.TenantMember(Constants.TenantRoles.Admin);   // row keyed on the lower-case upn

        Assert.True(await h.Sut.IsEligibleAsync(Tenant, "Admin@Contoso.INVALID", ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(TenantId, Upn), Times.Once);
    }

    [Fact]
    public async Task Application_principal_is_never_eligible()
    {
        var h = new Harness();
        var appKey = Constants.PrincipalKeys.ForApplication("22222222-2222-2222-2222-222222222222");
        h.TenantMember(Constants.TenantRoles.Admin, upn: appKey);   // even with an Admin row

        Assert.False(await h.Sut.IsEligibleAsync(Tenant, appKey, ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Unset_scope_is_never_eligible()
    {
        var h = new Harness();
        h.TenantMember(Constants.TenantRoles.Admin);

        Assert.False(await h.Sut.IsEligibleAsync(default, Upn, ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Blank_upn_is_never_eligible()
    {
        var h = new Harness();

        Assert.False(await h.Sut.IsEligibleAsync(Tenant, "  ", ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── IsEligibleAsync — platform scope ─────────────────────────────────────

    [Fact]
    public async Task Global_admin_with_matching_binding_is_eligible_for_platform_scope()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalAdmin);
        h.Binding(HomeTenantId, ObjectId);

        Assert.True(await h.Sut.IsEligibleAsync(NotificationScope.Platform, Upn, ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Global_admin_with_unpinned_binding_is_eligible_for_platform_scope()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalAdmin);
        h.Binding(HomeTenantId, objectId: string.Empty);   // object id pinned on first sign-in

        Assert.True(await h.Sut.IsEligibleAsync(NotificationScope.Platform, Upn, ObjectId, HomeTenantId));
    }

    [Fact]
    public async Task Global_admin_binding_with_another_object_id_is_not_eligible()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalAdmin);
        h.Binding(HomeTenantId, OtherObjectId);

        Assert.False(await h.Sut.IsEligibleAsync(NotificationScope.Platform, Upn, ObjectId, HomeTenantId));
    }

    [Fact]
    public async Task Global_admin_binding_homed_in_another_tenant_is_not_eligible()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalAdmin);
        h.Binding(OtherTenantId, ObjectId);

        Assert.False(await h.Sut.IsEligibleAsync(NotificationScope.Platform, Upn, ObjectId, HomeTenantId));
    }

    [Fact]
    public async Task Global_admin_without_binding_is_not_eligible()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalAdmin);   // no AdminIdentityBindings row

        Assert.False(await h.Sut.IsEligibleAsync(NotificationScope.Platform, Upn, ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetIdentityBindingAsync(Upn), Times.Once);
    }

    [Fact]
    public async Task Global_reader_is_not_eligible_for_platform_scope()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalReader);
        h.Binding(HomeTenantId, ObjectId);   // a perfect binding does not help

        Assert.False(await h.Sut.IsEligibleAsync(NotificationScope.Platform, Upn, ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetIdentityBindingAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Platform_lookup_lower_cases_the_upn()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalAdmin);   // rows keyed on the lower-case upn
        h.Binding(HomeTenantId, ObjectId);

        Assert.True(await h.Sut.IsEligibleAsync(NotificationScope.Platform, "Admin@Contoso.INVALID", ObjectId, HomeTenantId));
        h.AdminRepo.Verify(r => r.GetGlobalRoleAsync(Upn), Times.Once);
        h.AdminRepo.Verify(r => r.GetIdentityBindingAsync(Upn), Times.Once);
    }

    [Fact]
    public async Task Platform_binding_comparison_ignores_case()
    {
        var h = new Harness();
        h.GlobalRole(Constants.GlobalRoles.GlobalAdmin);
        h.Binding(HomeTenantId.ToUpperInvariant(), ObjectId.ToUpperInvariant());

        Assert.True(await h.Sut.IsEligibleAsync(NotificationScope.Platform, Upn, ObjectId, HomeTenantId));
    }

    // ── IsOwnerFreshAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_without_a_sign_in_stamp_is_not_fresh()
    {
        var h = new Harness();   // no PushOwners row: "never signed in"

        Assert.False(await h.Sut.IsOwnerFreshAsync(HomeTenantId, ObjectId, Now));
    }

    [Fact]
    public async Task Owner_who_signed_in_a_day_ago_is_fresh()
    {
        var h = new Harness();
        h.Owner(Now.AddDays(-1));

        Assert.True(await h.Sut.IsOwnerFreshAsync(HomeTenantId, ObjectId, Now));
    }

    [Fact]
    public async Task Owner_beyond_the_inactivity_window_is_not_fresh()
    {
        var h = new Harness();
        h.Owner(Now.AddDays(-(h.Settings.OwnerInactivityDays + 1)));

        Assert.False(await h.Sut.IsOwnerFreshAsync(HomeTenantId, ObjectId, Now));
    }

    [Fact]
    public async Task Owner_exactly_at_the_inactivity_boundary_is_fresh()
    {
        var h = new Harness();
        h.Owner(Now.AddDays(-h.Settings.OwnerInactivityDays));   // inclusive window (>=)

        Assert.True(await h.Sut.IsOwnerFreshAsync(HomeTenantId, ObjectId, Now));
    }

    [Fact]
    public async Task Owner_one_tick_past_the_inactivity_boundary_is_not_fresh()
    {
        var h = new Harness();
        h.Owner(Now.AddDays(-h.Settings.OwnerInactivityDays).AddTicks(-1));

        Assert.False(await h.Sut.IsOwnerFreshAsync(HomeTenantId, ObjectId, Now));
    }

    [Theory]
    [InlineData("", ObjectId)]
    [InlineData(HomeTenantId, "")]
    public async Task Blank_owner_identity_is_never_fresh(string homeTenantId, string objectId)
    {
        var h = new Harness();
        h.Owner(Now);   // a fresh stamp exists for the full identity

        Assert.False(await h.Sut.IsOwnerFreshAsync(homeTenantId, objectId, Now));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NotificationChannel Channel(WebhookProviderType provider, bool enabled = true) => new()
    {
        Id = provider.ToString().ToLowerInvariant() + "-channel",
        Name = provider.ToString(),
        ProviderType = (int)provider,
        // A Push channel has no destination of its own; every webhook provider needs one to parse as configured.
        Url = provider == WebhookProviderType.Push ? null : "https://hooks.example.invalid/endpoint",
        Enabled = enabled,
    };

    private sealed class Harness
    {
        public Mock<IAdminRepository> AdminRepo { get; } = new();
        public InMemoryPushDeviceRepository Repo { get; } = new();
        public Mock<TenantConfigurationService> TenantConfig { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public PushSettings Settings { get; }
        public PushEligibility Sut { get; }

        public Harness()
        {
            Settings = PushSettings.ForKeys(new VapidKeyRing(VapidKey.Generate()));

            TenantConfig = new Mock<TenantConfigurationService>(null!, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()), null!) { CallBase = false };

            AdminConfig = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
            // A platform without any ops channel until a test says otherwise.
            AdminConfig.Setup(a => a.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration());

            Sut = new PushEligibility(AdminRepo.Object, Repo, TenantConfig.Object, AdminConfig.Object, Settings);
        }

        public void OpsChannels(params NotificationChannel[] channels)
            => AdminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AdminConfiguration { OpsNotificationChannelsJson = NotificationChannel.SerializeList(channels) });

        public void TenantChannels(bool exists, params NotificationChannel[] channels)
            => TenantConfig.Setup(t => t.TryGetConfigurationAsync(TenantId))
                .ReturnsAsync((new TenantConfiguration { TenantId = TenantId, NotificationChannelsJson = NotificationChannel.SerializeList(channels) }, exists));

        public void TenantMember(string role, bool enabled = true, string upn = Upn)
            => AdminRepo.Setup(r => r.GetTenantMemberAsync(TenantId, upn))
                .ReturnsAsync(new TenantMember { TenantId = TenantId, Upn = upn, Role = role, IsEnabled = enabled });

        public void GlobalRole(string role)
            => AdminRepo.Setup(r => r.GetGlobalRoleAsync(Upn)).ReturnsAsync(role);

        public void Binding(string tenantId, string objectId)
            => AdminRepo.Setup(r => r.GetIdentityBindingAsync(Upn))
                .ReturnsAsync(new AdminIdentityBinding { Upn = Upn, TenantId = tenantId, ObjectId = objectId });

        public void Owner(DateTime lastSignInUtc)
            => Repo.Owners[(HomeTenantId, ObjectId)] = new PushOwner
            {
                HomeTenantId = HomeTenantId, ObjectId = ObjectId, Upn = Upn, LastSignInUtc = lastSignInUtc,
            };
    }
}
