using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>The maintenance run's device count: tenant scope only (the platform scope apart), statuses, platforms, open and delivery windows.</summary>
public class PushDeviceInventoryTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Counts_tenants_statuses_platforms_and_the_open_and_delivery_windows()
    {
        var inventory = PushDeviceInventory.Count(new[]
        {
            Device(TenantA, Constants.Push.DeviceStatus.Active, "ios-homescreen", opened: Now.AddDays(-1), delivered: Now.AddDays(-6)),
            Device(TenantA, Constants.Push.DeviceStatus.Active, "android-chrome", opened: Now.AddDays(-8), delivered: Now.AddDays(-8)),
            Device(TenantB, Constants.Push.DeviceStatus.Active, "something-new"),
            Device(TenantB, Constants.Push.DeviceStatus.Paused, "ios-homescreen", opened: Now.AddDays(-31)),
            Device(TenantB, Constants.Push.DeviceStatus.Stale, "windows-chromium"),
            Device(TenantB, Constants.Push.DeviceStatus.Pending, "firefox"),
            Device(Constants.Push.PlatformScope, Constants.Push.DeviceStatus.Active, "ios-homescreen", opened: Now),
        }, Now);

        Assert.Equal(2, inventory.Tenants);
        Assert.Equal(6, inventory.Devices);
        Assert.Equal(3, inventory.Active);
        Assert.Equal(1, inventory.Paused);
        Assert.Equal(1, inventory.Stale);
        Assert.Equal(1, inventory.Pending);
        Assert.Equal(1, inventory.Opened7d);
        Assert.Equal(2, inventory.Opened30d);
        Assert.Equal(1, inventory.Delivered7d);
        Assert.Equal(1, inventory.PlatformDevices);

        var m = inventory.ToMeasurements();
        Assert.Equal(1, m["ios"]);
        Assert.Equal(1, m["android"]);
        Assert.Equal(1, m["other"]);      // an unknown platform string lands in "other"
        Assert.Equal(0, m["windows"]);    // only Active devices count per platform
        Assert.Equal(0, m["macos"]);
        Assert.Equal(0, m["firefox"]);
    }

    [Fact]
    public void A_tenant_with_only_paused_devices_is_not_a_push_tenant_and_no_devices_report_zeros()
    {
        var paused = PushDeviceInventory.Count(new[] { Device(TenantA, Constants.Push.DeviceStatus.Paused, "ios-homescreen") }, Now);
        Assert.Equal(0, paused.Tenants);
        Assert.Equal(1, paused.Devices);

        var none = PushDeviceInventory.Count(Array.Empty<PushDevice>(), Now).ToMeasurements();
        Assert.All(none.Values, v => Assert.Equal(0, v));
        Assert.Equal(16, none.Count);
    }

    private static PushDevice Device(string scope, string status, string platform, DateTime? opened = null, DateTime? delivered = null) => new()
    {
        Scope = scope,
        DeviceId = Guid.NewGuid().ToString("N"),
        Status = status,
        Platform = platform,
        PairedUtc = Now.AddDays(-40),
        LastOpenedUtc = opened,
        LastDeliveredUtc = delivered,
    };
}
