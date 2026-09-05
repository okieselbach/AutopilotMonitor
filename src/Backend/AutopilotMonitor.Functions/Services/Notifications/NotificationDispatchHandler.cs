using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Consumer side of the <c>notification-dispatch</c> queue: resolves the envelope's channel
    /// ids against the tenant's CURRENT configuration (so secrets never travel in the queue and
    /// a channel disabled meanwhile is skipped) and hands the alert to
    /// <see cref="NotificationChannelDispatcher"/>. The dispatcher never throws — a failing
    /// destination only logs — so the queue guarantees the attempt, not the delivery; per-request
    /// retries stay with the transport's resilience policy.
    /// </summary>
    public class NotificationDispatchHandler
    {
        private readonly TenantConfigurationService _configService;
        private readonly NotificationChannelDispatcher _dispatcher;
        private readonly ILogger<NotificationDispatchHandler> _logger;

        public NotificationDispatchHandler(
            TenantConfigurationService configService,
            NotificationChannelDispatcher dispatcher,
            ILogger<NotificationDispatchHandler> logger)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public virtual async Task HandleAsync(NotificationDispatchEnvelope envelope, CancellationToken cancellationToken)
        {
            if (envelope is null) throw new ArgumentNullException(nameof(envelope));

            var (config, exists) = await _configService.TryGetConfigurationAsync(envelope.TenantId).ConfigureAwait(false);
            if (!exists)
            {
                _logger.LogWarning(
                    "NotificationDispatch: tenant {Tenant} has no configuration — alert {EventType} dropped",
                    envelope.TenantId, envelope.Alert?.EventType);
                return;
            }

            var wanted = new HashSet<string>(envelope.ChannelIds ?? new List<string>(), StringComparer.Ordinal);
            var targets = config.GetNotificationChannels()
                .Where(c => c.Enabled && wanted.Contains(c.Id))
                .ToList();

            if (targets.Count == 0)
            {
                _logger.LogWarning(
                    "NotificationDispatch: none of {Requested} channel(s) is still enabled for tenant {Tenant} — alert {EventType} dropped",
                    wanted.Count, envelope.TenantId, envelope.Alert?.EventType);
                return;
            }

            await _dispatcher.SendToChannelsAsync(targets, envelope.Alert).ConfigureAwait(false);
        }
    }
}
