using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>Writes the outcome of every channel send into the channel's health row.</summary>
    public interface INotificationChannelHealthRecorder
    {
        /// <summary>Never throws: a storage problem must not stop the next channel or the caller's pipeline.</summary>
        Task RecordAsync(NotificationScope scope, NotificationChannel channel, NotificationSendResult result, ChannelSendKind kind);
    }

    /// <summary>
    /// Folds each send outcome into the <see cref="NotificationChannelHealth"/> row with a
    /// compare-and-swap write, then acts on the transition: a <see cref="TransitionEventName"/>
    /// custom event on every status change, and the "channel failing" bell for the one write that
    /// stamped the failing episode. The bell goes to the portal only — never through a channel,
    /// one of which is the channel that just failed. Push channels are not recorded: their health
    /// lives on the paired device rows.
    /// </summary>
    public sealed class NotificationChannelHealthRecorder : INotificationChannelHealthRecorder
    {
        public const string TransitionEventName = "NotificationChannelHealth";
        public const string TenantBellType = "notification_channel_failing";
        public const string OpsBellType = "ops_channel_failing";
        public const string TenantSettingsHref = "/settings/tenant/notifications";
        public const string OpsSettingsHref = "/admin/settings/alerts";

        private const int CasAttempts = 4;

        private readonly INotificationChannelHealthRepository _repository;
        private readonly TenantNotificationService _tenantBell;
        private readonly GlobalNotificationService _globalBell;
        private readonly ILogger<NotificationChannelHealthRecorder> _logger;
        private readonly TelemetryClient? _telemetry;
        private readonly TimeProvider _time;

        public NotificationChannelHealthRecorder(
            INotificationChannelHealthRepository repository,
            TenantNotificationService tenantBell,
            GlobalNotificationService globalBell,
            ILogger<NotificationChannelHealthRecorder> logger,
            TelemetryClient? telemetry = null,
            TimeProvider? time = null)
        {
            _repository = repository;
            _tenantBell = tenantBell;
            _globalBell = globalBell;
            _logger = logger;
            _telemetry = telemetry;
            _time = time ?? TimeProvider.System;
        }

        public async Task RecordAsync(NotificationScope scope, NotificationChannel channel, NotificationSendResult result, ChannelSendKind kind)
        {
            if (!scope.IsSet || channel == null || string.IsNullOrWhiteSpace(channel.Id)
                || channel.ProviderType == (int)WebhookProviderType.Push)
                return;

            try
            {
                var fingerprint = NotificationChannelHealthEvaluator.Fingerprint(channel);
                for (var attempt = 1; attempt <= CasAttempts; attempt++)
                {
                    var (stored, etag) = await _repository.GetWithETagAsync(scope.Key, channel.Id).ConfigureAwait(false);
                    var fold = NotificationChannelHealthEvaluator.Apply(
                        stored, scope.Key, channel.Id, fingerprint, result, kind, _time.GetUtcNow().UtcDateTime);

                    if (await _repository.TryUpsertAsync(fold.Next, etag).ConfigureAwait(false))
                    {
                        await ActOnAsync(scope, channel, fold, kind).ConfigureAwait(false);
                        return;
                    }

                    await Task.Delay(30 * attempt).ConfigureAwait(false);
                }

                _logger.LogWarning(
                    "Health of notification channel {ChannelId} in scope {Scope} lost the write race {Attempts} times; outcome not recorded",
                    channel.Id, scope, CasAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording the outcome of notification channel {ChannelId} in scope {Scope} failed", channel.Id, scope);
            }
        }

        private async Task ActOnAsync(NotificationScope scope, NotificationChannel channel, ChannelHealthFold fold, ChannelSendKind kind)
        {
            if (fold.Transition != ChannelHealthTransition.None)
                Track(scope, channel, fold, kind);

            if (!fold.RaiseBell)
                return;

            var name = string.IsNullOrWhiteSpace(channel.Name) ? "Unnamed channel" : channel.Name.Trim();
            var title = "Notification channel failing";
            var message = $"\"{name}\" could not deliver the last {fold.Next.ConsecutiveFailures} notifications ({fold.Next.LastError}). "
                + "Check the destination, then send a test.";

            // Both services are fail-soft; the guard covers a mock or a future change that is not.
            try
            {
                if (scope.IsPlatform)
                    await _globalBell.CreateNotificationAsync(OpsBellType, title, message, OpsSettingsHref).ConfigureAwait(false);
                else
                    await _tenantBell.CreateNotificationAsync(scope.Key, TenantBellType, title, message, TenantSettingsHref).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Raising the failing bell for notification channel {ChannelId} in scope {Scope} failed", channel.Id, scope);
            }
        }

        private void Track(NotificationScope scope, NotificationChannel channel, ChannelHealthFold fold, ChannelSendKind kind)
        {
            try
            {
                _telemetry?.TrackEvent(TransitionEventName, new Dictionary<string, string>
                {
                    ["transition"] = fold.Transition == ChannelHealthTransition.BecameFailing ? "failing" : "recovered",
                    ["scope"] = scope.IsPlatform ? "platform" : "tenant",
                    ["tenantId"] = scope.IsPlatform ? string.Empty : scope.Key,
                    ["providerType"] = ((WebhookProviderType)channel.ProviderType).ToString(),
                    ["statusCode"] = fold.Next.LastStatusCode?.ToString() ?? string.Empty,
                    ["kind"] = kind == ChannelSendKind.Test ? "test" : "delivery",
                });
            }
            catch
            {
                // Telemetry never fails a send.
            }
        }
    }
}
