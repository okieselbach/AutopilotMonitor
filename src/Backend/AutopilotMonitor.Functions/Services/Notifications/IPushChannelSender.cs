using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications;

/// <summary>
/// The Push transport behind <see cref="NotificationChannelDispatcher"/>: fans one alert out to
/// the Active paired devices of a scope. Implemented by <c>PushDeliveryService</c>; an interface
/// so the dispatcher's routing tests need no push plumbing. Never throws.
/// </summary>
public interface IPushChannelSender
{
    Task SendAsync(NotificationScope scope, NotificationAlert alert);

    /// <summary>The "send test" variant: reports the outcome instead of swallowing it.</summary>
    Task<WebhookTestResult> SendWithResultAsync(NotificationScope scope, NotificationAlert alert);
}
