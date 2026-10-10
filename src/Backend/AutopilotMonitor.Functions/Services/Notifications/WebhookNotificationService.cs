using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Dispatches NotificationAlert payloads to webhook URLs using provider-specific renderers.
    /// Replaces TeamsNotificationService with a channel-agnostic approach.
    /// </summary>
    public class WebhookNotificationService
    {
        private readonly HttpClient _http;
        private readonly Dictionary<WebhookProviderType, INotificationRenderer> _renderers;

        public WebhookNotificationService(HttpClient http)
        {
            _http = http;
            _renderers = new Dictionary<WebhookProviderType, INotificationRenderer>
            {
                [WebhookProviderType.TeamsWorkflowWebhook] = new TeamsWorkflowAdaptiveCardRenderer(),
                [WebhookProviderType.Slack] = new SlackRenderer(),
                [WebhookProviderType.GenericJson] = new GenericJsonRenderer(),
                [WebhookProviderType.Discord] = new DiscordRenderer(),
            };
        }

        /// <summary>
        /// Sends a notification and reports the outcome; never throws. The one webhook send path —
        /// real deliveries and the "send test" endpoints alike (the dispatcher records the outcome).
        /// <paramref name="customHeaders"/> (generic webhooks only) are attached to the request,
        /// e.g. an API-key/Authorization header for a ticketing system or SMTP gateway.
        /// <paramref name="signingSecret"/> (generic webhooks only) adds HMAC signature headers
        /// (see <see cref="WebhookSignatureCalculator"/>).
        /// </summary>
        public virtual async Task<NotificationSendResult> SendAsync(string webhookUrl, WebhookProviderType providerType, NotificationAlert alert,
            IReadOnlyDictionary<string, string>? customHeaders = null, string? signingSecret = null)
        {
            if (string.IsNullOrEmpty(webhookUrl))
                return new NotificationSendResult { Success = false, Message = "Webhook URL is not configured." };

            if (providerType == WebhookProviderType.None)
                return new NotificationSendResult { Success = false, Message = "No webhook provider selected." };

            if (!_renderers.TryGetValue(providerType, out var renderer))
                return new NotificationSendResult { Success = false, Message = $"Unknown provider type: {providerType}" };

            try
            {
                var json = renderer.RenderToJson(alert);
                var response = await PostAsync(webhookUrl, json, customHeaders, signingSecret);
                var statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return new NotificationSendResult { Success = true, StatusCode = statusCode, Message = "Test notification sent successfully." };
                }

                var body = await response.Content.ReadAsStringAsync();
                return new NotificationSendResult
                {
                    Success = false,
                    StatusCode = statusCode,
                    Message = $"Webhook returned HTTP {statusCode}: {(body.Length > 200 ? body[..200] : body)}"
                };
            }
            catch (HttpRequestException ex) when (ex.InnerException is SsrfException refused)
            {
                return new NotificationSendResult { Success = false, Message = refused.Message };
            }
            catch (Exception ex)
            {
                return new NotificationSendResult { Success = false, Message = $"Connection error: {ex.Message}" };
            }
        }

        /// <summary>
        /// POSTs the rendered JSON, attaching any custom headers as request headers. Restricted
        /// (framing/host/content) headers are already filtered upstream by NotificationChannel.GetCustomHeaders().
        /// When a signing secret is present, HMAC signature headers are computed over the exact
        /// JSON string being posted — after custom headers, so they can never be spoofed by one.
        /// </summary>
        private Task<HttpResponseMessage> PostAsync(string webhookUrl, string json, IReadOnlyDictionary<string, string>? customHeaders,
            string? signingSecret = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            if (customHeaders != null)
            {
                foreach (var header in customHeaders)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (!string.IsNullOrEmpty(signingSecret))
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                request.Headers.Remove(WebhookSignatureCalculator.TimestampHeader);
                request.Headers.Remove(WebhookSignatureCalculator.SignatureHeader);
                request.Headers.TryAddWithoutValidation(WebhookSignatureCalculator.TimestampHeader, timestamp);
                request.Headers.TryAddWithoutValidation(WebhookSignatureCalculator.SignatureHeader,
                    WebhookSignatureCalculator.ComputeSignature(signingSecret, timestamp, json));
            }

            return _http.SendAsync(request);
        }
    }
}
