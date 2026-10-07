using System.Net;
using System.Net.Http;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The pairing flow end to end against the in-memory repository and a fake push service:
/// create → begin → redeem (Pending) → confirm (Active, confirmation push), the one-shot redeem,
/// the five-failure burn, expiry, the device token, re-subscribe re-arming a Stale row, the
/// role/channel gates, grant ownership (a grant answers only to its creator) and the
/// one-per-host-and-hour ops event for a refused subscription endpoint.
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
        public InMemoryPushDeviceRepository Repo { get; } = new();
        public VapidKeyRing Keys { get; } = new(VapidKey.Generate());
        public Mock<PushEligibility> Eligibility { get; }
        public Mock<OpsEventService> OpsEvents { get; }
        public RecordingHandler Http { get; } = new();
        public PushPairingService Sut { get; }
        public PushDeliveryService Delivery { get; }
        public (string Endpoint, string P256dh, string Auth) Subscription { get; }

        public Harness()
        {
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
                Mock.Of<IMaintenanceRepository>(), tenantConfig.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<PushPairingService>.Instance);

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
    internal sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<byte[]> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(Respond(request)) { Content = new StringContent(string.Empty) };
        }
    }
}
