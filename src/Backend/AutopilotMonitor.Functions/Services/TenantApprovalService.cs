using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Shared tenant-activation path: whitelist approval plus the follow-up side effects
/// (auto-promote of the onboarding user to TenantAdmin, welcome email). Used by both
/// the Global Admin approve endpoint (<c>PreviewWhitelistFunction</c>) and the
/// auto-approve queue worker (<c>TenantAutoApproveQueueWorker</c>) so the two
/// activation routes cannot drift.
/// </summary>
public class TenantApprovalService
{
    private readonly ILogger<TenantApprovalService> _logger;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly TenantConfigurationService _tenantConfigurationService;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly ResendEmailService _resendEmailService;

    public TenantApprovalService(
        ILogger<TenantApprovalService> logger,
        PreviewWhitelistService previewWhitelistService,
        TenantConfigurationService tenantConfigurationService,
        TenantAdminsService tenantAdminsService,
        ResendEmailService resendEmailService)
    {
        _logger = logger;
        _previewWhitelistService = previewWhitelistService;
        _tenantConfigurationService = tenantConfigurationService;
        _tenantAdminsService = tenantAdminsService;
        _resendEmailService = resendEmailService;
    }

    /// <summary>
    /// Activates a tenant: adds it to the whitelist, then runs the best-effort side
    /// effects. The whitelist add is the activation itself and fails loud; everything
    /// after it is non-fatal.
    /// </summary>
    public virtual async Task ApproveWithSideEffectsAsync(string tenantId, string approvedBy)
    {
        await _previewWhitelistService.ApproveAsync(tenantId, approvedBy);

        _logger.LogInformation("Tenant activated: {TenantId} by {ApprovedBy}", tenantId, approvedBy);

        // Auto-promote the tenant requester (first user who triggered tenant config creation)
        // as TenantAdmin if they are not already one.
        // This ensures whoever signed up doesn't need manual admin assignment after activation.
        try
        {
            var tenantConfig = await _tenantConfigurationService.GetConfigurationAsync(tenantId);
            var requesterUpn = PickRequesterUpn(tenantConfig);

            // Positive UPN-shape validation: real Azure AD UPNs always contain '@', while
            // every system-written sentinel ("System", "System (auto-re-enable)",
            // "System (Global Rate Limit Sync)", …) does not. An equality list against
            // known sentinels previously missed a third sentinel and corrupted 10 tenants'
            // TenantAdmins rows — keep this check shape-based, not enumeration-based.
            if (IsRealUserUpn(requesterUpn))
            {
                // IsRealUserUpn returned true → requesterUpn is non-null, non-empty, contains '@'
                var validUpn = requesterUpn!;
                var isAlreadyAdmin = await _tenantAdminsService.IsTenantAdminAsync(tenantId, validUpn);
                if (!isAlreadyAdmin)
                {
                    await _tenantAdminsService.AddTenantAdminAsync(tenantId, validUpn, approvedBy);
                    _logger.LogInformation(
                        "Auto-promoted tenant requester {RequesterUpn} as TenantAdmin for tenant {TenantId} on activation by {ApprovedBy}",
                        validUpn, tenantId, approvedBy);
                }
                else
                {
                    _logger.LogInformation(
                        "Tenant requester {RequesterUpn} is already a TenantAdmin for tenant {TenantId} — skipping auto-promote",
                        validUpn, tenantId);
                }
            }
            else
            {
                _logger.LogInformation(
                    "No valid tenant requester UPN found in TenantConfiguration for tenant {TenantId} (OnboardedBy: '{OnboardedBy}', UpdatedBy: '{UpdatedBy}') — skipping auto-promote",
                    tenantId, tenantConfig.OnboardedBy ?? "<null>", tenantConfig.UpdatedBy ?? "<null>");
            }

            // Fire-and-forget: send welcome email if notification email is configured
            var notificationEmail = await _previewWhitelistService.GetNotificationEmailAsync(tenantId);
            if (!string.IsNullOrWhiteSpace(notificationEmail))
            {
                _ = _resendEmailService.SendPreviewApprovedEmailAsync(
                        notificationEmail, tenantConfig.DomainName)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget welcome email failed for tenant {TenantId}", tenantId),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: activation already succeeded, admin promotion is best-effort
            _logger.LogWarning(ex,
                "Failed to auto-promote tenant requester as TenantAdmin for tenant {TenantId} — activation still succeeded",
                tenantId);
        }
    }

    /// <summary>
    /// Picks the UPN of the user to auto-promote as TenantAdmin on activation.
    /// Prefers <see cref="TenantConfiguration.OnboardedBy"/> (immutable, set once on first
    /// user login) over <see cref="TenantConfiguration.UpdatedBy"/> (mutable, can be
    /// clobbered by background jobs such as the global rate-limit sync). Fall back to
    /// <see cref="TenantConfiguration.UpdatedBy"/> for tenants onboarded before
    /// <see cref="TenantConfiguration.OnboardedBy"/> existed — callers still guard the
    /// result with <see cref="IsRealUserUpn"/> so a sentinel value cannot leak into
    /// the TenantAdmins table.
    /// </summary>
    internal static string? PickRequesterUpn(TenantConfiguration config)
    {
        if (config == null) return null;
        return !string.IsNullOrWhiteSpace(config.OnboardedBy)
            ? config.OnboardedBy
            : config.UpdatedBy;
    }

    /// <summary>
    /// True when the value looks like a real Azure AD user principal name
    /// (contains '@' and does not start with the "System" sentinel prefix
    /// used by background jobs that touch <see cref="TenantConfiguration.UpdatedBy"/>).
    /// </summary>
    internal static bool IsRealUserUpn(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn)) return false;
        if (upn.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return false;
        return upn.Contains('@');
    }
}
