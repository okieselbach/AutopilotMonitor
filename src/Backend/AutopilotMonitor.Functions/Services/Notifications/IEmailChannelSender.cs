using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications;

/// <summary>
/// The dispatcher's e-mail transport: a channel of <see cref="WebhookProviderType.Email"/> carries
/// its recipients in <see cref="NotificationChannel.Url"/> (one to five addresses, ';' or ','
/// separated) and sends through the platform's own sender identity — which is why the provider is
/// Global-Admin-only. Never throws into the dispatcher.
/// </summary>
public interface IEmailChannelSender
{
    /// <summary>Sends the alert and reports what the provider accepted — real deliveries and the "send test" endpoints alike.</summary>
    Task<NotificationSendResult> SendAlertAsync(string recipients, NotificationAlert alert);
}
