using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The role-loss hooks (K6): a tenant member who is removed, disabled or demoted below Operator
/// loses the paired push devices of that tenant, a removed or disabled Global Admin loses the
/// platform-scope devices, and no other mutation touches a device. The hook is optional — the
/// original constructors without a revoker keep working — and it keys on the NEW role only.
/// </summary>
public class PushRoleLossHookTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Upn = "member@contoso.invalid";
    private static readonly NotificationScope Tenant = NotificationScope.Tenant(TenantId);

    // ── TenantAdminsService ──────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_member_revokes_their_tenant_devices_once_as_member_removed()
    {
        var h = new TenantHarness();

        await h.Sut.RemoveTenantAdminAsync(TenantId, Upn);

        AssertRevokedOnce(h.Revoker, Tenant, Upn, "member_removed");
    }

    [Fact]
    public async Task Remove_hands_the_revoker_the_lower_cased_scope_and_upn()
    {
        var h = new TenantHarness();

        await h.Sut.RemoveTenantAdminAsync("22222222-2222-2222-2222-22222222ABCD", "Leaver@Contoso.INVALID");

        AssertRevokedOnce(h.Revoker, NotificationScope.Tenant("22222222-2222-2222-2222-22222222abcd"), "leaver@contoso.invalid", "member_removed");
    }

    [Fact]
    public async Task Remove_deletes_the_member_row_before_the_push_hook_fires()
    {
        // The role loss is durable first; the device revoke is the best-effort follow-up.
        var h = new TenantHarness();
        var order = new List<string>();
        h.AdminRepo.Setup(r => r.RemoveTenantMemberAsync(TenantId, Upn)).Callback(() => order.Add("row")).ReturnsAsync(true);
        h.Revoker.Setup(r => r.RevokeOwnerAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => order.Add("push")).Returns(Task.CompletedTask);

        await h.Sut.RemoveTenantAdminAsync(TenantId, Upn);

        Assert.Equal(new[] { "row", "push" }, order);
    }

    [Fact]
    public async Task Disabling_a_member_revokes_their_tenant_devices_once_as_member_disabled()
    {
        var h = new TenantHarness();

        await h.Sut.DisableTenantAdminAsync(TenantId, Upn);

        AssertRevokedOnce(h.Revoker, Tenant, Upn, "member_disabled");
    }

    [Fact]
    public async Task Enabling_a_member_never_touches_push_devices()
    {
        var h = new TenantHarness();

        await h.Sut.EnableTenantAdminAsync(TenantId, Upn);

        AssertNeverRevoked(h.Revoker);
    }

    [Fact]
    public async Task Demotion_to_viewer_revokes_once_as_role_lost()
    {
        var h = new TenantHarness();
        h.GivenMember(Constants.TenantRoles.Admin);

        var updated = await h.Sut.UpdateMemberPermissionsAsync(TenantId, Upn, Constants.TenantRoles.Viewer, false);

        Assert.True(updated);
        AssertRevokedOnce(h.Revoker, Tenant, Upn, "role_lost");
    }

    [Fact]
    public async Task Demotion_from_admin_to_operator_keeps_the_devices()
    {
        var h = new TenantHarness();
        h.GivenMember(Constants.TenantRoles.Admin);

        var updated = await h.Sut.UpdateMemberPermissionsAsync(TenantId, Upn, Constants.TenantRoles.Operator, true);

        Assert.True(updated);
        AssertNeverRevoked(h.Revoker);
    }

    [Fact]
    public async Task Promotion_from_operator_to_admin_keeps_the_devices()
    {
        var h = new TenantHarness();
        h.GivenMember(Constants.TenantRoles.Operator);

        var updated = await h.Sut.UpdateMemberPermissionsAsync(TenantId, Upn, Constants.TenantRoles.Admin, false);

        Assert.True(updated);
        AssertNeverRevoked(h.Revoker);
    }

    [Fact]
    public async Task Viewer_to_viewer_update_revokes_too_because_the_hook_keys_on_the_new_role_only()
    {
        // The service never reads the previous role, so an update that lands on Viewer revokes
        // whether or not it was a demotion. Idempotent in practice: a Viewer cannot pair, so the
        // revoke finds no device — and it cleans up after an earlier hook that failed soft.
        var h = new TenantHarness();
        h.GivenMember(Constants.TenantRoles.Viewer);

        var updated = await h.Sut.UpdateMemberPermissionsAsync(TenantId, Upn, Constants.TenantRoles.Viewer, false);

        Assert.True(updated);
        AssertRevokedOnce(h.Revoker, Tenant, Upn, "role_lost");
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Update_that_changes_no_row_revokes_nothing()
    {
        var h = new TenantHarness();
        h.AdminRepo.Setup(r => r.UpdateMemberPermissionsAsync(TenantId, Upn, Constants.TenantRoles.Viewer, false)).ReturnsAsync(false);

        var updated = await h.Sut.UpdateMemberPermissionsAsync(TenantId, Upn, Constants.TenantRoles.Viewer, false);

        Assert.False(updated);
        AssertNeverRevoked(h.Revoker);
    }

    [Fact]
    public async Task Tenant_service_without_a_revoker_still_completes_every_hook_bearing_mutation()
    {
        var h = new TenantHarness(withRevoker: false);

        await h.Sut.RemoveTenantAdminAsync(TenantId, Upn);
        await h.Sut.DisableTenantAdminAsync(TenantId, Upn);
        var updated = await h.Sut.UpdateMemberPermissionsAsync(TenantId, Upn, Constants.TenantRoles.Viewer, false);

        Assert.True(updated);
        h.AdminRepo.Verify(r => r.RemoveTenantMemberAsync(TenantId, Upn), Times.Once);
        h.AdminRepo.Verify(r => r.SetTenantMemberEnabledAsync(TenantId, Upn, false), Times.Once);
    }

    // ── GlobalAdminService ───────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_global_admin_revokes_their_platform_devices_once_as_global_admin_removed()
    {
        var h = new GlobalHarness();

        await h.Sut.RemoveGlobalAdminAsync(Upn);

        AssertRevokedOnce(h.Revoker, NotificationScope.Platform, Upn, "global_admin_removed");
    }

    [Fact]
    public async Task Disabling_a_global_admin_revokes_their_platform_devices_once_as_global_admin_disabled()
    {
        var h = new GlobalHarness();

        await h.Sut.DisableGlobalAdminAsync(Upn);

        AssertRevokedOnce(h.Revoker, NotificationScope.Platform, Upn, "global_admin_disabled");
    }

    [Fact]
    public async Task Global_remove_hands_the_revoker_the_lower_cased_upn()
    {
        var h = new GlobalHarness();

        await h.Sut.RemoveGlobalAdminAsync("Leaver@Contoso.INVALID");

        AssertRevokedOnce(h.Revoker, NotificationScope.Platform, "leaver@contoso.invalid", "global_admin_removed");
    }

    [Fact]
    public async Task Global_service_without_a_revoker_still_completes_remove_and_disable()
    {
        var h = new GlobalHarness(withRevoker: false);

        await h.Sut.RemoveGlobalAdminAsync(Upn);
        await h.Sut.DisableGlobalAdminAsync(Upn);

        h.AdminRepo.Verify(r => r.RemoveGlobalAdminAsync(Upn), Times.Once);
        h.AdminRepo.Verify(r => r.DisableGlobalAdminAsync(Upn), Times.Once);
    }

    // ── Assertions ───────────────────────────────────────────────────────────

    private static void AssertRevokedOnce(Mock<IPushDeviceRevoker> revoker, NotificationScope scope, string upn, string reason)
    {
        revoker.Verify(r => r.RevokeOwnerAsync(It.Is<NotificationScope>(s => s == scope), upn, reason), Times.Once);
        revoker.VerifyNoOtherCalls();
    }

    private static void AssertNeverRevoked(Mock<IPushDeviceRevoker> revoker)
        => revoker.Verify(r => r.RevokeOwnerAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

    // ── Harnesses ────────────────────────────────────────────────────────────

    private sealed class TenantHarness
    {
        public Mock<IAdminRepository> AdminRepo { get; } = new(MockBehavior.Loose);
        public Mock<IPushDeviceRevoker> Revoker { get; } = new(MockBehavior.Loose);
        public TenantAdminsService Sut { get; }

        public TenantHarness(bool withRevoker = true)
        {
            AdminRepo.Setup(r => r.RemoveTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            AdminRepo.Setup(r => r.SetTenantMemberEnabledAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(true);
            // The hook fires only after a successful write, so the default answer is "row updated".
            AdminRepo.Setup(r => r.UpdateMemberPermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(true);
            Revoker.Setup(r => r.RevokeOwnerAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            var cache = new MemoryCache(new MemoryCacheOptions());
            Sut = withRevoker
                ? new TenantAdminsService(AdminRepo.Object, cache, NullLogger<TenantAdminsService>.Instance, Revoker.Object)
                : new TenantAdminsService(AdminRepo.Object, cache, NullLogger<TenantAdminsService>.Instance);
        }

        /// <summary>States the member's current row. The service does not read it today; the arrange stays honest if it ever does.</summary>
        public void GivenMember(string role)
            => AdminRepo.Setup(r => r.GetTenantMemberAsync(TenantId, Upn))
                .ReturnsAsync(new TenantMember { TenantId = TenantId, Upn = Upn, Role = role, IsEnabled = true });
    }

    private sealed class GlobalHarness
    {
        public Mock<IAdminRepository> AdminRepo { get; } = new(MockBehavior.Loose);
        public Mock<IPushDeviceRevoker> Revoker { get; } = new(MockBehavior.Loose);
        public GlobalAdminService Sut { get; }

        public GlobalHarness(bool withRevoker = true)
        {
            AdminRepo.Setup(r => r.RemoveGlobalAdminAsync(It.IsAny<string>())).ReturnsAsync(true);
            AdminRepo.Setup(r => r.DisableGlobalAdminAsync(It.IsAny<string>())).ReturnsAsync(true);
            Revoker.Setup(r => r.RevokeOwnerAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            var cache = new MemoryCache(new MemoryCacheOptions());
            var bindings = new AdminIdentityBindingService(AdminRepo.Object, cache, NullLogger<AdminIdentityBindingService>.Instance);
            Sut = withRevoker
                ? new GlobalAdminService(AdminRepo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance, Revoker.Object)
                : new GlobalAdminService(AdminRepo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance);
        }
    }
}
