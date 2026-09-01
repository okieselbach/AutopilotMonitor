using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// The channel-level send API: takes <see cref="NotificationChannel"/> objects and routes each
    /// one to the transport its provider needs. Every notification that targets configured channels
    /// (enrollment, SLA, analyze rules, ops alerts) goes through here, so a new provider is added
    /// in exactly one place.
    /// <para>
    /// Two transports today: <see cref="WebhookProviderType.Telegram"/> goes to
    /// <see cref="TelegramNotificationService"/> (plain text through the platform bot, the channel's
    /// Url field carrying the chat ID); everything else is a rendered webhook POST via
    /// <see cref="WebhookNotificationService"/>. Telegram is deliberately NOT a renderer — it has no
    /// endpoint URL of its own and must never reach the SSRF-guarded webhook path.
    /// </para>
    /// </summary>
    public class NotificationChannelDispatcher
    {
        private readonly WebhookNotificationService _webhook;
        private readonly TelegramNotificationService _telegram;

        public NotificationChannelDispatcher(
            WebhookNotificationService webhook,
            TelegramNotificationService telegram)
        {
            _webhook = webhook;
            _telegram = telegram;
        }

        /// <summary>
        /// Sends a notification to every channel in <paramref name="channels"/> (callers pre-filter
        /// by <see cref="NotificationChannel.Enabled"/> and the relevant NotifyOn* toggle, or by
        /// rule-level channel ids). Channels are dispatched sequentially and independently — a
        /// failing destination only logs a warning and never blocks the remaining channels or the
        /// caller's pipeline.
        /// </summary>
        public virtual async Task SendToChannelsAsync(IEnumerable<NotificationChannel> channels, NotificationAlert alert)
        {
            foreach (var channel in channels)
            {
                if (channel == null || string.IsNullOrEmpty(channel.Url))
                    continue;

                if (channel.ProviderType == (int)WebhookProviderType.Telegram)
                {
                    await _telegram.SendOpsAlertAsync(channel.Url!, alert);
                    continue;
                }

                await _webhook.SendNotificationAsync(
                    channel.Url!,
                    (WebhookProviderType)channel.ProviderType,
                    alert,
                    channel.GetCustomHeaders(),
                    channel.GetSigningSecret());
            }
        }

        /// <summary>
        /// Sends to a single channel and REPORTS the outcome — the "send test notification"
        /// endpoints. Not fire-and-forget; never throws.
        /// </summary>
        public virtual async Task<WebhookTestResult> SendWithResultAsync(NotificationChannel channel, NotificationAlert alert)
        {
            if (channel == null || string.IsNullOrWhiteSpace(channel.Url))
                return new WebhookTestResult { Success = false, Message = "This channel has no destination configured." };

            if (channel.ProviderType == (int)WebhookProviderType.Telegram)
                return await _telegram.SendAlertWithResultAsync(channel.Url!, alert);

            return await _webhook.SendNotificationWithResultAsync(
                channel.Url!,
                (WebhookProviderType)channel.ProviderType,
                alert,
                channel.GetCustomHeaders(),
                channel.GetSigningSecret());
        }
    }
}
