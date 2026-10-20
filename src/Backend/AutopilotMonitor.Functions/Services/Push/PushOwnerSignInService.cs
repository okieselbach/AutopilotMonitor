using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>
/// What a portal sign-in means for the person's paired devices. It writes the freshness stamp with
/// the member role the Entra app-role claim grants (<see cref="PushOwner.ClaimRole"/>, recorded only
/// without a TenantAdmins row); it revokes the home-tenant devices once that claim role fell below
/// Operator and no row took its place — the claim's counterpart of the TenantAdmins role-loss hook
/// (K6); and it lifts an inactivity pause at once instead of at the next maintenance run. Never
/// throws: a push failure must not fail the sign-in.
/// </summary>
public class PushOwnerSignInService
{
    private readonly IPushDeviceRepository _repo;
    private readonly IAdminRepository _adminRepo;
    private readonly PushSettings _settings;
    private readonly PushDeliveryService _delivery;
    private readonly ILogger<PushOwnerSignInService> _logger;

    public PushOwnerSignInService(
        IPushDeviceRepository repo,
        IAdminRepository adminRepo,
        PushSettings settings,
        PushDeliveryService delivery,
        ILogger<PushOwnerSignInService> logger)
    {
        _repo = repo;
        _adminRepo = adminRepo;
        _settings = settings;
        _delivery = delivery;
        _logger = logger;
    }

    /// <param name="claimRole">The member role the token's app-role claim grants, only when the person has no TenantAdmins row; else null.</param>
    /// <param name="isGlobalAdmin">The person's platform devices resume too.</param>
    public async Task OnPortalSignInAsync(string homeTenantId, string objectId, string upn, string? claimRole, bool isGlobalAdmin, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(homeTenantId) || string.IsNullOrWhiteSpace(objectId) || string.IsNullOrWhiteSpace(upn))
            return;

        try
        {
            var previous = await _repo.GetOwnerAsync(homeTenantId, objectId).ConfigureAwait(false);
            await _repo.StampOwnerSignInAsync(homeTenantId, objectId, upn, nowUtc, claimRole).ConfigureAwait(false);

            var home = NotificationScope.Tenant(homeTenantId);
            if (PushEligibility.IsAlertingRole(previous?.ClaimRole) && !PushEligibility.IsAlertingRole(claimRole)
                && await _adminRepo.GetTenantMemberAsync(home.Key, upn.ToLowerInvariant()).ConfigureAwait(false) == null)
            {
                await _delivery.RevokeOwnerAsync(home, upn, "app_role_lost").ConfigureAwait(false);
            }

            if (previous == null || previous.LastSignInUtc < nowUtc.AddDays(-_settings.OwnerInactivityDays))
            {
                await ResumeInactivityPausesAsync(home, homeTenantId, objectId, nowUtc).ConfigureAwait(false);
                if (isGlobalAdmin)
                    await ResumeInactivityPausesAsync(NotificationScope.Platform, homeTenantId, objectId, nowUtc).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push sign-in handling failed for tenant {TenantId}", homeTenantId);
        }
    }

    private async Task ResumeInactivityPausesAsync(NotificationScope scope, string homeTenantId, string objectId, DateTime nowUtc)
    {
        foreach (var device in await _repo.GetDevicesAsync(scope.Key).ConfigureAwait(false))
        {
            if (device.Status == Constants.Push.DeviceStatus.Paused
                && device.StatusReason == "owner_inactive"
                && string.Equals(device.OwnerObjectId, objectId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(device.OwnerHomeTenantId, homeTenantId, StringComparison.OrdinalIgnoreCase))
            {
                await _delivery.ResumeAsync(device, nowUtc).ConfigureAwait(false);
            }
        }
    }
}
