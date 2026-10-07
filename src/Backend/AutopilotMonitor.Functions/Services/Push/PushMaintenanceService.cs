using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>
/// The push channel's retention, folded into the 2-hourly maintenance run (K27: no timer of its
/// own). Expired grants and their never-confirmed Pending devices go; Stale devices (endpoint
/// gone) go after <see cref="PushSettings.StaleDeviceRetentionDays"/>; Active devices whose
/// owner stopped signing in — or has no owner row at all — pause after
/// <see cref="PushSettings.OwnerInactivityDays"/> through the delivery service's one pause path
/// (a last "sign in to resume" push) and resume by themselves once the owner is back; Paused
/// devices are deleted <see cref="PushSettings.PausedDeviceRetentionDays"/> after the owner's
/// last sign-in (without an owner row: after the pause itself); expired session watches go.
/// Nothing here deletes a device a sign-in could still resume. Every step is fail-soft and
/// reports counts; nothing here is a hot path.
/// </summary>
public class PushMaintenanceService
{
    private readonly IPushDeviceRepository _repo;
    private readonly PushSettings _settings;
    private readonly PushDeliveryService _delivery;
    private readonly ILogger<PushMaintenanceService> _logger;

    public PushMaintenanceService(
        IPushDeviceRepository repo,
        PushSettings settings,
        PushDeliveryService delivery,
        ILogger<PushMaintenanceService> logger)
    {
        _repo = repo;
        _settings = settings;
        _delivery = delivery;
        _logger = logger;
    }

    public async Task<PushMaintenanceResult> RunAsync(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var result = new PushMaintenanceResult();

        try
        {
            foreach (var grant in await _repo.GetExpiredGrantsAsync(now).ConfigureAwait(false))
            {
                if (grant.DeviceId != null && grant.Status == Constants.Push.PairingStatus.Redeemed)
                {
                    var pending = await _repo.GetDeviceAsync(grant.TenantId, grant.DeviceId).ConfigureAwait(false);
                    if (pending != null && pending.Status == Constants.Push.DeviceStatus.Pending)
                    {
                        await _repo.DeleteDeviceAsync(pending.Scope, pending.DeviceId).ConfigureAwait(false);
                        result.PendingDevicesDeleted++;
                    }
                }
                await _repo.DeleteGrantAsync(grant.CodeHash).ConfigureAwait(false);
                result.GrantsDeleted++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push maintenance: grant sweep failed");
        }

        try
        {
            var lastSignIns = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in await _repo.GetAllDevicesAsync().ConfigureAwait(false))
            {
                switch (device.Status)
                {
                    case Constants.Push.DeviceStatus.Pending when device.PairedUtc < now.AddMinutes(-(Constants.Push.PairingGrantMinutes + 5)):
                        await _repo.DeleteDeviceAsync(device.Scope, device.DeviceId).ConfigureAwait(false);
                        result.PendingDevicesDeleted++;
                        break;

                    case Constants.Push.DeviceStatus.Stale when (device.StatusChangedUtc ?? device.PairedUtc) < now.AddDays(-_settings.StaleDeviceRetentionDays):
                        await _repo.DeleteDeviceAsync(device.Scope, device.DeviceId).ConfigureAwait(false);
                        result.StaleDevicesDeleted++;
                        break;

                    case Constants.Push.DeviceStatus.Active:
                    {
                        var lastSignIn = await LastSignInAsync(device, lastSignIns).ConfigureAwait(false);
                        if (lastSignIn == null || lastSignIn < now.AddDays(-_settings.OwnerInactivityDays))
                        {
                            await _delivery.PauseAsync(device, "owner_inactive", NotificationScope.FromKey(device.Scope), now).ConfigureAwait(false);
                            result.DevicesPaused++;
                        }
                        break;
                    }

                    case Constants.Push.DeviceStatus.Paused:
                    {
                        var lastSignIn = await LastSignInAsync(device, lastSignIns).ConfigureAwait(false);
                        // Without an owner row the pause is the anchor: a sign-in since would have written the row.
                        var anchor = lastSignIn ?? device.StatusChangedUtc ?? device.PairedUtc;
                        if (anchor < now.AddDays(-_settings.PausedDeviceRetentionDays))
                        {
                            await _repo.DeleteDeviceAsync(device.Scope, device.DeviceId).ConfigureAwait(false);
                            result.InactiveDevicesDeleted++;
                        }
                        else if (device.StatusReason == "owner_inactive" && lastSignIn >= now.AddDays(-_settings.OwnerInactivityDays))
                        {
                            await _delivery.ResumeAsync(device, now).ConfigureAwait(false);
                            result.DevicesResumed++;
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push maintenance: device sweep failed");
        }

        try
        {
            foreach (var watch in await _repo.GetExpiredWatchesAsync(now).ConfigureAwait(false))
            {
                await _repo.DeleteWatchAsync(watch.TenantId, watch.SessionId, watch.OwnerObjectId).ConfigureAwait(false);
                result.WatchesDeleted++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push maintenance: watch sweep failed");
        }

        if (result.Any)
            _logger.LogWarning("Push maintenance: {Result}", result);
        return result;
    }

    private async Task<DateTime?> LastSignInAsync(PushDevice device, Dictionary<string, DateTime?> cache)
    {
        var key = $"{device.OwnerHomeTenantId}|{device.OwnerObjectId}";
        if (cache.TryGetValue(key, out var cached))
            return cached;
        var owner = await _repo.GetOwnerAsync(device.OwnerHomeTenantId, device.OwnerObjectId).ConfigureAwait(false);
        cache[key] = owner?.LastSignInUtc;
        return owner?.LastSignInUtc;
    }
}

public sealed class PushMaintenanceResult
{
    public int GrantsDeleted { get; set; }
    public int PendingDevicesDeleted { get; set; }
    public int StaleDevicesDeleted { get; set; }
    public int InactiveDevicesDeleted { get; set; }
    public int DevicesPaused { get; set; }
    public int DevicesResumed { get; set; }
    public int WatchesDeleted { get; set; }
    public bool Any => GrantsDeleted + PendingDevicesDeleted + StaleDevicesDeleted + InactiveDevicesDeleted + DevicesPaused + DevicesResumed + WatchesDeleted > 0;
    public override string ToString()
        => $"grants={GrantsDeleted} pending={PendingDevicesDeleted} stale={StaleDevicesDeleted} inactive={InactiveDevicesDeleted} paused={DevicesPaused} resumed={DevicesResumed} watches={WatchesDeleted}";
}
