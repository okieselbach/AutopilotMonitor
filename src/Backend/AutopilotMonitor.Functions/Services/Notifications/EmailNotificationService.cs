using AutopilotMonitor.Functions.Services;
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

    /// <summary>The platform's automated-mail headers (RFC 3834), shared with the template mails.</summary>
    public static IReadOnlyDictionary<string, string> Headers => EmailService.AutomatedHeaders;

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
        var text = EmailAlertRenderer.Text(alert);
        var outcomes = new List<EmailSendOutcome>();
        foreach (var address in addresses.Take(MaxRecipients))
            outcomes.Add(await _email.SendMessageAsync(address, subject, html, Tag, text, Headers).ConfigureAwait(false));

        var attempted = outcomes.Count;
        var accepted = outcomes.Count(o => o.Accepted);
        // The provider's status word and message ids: "queued" (deferred by the provider) is not
        // "sent", and the id is what its activity log is searched by when a mail never arrives.
        var statuses = string.Join(",", outcomes.Where(o => o.Status != null).Select(o => o.Status!).Distinct());
        var ids = string.Join(",", outcomes.Where(o => o.ProviderId != null).Select(o => o.ProviderId!));
        Track(accepted == attempted ? "delivered" : accepted == 0 ? "failed" : "partial", attempted, accepted, statuses, ids);
        if (accepted < attempted)
            _logger.LogWarning("E-mail alert {EventType}: provider accepted {Accepted} of {Attempted} recipient(s)", alert.EventType, accepted, attempted);

        var detail = statuses.Length > 0 ? $" Provider status: {statuses}{(ids.Length > 0 ? $", id {ids}" : string.Empty)}." : string.Empty;
        return new WebhookTestResult
        {
            Success = accepted > 0,
            Message = (accepted == attempted
                ? $"Sent to {accepted} recipient(s)."
                : $"The e-mail provider accepted {accepted} of {attempted} recipient(s) — see the backend log for the rejection.") + detail,
        };
    }

    private void Track(string outcome, int attempted, int accepted, string statuses = "", string ids = "")
    {
        try
        {
            _telemetry?.TrackEvent(DeliveryEventName, new Dictionary<string, string>
            {
                ["outcome"] = outcome,
                ["attempted"] = attempted.ToString(),
                ["accepted"] = accepted.ToString(),
                ["status"] = statuses,
                ["providerIds"] = ids,
            });
        }
        catch
        {
            // Telemetry never fails a send.
        }
    }
}
