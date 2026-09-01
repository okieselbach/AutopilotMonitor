using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Sends a test notification to one PLATFORM ops channel — the operator-side counterpart of
    /// <see cref="TestWebhookNotificationFunction"/>, which tests a tenant's channels. Same
    /// response contract; the only difference is the channel list it resolves against.
    /// </summary>
    public class TestOpsChannelFunction
    {
        private readonly ILogger<TestOpsChannelFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly NotificationChannelDispatcher _channelDispatcher;

        public TestOpsChannelFunction(
            ILogger<TestOpsChannelFunction> logger,
            AdminConfigurationService adminConfigService,
            NotificationChannelDispatcher channelDispatcher)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
            _channelDispatcher = channelDispatcher;
        }

        [Function("TestOpsChannel")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/config/test-ops-channel")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                _logger.LogInformation("Test ops channel requested by {User}", userIdentifier);

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

                var config = await _adminConfigService.GetConfigurationAsync();

                // Resolves against the synthesized legacy channels too, so an operator can test
                // the existing setup before ever saving the migrated list.
                var channels = config.GetOpsNotificationChannels();
                var channel = channelId != null
                    ? channels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase))
                    : channels.FirstOrDefault();

                if (channel == null || string.IsNullOrEmpty(channel.Url))
                {
                    var notFound = req.CreateResponse(HttpStatusCode.OK);
                    await notFound.WriteAsJsonAsync(new TestWebhookNotificationResponse
                    {
                        Success = false,
                        Message = channelId != null
                            ? "The selected channel was not found. Save your changes before testing."
                            : "No ops notification channel is configured. Please add one first."
                    });
                    return notFound;
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
                _logger.LogError(ex, "Error sending ops channel test notification");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
