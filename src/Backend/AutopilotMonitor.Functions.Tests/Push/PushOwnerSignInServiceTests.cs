using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// What a portal sign-in does to the person's push devices: the stamp carries the app-role claim
/// role (null clears it); losing that role with no TenantAdmins row taking its place revokes the
/// home-tenant devices (wipe push, then the row is gone); and a sign-in after the inactivity window
/// — or the first one ever — resumes this owner's owner_inactive pauses at once, the platform ones
/// only for a Global Admin. Fail-soft towards the sign-in.
/// </summary>
public class PushOwnerSignInServiceTests
{
    // The delivery harness's tenant, and the person its AddDevice defaults to (oid derived from the UPN).
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string Upn = "admin@contoso.invalid";
    private const string ObjectId = "oid-admin@contoso.invalid";
    private static readonly DateTime Now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(Constants.TenantRoles.Admin)]
    [InlineData(Constants.TenantRoles.Viewer)]
    [InlineData(null)]
    public async Task Sign_in_stamps_the_time_and_the_claim_role(string? claimRole)
    {
        var h = new Harness();
        h.Previous(Now.AddDays(-1), Constants.TenantRoles.Operator);

        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, claimRole, isGlobalAdmin: false, Now);

        var owner = h.Push.Repo.Owners[(TenantId, ObjectId)];
        Assert.Equal(Now, owner.LastSignInUtc);
        Assert.Equal(claimRole, owner.ClaimRole);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Constants.TenantRoles.Viewer)]
    public async Task Losing_the_claim_role_revokes_the_home_tenant_devices(string? claimRole)
    {
        var h = new Harness();
        h.Previous(Now.AddDays(-1), Constants.TenantRoles.Admin);
        h.Push.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        h.Push.AddDevice("d2", Constants.Push.DeviceStatus.Active, upn: "other@contoso.invalid");
        h.Push.AddDevice("p1", Constants.Push.DeviceStatus.Active, scope: Constants.Push.PlatformScope);

        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, claimRole, isGlobalAdmin: false, Now);

        Assert.False(h.Push.Repo.Devices.ContainsKey((TenantId, "d1")));
        Assert.True(h.Push.Repo.Devices.ContainsKey((TenantId, "d2")));
        Assert.True(h.Push.Repo.Devices.ContainsKey((Constants.Push.PlatformScope, "p1")));
        // The wipe push went to the revoked device first.
        Assert.EndsWith("/d1", Assert.Single(h.Push.Http.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task A_member_row_that_took_the_claims_place_keeps_the_devices()
    {
        var h = new Harness();
        h.Previous(Now.AddDays(-1), Constants.TenantRoles.Admin);
        h.Push.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        h.AdminRepo.Setup(r => r.GetTenantMemberAsync(TenantId, Upn))
            .ReturnsAsync(new TenantMember { TenantId = TenantId, Upn = Upn, Role = Constants.TenantRoles.Admin, IsEnabled = true });

        // With a row the sign-in records no claim role — that is not a loss.
        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, claimRole: null, isGlobalAdmin: false, Now);

        Assert.True(h.Push.Repo.Devices.ContainsKey((TenantId, "d1")));
        Assert.Empty(h.Push.Http.Requests);
    }

    [Theory]
    [InlineData(Constants.TenantRoles.Admin, Constants.TenantRoles.Operator)]
    [InlineData(null, null)]
    [InlineData(Constants.TenantRoles.Viewer, null)]
    public async Task A_sign_in_that_loses_no_alerting_claim_role_revokes_nothing(string? before, string? after)
    {
        var h = new Harness();
        h.Previous(Now.AddDays(-1), before);
        h.Push.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, after, isGlobalAdmin: false, Now);

        Assert.True(h.Push.Repo.Devices.ContainsKey((TenantId, "d1")));
        h.AdminRepo.Verify(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_sign_in_after_the_inactivity_window_resumes_only_this_owners_inactivity_pauses()
    {
        var h = new Harness();
        h.Previous(Now.AddDays(-(h.Push.Settings.OwnerInactivityDays + 5)), claimRole: null);
        h.Push.AddDevice("d1", Constants.Push.DeviceStatus.Paused, statusReason: "owner_inactive");
        h.Push.AddDevice("d2", Constants.Push.DeviceStatus.Paused, statusReason: "delivery_failures");
        h.Push.AddDevice("d3", Constants.Push.DeviceStatus.Paused, upn: "other@contoso.invalid", statusReason: "owner_inactive");
        h.Push.AddDevice("p1", Constants.Push.DeviceStatus.Paused, scope: Constants.Push.PlatformScope, statusReason: "owner_inactive");

        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, claimRole: null, isGlobalAdmin: false, Now);

        Assert.Equal(Constants.Push.DeviceStatus.Active, h.Push.Repo.Devices[(TenantId, "d1")].Status);
        Assert.Null(h.Push.Repo.Devices[(TenantId, "d1")].StatusReason);
        Assert.Equal(Constants.Push.DeviceStatus.Paused, h.Push.Repo.Devices[(TenantId, "d2")].Status);
        Assert.Equal(Constants.Push.DeviceStatus.Paused, h.Push.Repo.Devices[(TenantId, "d3")].Status);
        // Not a Global Admin: the platform partition is not theirs to resume.
        Assert.Equal(Constants.Push.DeviceStatus.Paused, h.Push.Repo.Devices[(Constants.Push.PlatformScope, "p1")].Status);
        Assert.Empty(h.Push.Http.Requests);   // a resume is silent
    }

    [Fact]
    public async Task A_global_admins_sign_in_resumes_their_platform_pauses_too()
    {
        var h = new Harness();
        h.Previous(Now.AddDays(-(h.Push.Settings.OwnerInactivityDays + 5)), claimRole: null);
        h.Push.AddDevice("p1", Constants.Push.DeviceStatus.Paused, scope: Constants.Push.PlatformScope, statusReason: "owner_inactive");
        h.Push.AddDevice("p2", Constants.Push.DeviceStatus.Paused, upn: "ga2@contoso.invalid", scope: Constants.Push.PlatformScope, statusReason: "owner_inactive");

        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, claimRole: null, isGlobalAdmin: true, Now);

        Assert.Equal(Constants.Push.DeviceStatus.Active, h.Push.Repo.Devices[(Constants.Push.PlatformScope, "p1")].Status);
        Assert.Equal(Constants.Push.DeviceStatus.Paused, h.Push.Repo.Devices[(Constants.Push.PlatformScope, "p2")].Status);
    }

    [Fact]
    public async Task The_first_sign_in_ever_resumes_too()
    {
        var h = new Harness();   // no PushOwners row yet
        h.Push.AddDevice("d1", Constants.Push.DeviceStatus.Paused, statusReason: "owner_inactive");

        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, claimRole: null, isGlobalAdmin: false, Now);

        Assert.Equal(Constants.Push.DeviceStatus.Active, h.Push.Repo.Devices[(TenantId, "d1")].Status);
    }

    [Fact]
    public async Task A_sign_in_within_the_window_resumes_nothing()
    {
        var h = new Harness();
        h.Previous(Now.AddDays(-1), claimRole: null);
        h.Push.AddDevice("d1", Constants.Push.DeviceStatus.Paused, statusReason: "owner_inactive");

        await h.Sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, claimRole: null, isGlobalAdmin: true, Now);

        Assert.Equal(Constants.Push.DeviceStatus.Paused, h.Push.Repo.Devices[(TenantId, "d1")].Status);
    }

    [Fact]
    public async Task A_storage_failure_never_reaches_the_sign_in()
    {
        var push = new PushDeliveryServiceTests.Harness();
        var repo = new Mock<IPushDeviceRepository>();
        repo.Setup(r => r.GetOwnerAsync(It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("storage unavailable"));
        var sut = new PushOwnerSignInService(repo.Object, new Mock<IAdminRepository>().Object, push.Settings, push.Sut,
            NullLogger<PushOwnerSignInService>.Instance);

        var exception = await Record.ExceptionAsync(() => sut.OnPortalSignInAsync(TenantId, ObjectId, Upn, null, isGlobalAdmin: false, Now));

        Assert.Null(exception);
    }

    private sealed class Harness
    {
        public PushDeliveryServiceTests.Harness Push { get; } = new();
        public Mock<IAdminRepository> AdminRepo { get; } = new();
        public PushOwnerSignInService Sut { get; }

        public Harness()
            => Sut = new PushOwnerSignInService(Push.Repo, AdminRepo.Object, Push.Settings, Push.Sut, NullLogger<PushOwnerSignInService>.Instance);

        /// <summary>The stamp the previous sign-in left.</summary>
        public void Previous(DateTime lastSignInUtc, string? claimRole)
            => Push.Repo.Owners[(TenantId, ObjectId)] = new PushOwner
            {
                HomeTenantId = TenantId, ObjectId = ObjectId, Upn = Upn, LastSignInUtc = lastSignInUtc, ClaimRole = claimRole,
            };
    }
}
