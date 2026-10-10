using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// The channel-level send API: takes <see cref="NotificationChannel"/> objects and routes each
    /// one to the transport its provider needs. Every notification that targets configured channels
    /// (enrollment, SLA, analyze rules, ops alerts) goes through here, so a new provider is added
    /// in exactly one place — and so does the outcome: every non-Push send, real or test, is
    /// recorded in the channel's health row by <see cref="INotificationChannelHealthRecorder"/>.
    /// <para>
    /// Four transports: <see cref="WebhookProviderType.Email"/> goes to the
    /// <see cref="IEmailChannelSender"/> (the platform's own sender, recipients in the channel's
    /// Url); <see cref="WebhookProviderType.Telegram"/> goes to
    /// <see cref="TelegramNotificationService"/> (plain text through the platform bot, the channel's
    /// Url field carrying the chat ID); <see cref="WebhookProviderType.Push"/> goes to the
    /// <see cref="IPushChannelSender"/> (encrypted Web Push to the paired devices of the
    /// <see cref="NotificationScope"/>, the channel carrying no destination at all, health kept
    /// per device); everything else is a rendered webhook POST via
    /// <see cref="WebhookNotificationService"/>. Neither Telegram nor Push is a renderer — they
    /// have no caller-supplied endpoint and must never reach the SSRF-guarded webhook path.
    /// </para>
    /// </summary>
    public class NotificationChannelDispatcher
    {
        private readonly WebhookNotificationService _webhook;
        private readonly TelegramNotificationService _telegram;
        private readonly IPushChannelSender _push;
        private readonly IEmailChannelSender? _email;
        private readonly INotificationChannelHealthRecorder? _health;
        private readonly ILogger<NotificationChannelDispatcher> _logger;

        public NotificationChannelDispatcher(
            WebhookNotificationService webhook,
            TelegramNotificationService telegram,
            IPushChannelSender push)
        {
            _webhook = webhook;
            _telegram = telegram;
            _push = push;
            _logger = NullLogger<NotificationChannelDispatcher>.Instance;
        }

        /// <summary>With the e-mail transport; the shorter overload stays for the existing mocks.</summary>
        public NotificationChannelDispatcher(
            WebhookNotificationService webhook,
            TelegramNotificationService telegram,
            IPushChannelSender push,
            IEmailChannelSender email)
            : this(webhook, telegram, push)
        {
            _email = email;
        }

        /// <summary>The full dispatcher (DI picks this one): outcomes are logged and recorded.</summary>
        public NotificationChannelDispatcher(
            WebhookNotificationService webhook,
            TelegramNotificationService telegram,
            IPushChannelSender push,
            IEmailChannelSender email,
            INotificationChannelHealthRecorder health,
            ILogger<NotificationChannelDispatcher> logger)
            : this(webhook, telegram, push, email)
        {
            _health = health;
            _logger = logger;
        }

        /// <summary>
        /// Sends a notification to every channel in <paramref name="channels"/> (callers pre-filter
        /// by <see cref="NotificationChannel.Enabled"/> and the relevant NotifyOn* toggle, or by
        /// rule-level channel ids) on behalf of <paramref name="scope"/>. Channels are dispatched
        /// sequentially and independently — a failing destination is logged and recorded, and
        /// never blocks the remaining channels or the caller's pipeline.
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

                var result = await SendToTransportAsync(channel, alert);
                if (!result.Success)
                {
                    _logger.LogWarning(
                        "Notification channel {ChannelId} (provider {ProviderType}, scope {Scope}) did not accept {EventType}: {Message}",
                        channel.Id, (WebhookProviderType)channel.ProviderType, scope, alert.EventType, result.Message);
                }

                if (_health != null)
                    await _health.RecordAsync(scope, channel, result, ChannelSendKind.Delivery);
            }
        }

        /// <summary>
        /// Sends to a single channel and REPORTS the outcome — the "send test notification"
        /// endpoints. The outcome is recorded like a delivery, so a fixed channel turns healthy
        /// the moment its test succeeds, but a failing test never raises the bell. Never throws.
        /// </summary>
        public virtual async Task<NotificationSendResult> SendTestAsync(
            NotificationChannel channel, NotificationAlert alert, NotificationScope scope)
        {
            if (channel == null || !channel.HasDestination())
                return new NotificationSendResult { Success = false, Message = "This channel has no destination configured." };

            if (channel.ProviderType == (int)WebhookProviderType.Push)
                return await _push.SendWithResultAsync(scope, alert);

            var result = await SendToTransportAsync(channel, alert);
            if (_health != null)
                await _health.RecordAsync(scope, channel, result, ChannelSendKind.Test);
            return result;
        }

        private async Task<NotificationSendResult> SendToTransportAsync(NotificationChannel channel, NotificationAlert alert)
        {
            try
            {
                if (channel.ProviderType == (int)WebhookProviderType.Email)
                {
                    return _email == null
                        ? new NotificationSendResult { Success = false, Message = "The e-mail transport is not registered." }
                        : await _email.SendAlertAsync(channel.Url!, alert);
                }

                if (channel.ProviderType == (int)WebhookProviderType.Telegram)
                    return await _telegram.SendAlertAsync(channel.Url!, alert);

                return await _webhook.SendAsync(
                    channel.Url!,
                    (WebhookProviderType)channel.ProviderType,
                    alert,
                    channel.GetCustomHeaders(),
                    channel.GetSigningSecret());
            }
            catch (Exception ex)
            {
                // The transports report instead of throwing; this guard keeps a surprise in one
                // channel from stopping the others.
                return new NotificationSendResult { Success = false, Message = $"Unexpected error: {ex.Message}" };
            }
        }
    }
}
