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
    /// Sends the post-offboarding "sorry to see you go" farewell email.
    /// No-op if the API key or recipient email is not configured. Best-effort: failures
    /// are logged as warnings and never propagated (the offboarding correctness contract
    /// does not depend on email delivery).
    /// </summary>
    public async Task SendAsync(string toEmail, string domainName, string tenantId, CancellationToken ct = default)
    {
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
    }
}
