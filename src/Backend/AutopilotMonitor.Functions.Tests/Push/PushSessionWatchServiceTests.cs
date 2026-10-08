using System.Net.Http;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// Session watches ("notify my devices when this session ends"): the watch row lives in the
/// SESSION's tenant, the devices it fires on are the watcher's OWN scopes (home tenant, plus the
/// platform partition for a Global Admin), gated by an enabled Push channel and the role check at
/// watch time and again at notify time. 48-hour lifetime, one-hour push TTL, consumed at the
/// terminal status, fail-soft towards the ingest path.
/// </summary>
public class PushSessionWatchServiceTests
{
    private const string HomeTenantId = "11111111-1111-1111-1111-111111111111";
    private const string CustomerTenantId = "22222222-2222-2222-2222-222222222222";
    private const string SessionId = "33333333-3333-3333-3333-333333333333";
    private static readonly NotificationScope Home = NotificationScope.Tenant(HomeTenantId);

    private static readonly PushCaller Admin = new("admin@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000001", HomeTenantId, IsScopeAdmin: true);
    private static readonly PushCaller Operator = new("ops@contoso.invalid", "aaaaaaaa-0000-0000-0000-000000000002", HomeTenantId, IsScopeAdmin: false);
    private static readonly PushCaller GlobalAdmin = new("ga@contoso.invalid", "bbbbbbbb-0000-0000-0000-000000000001", HomeTenantId, IsScopeAdmin: true, IsGlobalAdmin: true);
    private static readonly PushCaller OtherGlobalAdmin = new("ga2@contoso.invalid", "bbbbbbbb-0000-0000-0000-000000000002", HomeTenantId, IsScopeAdmin: true, IsGlobalAdmin: true);
    private static readonly PushCaller CustomerAdmin = new("admin@fabrikam.invalid", "cccccccc-0000-0000-0000-000000000001", CustomerTenantId, IsScopeAdmin: true);

    // ── WatchAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Watch_without_a_push_channel_in_any_own_scope_is_refused()
    {
        var h = new Harness();
        h.AddDevice("d1", Admin, HomeTenantId);
        h.Eligibility.Setup(e => e.HasPushChannelAsync(It.IsAny<NotificationScope>())).ReturnsAsync(false);

        var result = await h.Sut.WatchAsync(HomeTenantId, SessionId, Admin);

        Assert.Equal(PushOpError.ChannelRequired, result.Error);
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Watch_with_a_channel_but_an_ineligible_caller_is_refused()
    {
        var h = new Harness();
        h.AddDevice("d1", Admin, HomeTenantId);
        h.Eligibility.Setup(e => e.IsEligibleAsync(Home, Admin.Upn, Admin.ObjectId, Admin.HomeTenantId)).ReturnsAsync(false);

        var result = await h.Sut.WatchAsync(HomeTenantId, SessionId, Admin);

        Assert.Equal(PushOpError.NotEligible, result.Error);
        Assert.Empty(h.Repo.Watches);
    }

    [Theory]
    [InlineData(Constants.Push.DeviceStatus.Pending)]
    [InlineData(Constants.Push.DeviceStatus.Paused)]
    [InlineData(Constants.Push.DeviceStatus.Stale)]
    public async Task Watch_without_an_active_device_of_the_caller_is_a_conflict(string status)
    {
        var h = new Harness();
        h.AddDevice("d1", Admin, HomeTenantId, status);

        var result = await h.Sut.WatchAsync(HomeTenantId, SessionId, Admin);

        Assert.Equal(PushOpError.Conflict, result.Error);
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Another_owners_active_device_in_the_scope_does_not_count_for_the_caller()
    {
        var h = new Harness();
        h.AddDevice("d1", Operator, HomeTenantId);

        var result = await h.Sut.WatchAsync(HomeTenantId, SessionId, Admin);

        Assert.Equal(PushOpError.Conflict, result.Error);
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Tenant_admin_watch_is_stored_in_the_session_tenant_with_the_home_scope_and_a_48_hour_expiry()
    {
        var h = new Harness();
        h.AddDevice("d1", Admin, HomeTenantId);

        var before = DateTime.UtcNow;
        var result = await h.Sut.WatchAsync(HomeTenantId, SessionId, Admin);
        var after = DateTime.UtcNow;

        Assert.True(result.Ok);
        Assert.True(result.Value!.Watching);
        var row = h.Repo.Watches[(HomeTenantId, SessionId, Admin.ObjectId)];
        Assert.Equal(HomeTenantId, row.TenantId);
        Assert.Equal(SessionId, row.SessionId);
        Assert.Equal(Admin.Upn, row.OwnerUpn);
        Assert.Equal(HomeTenantId, row.OwnerScopes);
        Assert.InRange(row.ExpiresUtc, before.AddHours(48), after.AddHours(48));
        Assert.Equal(row.ExpiresUtc, result.Value.ExpiresUtc!.Value);
    }

    [Fact]
    public async Task Global_admin_with_only_a_platform_device_watches_a_customer_session_from_the_platform_scope()
    {
        var h = new Harness();
        h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);

        var result = await h.Sut.WatchAsync(CustomerTenantId, SessionId, GlobalAdmin);

        Assert.True(result.Ok);
        // The row lives in the SESSION's tenant partition, not in the platform or the home partition.
        var row = Assert.Single(h.Repo.Watches.Values);
        Assert.True(h.Repo.Watches.ContainsKey((CustomerTenantId, SessionId, GlobalAdmin.ObjectId)));
        Assert.Equal(CustomerTenantId, row.TenantId);
        Assert.Equal(Constants.Push.PlatformScope, row.OwnerScopes);
    }

    [Fact]
    public async Task Global_admin_with_platform_and_home_devices_records_both_scopes_platform_first()
    {
        var h = new Harness();
        h.AddDevice("h1", GlobalAdmin, HomeTenantId);
        h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);

        var result = await h.Sut.WatchAsync(CustomerTenantId, SessionId, GlobalAdmin);

        Assert.True(result.Ok);
        Assert.Equal($"{Constants.Push.PlatformScope};{HomeTenantId}", h.Repo.Watches[(CustomerTenantId, SessionId, GlobalAdmin.ObjectId)].OwnerScopes);
    }

    [Fact]
    public async Task A_scope_without_a_push_channel_is_left_out_of_the_watch_scopes()
    {
        var h = new Harness();
        h.AddDevice("h1", GlobalAdmin, HomeTenantId);
        h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);
        h.Eligibility.Setup(e => e.HasPushChannelAsync(Home)).ReturnsAsync(false);

        var result = await h.Sut.WatchAsync(CustomerTenantId, SessionId, GlobalAdmin);

        Assert.True(result.Ok);
        Assert.Equal(Constants.Push.PlatformScope, h.Repo.Watches[(CustomerTenantId, SessionId, GlobalAdmin.ObjectId)].OwnerScopes);
    }

    [Fact]
    public void Own_scopes_are_the_platform_first_for_a_global_admin_and_the_home_tenant_for_everyone()
    {
        var globalAdminScopes = PushSessionWatchService.OwnScopes(GlobalAdmin);
        Assert.Equal(2, globalAdminScopes.Count);
        Assert.True(globalAdminScopes[0].IsPlatform);
        Assert.Equal(Home, globalAdminScopes[1]);

        Assert.Equal(Home, Assert.Single(PushSessionWatchService.OwnScopes(Admin)));
        Assert.Empty(PushSessionWatchService.OwnScopes(new PushCaller("app@contoso.invalid", "dddddddd-0000-0000-0000-000000000001", string.Empty, IsScopeAdmin: false)));
    }

    // ── GetAsync / UnwatchAsync ──────────────────────────────────────────────

    [Fact]
    public async Task Get_reports_an_active_watch_with_its_expiry()
    {
        var h = new Harness();
        var watch = h.AddWatch(HomeTenantId, SessionId, Admin, HomeTenantId);

        var status = await h.Sut.GetAsync(HomeTenantId, SessionId, Admin);

        Assert.True(status.Watching);
        Assert.Equal(watch.ExpiresUtc, status.ExpiresUtc!.Value);
    }

