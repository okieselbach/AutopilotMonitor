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
    /// Three transports: <see cref="WebhookProviderType.Telegram"/> goes to
    /// <see cref="TelegramNotificationService"/> (plain text through the platform bot, the channel's
    /// Url field carrying the chat ID); <see cref="WebhookProviderType.Push"/> goes to the
    /// <see cref="IPushChannelSender"/> (encrypted Web Push to the paired devices of the
    /// <see cref="NotificationScope"/>, the channel carrying no destination at all); everything
    /// else is a rendered webhook POST via <see cref="WebhookNotificationService"/>. Neither
    /// Telegram nor Push is a renderer — they have no caller-supplied endpoint and must never
    /// reach the SSRF-guarded webhook path.
    /// </para>
    /// </summary>
    public class NotificationChannelDispatcher
    {
        private readonly WebhookNotificationService _webhook;
        private readonly TelegramNotificationService _telegram;
        private readonly IPushChannelSender _push;

        public NotificationChannelDispatcher(
            WebhookNotificationService webhook,
            TelegramNotificationService telegram,
            IPushChannelSender push)
        {
            _webhook = webhook;
            _telegram = telegram;
            _push = push;
        }

        /// <summary>
        /// Sends a notification to every channel in <paramref name="channels"/> (callers pre-filter
        /// by <see cref="NotificationChannel.Enabled"/> and the relevant NotifyOn* toggle, or by
        /// rule-level channel ids) on behalf of <paramref name="scope"/>. Channels are dispatched
        /// sequentially and independently — a failing destination only logs a warning and never
        /// blocks the remaining channels or the caller's pipeline.
        /// </summary>
        public virtual async Task SendToChannelsAsync(
            IEnumerable<NotificationChannel> channels, NotificationAlert alert, NotificationScope scope)
        {
            foreach (var channel in channels)
            {
                if (channel == null || !channel.HasDestination())
                    continue;

                if (channel.ProviderType == (int)WebhookProviderType.Push)
                {
                    await _push.SendAsync(scope, alert);
                    continue;
                }

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
        public virtual async Task<WebhookTestResult> SendWithResultAsync(
            NotificationChannel channel, NotificationAlert alert, NotificationScope scope)
        {
            if (channel == null || !channel.HasDestination())
                return new WebhookTestResult { Success = false, Message = "This channel has no destination configured." };

            if (channel.ProviderType == (int)WebhookProviderType.Push)
                return await _push.SendWithResultAsync(scope, alert);

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
