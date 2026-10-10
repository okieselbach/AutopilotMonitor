using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Delivery status of the stored notification channels: a tenant's channels and the
    /// platform's ops channels. Same audience as the channels' "Send Test" button — the people
    /// who can fix a failing destination.
    /// </summary>
    public class NotificationChannelHealthFunction
    {
        private readonly ILogger<NotificationChannelHealthFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly INotificationChannelHealthRepository _repository;
        private readonly TimeProvider _time;

        public NotificationChannelHealthFunction(
            ILogger<NotificationChannelHealthFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            INotificationChannelHealthRepository repository,
            TimeProvider? time = null)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _repository = repository;
            _time = time ?? TimeProvider.System;
        }

        [Function("GetNotificationChannelHealth")]
        public async Task<HttpResponseData> GetTenant(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/notification-channel-health")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var targetTenantId = req.GetRequestContext().TargetTenantId;
                var config = await _configService.GetConfigurationAsync(targetTenantId);
                var body = await BuildAsync(config.GetNotificationChannels(), NotificationScope.Tenant(targetTenantId));
                return await req.OkAsync(body);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetNotificationChannelHealth");
            }
        }

        [Function("GetOpsChannelHealth")]
        public async Task<HttpResponseData> GetOps(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/config/ops-channel-health")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var config = await _adminConfigService.GetConfigurationAsync();
                var body = await BuildAsync(config.GetOpsNotificationChannels(), NotificationScope.Platform);
                return await req.OkAsync(body);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetOpsChannelHealth");
            }
        }

        /// <summary>
        /// One entry per stored non-Push channel, in channel order. Rows of deleted channels are
        /// never returned, and a row of an earlier destination reads as no deliveries yet.
        /// </summary>
        internal async Task<NotificationChannelHealthResponse> BuildAsync(IEnumerable<NotificationChannel> channels, NotificationScope scope)
        {
            var rows = (await _repository.ListAsync(scope.Key))
                .GroupBy(r => r.ChannelId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var now = _time.GetUtcNow().UtcDateTime;

            var response = new NotificationChannelHealthResponse();
            foreach (var channel in channels)
            {
                if (channel == null || string.IsNullOrWhiteSpace(channel.Id)
                    || channel.ProviderType == (int)WebhookProviderType.Push)
                    continue;

                rows.TryGetValue(channel.Id, out var row);
                var current = NotificationChannelHealthEvaluator.Current(row, NotificationChannelHealthEvaluator.Fingerprint(channel));
                response.Channels.Add(NotificationChannelHealthEvaluator.ToDto(channel.Id, current, now));
            }
            return response;
        }
    }
}