    [Fact]
    public async Task Get_reports_not_watching_without_a_row_of_the_caller()
    {
        var h = new Harness();
        h.AddWatch(HomeTenantId, SessionId, Operator, HomeTenantId);   // somebody else's watch on the same session

        var status = await h.Sut.GetAsync(HomeTenantId, SessionId, Admin);

        Assert.False(status.Watching);
        Assert.Null(status.ExpiresUtc);
    }

    [Fact]
    public async Task Get_reports_an_expired_row_as_not_watching()
    {
        var h = new Harness();
        h.AddWatch(HomeTenantId, SessionId, Admin, HomeTenantId, expiresUtc: DateTime.UtcNow.AddMinutes(-1));

        var status = await h.Sut.GetAsync(HomeTenantId, SessionId, Admin);

        Assert.False(status.Watching);
        Assert.Null(status.ExpiresUtc);
    }

    [Fact]
    public async Task Unwatch_deletes_the_callers_row_only()
    {
        var h = new Harness();
        h.AddWatch(HomeTenantId, SessionId, Admin, HomeTenantId);
        h.AddWatch(HomeTenantId, SessionId, Operator, HomeTenantId);

        var status = await h.Sut.UnwatchAsync(HomeTenantId, SessionId, Admin);

        Assert.False(status.Watching);
        Assert.False(h.Repo.Watches.ContainsKey((HomeTenantId, SessionId, Admin.ObjectId)));
        Assert.True(h.Repo.Watches.ContainsKey((HomeTenantId, SessionId, Operator.ObjectId)));
    }

    // ── NotifyAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Notify_pushes_to_each_device_of_the_watcher_in_the_watch_scopes_and_consumes_the_watch()
    {
        var h = new Harness();
        var p1 = h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);
        var p2 = h.AddDevice("p2", GlobalAdmin, Constants.Push.PlatformScope);
        h.AddDevice("p3", OtherGlobalAdmin, Constants.Push.PlatformScope);
        h.AddDevice("c1", GlobalAdmin, CustomerTenantId);        // the session's tenant is not a watch scope here
        h.AddDevice("c2", CustomerAdmin, CustomerTenantId);
        h.AddWatch(CustomerTenantId, SessionId, GlobalAdmin, Constants.Push.PlatformScope);

        await h.Sut.NotifyAsync(CustomerTenantId, SessionId, success: true, "DESKTOP-4711", "1234567890");

