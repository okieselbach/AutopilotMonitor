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
        private readonly ILogger<WebhookNotificationService> _logger;
        private readonly Dictionary<WebhookProviderType, INotificationRenderer> _renderers;

        public WebhookNotificationService(HttpClient http, ILogger<WebhookNotificationService> logger)
        {
            _http = http;
            _logger = logger;
            _renderers = new Dictionary<WebhookProviderType, INotificationRenderer>
            {
                [WebhookProviderType.TeamsLegacyConnector] = new LegacyTeamsConnectorRenderer(),
                [WebhookProviderType.TeamsWorkflowWebhook] = new TeamsWorkflowAdaptiveCardRenderer(),
                [WebhookProviderType.Slack] = new SlackRenderer(),
                [WebhookProviderType.GenericJson] = new GenericJsonRenderer(),
                [WebhookProviderType.Discord] = new DiscordRenderer(),
            };
        }

        /// <summary>
        /// Sends a notification (fire-and-forget, non-fatal). Exceptions are logged as warnings.
        /// <paramref name="customHeaders"/> (generic webhooks only) are attached to the request,
        /// e.g. an API-key/Authorization header for a ticketing system or SMTP gateway.
        /// <paramref name="signingSecret"/> (generic webhooks only) adds HMAC signature headers
        /// (see <see cref="WebhookSignatureCalculator"/>).
        /// </summary>
        public virtual async Task SendNotificationAsync(string webhookUrl, WebhookProviderType providerType, NotificationAlert alert,
            IReadOnlyDictionary<string, string>? customHeaders = null, string? signingSecret = null)
        {
            if (string.IsNullOrEmpty(webhookUrl) || providerType == WebhookProviderType.None)
                return;

            try
            {
                if (!_renderers.TryGetValue(providerType, out var renderer))
                {
                    _logger.LogWarning("No renderer registered for webhook provider type {ProviderType}", providerType);
                    return;
                }

                await SsrfGuard.ValidateDestinationAsync(webhookUrl);

                var json = renderer.RenderToJson(alert);
                var response = await PostAsync(webhookUrl, json, customHeaders, signingSecret);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Webhook notification sent: {Summary} (provider={ProviderType})", alert.Summary, providerType);
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Webhook returned {StatusCode} for {Summary}: {Body}", (int)response.StatusCode, alert.Summary, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send webhook notification: {Summary}", alert.Summary);
            }
        }

        /// <summary>
        /// Sends a notification and returns the result (for test endpoint). Not fire-and-forget.
        /// <paramref name="customHeaders"/> and <paramref name="signingSecret"/> (generic
        /// webhooks only) are applied exactly like in <see cref="SendNotificationAsync"/>.
        /// </summary>
        public virtual async Task<WebhookTestResult> SendNotificationWithResultAsync(string webhookUrl, WebhookProviderType providerType, NotificationAlert alert,
            IReadOnlyDictionary<string, string>? customHeaders = null, string? signingSecret = null)
        {
            if (string.IsNullOrEmpty(webhookUrl))
                return new WebhookTestResult { Success = false, Message = "Webhook URL is not configured." };

            if (providerType == WebhookProviderType.None)
                return new WebhookTestResult { Success = false, Message = "No webhook provider selected." };

            if (!_renderers.TryGetValue(providerType, out var renderer))
                return new WebhookTestResult { Success = false, Message = $"Unknown provider type: {providerType}" };

            try
            {
                await SsrfGuard.ValidateDestinationAsync(webhookUrl);
            }
            catch (SsrfException ex)
            {
                return new WebhookTestResult { Success = false, Message = ex.Message };
            }

            try
            {
                var json = renderer.RenderToJson(alert);
                var response = await PostAsync(webhookUrl, json, customHeaders, signingSecret);
                var statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return new WebhookTestResult { Success = true, StatusCode = statusCode, Message = "Test notification sent successfully." };
                }

                var body = await response.Content.ReadAsStringAsync();
                return new WebhookTestResult
                {
                    Success = false,
                    StatusCode = statusCode,
                    Message = $"Webhook returned HTTP {statusCode}: {(body.Length > 200 ? body[..200] : body)}"
                };
            }
            catch (Exception ex)
            {
                return new WebhookTestResult { Success = false, Message = $"Connection error: {ex.Message}" };
            }
        }

        /// <summary>
        /// POSTs the rendered JSON, attaching any custom headers as request headers. Restricted
        /// (framing/host/content) headers are already filtered upstream by GetGenericWebhookHeaders().
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

    /// <summary>
    /// Outcome of a single "send a test notification" attempt. Shared by every channel provider —
    /// <see cref="NotificationChannelDispatcher"/> returns it for Telegram channels too, which is
    /// why the type is not webhook-specific despite the name.
    /// </summary>
    public class WebhookTestResult
    {
        public bool Success { get; set; }
        public int? StatusCode { get; set; }
        public string Message { get; set; } = "";
    }
}
