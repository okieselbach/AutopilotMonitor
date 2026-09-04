using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class TestWebhookNotificationFunction
    {
        private readonly ILogger<TestWebhookNotificationFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly NotificationChannelDispatcher _channelDispatcher;

        public TestWebhookNotificationFunction(
            ILogger<TestWebhookNotificationFunction> logger,
            TenantConfigurationService configService,
            NotificationChannelDispatcher channelDispatcher)
        {
            _logger = logger;
            _configService = configService;
            _channelDispatcher = channelDispatcher;
        }

        [Function("TestWebhookNotification")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "config/{tenantId}/test-notification")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                string userIdentifier = requestCtx.UserPrincipalName;

                _logger.LogInformation("Test webhook notification requested by {User} for tenant {TenantId}",
                    userIdentifier, requestCtx.TargetTenantId);

                var tenantConfig = await _configService.GetConfigurationAsync(requestCtx.TargetTenantId);

                // Optional body { "channelId": "..." } tests one specific saved channel; without
                // it the first channel is used (which for non-migrated tenants is the one
                // synthesized from the legacy single-webhook fields — the pre-channels behavior).
                string? channelId = null;
                var body = await req.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        channelId = JsonConvert.DeserializeAnonymousType(body, new { channelId = (string?)null })?.channelId;
                    }
                    catch (JsonException)
                    {
                        // Malformed body → treat as no channel selection.
                    }
                }

                var channels = tenantConfig.GetNotificationChannels();
                var channel = channelId != null
                    ? channels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase))
                    : channels.FirstOrDefault();

                if (channel == null || string.IsNullOrEmpty(channel.Url))
                {
                    // 200 with success=false on purpose: a missing channel is a configuration state the
                    // UI renders inline, not a failed request.
                    return await req.OkAsync(new SuccessMessageResponse
                    {
                        Success = false,
                        Message = channelId != null
                            ? "The selected channel was not found. Save your changes before testing."
                            : "No notification channel is configured. Please add one first.",
                    });
                }

                var testAlert = NotificationAlertBuilder.BuildTestAlert();
                var result = await _channelDispatcher.SendWithResultAsync(channel, testAlert);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new TestWebhookNotificationResponse
                {
                    Success = result.Success,
                    StatusCode = result.StatusCode,
                    Message = result.Message
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "TestWebhookNotification");
            }
        }
    }
}
