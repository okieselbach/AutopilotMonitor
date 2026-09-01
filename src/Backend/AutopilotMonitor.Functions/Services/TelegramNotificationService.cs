using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Sends Telegram notifications for tenant signup events.
    /// Webhook URL is read from the PreviewConfig table via IConfigRepository.
    /// </summary>
    public class TelegramNotificationService
    {
        private readonly HttpClient _http;
        private readonly IConfigRepository _configRepo;
        private readonly ILogger<TelegramNotificationService> _logger;

        public TelegramNotificationService(
            HttpClient http,
            IConfigRepository configRepo,
            ILogger<TelegramNotificationService> logger)
        {
            _http = http;
            _configRepo = configRepo;
            _logger = logger;
        }

        /// <summary>
        /// Sends a Telegram message to the configured channel when a new tenant signs up.
        /// No-op if the webhook URL is not configured in the PreviewConfig table.
        /// </summary>
        public virtual async Task SendNewTenantSignupAsync(string tenantId, string upn)
        {
            try
            {
                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("Telegram webhook URL not configured — skipping tenant signup notification");
                    return;
                }

                var payload = new
                {
                    chat_id = "-1003632442830", // Telegram channel ID (as string)
                    text = $"New tenant signup!\nTenantID: {tenantId}\nUPN: {upn}"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(webhookUrl, content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation(
                        "Telegram tenant signup notification sent for tenant {TenantId}, UPN {Upn}",
                        tenantId, upn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to send Telegram tenant signup notification for tenant {TenantId}",
                    tenantId);
            }
        }

        /// <summary>
        /// Sends a Telegram notification when a Tenant Admin submits a session report.
        /// Best-effort — silently no-ops if the webhook URL is not configured.
        /// </summary>
        public async Task SendSessionReportAsync(string tenantId, string submittedBy, string sessionId, string reportId, string comment)
        {
            try
            {
                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("Telegram webhook URL not configured — skipping session report notification");
                    return;
                }

                var commentLine = string.IsNullOrWhiteSpace(comment) ? "" : $"\nComment: {comment}";
                var payload = new
                {
                    chat_id = "-1003632442830",
                    text = $"New Session Report submitted!\nTenantID: {tenantId}\nBy: {submittedBy}\nSessionID: {sessionId}\nReportID: {reportId}{commentLine}"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(webhookUrl, content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation(
                        "Telegram session report notification sent for report {ReportId}", reportId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram session report notification for {ReportId}", reportId);
            }
        }

        /// <summary>
        /// Sends a Telegram notification when a tenant admin submits a diag-files-only report
        /// (no session context). Best-effort — silently no-ops if the webhook URL is not configured.
        /// </summary>
        public async Task SendDiagFilesReportAsync(string tenantId, string submittedBy, string reportId, string comment)
        {
            try
            {
                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("Telegram webhook URL not configured — skipping diag-files report notification");
                    return;
                }

                var commentLine = string.IsNullOrWhiteSpace(comment) ? "" : $"\nComment: {comment}";
                var payload = new
                {
                    chat_id = "-1003632442830",
                    text = $"New Diag Files Report submitted!\nTenantID: {tenantId}\nBy: {submittedBy}\nReportID: {reportId}{commentLine}"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(webhookUrl, content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation(
                        "Telegram diag-files report notification sent for report {ReportId}", reportId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram diag-files report notification for {ReportId}", reportId);
            }
        }

        /// <summary>
        /// Posts content to a webhook URL with a single retry on transient failures (429, 5xx, network errors).
        /// </summary>
        private async Task<bool> PostWithRetryAsync(string webhookUrl, StringContent content, string context)
        {
            const int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var response = await _http.PostAsync(webhookUrl, content);

                    if (response.IsSuccessStatusCode)
                        return true;

                    var statusCode = (int)response.StatusCode;
                    var isTransient = statusCode == 429 || statusCode >= 500 && statusCode <= 599;

                    if (!isTransient || attempt >= maxAttempts)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning("Telegram webhook returned {StatusCode} for {Context}: {Body}", statusCode, context, body);
                        return false;
                    }

                    _logger.LogWarning(
                        "Telegram webhook returned {StatusCode} for {Context} (attempt {Attempt}/{MaxAttempts}). Retrying in 2s",
                        statusCode, context, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex) when (attempt < maxAttempts && (ex is HttpRequestException or TaskCanceledException))
                {
                    _logger.LogWarning(ex,
                        "Telegram webhook network error for {Context} (attempt {Attempt}/{MaxAttempts}). Retrying in 2s",
                        context, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
            return false;
        }

        /// <summary>
        /// Sends a Telegram notification when a user submits feedback via the in-app feedback bubble.
        /// Best-effort — silently no-ops if the webhook URL is not configured.
        /// </summary>
        public async Task SendFeedbackAsync(string tenantId, string upn, string displayName, int rating, string? comment)
        {
            try
            {
                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("Telegram webhook URL not configured — skipping feedback notification");
                    return;
                }

                var stars = new string('\u2605', rating) + new string('\u2606', 5 - rating);
                var commentLine = string.IsNullOrWhiteSpace(comment) ? "" : $"\nComment: {comment}";
                var payload = new
                {
                    chat_id = "-1003632442830",
                    text = $"New Feedback!\nRating: {stars} ({rating}/5)\nUser: {displayName} ({upn})\nTenant: {tenantId}{commentLine}"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(webhookUrl, content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Telegram feedback notification sent for user {Upn}", upn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram feedback notification for {Upn}", upn);
            }
        }

        /// <summary>
        /// Sends an alert to the specified Telegram chat.
        /// Converts the NotificationAlert into a plain-text Telegram message with severity emoji.
        /// Best-effort — silently no-ops on failure.
        /// <para>
        /// Virtual: <see cref="Notifications.NotificationChannelDispatcher"/> routes Telegram
        /// channels here, and its routing tests need a seam.
        /// </para>
        /// </summary>
        public virtual async Task SendOpsAlertAsync(string chatId, NotificationAlert alert)
        {
            try
            {
                await SendAlertWithResultAsync(chatId, alert);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram ops alert: {Title}", alert.Title);
            }
        }

        /// <summary>
        /// Same message as <see cref="SendOpsAlertAsync"/>, but REPORTS the outcome instead of
        /// swallowing it — for the channel "send test" endpoints. Never throws.
        /// </summary>
        public virtual async Task<Notifications.WebhookTestResult> SendAlertWithResultAsync(string chatId, NotificationAlert alert)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(chatId))
                    return new Notifications.WebhookTestResult { Success = false, Message = "No Telegram chat ID configured." };

                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    return new Notifications.WebhookTestResult
                    {
                        Success = false,
                        Message = "The platform Telegram bot is not configured — no message can be delivered."
                    };
                }

                var payload = new { chat_id = chatId, text = RenderAlertText(alert) };
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var sent = await PostWithRetryAsync(webhookUrl!, content, $"Alert:{alert.Title}");

                return sent
                    ? new Notifications.WebhookTestResult { Success = true, Message = "Test notification sent successfully." }
                    : new Notifications.WebhookTestResult
                    {
                        Success = false,
                        Message = "Telegram rejected the message — check the chat ID and that the bot is a member of that chat."
                    };
            }
            catch (Exception ex)
            {
                return new Notifications.WebhookTestResult { Success = false, Message = $"Connection error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Flattens an alert into the plain-text form Telegram accepts. Telegram has no card
        /// format, so facts become "Name: Value" lines — that is also what carries an ops event's
        /// structured payload into the message.
        /// </summary>
        internal static string RenderAlertText(NotificationAlert alert)
        {
            var icon = alert.Severity switch
            {
                NotificationSeverity.Error => "\U0001f7e0",   // orange circle
                NotificationSeverity.Warning => "\U0001f7e1", // yellow circle
                NotificationSeverity.Info => "\u2139\ufe0f",  // info
                _ => "\U0001f534"                              // red circle (fallback / success)
            };

            var sb = new StringBuilder();
            sb.AppendLine($"{icon} {alert.Title}");
            sb.AppendLine(alert.Summary);

            foreach (var fact in alert.Facts)
                sb.AppendLine($"{fact.Name}: {fact.Value}");

            return sb.ToString().TrimEnd();
        }

        private async Task<string?> GetWebhookUrlAsync()
        {
            try
            {
                var config = await _configRepo.GetPreviewConfigAsync();
                config.TryGetValue("WebhookUrl", out var value);
                return value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Telegram webhook URL from PreviewConfig table");
                return null;
            }
        }
    }
}
