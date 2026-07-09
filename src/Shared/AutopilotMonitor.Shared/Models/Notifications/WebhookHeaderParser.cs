using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AutopilotMonitor.Shared.Models.Notifications
{
    /// <summary>
    /// Parses custom webhook request headers from their JSON object form
    /// (<c>{ "Header-Name": "value", ... }</c>). Shared by the legacy single-webhook config
    /// (<c>TenantConfiguration.GetGenericWebhookHeaders</c>) and per-channel headers
    /// (<see cref="NotificationChannel.GetCustomHeaders"/>).
    /// </summary>
    public static class WebhookHeaderParser
    {
        /// <summary>
        /// HTTP headers that must never be set via the custom-header mechanism: framing/host/content
        /// headers are owned by the HTTP client and overriding them breaks the request or enables
        /// request-smuggling. Compared case-insensitively.
        /// </summary>
        private static readonly HashSet<string> RestrictedWebhookHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection",
            "Keep-Alive", "Upgrade", "TE", "Trailer", "Expect", "Proxy-Connection",
        };

        /// <summary>
        /// Parses the JSON into header name/value pairs, dropping restricted headers, blank
        /// names/values, and non-string values. Never throws — malformed JSON yields an empty
        /// dictionary (fail-soft; dispatch still proceeds without custom headers).
        /// </summary>
        public static IReadOnlyDictionary<string, string> Parse(string? headersJson)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(headersJson))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(headersJson!);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return result;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        continue;

                    var name = prop.Name.Trim();
                    var value = prop.Value.GetString();

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(value))
                        continue;
                    if (RestrictedWebhookHeaders.Contains(name))
                        continue;

                    result[name] = value!;
                }
            }
            catch (JsonException)
            {
                // Malformed JSON → no custom headers (fail-soft; dispatch still proceeds).
            }

            return result;
        }
    }
}
