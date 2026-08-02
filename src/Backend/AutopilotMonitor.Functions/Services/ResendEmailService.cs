using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Resend;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Sends transactional emails via Resend.com for tenant-activation notifications.
/// Best-effort: failures are logged as warnings and never propagated.
/// Temporary — remove after GA.
/// </summary>
public class ResendEmailService : IOffboardFarewellEmailSender
{
    /// <summary>
    /// Hard gate for the post-offboarding farewell email. While <c>false</c> the send
    /// path short-circuits before any Resend call. Flip to <c>true</c> only after the
    /// final template + feedback-form URL in <see cref="EmailTemplates"/> are signed off.
    /// This is the SOLE meaningful arm-switch: production already has <c>RESEND_API_KEY</c>
    /// set for the preview-approval flow, so the API-key check below would otherwise let
    /// a placeholder template ship.
    /// </summary>
    private const bool OffboardFarewellEmailArmed = false;

    private readonly string _apiKey;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(
        IConfiguration configuration,
        ILogger<ResendEmailService> logger)
    {
        _logger = logger;
        _apiKey = configuration["RESEND_API_KEY"] ?? string.Empty;
    }

    /// <summary>
    /// Sends the tenant-activation welcome email.
    /// No-op if the API key or recipient email is not configured.
    /// </summary>
    public virtual async Task SendPreviewApprovedEmailAsync(string toEmail, string domainName)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug("RESEND_API_KEY not configured — skipping preview approval email");
            return;
        }

        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogDebug("No notification email set — skipping preview approval email for {Domain}", domainName);
            return;
        }

        try
        {
            var resend = ResendClient.Create(_apiKey);

            var message = new EmailMessage
            {
                From = "Autopilot Monitor <noreply@autopilotmonitor.com>",
                To = toEmail,
                Subject = EmailTemplates.PreviewApprovedSubject,
                HtmlBody = EmailTemplates.GetPreviewApprovedHtml(domainName)
            };

            await resend.EmailSendAsync(message);

            _logger.LogInformation(
                "Preview approval email sent to {ToEmail} for domain {Domain}",
                toEmail, domainName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to send preview approval email to {ToEmail} for domain {Domain}",
                toEmail, domainName);
        }
    }

    /// <summary>
    /// Sends the post-offboarding "sorry to see you go" farewell email. Disarmed by
    /// default via <see cref="OffboardFarewellEmailArmed"/>; the method short-circuits
    /// before any Resend call. Even when armed, the standard <c>RESEND_API_KEY</c> /
    /// recipient-empty no-op fall-throughs still apply. Best-effort: failures are
    /// logged as warnings and never propagated (the offboarding correctness contract
    /// does not depend on email delivery).
    /// </summary>
    public async Task SendAsync(string toEmail, string domainName, string tenantId, CancellationToken ct = default)
    {
        if (!OffboardFarewellEmailArmed)
        {
            _logger.LogDebug(
                "OffboardFarewellEmail disarmed — skipping send for tenant {TenantId} ({Domain}). Flip ResendEmailService.OffboardFarewellEmailArmed to true to arm.",
                tenantId, domainName);
            return;
        }

        // The const-false guard above intentionally makes the rest of this method
        // unreachable until arming. Suppress CS0162 so the disarmed build stays clean.
#pragma warning disable CS0162 // Unreachable code detected
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug(
                "RESEND_API_KEY not configured — skipping offboard farewell email for tenant {TenantId}",
                tenantId);
            return;
        }

        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogDebug(
                "No notification email captured — skipping offboard farewell email for tenant {TenantId} ({Domain})",
                tenantId, domainName);
            return;
        }

        try
        {
            var resend = ResendClient.Create(_apiKey);

            var message = new EmailMessage
            {
                From = "Autopilot Monitor <noreply@autopilotmonitor.com>",
                To = toEmail,
                Subject = EmailTemplates.OffboardingFarewellSubject,
                HtmlBody = EmailTemplates.GetOffboardingFarewellHtml(domainName)
            };

            await resend.EmailSendAsync(message, ct);

            _logger.LogInformation(
                "Offboard farewell email sent to {ToEmail} for tenant {TenantId} ({Domain})",
                toEmail, tenantId, domainName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to send offboard farewell email to {ToEmail} for tenant {TenantId} ({Domain})",
                toEmail, tenantId, domainName);
        }
#pragma warning restore CS0162
    }
}
