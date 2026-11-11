using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The feature switch in front of every bootstrap route (Pro plan, or the per-tenant flag). A "no"
/// is confirmed against the stored row first (<see cref="TenantConfigurationService.ConfirmBeforeRefusalAsync"/>):
/// the cached copy can predate the plan change that enabled the feature, and a tenant that has just
/// become Pro creates and uses its first code at once. False for an unknown tenant.
/// </summary>
internal static class BootstrapFeatureGate
{
    public static async Task<bool> IsEnabledAsync(TenantConfigurationService configService, string tenantId)
    {
        var (config, exists) = await configService.TryGetConfigurationAsync(tenantId);
        if (!exists)
            return false;
        if (TenantEntitlementService.IsBootstrapEnabled(config, DateTime.UtcNow))
            return true;

        var stored = await configService.ConfirmBeforeRefusalAsync(tenantId);
        return stored != null && TenantEntitlementService.IsBootstrapEnabled(stored, DateTime.UtcNow);
    }
}