        Assert.Equal(Endpoints(p1, p2), SentTo(h));
        Assert.Empty(h.Repo.Watches);
        Assert.NotNull(h.Repo.Devices[(Constants.Push.PlatformScope, "p1")].LastDeliveredUtc);
        Assert.NotNull(h.Repo.Devices[(Constants.Push.PlatformScope, "p2")].LastDeliveredUtc);
        Assert.Null(h.Repo.Devices[(Constants.Push.PlatformScope, "p3")].LastDeliveredUtc);
        Assert.Null(h.Repo.Devices[(CustomerTenantId, "c1")].LastDeliveredUtc);
        Assert.Null(h.Repo.Devices[(CustomerTenantId, "c2")].LastDeliveredUtc);
    }

    [Fact]
    public async Task Notify_fires_every_watcher_of_the_session_and_consumes_each_watch()
    {
        var h = new Harness();
        var d1 = h.AddDevice("d1", Admin, HomeTenantId);
        var d2 = h.AddDevice("d2", Operator, HomeTenantId);
        h.AddWatch(HomeTenantId, SessionId, Admin, HomeTenantId);
        h.AddWatch(HomeTenantId, SessionId, Operator, HomeTenantId);

        await h.Sut.NotifyAsync(HomeTenantId, SessionId, success: false, "DESKTOP-4711", null);

        Assert.Equal(Endpoints(d1, d2), SentTo(h));
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Notify_with_empty_owner_scopes_falls_back_to_the_session_tenant()
    {
        var h = new Harness();
        var device = h.AddDevice("d1", Admin, HomeTenantId);
        h.AddWatch(HomeTenantId, SessionId, Admin, ownerScopes: string.Empty);   // legacy row

        await h.Sut.NotifyAsync(HomeTenantId, SessionId, success: true, "DESKTOP-4711", null);

        var request = Assert.Single(h.Http.Requests);
        Assert.Equal(device.Endpoint, request.RequestUri!.ToString());
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Notify_deletes_an_expired_watch_and_sends_nothing()
    {
        var h = new Harness();
        h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);
        h.AddWatch(CustomerTenantId, SessionId, GlobalAdmin, Constants.Push.PlatformScope, expiresUtc: DateTime.UtcNow.AddMinutes(-1));

        await h.Sut.NotifyAsync(CustomerTenantId, SessionId, success: false, "DESKTOP-4711", "1234567890");

        Assert.Empty(h.Http.Requests);
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Notify_without_watches_neither_writes_nor_sends()
    {
        var repo = new Mock<IPushDeviceRepository>();
        repo.Setup(r => r.GetWatchesForSessionAsync(CustomerTenantId, SessionId)).ReturnsAsync(new List<PushSessionWatch>());
        var h = new Harness(repo.Object);

        await h.Sut.NotifyAsync(CustomerTenantId, SessionId, success: true, "DESKTOP-4711", null);

        repo.Verify(r => r.GetWatchesForSessionAsync(CustomerTenantId, SessionId), Times.Once);
        repo.VerifyNoOtherCalls();
        Assert.Empty(h.Http.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Notify_push_carries_the_one_hour_ttl_and_high_urgency(bool success)
    {
        var h = new Harness();
        h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);
        h.AddWatch(CustomerTenantId, SessionId, GlobalAdmin, Constants.Push.PlatformScope);

        await h.Sut.NotifyAsync(CustomerTenantId, SessionId, success, "DESKTOP-4711", "1234567890");

        var request = Assert.Single(h.Http.Requests);
        Assert.Equal("3600", Header(request, "TTL"));   // WatchPushTtl: "Watch bis Session-Ende + 1 h"
        Assert.Equal("high", Header(request, "Urgency"));
    }

    [Fact]
    public async Task Notify_skips_a_watcher_who_lost_eligibility_and_still_consumes_the_watch()
    {
        var h = new Harness();
        h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);
        h.AddWatch(CustomerTenantId, SessionId, GlobalAdmin, Constants.Push.PlatformScope);
        h.Eligibility.Setup(e => e.IsEligibleAsync(NotificationScope.Platform, GlobalAdmin.Upn, GlobalAdmin.ObjectId, GlobalAdmin.HomeTenantId)).ReturnsAsync(false);

        await h.Sut.NotifyAsync(CustomerTenantId, SessionId, success: true, "DESKTOP-4711", "1234567890");

        Assert.Empty(h.Http.Requests);
        Assert.Equal(Constants.Push.DeviceStatus.Active, h.Repo.Devices[(Constants.Push.PlatformScope, "p1")].Status);
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Notify_pauses_the_devices_of_an_inactive_watcher_instead_of_pushing()
    {
        var h = new Harness();
        h.AddDevice("p1", GlobalAdmin, Constants.Push.PlatformScope);
        h.AddWatch(CustomerTenantId, SessionId, GlobalAdmin, Constants.Push.PlatformScope);
        h.Eligibility.Setup(e => e.IsOwnerFreshAsync(GlobalAdmin.HomeTenantId, GlobalAdmin.ObjectId, It.IsAny<DateTime>())).ReturnsAsync(false);

        await h.Sut.NotifyAsync(CustomerTenantId, SessionId, success: true, "DESKTOP-4711", "1234567890");

        var row = h.Repo.Devices[(Constants.Push.PlatformScope, "p1")];
        Assert.Equal(Constants.Push.DeviceStatus.Paused, row.Status);
        Assert.Equal("owner_inactive", row.StatusReason);
        // The pause path sends its own "sign in to resume" push (system TTL 30 min); the watch push (TTL 1 h) never goes out.
        var request = Assert.Single(h.Http.Requests);
        Assert.Equal("1800", Header(request, "TTL"));
        Assert.Empty(h.Repo.Watches);
    }

    [Fact]
    public async Task Notify_never_throws_when_the_repository_fails()
    {
        var repo = new Mock<IPushDeviceRepository>();
        repo.Setup(r => r.GetWatchesForSessionAsync(It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("storage unavailable"));
        var h = new Harness(repo.Object);

        var exception = await Record.ExceptionAsync(() => h.Sut.NotifyAsync(CustomerTenantId, SessionId, success: true, "DESKTOP-4711", "1234567890"));

        Assert.Null(exception);
        Assert.Empty(h.Http.Requests);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Header(HttpRequestMessage request, string name) => request.Headers.GetValues(name).Single();

    private static string[] Endpoints(params PushDevice[] devices) => devices.Select(d => d.Endpoint).Order().ToArray();

    private static string[] SentTo(Harness h) => h.Http.Requests.Select(r => r.RequestUri!.ToString()).Order().ToArray();

    internal sealed class Harness
    {
        public InMemoryPushDeviceRepository Repo { get; } = new();
        public VapidKeyRing Keys { get; } = new(VapidKey.Generate());
        public Mock<PushEligibility> Eligibility { get; }
        public PushPairingServiceTests.RecordingHandler Http { get; } = new();
        public PushSessionWatchService Sut { get; }
        private readonly (string P256dh, string Auth) _subscription;

        /// <param name="repo">The repository both services run on; defaults to <see cref="Repo"/>, a mock for the fail-soft cases.</param>
        public Harness(IPushDeviceRepository? repo = null)
        {
            repo ??= Repo;
            Eligibility = new Mock<PushEligibility>(null!, null!, null!, null!, null!) { CallBase = false };
            Eligibility.Setup(e => e.HasPushChannelAsync(It.IsAny<NotificationScope>())).ReturnsAsync(true);
            Eligibility.Setup(e => e.IsEligibleAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            Eligibility.Setup(e => e.IsOwnerFreshAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).ReturnsAsync(true);

            var opsEvents = new Mock<OpsEventService>(null!, null!, null!) { CallBase = false };
            opsEvents.Setup(o => o.RecordPushDeliveryFailedAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            var delivery = new PushDeliveryService(repo, Eligibility.Object, PushSettings.ForKeys(Keys), new WebPushSender(new HttpClient(Http)),
                new Lazy<OpsEventService>(() => opsEvents.Object), new MemoryCache(new MemoryCacheOptions()),
                NullLogger<PushDeliveryService>.Instance);
            Sut = new PushSessionWatchService(repo, Eligibility.Object, delivery, NullLogger<PushSessionWatchService>.Instance);

            // A browser-shaped subscription: a fresh P-256 point + 16-byte auth secret, so the per-device encryption succeeds.
            using var ua = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var p = ua.ExportParameters(false);
            var point = new byte[65];
            point[0] = 0x04;
            p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X!.Length));
            p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y!.Length));
            _subscription = (System.Buffers.Text.Base64Url.EncodeToString(point),
                System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
        }

        public PushDevice AddDevice(string id, PushCaller owner, string scope, string status = Constants.Push.DeviceStatus.Active)
        {
            var device = new PushDevice
            {
                Scope = scope,
                DeviceId = id,
                OwnerUpn = owner.Upn,
                OwnerObjectId = owner.ObjectId,
                OwnerHomeTenantId = owner.HomeTenantId,
                Kind = "webpush",
                Endpoint = $"https://web.push.apple.com/QF/{id}",
                P256dh = _subscription.P256dh,
                Auth = _subscription.Auth,
                DeviceSecretHash = "hash",
                VapidKid = Keys.Active.Kid,
                Label = id,
                Platform = "ios-homescreen",
                Status = status,
                PairedUtc = DateTime.UtcNow,
            };
            Repo.Devices[(scope, id)] = device;
            return device;
        }

        public PushSessionWatch AddWatch(string tenantId, string sessionId, PushCaller owner, string ownerScopes, DateTime? expiresUtc = null)
        {
            var now = DateTime.UtcNow;
            var watch = new PushSessionWatch
            {
                TenantId = tenantId,
                SessionId = sessionId,
                OwnerObjectId = owner.ObjectId,
                OwnerUpn = owner.Upn,
                OwnerScopes = ownerScopes,
                CreatedUtc = now,
                ExpiresUtc = expiresUtc ?? now + PushSessionWatchService.WatchLifetime,
            };
            Repo.Watches[(tenantId, sessionId, owner.ObjectId)] = watch;
            return watch;
        }
    }
}
