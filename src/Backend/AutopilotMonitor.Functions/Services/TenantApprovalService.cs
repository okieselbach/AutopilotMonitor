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
    private readonly IEmailService _emailService;
    private readonly OpsEventService _opsEventService;

    public TenantApprovalService(
        ILogger<TenantApprovalService> logger,
        PreviewWhitelistService previewWhitelistService,
        TenantConfigurationService tenantConfigurationService,
        TenantAdminsService tenantAdminsService,
        IEmailService emailService,
        OpsEventService opsEventService)
    {
        _logger = logger;
        _previewWhitelistService = previewWhitelistService;
        _tenantConfigurationService = tenantConfigurationService;
        _tenantAdminsService = tenantAdminsService;
        _emailService = emailService;
        _opsEventService = opsEventService;
    }

    /// <summary>
    /// Activates a tenant: adds it to the whitelist, then runs the best-effort side
    /// effects. The whitelist add is the activation itself and fails loud; everything
    /// after it is non-fatal. Returns false when the tenant was ALREADY activated
    /// (concurrent duplicate or repeat approve) — the conditional whitelist insert makes
    /// this idempotent, so a lost race never re-sends the welcome mail or re-promotes.
    /// </summary>
    public virtual async Task<bool> ApproveWithSideEffectsAsync(string tenantId, string approvedBy)
    {
        if (!await _previewWhitelistService.ApproveAsync(tenantId, approvedBy))
        {
            _logger.LogInformation(
                "Tenant {TenantId} already activated — skipping activation side effects (approve by {ApprovedBy})",
                tenantId, approvedBy);
            return false;
        }

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

        }
        catch (Exception ex)
        {
            // Non-fatal: activation already succeeded, admin promotion is best-effort
            _logger.LogWarning(ex,
                "Failed to auto-promote tenant requester as TenantAdmin for tenant {TenantId} — activation still succeeded",
                tenantId);
        }

        // Outside the promote try/catch: a promote failure must not swallow the mail.
        await TrySendWelcomeEmailAsync(tenantId);

        return true;
    }

    /// <summary>
    /// Sends the activation welcome email exactly once per activation, no matter which
    /// path gets there first. Both paths call this AFTER their own write (approval writes
    /// the whitelist row first; the notification-email save writes the address row first):
    /// write-then-read on both sides guarantees at least one caller sees both halves, and
    /// the conditional sent-marker insert guarantees at most one sends.
    /// <para>
    /// Address resolution is activation-page address first, tenant contact address second.
    /// The signup admin UPN is deliberately NOT a fallback: those accounts frequently have
    /// no mailbox, and the resulting bounces cost sender reputation for every other tenant.
    /// </para>
    /// <para>
    /// Best-effort and never throws; false when nothing was sent. Every outcome except the
    /// duplicate-suppression one is recorded as an ops event — this path fails soft, so
    /// without that record a customer onboarded without a welcome mail leaves no trace
    /// (worker application logs below Warning never reach Application Insights).
    /// </para>
    /// </summary>
    public virtual async Task<bool> TrySendWelcomeEmailAsync(string tenantId)
    {
        string? domainName = null;
        string? recipient = null;

        try
        {
            var tenantConfig = await _tenantConfigurationService.GetConfigurationAsync(tenantId);
            domainName = tenantConfig.DomainName;

            var addressSource = "activation page";
            recipient = await _previewWhitelistService.GetNotificationEmailAsync(tenantId);
            if (string.IsNullOrWhiteSpace(recipient))
            {
                recipient = tenantConfig.ContactEmail;
                addressSource = "tenant contact address";
            }

            if (string.IsNullOrWhiteSpace(recipient))
            {
                _logger.LogInformation(
                    "No address for tenant {TenantId} yet — welcome mail deferred to the notification-email save path",
                    tenantId);
                await _opsEventService.RecordWelcomeEmailSkippedAsync(tenantId, domainName,
                    "no address entered on the activation page and no tenant contact address");
                return false;
            }

            // Marker strictly AFTER the address check: consuming it with no address to
            // send to would permanently suppress the mail.
            if (!await _previewWhitelistService.TryMarkWelcomeEmailSentAsync(tenantId))
            {
                _logger.LogInformation(
                    "Welcome email already sent for tenant {TenantId} — skipping duplicate", tenantId);
                return false;
            }

            // Awaited rather than fire-and-forget: a discarded Task can be torn down when the
            // function invocation ends, and its result is the only thing that distinguishes
            // "sent" from "provider refused" for the ops event below.
            var sent = await _emailService.SendPreviewApprovedEmailAsync(recipient, tenantConfig.DomainName);
            if (sent)
            {
                _logger.LogInformation(
                    "Welcome email sent to {Email} for tenant {TenantId}", recipient, tenantId);
                await _opsEventService.RecordWelcomeEmailSentAsync(tenantId, domainName, recipient, addressSource);
                return true;
            }

            // The marker means "delivered", not "attempted": leaving it behind after a refused
            // send would suppress this tenant's welcome mail permanently, so a later address
            // save (or the GA button) can try again.
            await _previewWhitelistService.ClearWelcomeEmailSentMarkerAsync(tenantId);
            await _opsEventService.RecordWelcomeEmailFailedAsync(tenantId, domainName, recipient,
                "the email provider did not accept the message");
            return false;
        }
        catch (Exception ex)
        {
            // Courtesy side effect — the GA "Send Welcome Email" button is the manual fallback.
            _logger.LogWarning(ex, "Failed to send welcome email for tenant {TenantId}", tenantId);

            try
            {
                if (string.IsNullOrWhiteSpace(recipient))
                {
                    await _opsEventService.RecordWelcomeEmailSkippedAsync(tenantId, domainName,
                        $"address lookup failed ({ex.GetType().Name})");
                }
                else
                {
                    await _opsEventService.RecordWelcomeEmailFailedAsync(tenantId, domainName, recipient,
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            catch (Exception opsEx)
            {
                // The ops event is the record of a failure, never a second source of one.
                _logger.LogWarning(opsEx,
                    "Could not record the welcome-email failure for tenant {TenantId}", tenantId);
            }

            return false;
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
