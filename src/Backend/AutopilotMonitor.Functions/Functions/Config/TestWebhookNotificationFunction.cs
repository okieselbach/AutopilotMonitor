using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
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
        private readonly WebhookNotificationService _webhookNotificationService;

        public TestWebhookNotificationFunction(
            ILogger<TestWebhookNotificationFunction> logger,
            TenantConfigurationService configService,
            WebhookNotificationService webhookNotificationService)
        {
            _logger = logger;
            _configService = configService;
            _webhookNotificationService = webhookNotificationService;
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
                var (webhookUrl, providerTypeInt) = tenantConfig.GetEffectiveWebhookConfig();

                if (string.IsNullOrEmpty(webhookUrl) || providerTypeInt == 0)
                {
                    var noConfigResponse = req.CreateResponse(HttpStatusCode.OK);
                    await noConfigResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "No webhook is configured. Please set a webhook URL and provider first."
                    });
                    return noConfigResponse;
                }

                var providerType = (WebhookProviderType)providerTypeInt;
                var testAlert = NotificationAlertBuilder.BuildTestAlert();
                var result = await _webhookNotificationService.SendNotificationWithResultAsync(webhookUrl, providerType, testAlert, tenantConfig.GetGenericWebhookHeaders());

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = result.Success,
                    statusCode = result.StatusCode,
                    message = result.Message
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending test webhook notification for tenant {TenantId}", tenantId);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
