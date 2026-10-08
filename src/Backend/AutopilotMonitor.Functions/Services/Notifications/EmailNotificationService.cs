using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Notifications;

/// <summary>
/// E-mail as a notification-channel provider (plan email-notification-channel): renders the alert
/// with <see cref="EmailAlertRenderer"/> and sends one message per recipient through
/// <see cref="EmailService.SendMessageAsync"/>, the same provider path as the welcome and farewell
/// mails. Outcomes land in the <c>EmailDelivery</c> custom event and the log — never in an ops
/// event, because an ops event could be bound to this very channel.
/// </summary>
public class EmailNotificationService : IEmailChannelSender
{
    public const string DeliveryEventName = "EmailDelivery";
    public const int MaxRecipients = 5;
    public const string Tag = "alert";

    private readonly EmailService _email;
    private readonly ILogger<EmailNotificationService> _logger;
    private readonly TelemetryClient? _telemetry;

    public EmailNotificationService(EmailService email, ILogger<EmailNotificationService> logger, TelemetryClient? telemetry = null)
    {
        _email = email;
        _logger = logger;
        _telemetry = telemetry;
    }

    /// <summary>
    /// The recipients a channel's destination names: trimmed, de-duplicated (case-insensitive),
    /// in order, blanks dropped. Validation of the addresses themselves happens when the channel
    /// is saved (<c>TenantConfigValidation.ValidateEmailDestination</c>).
    /// </summary>
    public static IReadOnlyList<string> ParseRecipients(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in destination.Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var address = part.Trim();
            if (address.Length > 0 && seen.Add(address))
                result.Add(address);
        }
        return result;
    }

    public async Task SendOpsAlertAsync(string recipients, NotificationAlert alert)
    {
        try
        {
            await SendAlertWithResultAsync(recipients, alert).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E-mail alert send failed for {EventType}", alert?.EventType);
        }
    }

    public async Task<WebhookTestResult> SendAlertWithResultAsync(string recipients, NotificationAlert alert)
    {
        var addresses = ParseRecipients(recipients);
        if (addresses.Count == 0)
            return new WebhookTestResult { Success = false, Message = "This e-mail channel has no recipient." };
        if (!_email.IsConfigured)
        {
            Track("not_configured", addresses.Count, 0);
            return new WebhookTestResult { Success = false, Message = $"The e-mail provider is not configured ({EmailService.ApiKeyConfigKey})." };
        }

        var subject = EmailAlertRenderer.Subject(alert);
        var html = EmailAlertRenderer.Html(alert);
        var accepted = 0;
        foreach (var address in addresses.Take(MaxRecipients))
        {
            if (await _email.SendMessageAsync(address, subject, html, Tag).ConfigureAwait(false))
                accepted++;
        }

        var attempted = Math.Min(addresses.Count, MaxRecipients);
        Track(accepted == attempted ? "delivered" : accepted == 0 ? "failed" : "partial", attempted, accepted);
        if (accepted < attempted)
            _logger.LogWarning("E-mail alert {EventType}: provider accepted {Accepted} of {Attempted} recipient(s)", alert.EventType, accepted, attempted);

        return new WebhookTestResult
        {
            Success = accepted > 0,
            Message = accepted == attempted
                ? $"Sent to {accepted} recipient(s)."
                : $"The e-mail provider accepted {accepted} of {attempted} recipient(s) — see the backend log for the rejection.",
        };
    }

    private void Track(string outcome, int attempted, int accepted)
    {
        try
        {
            _telemetry?.TrackEvent(DeliveryEventName, new Dictionary<string, string>
            {
                ["outcome"] = outcome,
                ["attempted"] = attempted.ToString(),
                ["accepted"] = accepted.ToString(),
            });
        }
        catch
        {
            // Telemetry never fails a send.
        }
    }
}
