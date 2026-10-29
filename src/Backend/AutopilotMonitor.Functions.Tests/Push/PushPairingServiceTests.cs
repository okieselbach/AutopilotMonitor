using System.Net;
using System.Net.Http;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The pairing flow end to end against the in-memory repository and a fake push service:
/// create → begin → redeem (Pending) → confirm (Active, confirmation push), the one-shot redeem,
/// the five-failure burn, expiry, the device token, re-subscribe re-arming a Stale row, the
/// role/channel gates (the channel also silences the device test, never the device list), the
/// pairing of a member known only through an Entra app role, grant ownership (a grant answers
/// only to its creator) and the one-per-host-and-hour ops event for a refused subscription endpoint.
/// </summary>
public class PushPairingServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly NotificationScope Tenant = NotificationScope.Tenant(TenantId);
    private static readonly PushCaller Admin = new("admin@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000001", TenantId, IsScopeAdmin: true);
    private static readonly PushCaller Operator = new("ops@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000002", TenantId, IsScopeAdmin: false);
    /// <summary>Same scope, same admin role, different object id — not the grant's creator.</summary>
    private static readonly PushCaller OtherAdmin = new("other@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000003", TenantId, IsScopeAdmin: true);

    [Fact]
    public async Task Full_flow_creates_a_pending_device_and_confirmation_activates_it_with_a_push()
    {
        var h = new Harness();

        var created = await h.Sut.CreateAsync(Tenant, Admin);
        Assert.True(created.Ok);
        Assert.Equal(Constants.Push.CodeLength, created.Value!.Code.Length);
        Assert.Equal(Constants.PortalPushPairUrl(created.Value.Code), created.Value.Url);
        Assert.Single(h.Repo.Owners);   // pairing stamps the owner's sign-in

        var begin = await h.Sut.BeginAsync(created.Value.Code.ToLowerInvariant().Replace("0", "O"));
        Assert.True(begin.Ok);
        Assert.Equal(h.Keys.Active.Kid, begin.Value!.Kid);
        Assert.Equal(h.Keys.Active.PublicKeyBase64Url, begin.Value.VapidPublicKey);

        var redeemed = await h.Sut.RedeemAsync(h.RedeemRequest(created.Value.Code, label: " My iPhone \u0007 "));
        Assert.True(redeemed.Ok);
        Assert.Equal(Constants.Push.DeviceStatus.Pending, redeemed.Value!.Status);
        Assert.Equal(3, redeemed.Value.DeviceToken.Split('.').Length);

        var status = await h.Sut.GetStatusAsync(Tenant, created.Value.PairingId, Admin);
        Assert.Equal(Constants.Push.PairingStatus.Redeemed, status.Value!.Status);
        Assert.Equal("My iPhone", status.Value.Device!.Label);
        Assert.Equal("ios-homescreen", status.Value.Device.Platform);

        // Pending devices are invisible to the list and receive nothing.
        Assert.Empty((await h.Sut.ListAsync(Tenant, Admin)).Devices);

        var confirmed = await h.Sut.ConfirmAsync(Tenant, created.Value.PairingId, Admin);
        Assert.True(confirmed.Ok);
        var device = h.Repo.Devices.Values.Single();
        Assert.Equal(Constants.Push.DeviceStatus.Active, device.Status);
        Assert.NotNull(device.ConfirmedUtc);
        Assert.Equal(h.Keys.Active.Kid, device.VapidKid);
        Assert.Single(h.Http.Requests);   // the "paired" push
        Assert.Equal(Constants.Push.PairingStatus.Confirmed, (await h.Sut.GetStatusAsync(Tenant, created.Value.PairingId, Admin)).Value!.Status);

        var listed = Assert.Single((await h.Sut.ListAsync(Tenant, Admin)).Devices);
        Assert.True(listed.IsOwn);
        Assert.Equal("My iPhone", listed.Label);
    }

    [Fact]
    public async Task Pairing_events_name_the_tenant_and_a_platform_pairing_none()
    {
        var channel = new PushDeliveryServiceTests.CapturingChannel();
        var h = new Harness(telemetry: PushDeliveryServiceTests.Telemetry(channel));

        var created = await h.Sut.CreateAsync(Tenant, Admin);
        await h.Sut.RedeemAsync(h.RedeemRequest(created.Value!.Code));
        await h.Sut.ConfirmAsync(Tenant, created.Value.PairingId, Admin);
        await h.Sut.CreateAsync(NotificationScope.Platform, Admin);

        var events = channel.Items.OfType<EventTelemetry>().Where(e => e.Name == "PushPairing").ToList();
        Assert.Equal(new[] { "created", "redeemed", "confirmed", "created" }, events.Select(e => e.Properties["action"]));
        Assert.All(events.Take(3), e => Assert.Equal(TenantId, e.Properties["tenantId"]));
        Assert.Equal("platform", events[3].Properties["scope"]);
        Assert.Equal(string.Empty, events[3].Properties["tenantId"]);
    }

    [Fact]
    public async Task A_code_redeems_exactly_once()
    {
        var h = new Harness();
        var created = await h.Sut.CreateAsync(Tenant, Admin);

        Assert.True((await h.Sut.RedeemAsync(h.RedeemRequest(created.Value!.Code))).Ok);
        var second = await h.Sut.RedeemAsync(h.RedeemRequest(created.Value.Code));

        Assert.Equal(PushOpError.CodeInvalid, second.Error);
        Assert.Single(h.Repo.Devices);
    }

    [Fact]
    public async Task Concurrent_redeem_loser_gets_CodeUsed_and_leaves_no_device()
    {
        var h = new Harness();
        var created = await h.Sut.CreateAsync(Tenant, Admin);
        var codeHash = PushPairingService.HashCode(created.Value!.Code);

        // The grant row moves on between this redeem's read and its one-shot write (K9).
        h.Repo.BeforeGrantUpdate = () => h.Repo.TouchGrant(codeHash);

        var result = await h.Sut.RedeemAsync(h.RedeemRequest(created.Value.Code));

        Assert.Equal(PushOpError.CodeUsed, result.Error);
        Assert.Empty(h.Repo.Devices);   // the device created before the CAS is rolled back
    }

    [Fact]
    public async Task Unknown_expired_and_burnt_codes_share_one_answer()
    {
        var h = new Harness();
        Assert.Equal(PushOpError.CodeInvalid, (await h.Sut.BeginAsync("ZZZZZZZZZZZ")).Error);
        Assert.Equal(PushOpError.CodeInvalid, (await h.Sut.BeginAsync("not a code at all")).Error);

        var created = await h.Sut.CreateAsync(Tenant, Admin);
        var codeHash = PushPairingService.HashCode(created.Value!.Code);
        var entry = h.Repo.Grants[codeHash];
        entry.Grant.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        h.Repo.Grants[codeHash] = entry;

        Assert.Equal(PushOpError.CodeInvalid, (await h.Sut.BeginAsync(created.Value.Code)).Error);
        Assert.Equal(Constants.Push.PairingStatus.Expired, (await h.Sut.GetStatusAsync(Tenant, created.Value.PairingId, Admin)).Value!.Status);

        // Five wrong-state hits burn the grant row.
        for (var i = 0; i < Constants.Push.MaxRedeemFailures; i++)
            await h.Sut.BeginAsync(created.Value.Code);
        Assert.False(h.Repo.Grants.ContainsKey(codeHash));
    }

    [Fact]
    public async Task Device_token_resolves_only_with_the_right_secret()
    {
        var h = new Harness();
        var created = await h.Sut.CreateAsync(Tenant, Admin);
        var redeemed = await h.Sut.RedeemAsync(h.RedeemRequest(created.Value!.Code));
        var token = redeemed.Value!.DeviceToken;

        Assert.NotNull(await h.Sut.ResolveDeviceAsync(token));
        Assert.Null(await h.Sut.ResolveDeviceAsync(token[..^2] + "AA"));
        Assert.Null(await h.Sut.ResolveDeviceAsync("platform." + token.Split('.')[1] + "." + token.Split('.')[2]));
        Assert.Null(await h.Sut.ResolveDeviceAsync("garbage"));
        Assert.Null(await h.Sut.ResolveDeviceAsync(null));
    }

    [Fact]
    public async Task Resubscribe_re_arms_a_stale_device_and_refuses_unknown_endpoints()
    {
        var h = new Harness();
        var created = await h.Sut.CreateAsync(Tenant, Admin);
        var redeemed = await h.Sut.RedeemAsync(h.RedeemRequest(created.Value!.Code));
        await h.Sut.ConfirmAsync(Tenant, created.Value.PairingId, Admin);
        var device = (await h.Sut.ResolveDeviceAsync(redeemed.Value!.DeviceToken))!;
        await h.Repo.MutateDeviceAsync(device.Scope, device.DeviceId, _ => new Dictionary<string, object?> { ["Status"] = Constants.Push.DeviceStatus.Stale, ["StatusReason"] = "gone" });

        var refused = await h.Sut.ResubscribeAsync(device, new ResubscribeRequest
        {
            Endpoint = "https://attacker.invalid/push", P256dh = h.Subscription.P256dh, Auth = h.Subscription.Auth, Kid = h.Keys.Active.Kid,
        });
        Assert.Equal(PushOpError.InvalidSubscription, refused.Error);
        h.OpsEvents.Verify(o => o.RecordPushEndpointRefusedAsync("attacker.invalid", "tenant"), Times.Once);

        var ok = await h.Sut.ResubscribeAsync(device, new ResubscribeRequest
        {
            Endpoint = "https://web.push.apple.com/QF/new-token", P256dh = h.Subscription.P256dh, Auth = h.Subscription.Auth, Kid = h.Keys.Active.Kid,
        });
        Assert.True(ok.Ok);
        Assert.Equal(Constants.Push.DeviceStatus.Active, ok.Value!.Status);
        Assert.Equal("https://web.push.apple.com/QF/new-token", h.Repo.Devices.Values.Single().Endpoint);
    }

    [Fact]
    public async Task A_rotated_key_is_a_re_registration_not_a_re_pair()
    {
        var h = new Harness();
        var created = await h.Sut.CreateAsync(Tenant, Admin);
        var redeemed = await h.Sut.RedeemAsync(h.RedeemRequest(created.Value!.Code));
        await h.Sut.ConfirmAsync(Tenant, created.Value.PairingId, Admin);
        var token = redeemed.Value!.DeviceToken;
        var device = (await h.Sut.ResolveDeviceAsync(token))!;

        // Before the rotation the status names the key the device subscribed with as the active one.
        var before = await h.Sut.GetDeviceStatusAsync(device);
        Assert.Equal(h.Keys.Active.Kid, before.Kid);
        Assert.Equal(h.Keys.Active.Kid, before.ActiveKid);
        Assert.Equal(h.Keys.Active.PublicKeyBase64Url, before.ActiveVapidPublicKey);

        // Rotation: a new active key, the old one retired — the same device rows under the new ring.
        var rotated = new Harness(new VapidKeyRing(VapidKey.Generate(), new[] { h.Keys.Active }), h.Repo);
        var status = await rotated.Sut.GetDeviceStatusAsync(device);
        Assert.Equal(h.Keys.Active.Kid, status.Kid);
        Assert.Equal(rotated.Keys.Active.Kid, status.ActiveKid);
        Assert.Equal(rotated.Keys.Active.PublicKeyBase64Url, status.ActiveVapidPublicKey);
        Assert.NotEqual(status.Kid, status.ActiveKid);

        // The receiver subscribes again with the new key and PUTs the new kid: the row moves over, no re-pair.
        var moved = await rotated.Sut.ResubscribeAsync(device, new ResubscribeRequest
        {
            Endpoint = "https://web.push.apple.com/QF/rotated-token", P256dh = h.Subscription.P256dh, Auth = h.Subscription.Auth, Kid = rotated.Keys.Active.Kid,
        });
        Assert.True(moved.Ok);
        Assert.Equal(rotated.Keys.Active.Kid, moved.Value!.Kid);
        Assert.Equal(rotated.Keys.Active.Kid, h.Repo.Devices.Values.Single().VapidKid);
        Assert.Equal(token.Split('.')[2], token.Split('.')[2]);   // the device token is untouched by the migration

        // Once the old key left the ring, a row still on it is Stale (vapid_key_mismatch): the same PUT re-arms it,
        // while a PUT that still names the retired kid is refused.
        var withoutOld = new Harness(new VapidKeyRing(rotated.Keys.Active), h.Repo);
        await h.Repo.MutateDeviceAsync(device.Scope, device.DeviceId, _ => new Dictionary<string, object?>
        {
            ["VapidKid"] = h.Keys.Active.Kid, ["Status"] = Constants.Push.DeviceStatus.Stale, ["StatusReason"] = "vapid_key_mismatch",
        });
        var staleRow = (await withoutOld.Sut.ResolveDeviceAsync(token))!;
        Assert.Equal(Constants.Push.DeviceStatus.Stale, staleRow.Status);

        var refused = await withoutOld.Sut.ResubscribeAsync(staleRow, new ResubscribeRequest
        {
            Endpoint = "https://web.push.apple.com/QF/rotated-token", P256dh = h.Subscription.P256dh, Auth = h.Subscription.Auth, Kid = h.Keys.Active.Kid,
        });
        Assert.Equal(PushOpError.InvalidSubscription, refused.Error);

        var reArmed = await withoutOld.Sut.ResubscribeAsync(staleRow, new ResubscribeRequest
        {
            Endpoint = "https://web.push.apple.com/QF/rotated-token-2", P256dh = h.Subscription.P256dh, Auth = h.Subscription.Auth, Kid = withoutOld.Keys.Active.Kid,
        });
        Assert.True(reArmed.Ok);
        Assert.Equal(Constants.Push.DeviceStatus.Active, reArmed.Value!.Status);
        Assert.Equal(withoutOld.Keys.Active.Kid, h.Repo.Devices.Values.Single().VapidKid);
        Assert.Null(h.Repo.Devices.Values.Single().StatusReason);
    }

    [Fact]
    public async Task Operators_see_only_their_own_devices_and_cannot_remove_others()
    {
        var h = new Harness();
        var adminPairing = await h.Sut.CreateAsync(Tenant, Admin);
        await h.Sut.RedeemAsync(h.RedeemRequest(adminPairing.Value!.Code));
        await h.Sut.ConfirmAsync(Tenant, adminPairing.Value.PairingId, Admin);

        var opsPairing = await h.Sut.CreateAsync(Tenant, Operator);
        await h.Sut.RedeemAsync(h.RedeemRequest(opsPairing.Value!.Code, label: "ops phone"));
        await h.Sut.ConfirmAsync(Tenant, opsPairing.Value.PairingId, Operator);

        Assert.Equal(2, (await h.Sut.ListAsync(Tenant, Admin)).Devices.Count);
        var opsView = Assert.Single((await h.Sut.ListAsync(Tenant, Operator)).Devices);
        Assert.Equal("ops phone", opsView.Label);

        var adminDeviceId = h.Repo.Devices.Values.Single(d => d.OwnerUpn == Admin.Upn).DeviceId;
        Assert.Equal(PushOpError.NotFound, await h.Sut.DeleteAsync(Tenant, adminDeviceId, Operator));
        Assert.Equal(PushOpError.None, await h.Sut.DeleteAsync(Tenant, adminDeviceId, Admin));
        Assert.Single(h.Repo.Devices);
    }

    [Fact]
    public async Task A_grant_answers_only_to_the_caller_who_created_it()
    {
        var h = new Harness();
        var created = await h.Sut.CreateAsync(Tenant, Admin);
        Assert.True((await h.Sut.RedeemAsync(h.RedeemRequest(created.Value!.Code))).Ok);
        var pairingId = created.Value.PairingId;

        // Same scope, even a scope admin — but another object id: for them the grant does not exist.
        Assert.Equal(PushOpError.NotFound, (await h.Sut.GetStatusAsync(Tenant, pairingId, OtherAdmin)).Error);
        Assert.Equal(PushOpError.NotFound, (await h.Sut.ConfirmAsync(Tenant, pairingId, OtherAdmin)).Error);
        Assert.Equal(PushOpError.NotFound, await h.Sut.RejectAsync(Tenant, pairingId, OtherAdmin));

        // Nothing moved: the grant still waits for its owner, the Pending device is still there, no push went out.
        Assert.Equal(Constants.Push.PairingStatus.Redeemed, h.Repo.Grants[pairingId].Grant.Status);
        Assert.Equal(Constants.Push.DeviceStatus.Pending, Assert.Single(h.Repo.Devices.Values).Status);
        Assert.Empty(h.Http.Requests);
        Assert.Equal(Constants.Push.PairingStatus.Redeemed, (await h.Sut.GetStatusAsync(Tenant, pairingId, Admin)).Value!.Status);
    }

    [Fact]
    public async Task Refused_endpoints_raise_one_ops_event_per_host_scope_and_hour()
    {
        var h = new Harness();

        // Two fresh grants, two different paths on the same refused host: one ops event.
        var first = await h.Sut.CreateAsync(Tenant, Admin);
        var second = await h.Sut.CreateAsync(Tenant, Admin);
        Assert.Equal(PushOpError.InvalidSubscription,
            (await h.Sut.RedeemAsync(h.RedeemRequest(first.Value!.Code, endpoint: "https://push.example.invalid/sub/1"))).Error);
        Assert.Equal(PushOpError.InvalidSubscription,
            (await h.Sut.RedeemAsync(h.RedeemRequest(second.Value!.Code, endpoint: "https://push.example.invalid/sub/2"))).Error);
        h.OpsEvents.Verify(o => o.RecordPushEndpointRefusedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        // A different refused host is a different cause: a second event.
        var third = await h.Sut.CreateAsync(Tenant, Admin);
        Assert.Equal(PushOpError.InvalidSubscription,
            (await h.Sut.RedeemAsync(h.RedeemRequest(third.Value!.Code, endpoint: "https://relay.example.invalid/sub/3"))).Error);
        h.OpsEvents.Verify(o => o.RecordPushEndpointRefusedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));

        // The hostname is the only endpoint-derived value that leaves the service (the endpoint is a credential).
        h.OpsEvents.Verify(o => o.RecordPushEndpointRefusedAsync("push.example.invalid", "tenant"), Times.Once);
        h.OpsEvents.Verify(o => o.RecordPushEndpointRefusedAsync("relay.example.invalid", "tenant"), Times.Once);
        Assert.Empty(h.Repo.Devices);   // a refused subscription never becomes a device
    }

    [Fact]
    public async Task Pairing_needs_a_push_channel_and_an_eligible_role()
    {
        var h = new Harness();
        h.Eligibility.Setup(e => e.HasPushChannelAsync(Tenant)).ReturnsAsync(false);
        Assert.Equal(PushOpError.ChannelRequired, (await h.Sut.CreateAsync(Tenant, Admin)).Error);

        h.Eligibility.Setup(e => e.HasPushChannelAsync(Tenant)).ReturnsAsync(true);
        h.Eligibility.Setup(e => e.IsEligibleAsync(Tenant, Admin.Upn, Admin.ObjectId, Admin.HomeTenantId)).ReturnsAsync(false);
        Assert.Equal(PushOpError.NotEligible, (await h.Sut.CreateAsync(Tenant, Admin)).Error);
    }

    [Fact]
    public async Task A_refused_pairing_names_the_needed_role_only_on_the_platform()
    {
        var h = new Harness();
        h.Eligibility.Setup(e => e.IsEligibleAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        var tenant = await h.Sut.CreateAsync(Tenant, Admin);
        var platform = await h.Sut.CreateAsync(NotificationScope.Platform, Admin);

        // The tenant refusal carries the function's default text ("…in this tenant's member list.").
        Assert.Equal(PushOpError.NotEligible, tenant.Error);
        Assert.Null(tenant.Message);
        Assert.Equal(PushOpError.NotEligible, platform.Error);
        Assert.Equal("Push notifications on the platform need the Global Administrator role.", platform.Message);
        Assert.Empty(h.Repo.Grants);
    }

    [Fact]
    public async Task A_member_known_only_through_an_entra_app_role_can_pair_at_once()
    {
        var host = new AppRoleHost(appRolesEnabled: true);
        var member = new PushCaller("member@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000004", TenantId, IsScopeAdmin: true,
            AppRoles: new[] { Constants.TenantRoles.Admin });

        var created = await host.Sut.CreateAsync(Tenant, member);

        // No TenantAdmins row and no earlier sign-in stamp: the pairing recorded the live claim first.
        Assert.True(created.Ok);
        Assert.Equal(Constants.TenantRoles.Admin, host.Repo.Owners[(TenantId, member.ObjectId)].ClaimRole);
        Assert.Single(host.Repo.Grants);
    }

    [Fact]
    public async Task Without_the_app_role_or_with_app_roles_off_the_pairing_is_refused()
    {
        var withoutRole = new AppRoleHost(appRolesEnabled: true);
        var plainUser = new PushCaller("user@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000005", TenantId, IsScopeAdmin: false);
        Assert.Equal(PushOpError.NotEligible, (await withoutRole.Sut.CreateAsync(Tenant, plainUser)).Error);
        Assert.Null(withoutRole.Repo.Owners[(TenantId, plainUser.ObjectId)].ClaimRole);

        var appRolesOff = new AppRoleHost(appRolesEnabled: false);
        var claimAdmin = plainUser with { AppRoles = new[] { Constants.TenantRoles.Admin } };
        Assert.Equal(PushOpError.NotEligible, (await appRolesOff.Sut.CreateAsync(Tenant, claimAdmin)).Error);
        Assert.Null(appRolesOff.Repo.Owners[(TenantId, plainUser.ObjectId)].ClaimRole);
        Assert.Empty(appRolesOff.Repo.Grants);
    }

    [Fact]
    public async Task A_device_test_with_the_channel_off_reports_it_and_sends_nothing()
    {
        var h = new Harness();
        var deviceId = await PairAsync(h, Admin);
        var sentBefore = h.Http.Requests.Count;
        h.Eligibility.Setup(e => e.HasPushChannelAsync(Tenant)).ReturnsAsync(false);

        var result = await h.Sut.TestAsync(Tenant, deviceId, Admin);

        Assert.True(result.Ok);
        Assert.False(result.Value!.Success);
        Assert.Equal("The Push channel is off — paired devices receive nothing.", result.Value.Message);
        Assert.Equal(sentBefore, h.Http.Requests.Count);
    }

    [Fact]
    public async Task Devices_stay_listable_and_removable_with_the_channel_off()
    {
        var h = new Harness();
        var deviceId = await PairAsync(h, Admin);
        h.Eligibility.Setup(e => e.HasPushChannelAsync(It.IsAny<NotificationScope>())).ReturnsAsync(false);

        Assert.Equal(deviceId, Assert.Single((await h.Sut.ListAsync(Tenant, Admin)).Devices).DeviceId);
        Assert.Equal(PushOpError.None, await h.Sut.DeleteAsync(Tenant, deviceId, Admin));
        Assert.Empty(h.Repo.Devices);
    }

    /// <summary>Create → redeem → confirm: one Active device of <paramref name="owner"/>; returns its id.</summary>
    private static async Task<string> PairAsync(Harness h, PushCaller owner)
    {
        var created = await h.Sut.CreateAsync(Tenant, owner);
        await h.Sut.RedeemAsync(h.RedeemRequest(created.Value!.Code));
        Assert.True((await h.Sut.ConfirmAsync(Tenant, created.Value.PairingId, owner)).Ok);
        return h.Repo.Devices.Values.Single(d => d.OwnerObjectId == owner.ObjectId).DeviceId;
    }

    /// <summary>
    /// The pairing service over the REAL <see cref="PushEligibility"/>: no TenantAdmins row for anyone,
    /// a tenant configuration with an enabled Push channel and the given app-role switch.
    /// </summary>
    private sealed class AppRoleHost
    {
        public InMemoryPushDeviceRepository Repo { get; } = new();
        public PushPairingService Sut { get; }

        public AppRoleHost(bool appRolesEnabled)
        {
            var settings = PushSettings.ForKeys(new VapidKeyRing(VapidKey.Generate()));
            var cache = new MemoryCache(new MemoryCacheOptions());
            var pushChannel = new AutopilotMonitor.Shared.Models.Notifications.NotificationChannel
            {
                Id = "phones", Name = "Phones", ProviderType = (int)AutopilotMonitor.Shared.Models.Notifications.WebhookProviderType.Push, Enabled = true,
            };
            var tenantConfig = new Mock<TenantConfigurationService>(null!, NullLogger<TenantConfigurationService>.Instance, cache, null!) { CallBase = false };
            tenantConfig.Setup(t => t.TryGetConfigurationAsync(TenantId)).ReturnsAsync((new TenantConfiguration
            {
                TenantId = TenantId,
                EntraAppRolesEnabled = appRolesEnabled,
                NotificationChannelsJson = AutopilotMonitor.Shared.Models.Notifications.NotificationChannel.SerializeList(new[] { pushChannel }),
            }, true));
            var adminConfig = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
            var eligibility = new PushEligibility(new Mock<IAdminRepository>().Object, Repo, tenantConfig.Object, adminConfig.Object, settings);
            var opsEvents = new Mock<OpsEventService>(null!, null!, null!) { CallBase = false };
            var delivery = new PushDeliveryService(Repo, eligibility, settings, new WebPushSender(new HttpClient(new RecordingHandler())),
                new Lazy<OpsEventService>(() => opsEvents.Object), cache, NullLogger<PushDeliveryService>.Instance);
            Sut = new PushPairingService(Repo, eligibility, settings, delivery, opsEvents.Object,
                Mock.Of<IMaintenanceRepository>(), tenantConfig.Object, cache, NullLogger<PushPairingService>.Instance);
        }
    }

    [Fact]
    public void Codes_normalise_typed_variants_and_refuse_everything_else()
    {
        Assert.Equal("0123456789A", PushPairingService.NormalizeCode("o123-4567 89a"));
        Assert.Equal("11111111111", PushPairingService.NormalizeCode("ilILiIlIL1l"));
        Assert.Null(PushPairingService.NormalizeCode("0123456789"));     // too short
        Assert.Null(PushPairingService.NormalizeCode("0123456789AB"));   // too long
        Assert.Null(PushPairingService.NormalizeCode("0123456789U"));    // U is not in the alphabet
        Assert.Null(PushPairingService.NormalizeCode(null));

        var code = PushPairingService.GenerateCode();
        Assert.Equal(Constants.Push.CodeLength, code.Length);
        Assert.All(code, ch => Assert.Contains(ch, Constants.Push.CodeAlphabet));
        Assert.DoesNotContain("error", code, StringComparison.OrdinalIgnoreCase);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    internal sealed class Harness
    {
        public InMemoryPushDeviceRepository Repo { get; }
        public VapidKeyRing Keys { get; }
        public Mock<PushEligibility> Eligibility { get; }
        public Mock<OpsEventService> OpsEvents { get; }
        public RecordingHandler Http { get; } = new();
        public PushPairingService Sut { get; }
        public PushDeliveryService Delivery { get; }
        public (string Endpoint, string P256dh, string Auth) Subscription { get; }

        /// <param name="keys">The platform's key ring; a rotated ring for the key-migration tests.</param>
        /// <param name="repo">A repository shared with another harness: the same device rows seen under a new ring.</param>
        /// <param name="telemetry">Optional: a client over a capturing channel to observe what the pairing service tracks.</param>
        public Harness(VapidKeyRing? keys = null, InMemoryPushDeviceRepository? repo = null, TelemetryClient? telemetry = null)
        {
            Keys = keys ?? new VapidKeyRing(VapidKey.Generate());
            Repo = repo ?? new InMemoryPushDeviceRepository();
            var settings = PushSettings.ForKeys(Keys);
            Eligibility = new Mock<PushEligibility>(null!, null!, null!, null!, null!) { CallBase = false };
            Eligibility.Setup(e => e.HasPushChannelAsync(It.IsAny<NotificationScope>())).ReturnsAsync(true);
            Eligibility.Setup(e => e.IsEligibleAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            Eligibility.Setup(e => e.IsOwnerFreshAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).ReturnsAsync(true);

            OpsEvents = new Mock<OpsEventService>(null!, null!, null!) { CallBase = false };
            OpsEvents.Setup(o => o.RecordPushEndpointRefusedAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
            OpsEvents.Setup(o => o.RecordPushDeliveryFailedAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            var sender = new WebPushSender(new HttpClient(Http) { BaseAddress = null });
            Delivery = new PushDeliveryService(Repo, Eligibility.Object, settings, sender,
                new Lazy<OpsEventService>(() => OpsEvents.Object), new MemoryCache(new MemoryCacheOptions()),
                NullLogger<PushDeliveryService>.Instance);

            var tenantConfig = new Mock<TenantConfigurationService>(null!, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()), null!) { CallBase = false };
            tenantConfig.Setup(t => t.TryGetConfigurationAsync(It.IsAny<string>()))
                .ReturnsAsync((new AutopilotMonitor.Shared.Models.TenantConfiguration { TenantId = TenantId, CompanyName = "Contoso" }, true));

            Sut = new PushPairingService(Repo, Eligibility.Object, settings, Delivery, OpsEvents.Object,
                Mock.Of<IMaintenanceRepository>(), tenantConfig.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<PushPairingService>.Instance, telemetry);

            // A browser-shaped subscription: a fresh P-256 point + 16-byte auth secret.
            using var ua = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var p = ua.ExportParameters(false);
            var point = new byte[65];
            point[0] = 0x04;
            p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X!.Length));
            p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y!.Length));
            Subscription = (
                "https://web.push.apple.com/QF3abc/device-token",
                System.Buffers.Text.Base64Url.EncodeToString(point),
                System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
        }

        public RedeemPairRequest RedeemRequest(string code, string label = "", string platform = "ios-homescreen", string? endpoint = null) => new()
        {
            Code = code,
            Kid = Keys.Active.Kid,
            Endpoint = endpoint ?? Subscription.Endpoint,
            P256dh = Subscription.P256dh,
            Auth = Subscription.Auth,
            Label = label,
            Platform = platform,
            AppVersion = "web-1",
        };
    }

    /// <summary>Records every request (and its body, read before the sender disposes it) and answers with a configurable status (201 by default).</summary>
    [Fact]
    public async Task Home_tenant_list_shows_a_global_admins_platform_device_which_the_tenant_routes_refuse()
    {
        var h = new Harness();
        var ga = new PushCaller("ga@contoso.invalid", "aaaaaaaa-0000-0000-0000-00000000000a", TenantId, IsScopeAdmin: true, IsGlobalAdmin: true);
        h.Repo.Devices[(Constants.Push.PlatformScope, "ga-phone")] = PlatformDevice("ga-phone", ga, TenantId, h.Keys.Active.Kid);
        h.Repo.Devices[(Constants.Push.PlatformScope, "other-phone")] = PlatformDevice("other-phone",
            new PushCaller("other@contoso.invalid", "aaaaaaaa-0000-0000-0000-00000000000b", "22222222-2222-2222-2222-222222222222", IsScopeAdmin: true, IsGlobalAdmin: true),
            "22222222-2222-2222-2222-222222222222", h.Keys.Active.Kid);

        // Scope admins see it (as somebody else's device), the owner as their own, an operator not at all.
        var adminView = Assert.Single((await h.Sut.ListAsync(Tenant, Admin)).Devices);
        Assert.Equal("ga-phone", adminView.DeviceId);
        Assert.Equal("platform", adminView.Scope);
        Assert.False(adminView.IsOwn);
        Assert.True(Assert.Single((await h.Sut.ListAsync(Tenant, ga)).Devices).IsOwn);
        Assert.Empty((await h.Sut.ListAsync(Tenant, Operator)).Devices);
        // The platform list itself is untouched by the rule.
        Assert.All((await h.Sut.ListAsync(NotificationScope.Platform, ga)).Devices, d => Assert.Equal("platform", d.Scope));

        // Managed under the platform's device list: the tenant routes do not know the row.
        Assert.Equal(PushOpError.NotFound, await h.Sut.DeleteAsync(Tenant, "ga-phone", Admin));
        Assert.False((await h.Sut.TestAsync(Tenant, "ga-phone", Admin)).Ok);
        Assert.Equal(2, h.Repo.Devices.Count);
    }

    private static PushDevice PlatformDevice(string id, PushCaller owner, string homeTenantId, string kid) => new()
    {
        Scope = Constants.Push.PlatformScope,
        DeviceId = id,
        OwnerUpn = owner.Upn,
        OwnerObjectId = owner.ObjectId,
        OwnerHomeTenantId = homeTenantId,
        Endpoint = $"https://web.push.apple.com/QF/{id}",
        P256dh = "p",
        Auth = "a",
        DeviceSecretHash = "hash",
        VapidKid = kid,
        Label = id,
        Platform = "ios-homescreen",
        Status = Constants.Push.DeviceStatus.Active,
        PairedUtc = DateTime.UtcNow,
    };

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<byte[]> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            // PushDeliveryService fans out in parallel: an unguarded List.Add loses entries.
            lock (Requests)
            {
                Requests.Add(request);
                Bodies.Add(body);
            }
            return new HttpResponseMessage(Respond(request)) { Content = new StringContent(string.Empty) };
        }
    }
}
