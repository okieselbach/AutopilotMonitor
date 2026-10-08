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
    /// <summary>Fire-and-forget semantics for the dispatcher: logs, never throws.</summary>
    Task SendOpsAlertAsync(string recipients, NotificationAlert alert);

    /// <summary>The "send test" path: reports what the provider accepted.</summary>
    Task<WebhookTestResult> SendAlertWithResultAsync(string recipients, NotificationAlert alert);
}
