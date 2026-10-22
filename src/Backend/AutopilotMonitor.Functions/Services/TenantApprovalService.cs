using AutopilotMonitor.Functions.Extensions;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Shared tenant-activation path: whitelist approval plus the follow-up side effects
/// (auto-promote of the onboarding user to TenantAdmin, welcome email). Used by both
/// the Global Admin approve endpoint (<c>PreviewWhitelistFunction</c>) and the
/// auto-approve queue worker (<c>TenantAutoApproveQueueFunction</c>) so the two
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
    /// Activates a tenant. The order of the writes is the invariant: the onboarding member's Admin row
    /// is seated BEFORE the activation row, so no observable state has the tenant activated while the
    /// designated admin's row is still missing (auth/me would otherwise seat the first roleless caller
    /// of that moment). The two rows live in different tables, which rules out one atomic batch, hence
    /// "before". The conditional activation insert stays the arbiter and fails loud; false means the
    /// tenant was activated concurrently (double signup), and the seat written just before is the same
    /// row the winner seats, so the loser only skips the mail. A tenant that is already activated
    /// returns false before anything is written. Everything after the activation row is best-effort.
    /// </summary>
    public virtual async Task<bool> ApproveWithSideEffectsAsync(string tenantId, string approvedBy)
    {
        if (await _previewWhitelistService.IsApprovedFreshAsync(tenantId))
        {
            _logger.LogInformation(
                "Tenant {TenantId} already activated — nothing to do (approve by {ApprovedBy})",
                tenantId, approvedBy);
            return false;
        }

        await TrySeatOnboardingMemberAsync(tenantId, approvedBy);

        if (!await _previewWhitelistService.ApproveAsync(tenantId, approvedBy))
        {
            _logger.LogInformation(
                "Tenant {TenantId} was activated concurrently — skipping activation side effects (approve by {ApprovedBy})",
                tenantId, approvedBy);
            return false;
        }

        _logger.LogInformation("Tenant activated: {TenantId} by {ApprovedBy}", tenantId, approvedBy);

        await TrySendWelcomeEmailAsync(tenantId);

        return true;
    }

    /// <summary>
    /// Seats the onboarding member (OnboardedBy, written once by the first member login and never by an
    /// external identity; never UpdatedBy, the last writer) as Tenant Admin, so whoever signed up needs
    /// no manual assignment after activation. A row the tenant already holds for them stays untouched.
    /// Best-effort: a storage failure here must not block the activation. With the seat missing, auth/me
    /// seats the designated member on their next sign-in and nobody else.
    /// </summary>
    private async Task TrySeatOnboardingMemberAsync(string tenantId, string approvedBy)
    {
        try
        {
            // Fresh read: a cached view can be the default row an earlier request on this instance
            // created before the first sign-in seeded OnboardedBy.
            var tenantConfig = await _tenantConfigurationService.GetConfigurationFreshAsync(tenantId);
            var requesterUpn = tenantConfig?.OnboardedBy;

            // Positive UPN-shape validation: real Azure AD UPNs always contain '@', while
            // every system-written sentinel ("System", "System (auto-re-enable)",
            // "System (Global Rate Limit Sync)", …) does not. An equality list against
            // known sentinels previously missed a third sentinel and corrupted 10 tenants'
            // TenantAdmins rows — keep this check shape-based, not enumeration-based.
            if (!IsPromotableUpn(requesterUpn))
            {
                _logger.LogInformation(
                    "No promotable onboarding UPN in TenantConfiguration for tenant {TenantId} (OnboardedBy: '{OnboardedBy}') — skipping auto-promote",
                    tenantId, requesterUpn ?? "<null>");
                return;
            }

            if (await _tenantAdminsService.TryAddTenantAdminAsync(tenantId, requesterUpn!, approvedBy))
            {
                _logger.LogInformation(
                    "Auto-promoted tenant requester {RequesterUpn} as TenantAdmin for tenant {TenantId} on activation by {ApprovedBy}",
                    requesterUpn, tenantId, approvedBy);
            }
            else
            {
                _logger.LogInformation(
                    "Tenant requester {RequesterUpn} already has a member row in tenant {TenantId} — left untouched",
                    requesterUpn, tenantId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to auto-promote tenant requester as TenantAdmin for tenant {TenantId} — activation continues",
                tenantId);
        }
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
    /// True when a stored onboarding UPN may be promoted to TenantAdmin: shaped like a real user
    /// principal name (contains '@', no "System" sentinel prefix) and not an external account's
    /// resource-tenant UPN (<c>#EXT#</c>), which no member login writes.
    /// </summary>
    internal static bool IsPromotableUpn(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn)) return false;
        if (upn.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return false;
        if (ClaimsPrincipalExtensions.IsExternalUpn(upn)) return false;
        return upn.Contains('@');
    }
}
