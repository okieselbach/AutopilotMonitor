using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>
/// The ONE answer to "may this person pair and receive push in this scope", asked at pairing
/// time and again at every send (Korrektur K2). Tenant scope: the TenantAdmins row first — enabled
/// with role Admin or Operator (Viewer may not pair, F10), a disabled row is a no. Without a row,
/// the member role the Entra app-role claim granted at the person's last portal sign-in
/// (<see cref="PushOwner.ClaimRole"/> — a send has no token to re-read), only in the person's home
/// tenant and only while that tenant keeps app roles enabled; the sign-in that loses the claim
/// role revokes the devices. Platform scope: an enabled GlobalAdmins row with role GlobalAdmin
/// (Global Reader may not, F6) whose identity binding matches the owner's home tenant and object
/// id — the same corroboration <c>GlobalAdminService</c> performs for a live caller. Reads the
/// repositories, not the per-process role caches, so a revocation takes effect at the next send.
/// </summary>
public class PushEligibility
{
    private readonly IAdminRepository _adminRepo;
    private readonly IPushDeviceRepository _pushRepo;
    private readonly TenantConfigurationService _tenantConfig;
    private readonly AdminConfigurationService _adminConfig;
    private readonly PushSettings _settings;

    public PushEligibility(
        IAdminRepository adminRepo,
        IPushDeviceRepository pushRepo,
        TenantConfigurationService tenantConfig,
        AdminConfigurationService adminConfig,
        PushSettings settings)
    {
        _adminRepo = adminRepo;
        _pushRepo = pushRepo;
        _tenantConfig = tenantConfig;
        _adminConfig = adminConfig;
        _settings = settings;
    }

    /// <summary>
    /// The channel is the one switch (K4): nothing is paired, watched or sent in a scope that has
    /// no enabled Push channel. Platform scope reads the ops channels, tenant scope the tenant's.
    /// </summary>
    public virtual async Task<bool> HasPushChannelAsync(NotificationScope scope)
    {
        if (!scope.IsSet)
            return false;

        if (scope.IsPlatform)
        {
            var admin = await _adminConfig.GetConfigurationAsync();
            return admin.GetOpsNotificationChannels().Any(IsEnabledPush);
        }

        var (config, exists) = await _tenantConfig.TryGetConfigurationAsync(scope.Key);
        return exists && config.GetNotificationChannels().Any(IsEnabledPush);
    }

    private static bool IsEnabledPush(NotificationChannel c)
        => c.Enabled && c.ProviderType == (int)WebhookProviderType.Push;

    /// <summary>Role predicate shared by the pairing routes and the send-time recipient filter.</summary>
    public virtual async Task<bool> IsEligibleAsync(NotificationScope scope, string upn, string objectId, string homeTenantId)
    {
        if (!scope.IsSet || string.IsNullOrWhiteSpace(upn) || Constants.PrincipalKeys.IsApplication(upn))
            return false;

        upn = upn.ToLowerInvariant();

        if (scope.IsPlatform)
        {
            if (await _adminRepo.GetGlobalRoleAsync(upn) != Constants.GlobalRoles.GlobalAdmin)
                return false;

            // A GlobalAdmins row without a binding is inert (D-279 posture); a device whose owner
            // identity no longer matches the binding is somebody else's device by now.
            var binding = await _adminRepo.GetIdentityBindingAsync(upn);
            return binding != null
                && string.Equals(binding.TenantId, homeTenantId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(binding.ObjectId)
                    || string.Equals(binding.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
        }

        var member = await _adminRepo.GetTenantMemberAsync(scope.Key, upn);
        if (member != null)
            return member.IsEnabled && IsAlertingRole(string.IsNullOrEmpty(member.Role) ? Constants.TenantRoles.Admin : member.Role);

        if (string.IsNullOrEmpty(objectId) || !string.Equals(scope.Key, homeTenantId, StringComparison.OrdinalIgnoreCase))
            return false;
        var owner = await _pushRepo.GetOwnerAsync(homeTenantId, objectId);
        if (owner == null || !IsAlertingRole(owner.ClaimRole))
            return false;
        var (config, exists) = await _tenantConfig.TryGetConfigurationAsync(scope.Key);
        return exists && config.EntraAppRolesEnabled;
    }

    /// <summary>Admin and Operator receive push; Viewer never does (F10).</summary>
    public static bool IsAlertingRole(string? role)
        => role == Constants.TenantRoles.Admin || role == Constants.TenantRoles.Operator;

    /// <summary>
    /// The member role a live caller's Entra app-role claim grants in their home tenant — what a
    /// pairing records as <see cref="PushOwner.ClaimRole"/>, the same rule a portal sign-in follows:
    /// only without a TenantAdmins row (a row, enabled or disabled, decides alone) and only while the
    /// tenant has app roles enabled. Null otherwise.
    /// </summary>
    public virtual async Task<string?> ClaimRoleAsync(string homeTenantId, string upn, IReadOnlyList<string>? appRoles)
    {
        if (appRoles == null || appRoles.Count == 0 || string.IsNullOrWhiteSpace(homeTenantId)
            || string.IsNullOrWhiteSpace(upn) || Constants.PrincipalKeys.IsApplication(upn))
            return null;

        var home = NotificationScope.Tenant(homeTenantId).Key;
        if (await _adminRepo.GetTenantMemberAsync(home, upn.ToLowerInvariant()) != null)
            return null;

        var (config, exists) = await _tenantConfig.TryGetConfigurationAsync(home);
        return EntraAppRoleResolver.Resolve(TableMemberState.NotPresent, null, appRoles, exists && config.EntraAppRolesEnabled)?.Role;
    }

    /// <summary>
    /// Freshness (K3): the owner signed in to the portal within the inactivity window. The stamp
    /// is written at pairing and on every portal sign-in, so an absent row means "never" — not
    /// fresh. A device of an owner who stopped signing in pauses; roles are app-internal and a
    /// deactivated Entra account would otherwise keep receiving alerts. The window also bounds
    /// the age of a recorded claim role.
    /// </summary>
    public virtual async Task<bool> IsOwnerFreshAsync(string homeTenantId, string objectId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(homeTenantId) || string.IsNullOrEmpty(objectId))
            return false;

        var owner = await _pushRepo.GetOwnerAsync(homeTenantId, objectId);
        return owner != null && owner.LastSignInUtc >= nowUtc.AddDays(-_settings.OwnerInactivityDays);
    }
}
