using System.Net;
using System.Net.Http;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// Fan-out semantics of the Push transport: only Active devices of the scope, owner role and
/// freshness re-checked at send time, 404/410 ⇒ Stale (never deleted), the flood window with its
/// single "muted" push, an unset scope refused, a kid outside the ring ⇒ Stale, and the
/// role-loss revoke (wipe push, then the row is gone). Below that the maintenance sweep's pause /
/// retention / resume rules (an owner without a sign-in row pauses, a Paused row is retained from
/// its last sign-in or the pause itself, only an owner_inactive pause ever resumes), the
/// PushPairing lifecycle telemetry and the per-owner system send behind the session watch.
/// </summary>
public class PushDeliveryServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly NotificationScope Tenant = NotificationScope.Tenant(TenantId);

    [Fact]
    public async Task Sends_to_active_devices_only_and_records_delivery()
    {
        var h = new Harness();
        var active = h.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        h.AddDevice("d2", Constants.Push.DeviceStatus.Pending);
        h.AddDevice("d3", Constants.Push.DeviceStatus.Paused);
        // Another org's Global Admin: a tenant alert never reaches a platform device unless this is the
        // owner's home tenant (D-334, pinned below).
        h.AddDevice("d4", Constants.Push.DeviceStatus.Active, scope: Constants.Push.PlatformScope)
            .OwnerHomeTenantId = "22222222-2222-2222-2222-222222222222";

        var stats = await h.Sut.SendCoreAsync(Tenant, Alert());

        Assert.Equal(1, stats.Targets);
        Assert.Equal(1, stats.Delivered);
        var request = Assert.Single(h.Http.Requests);
        Assert.Equal(active.Endpoint, request.RequestUri!.ToString());
        Assert.True(request.Headers.Contains("TTL"));
        Assert.StartsWith("vapid t=", request.Headers.Authorization!.ToString());
        Assert.NotNull(h.Repo.Devices[(TenantId, "d1")].LastDeliveredUtc);
        Assert.Equal(1, h.Repo.Devices[(TenantId, "d1")].WindowCount);
    }

    [Fact]
    public async Task Payload_on_the_wire_is_encrypted_not_plaintext()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        await h.Sut.SendCoreAsync(Tenant, Alert());

        var body = h.Http.Bodies.Single();
        Assert.True(body.Length > 86 + 16);
        Assert.DoesNotContain("DESKTOP-4711", System.Text.Encoding.Latin1.GetString(body));
    }

    [Fact]
    public async Task Owner_without_role_is_skipped_and_stale_owner_is_paused()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active, upn: "gone@contoso.invalid");
        h.AddDevice("d2", Constants.Push.DeviceStatus.Active, upn: "inactive@contoso.invalid");
        h.Eligibility.Setup(e => e.IsEligibleAsync(Tenant, "gone@contoso.invalid", It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        h.Eligibility.Setup(e => e.IsOwnerFreshAsync(TenantId, "oid-inactive@contoso.invalid", It.IsAny<DateTime>())).ReturnsAsync(false);

        var stats = await h.Sut.SendCoreAsync(Tenant, Alert());

        Assert.Equal(0, stats.Targets);
        Assert.Equal(Constants.Push.DeviceStatus.Active, h.Repo.Devices[(TenantId, "d1")].Status);
        Assert.Equal(Constants.Push.DeviceStatus.Paused, h.Repo.Devices[(TenantId, "d2")].Status);
        Assert.Equal("owner_inactive", h.Repo.Devices[(TenantId, "d2")].StatusReason);
        Assert.Single(h.Http.Requests);   // the "paused" push to d2
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Gone_endpoints_become_stale_rows_never_deleted(HttpStatusCode status)
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        h.Http.Respond = _ => status;

        await h.Sut.SendCoreAsync(Tenant, Alert());

        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Stale, row.Status);
        Assert.Equal("gone", row.StatusReason);
        Assert.Equal((int)status, row.LastStatusCode);
    }

    [Fact]
    public async Task Flood_window_mutes_after_the_cap_with_one_summary_push()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        for (var i = 0; i < PushDeliveryService.FloodWindowCap + 3; i++)
            await h.Sut.SendCoreAsync(Tenant, Alert());

        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(PushDeliveryService.FloodWindowCap, row.WindowCount);
        Assert.Equal(3, row.SuppressedCount);
        // cap alerts + exactly one "muted" push
        Assert.Equal(PushDeliveryService.FloodWindowCap + 1, h.Http.Requests.Count);
    }

    [Fact]
    public async Task Unset_scope_sends_nothing()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        var stats = await h.Sut.SendCoreAsync(default, Alert());

        Assert.Equal(0, stats.Targets);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Device_with_unknown_kid_goes_stale_with_a_config_error()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active, kid: "ZZZZZZZZ");

        await h.Sut.SendCoreAsync(Tenant, Alert());

        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Stale, row.Status);
        Assert.Equal("vapid_key_mismatch", row.StatusReason);
        Assert.Empty(h.Http.Requests);
        h.OpsEvents.Verify(o => o.RecordPushDeliveryFailedAsync(It.IsAny<string>(), null, "ZZZZZZZZ", "vapid_key_mismatch"), Times.Once);
    }

    [Fact]
    public async Task Config_error_ops_event_is_raised_once_per_hour()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        h.Http.Respond = _ => HttpStatusCode.Forbidden;

        await h.Sut.SendCoreAsync(Tenant, Alert());
        await h.Sut.SendCoreAsync(Tenant, Alert());

        h.OpsEvents.Verify(o => o.RecordPushDeliveryFailedAsync("apple", 403, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.Equal(2, h.Repo.Devices[(TenantId, "d1")].ConsecutiveFailures);
    }

    [Fact]
    public async Task Revoke_sends_the_wipe_push_then_deletes_the_owners_devices()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active, upn: "leaver@contoso.invalid");
        h.AddDevice("d2", Constants.Push.DeviceStatus.Active, upn: "stays@contoso.invalid");

        await h.Sut.RevokeOwnerAsync(Tenant, "leaver@contoso.invalid", "member_removed");

        Assert.Single(h.Http.Requests);
        Assert.False(h.Repo.Devices.ContainsKey((TenantId, "d1")));
        Assert.True(h.Repo.Devices.ContainsKey((TenantId, "d2")));
    }

    [Fact]
    public async Task Test_result_reports_delivery_counts()
    {
        var h = new Harness();
        var result = await h.Sut.SendWithResultAsync(Tenant, Alert());
        Assert.False(result.Success);
        Assert.Contains("No active paired device", result.Message);

        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        result = await h.Sut.SendWithResultAsync(Tenant, Alert());
        Assert.True(result.Success);
        Assert.Contains("1 of 1", result.Message);
    }

    [Fact]
    public async Task Maintenance_sweeps_grants_pending_stale_and_inactive_devices()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        h.AddDevice("pending-old", Constants.Push.DeviceStatus.Pending, pairedUtc: now.AddMinutes(-30));
        h.AddDevice("stale-old", Constants.Push.DeviceStatus.Stale, statusChangedUtc: now.AddDays(-20));
        h.AddDevice("stale-new", Constants.Push.DeviceStatus.Stale, statusChangedUtc: now.AddDays(-2));
        h.AddDevice("active-inactive-owner", Constants.Push.DeviceStatus.Active, upn: "quiet@contoso.invalid");
        h.AddDevice("paused-forgotten", Constants.Push.DeviceStatus.Paused, upn: "forgotten@contoso.invalid", statusChangedUtc: now.AddDays(-100));
        h.Repo.Owners[(TenantId, "oid-quiet@contoso.invalid")] = new PushOwner { HomeTenantId = TenantId, ObjectId = "oid-quiet@contoso.invalid", LastSignInUtc = now.AddDays(-45) };
        h.Repo.Owners[(TenantId, "oid-forgotten@contoso.invalid")] = new PushOwner { HomeTenantId = TenantId, ObjectId = "oid-forgotten@contoso.invalid", LastSignInUtc = now.AddDays(-120) };
        h.Repo.Grants["expired"] = (new PushPairingGrant { CodeHash = "expired", TenantId = TenantId, Status = Constants.Push.PairingStatus.Pending, ExpiresUtc = now.AddMinutes(-1) }, 1);
        h.Repo.Grants["fresh"] = (new PushPairingGrant { CodeHash = "fresh", TenantId = TenantId, Status = Constants.Push.PairingStatus.Pending, ExpiresUtc = now.AddMinutes(5) }, 1);

        var maintenance = new PushMaintenanceService(h.Repo, h.Settings, h.Sut, NullLogger<PushMaintenanceService>.Instance);
        var result = await maintenance.RunAsync(now);

        Assert.Equal(1, result.GrantsDeleted);
        Assert.True(h.Repo.Grants.ContainsKey("fresh"));
        Assert.False(h.Repo.Devices.ContainsKey((TenantId, "pending-old")));
        Assert.False(h.Repo.Devices.ContainsKey((TenantId, "stale-old")));
        Assert.True(h.Repo.Devices.ContainsKey((TenantId, "stale-new")));
        Assert.Equal(Constants.Push.DeviceStatus.Paused, h.Repo.Devices[(TenantId, "active-inactive-owner")].Status);
        Assert.False(h.Repo.Devices.ContainsKey((TenantId, "paused-forgotten")));
    }

    // ── Maintenance: pause, retention, resume ────────────────────────────────

    [Fact]
    public async Task Maintenance_pauses_an_active_device_whose_owner_has_no_sign_in_row()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        var device = h.AddDevice("d1", Constants.Push.DeviceStatus.Active, upn: "unknown@contoso.invalid");

        var result = await h.Maintenance.RunAsync(now);

        Assert.Equal(1, result.DevicesPaused);
        Assert.Equal(0, result.InactiveDevicesDeleted);
        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Paused, row.Status);
        Assert.Equal("owner_inactive", row.StatusReason);
        Assert.Equal<DateTime?>(now, row.StatusChangedUtc);
        var request = Assert.Single(h.Http.Requests);   // the "paused" push, nothing else
        Assert.Equal(device.Endpoint, request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Paused_device_without_an_owner_row_is_deleted_once_the_pause_is_older_than_the_retention()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        h.AddDevice("d1", Constants.Push.DeviceStatus.Paused, upn: "unknown@contoso.invalid", statusChangedUtc: now.AddDays(-100), statusReason: "owner_inactive");

        var result = await h.Maintenance.RunAsync(now);

        Assert.Equal(1, result.InactiveDevicesDeleted);
        Assert.False(h.Repo.Devices.ContainsKey((TenantId, "d1")));
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Paused_device_without_an_owner_row_is_kept_inside_the_retention_window()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        h.AddDevice("d1", Constants.Push.DeviceStatus.Paused, upn: "unknown@contoso.invalid", statusChangedUtc: now.AddDays(-10), statusReason: "owner_inactive");

        var result = await h.Maintenance.RunAsync(now);

        Assert.Equal(0, result.InactiveDevicesDeleted);
        Assert.Equal(0, result.DevicesResumed);   // no sign-in row ⇒ nothing to resume from
        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Paused, row.Status);
        Assert.Equal("owner_inactive", row.StatusReason);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Paused_device_without_a_status_change_anchors_its_retention_on_the_pairing()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        h.AddDevice("d1", Constants.Push.DeviceStatus.Paused, upn: "unknown@contoso.invalid", pairedUtc: now.AddDays(-100));

        var result = await h.Maintenance.RunAsync(now);

        Assert.Equal(1, result.InactiveDevicesDeleted);
        Assert.False(h.Repo.Devices.ContainsKey((TenantId, "d1")));
    }

    [Fact]
    public async Task Maintenance_resumes_an_owner_inactive_pause_silently_once_the_owner_signed_in_again()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        h.AddDevice("d1", Constants.Push.DeviceStatus.Paused, upn: "back@contoso.invalid", statusChangedUtc: now.AddDays(-40), statusReason: "owner_inactive");
        h.SignIn("back@contoso.invalid", now.AddDays(-1));

        var result = await h.Maintenance.RunAsync(now);

        Assert.Equal(1, result.DevicesResumed);
        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Active, row.Status);
        Assert.Null(row.StatusReason);
        Assert.Equal<DateTime?>(now, row.StatusChangedUtc);
        Assert.Empty(h.Http.Requests);   // resuming sends nothing
    }

    [Fact]
    public async Task Maintenance_never_re_arms_a_delivery_failures_pause()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        h.AddDevice("d1", Constants.Push.DeviceStatus.Paused, upn: "fresh@contoso.invalid", statusChangedUtc: now.AddDays(-5), statusReason: "delivery_failures");
        h.SignIn("fresh@contoso.invalid", now.AddDays(-1));

        var result = await h.Maintenance.RunAsync(now);

        Assert.Equal(0, result.DevicesResumed);
        Assert.Equal(0, result.InactiveDevicesDeleted);
        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Paused, row.Status);
        Assert.Equal("delivery_failures", row.StatusReason);
        Assert.Empty(h.Http.Requests);
    }

    // ── Pause / resume transitions ───────────────────────────────────────────

    [Fact]
    public async Task PauseAsync_still_sends_the_push_but_leaves_a_stale_row_stale()
    {
        var h = new Harness();
        var now = DateTime.UtcNow;
        var stale = h.AddDevice("d1", Constants.Push.DeviceStatus.Stale, statusChangedUtc: now.AddDays(-1), statusReason: "gone");

        await h.Sut.PauseAsync(stale, "owner_inactive", Tenant, now);

        Assert.Single(h.Http.Requests);   // the pause push attempt
        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Stale, row.Status);
        Assert.Equal("gone", row.StatusReason);
        Assert.Equal<DateTime?>(now.AddDays(-1), row.StatusChangedUtc);
    }

    [Fact]
    public async Task ResumeAsync_on_an_active_row_changes_nothing()
    {
        var channel = new CapturingChannel();
        var h = new Harness(Telemetry(channel));
        var now = DateTime.UtcNow;
        var active = h.AddDevice("d1", Constants.Push.DeviceStatus.Active, statusChangedUtc: now.AddDays(-3));

        await h.Sut.ResumeAsync(active, now);

        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Active, row.Status);
        Assert.Null(row.StatusReason);
        Assert.Equal<DateTime?>(now.AddDays(-3), row.StatusChangedUtc);
        Assert.Empty(h.Http.Requests);
        Assert.Empty(PairingEvents(channel));   // nothing landed ⇒ nothing tracked
    }

    // ── Telemetry: the PushPairing lifecycle event ───────────────────────────

    [Fact]
    public async Task PauseAsync_tracks_a_paused_pairing_event()
    {
        var channel = new CapturingChannel();
        var h = new Harness(Telemetry(channel));
        var device = h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        await h.Sut.PauseAsync(device, "owner_inactive", Tenant, DateTime.UtcNow);

        var e = Assert.Single(PairingEvents(channel));
        Assert.Equal("paused", e.Properties["action"]);
        Assert.Equal("owner_inactive", e.Properties["reason"]);
        Assert.Equal("tenant", e.Properties["scope"]);
    }

    [Fact]
    public async Task ResumeAsync_tracks_a_resumed_pairing_event_naming_the_lifted_reason()
    {
        var channel = new CapturingChannel();
        var h = new Harness(Telemetry(channel));
        h.AddDevice("d1", Constants.Push.DeviceStatus.Paused, statusReason: "owner_inactive");
        var device = (await h.Repo.GetDeviceAsync(TenantId, "d1"))!;   // the row as maintenance reads it

        await h.Sut.ResumeAsync(device, DateTime.UtcNow);

        var e = Assert.Single(PairingEvents(channel));
        Assert.Equal("resumed", e.Properties["action"]);
        Assert.Equal("owner_inactive", e.Properties["reason"]);
        Assert.Equal("tenant", e.Properties["scope"]);
    }

    [Fact]
    public async Task RevokeDeviceAsync_tracks_a_revoked_pairing_event_with_the_given_reason()
    {
        var channel = new CapturingChannel();
        var h = new Harness(Telemetry(channel));
        var device = h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        await h.Sut.RevokeDeviceAsync(device, "member_removed");

        var e = Assert.Single(PairingEvents(channel));
        Assert.Equal("revoked", e.Properties["action"]);
        Assert.Equal("member_removed", e.Properties["reason"]);
        Assert.Equal("tenant", e.Properties["scope"]);
        Assert.False(h.Repo.Devices.ContainsKey((TenantId, "d1")));
    }

    // ── SendSystemToOwnerAsync: the session-watch fan-out ────────────────────

    [Fact]
    public async Task System_push_to_an_owner_reaches_only_that_owners_active_devices_in_the_scope()
    {
        var h = new Harness();
        var d1 = h.AddDevice("d1", Constants.Push.DeviceStatus.Active, upn: "watcher@contoso.invalid");
        var d2 = h.AddDevice("d2", Constants.Push.DeviceStatus.Active, upn: "watcher@contoso.invalid");
        h.AddDevice("d3", Constants.Push.DeviceStatus.Active, upn: "other@contoso.invalid");
        h.AddDevice("d4", Constants.Push.DeviceStatus.Paused, upn: "watcher@contoso.invalid", statusReason: "owner_inactive");
        h.AddDevice("d5", Constants.Push.DeviceStatus.Active, upn: "watcher@contoso.invalid", scope: Constants.Push.PlatformScope);

        var delivered = await h.Sut.SendSystemToOwnerAsync(Tenant, "oid-watcher@contoso.invalid", SessionFinished());

        Assert.Equal(2, delivered);
        Assert.Equal(2, h.Http.Requests.Count);
        Assert.Contains(h.Http.Requests, r => r.RequestUri!.ToString() == d1.Endpoint);
        Assert.Contains(h.Http.Requests, r => r.RequestUri!.ToString() == d2.Endpoint);
    }

    [Fact]
    public async Task System_push_to_an_owner_refuses_an_unset_scope()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        var delivered = await h.Sut.SendSystemToOwnerAsync(default, "oid-admin@contoso.invalid", SessionFinished());

        Assert.Equal(0, delivered);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task System_push_to_an_owner_who_lost_the_role_sends_nothing()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active, upn: "gone@contoso.invalid");
        h.Eligibility.Setup(e => e.IsEligibleAsync(Tenant, "gone@contoso.invalid", It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        var delivered = await h.Sut.SendSystemToOwnerAsync(Tenant, "oid-gone@contoso.invalid", SessionFinished());

        Assert.Equal(0, delivered);
        Assert.Empty(h.Http.Requests);
        Assert.Equal(Constants.Push.DeviceStatus.Active, h.Repo.Devices[(TenantId, "d1")].Status);
    }

    [Fact]
    public async Task System_push_to_a_stale_owner_pauses_the_device_instead_of_delivering()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active, upn: "inactive@contoso.invalid");
        h.Eligibility.Setup(e => e.IsOwnerFreshAsync(TenantId, "oid-inactive@contoso.invalid", It.IsAny<DateTime>())).ReturnsAsync(false);

        var delivered = await h.Sut.SendSystemToOwnerAsync(Tenant, "oid-inactive@contoso.invalid", SessionFinished());

        Assert.Equal(0, delivered);
        Assert.Single(h.Http.Requests);   // the "paused" push, never the session message
        var row = h.Repo.Devices[(TenantId, "d1")];
        Assert.Equal(Constants.Push.DeviceStatus.Paused, row.Status);
        Assert.Equal("owner_inactive", row.StatusReason);
    }

    [Fact]
    public async Task System_push_to_an_owner_carries_the_requested_ttl()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        await h.Sut.SendSystemToOwnerAsync(Tenant, "oid-admin@contoso.invalid", SessionFinished(), TimeSpan.FromMinutes(7));

        var request = Assert.Single(h.Http.Requests);
        Assert.Equal("420", Assert.Single(request.Headers.GetValues("TTL")));
    }

    [Fact]
    public async Task System_push_to_an_owner_defaults_to_the_thirty_minute_ttl()
    {
        var h = new Harness();
        h.AddDevice("d1", Constants.Push.DeviceStatus.Active);

        await h.Sut.SendSystemToOwnerAsync(Tenant, "oid-admin@contoso.invalid", SessionFinished());

        var request = Assert.Single(h.Http.Requests);
        Assert.Equal("1800", Assert.Single(request.Headers.GetValues("TTL")));
    }

    private static PushPayload SessionFinished()
        => PushAlertProjector.SystemMessage("session_watch", "Session finished", "DESKTOP-4711 finished enrollment.", "success", Tenant, DateTime.UtcNow);

    private static TelemetryClient Telemetry(CapturingChannel channel)
        => new(new TelemetryConfiguration { TelemetryChannel = channel, ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000" });

    /// <summary>The PushPairing lifecycle events only — every send also tracks a PushDelivery event.</summary>
    private static List<EventTelemetry> PairingEvents(CapturingChannel channel)
        => channel.Items.OfType<EventTelemetry>().Where(e => e.Name == "PushPairing").ToList();

    /// <summary>Captures what the service tracks (the startup-telemetry test keeps its own copy private).</summary>
    private sealed class CapturingChannel : ITelemetryChannel
    {
        public List<ITelemetry> Items { get; } = new();
        public bool? DeveloperMode { get; set; }
        public string EndpointAddress { get; set; } = "";
        public void Send(ITelemetry item) => Items.Add(item);
        public void Flush() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Tenant_alert_also_reaches_platform_devices_whose_owners_home_tenant_it_is()
    {
        var h = new Harness();
        var own = h.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        var ga = h.AddDevice("ga", Constants.Push.DeviceStatus.Active, upn: "ga@contoso.invalid", scope: Constants.Push.PlatformScope);
        h.AddDevice("ga-paused", Constants.Push.DeviceStatus.Paused, upn: "ga@contoso.invalid", scope: Constants.Push.PlatformScope);
        // Another Global Admin whose home tenant is a different one: never reached by this tenant.
        h.AddDevice("other-ga", Constants.Push.DeviceStatus.Active, upn: "other@contoso.invalid", scope: Constants.Push.PlatformScope)
            .OwnerHomeTenantId = "22222222-2222-2222-2222-222222222222";

        var stats = await h.Sut.SendCoreAsync(Tenant, Alert());

        Assert.Equal(2, stats.Targets);
        Assert.Equal(2, stats.Delivered);
        Assert.Equal(
            new[] { own.Endpoint, ga.Endpoint }.OrderBy(x => x),
            h.Http.Requests.Select(r => r.RequestUri!.ToString()).OrderBy(x => x));
        // The tenant row passes the tenant rule, the platform row the platform rule.
        h.Eligibility.Verify(e => e.IsEligibleAsync(Tenant, "admin@contoso.invalid", It.IsAny<string>(), TenantId), Times.Once);
        h.Eligibility.Verify(e => e.IsEligibleAsync(NotificationScope.Platform, "ga@contoso.invalid", It.IsAny<string>(), TenantId), Times.Once);
        h.Eligibility.Verify(e => e.IsEligibleAsync(Tenant, "ga@contoso.invalid", It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Former_global_admins_platform_device_gets_nothing_from_its_home_tenant_and_a_platform_alert_never_pulls_tenant_rows()
    {
        var h = new Harness();
        var tenantRow = h.AddDevice("d1", Constants.Push.DeviceStatus.Active);
        var ga = h.AddDevice("ga", Constants.Push.DeviceStatus.Active, upn: "ga@contoso.invalid", scope: Constants.Push.PlatformScope);
        h.Eligibility.Setup(e => e.IsEligibleAsync(NotificationScope.Platform, "ga@contoso.invalid", It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        var tenantStats = await h.Sut.SendCoreAsync(Tenant, Alert());
        Assert.Equal(1, tenantStats.Targets);
        Assert.Equal(tenantRow.Endpoint, Assert.Single(h.Http.Requests).RequestUri!.ToString());

        h.Eligibility.Setup(e => e.IsEligibleAsync(NotificationScope.Platform, "ga@contoso.invalid", It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var platformStats = await h.Sut.SendCoreAsync(NotificationScope.Platform, Alert());
        Assert.Equal(1, platformStats.Targets);
        Assert.Equal(2, h.Http.Requests.Count);
        Assert.Equal(ga.Endpoint, h.Http.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Same_endpoint_in_both_scopes_is_sent_to_once()
    {
        var h = new Harness();
        var tenantRow = h.AddDevice("d1", Constants.Push.DeviceStatus.Active, upn: "ga@contoso.invalid");
        h.AddDevice("ga", Constants.Push.DeviceStatus.Active, upn: "ga@contoso.invalid", scope: Constants.Push.PlatformScope)
            .Endpoint = tenantRow.Endpoint;

        var stats = await h.Sut.SendCoreAsync(Tenant, Alert());

        Assert.Equal(1, stats.Targets);
        Assert.Single(h.Http.Requests);
    }

    private static NotificationAlert Alert() => new()
    {
        EventType = "enrollment_failed",
        Title = "Enrollment Failed",
        Summary = "Enrollment Failed: DESKTOP-4711",
        Severity = NotificationSeverity.Error,
        Facts = { new() { Name = "Device", Value = "DESKTOP-4711" } },
    };

    internal sealed class Harness
    {
        public InMemoryPushDeviceRepository Repo { get; } = new();
        public VapidKeyRing Keys { get; } = new(VapidKey.Generate());
        public PushSettings Settings { get; }
        public Mock<PushEligibility> Eligibility { get; }
        public Mock<OpsEventService> OpsEvents { get; }
        public PushPairingServiceTests.RecordingHandler Http { get; } = new();
        public PushDeliveryService Sut { get; }
        public PushMaintenanceService Maintenance { get; }
        private readonly (string P256dh, string Auth) _keys;

        /// <param name="telemetry">Optional: a client over a capturing channel to observe what the service tracks.</param>
        public Harness(TelemetryClient? telemetry = null)
        {
            Settings = PushSettings.ForKeys(Keys);
            Eligibility = new Mock<PushEligibility>(null!, null!, null!, null!, null!) { CallBase = false };
            Eligibility.Setup(e => e.IsEligibleAsync(It.IsAny<NotificationScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            Eligibility.Setup(e => e.IsOwnerFreshAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).ReturnsAsync(true);
            OpsEvents = new Mock<OpsEventService>(null!, null!, null!) { CallBase = false };
            OpsEvents.Setup(o => o.RecordPushDeliveryFailedAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            Sut = new PushDeliveryService(Repo, Eligibility.Object, Settings, new WebPushSender(new HttpClient(Http)),
                new Lazy<OpsEventService>(() => OpsEvents.Object), new MemoryCache(new MemoryCacheOptions()),
                NullLogger<PushDeliveryService>.Instance, telemetry);
            Maintenance = new PushMaintenanceService(Repo, Settings, Sut, NullLogger<PushMaintenanceService>.Instance);

            using var ua = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var p = ua.ExportParameters(false);
            var point = new byte[65];
            point[0] = 0x04;
            p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X!.Length));
            p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y!.Length));
            _keys = (System.Buffers.Text.Base64Url.EncodeToString(point),
                System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
        }

        /// <summary>The owner's PushOwners stamp, keyed the way AddDevice derives the object id from the UPN.</summary>
        public void SignIn(string upn, DateTime lastSignInUtc)
            => Repo.Owners[(TenantId, "oid-" + upn)] = new PushOwner { HomeTenantId = TenantId, ObjectId = "oid-" + upn, Upn = upn, LastSignInUtc = lastSignInUtc };

        public PushDevice AddDevice(string id, string status, string upn = "admin@contoso.invalid", string scope = TenantId,
            string? kid = null, DateTime? pairedUtc = null, DateTime? statusChangedUtc = null, string? statusReason = null)
        {
            var device = new PushDevice
            {
                Scope = scope,
                DeviceId = id,
                OwnerUpn = upn,
                OwnerObjectId = "oid-" + upn,
                OwnerHomeTenantId = TenantId,
                Endpoint = $"https://web.push.apple.com/QF/{id}",
                P256dh = _keys.P256dh,
                Auth = _keys.Auth,
                DeviceSecretHash = "hash",
                VapidKid = kid ?? Keys.Active.Kid,
                Label = id,
                Platform = "ios-homescreen",
                Status = status,
                StatusReason = statusReason,
                PairedUtc = pairedUtc ?? DateTime.UtcNow,
                StatusChangedUtc = statusChangedUtc,
            };
            Repo.Devices[(scope, id)] = device;
            return device;
        }
    }
}
