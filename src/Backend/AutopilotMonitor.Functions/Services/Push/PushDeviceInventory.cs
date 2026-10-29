using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>
/// The paired devices as the 2-hourly maintenance run finds them, reported as the <c>PushDeviceInventory</c>
/// event: how many tenants and devices use push, on which platforms, and how many devices opened the receiver
/// app or received a push lately. Counts only — no device, owner or tenant id leaves the backend this way.
/// Everything except <see cref="PlatformDevices"/> counts the tenant scope; the platform scope holds the
/// operators' own devices.
/// </summary>
public sealed class PushDeviceInventory
{
    public const string EventName = "PushDeviceInventory";

    /// <summary>Tenants with at least one Active device.</summary>
    public int Tenants { get; private init; }
    public int Devices { get; private init; }
    public int Active { get; private init; }
    public int Pending { get; private init; }
    public int Paused { get; private init; }
    public int Stale { get; private init; }
    /// <summary>Devices whose receiver app asked for its status (every open does) in the last 7 / 30 days.</summary>
    public int Opened7d { get; private init; }
    public int Opened30d { get; private init; }
    public int Delivered7d { get; private init; }
    /// <summary>Active devices per platform, under the short names the measurements carry.</summary>
    public IReadOnlyDictionary<string, int> ActiveByPlatform { get; private init; } = new Dictionary<string, int>();
    public int PlatformDevices { get; private init; }

    public static PushDeviceInventory Count(IEnumerable<PushDevice> devices, DateTime now)
    {
        var tenantDevices = new List<PushDevice>();
        var platformDevices = 0;
        foreach (var device in devices)
        {
            if (device.Scope == Constants.Push.PlatformScope)
                platformDevices++;
            else
                tenantDevices.Add(device);
        }

        var active = tenantDevices.Where(d => d.Status == Constants.Push.DeviceStatus.Active).ToList();
        var byPlatform = PlatformNames.Values.Distinct().ToDictionary(name => name, _ => 0);
        foreach (var device in active)
            byPlatform[PlatformName(device.Platform)]++;

        return new PushDeviceInventory
        {
            Tenants = active.Select(d => d.Scope).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Devices = tenantDevices.Count,
            Active = active.Count,
            Pending = tenantDevices.Count(d => d.Status == Constants.Push.DeviceStatus.Pending),
            Paused = tenantDevices.Count(d => d.Status == Constants.Push.DeviceStatus.Paused),
            Stale = tenantDevices.Count(d => d.Status == Constants.Push.DeviceStatus.Stale),
            Opened7d = tenantDevices.Count(d => d.LastOpenedUtc >= now.AddDays(-7)),
            Opened30d = tenantDevices.Count(d => d.LastOpenedUtc >= now.AddDays(-30)),
            Delivered7d = tenantDevices.Count(d => d.LastDeliveredUtc >= now.AddDays(-7)),
            ActiveByPlatform = byPlatform,
            PlatformDevices = platformDevices,
        };
    }

    public Dictionary<string, double> ToMeasurements()
    {
        var measurements = new Dictionary<string, double>
        {
            ["tenants"] = Tenants,
            ["devices"] = Devices,
            ["active"] = Active,
            ["pending"] = Pending,
            ["paused"] = Paused,
            ["stale"] = Stale,
            ["opened7d"] = Opened7d,
            ["opened30d"] = Opened30d,
            ["delivered7d"] = Delivered7d,
            ["platformDevices"] = PlatformDevices,
        };
        foreach (var (name, count) in ActiveByPlatform)
            measurements[name] = count;
        return measurements;
    }

    /// <summary>The closed platform list (<see cref="Constants.Push.Platforms"/>) under KQL-friendly names.</summary>
    private static readonly IReadOnlyDictionary<string, string> PlatformNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ios-homescreen"] = "ios",
        ["android-chrome"] = "android",
        ["windows-chromium"] = "windows",
        ["macos-safari"] = "macos",
        ["firefox"] = "firefox",
        ["other"] = "other",
    };

    private static string PlatformName(string platform)
        => PlatformNames.TryGetValue(platform ?? string.Empty, out var name) ? name : "other";
}
