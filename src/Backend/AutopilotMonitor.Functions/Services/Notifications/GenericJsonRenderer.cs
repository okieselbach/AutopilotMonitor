using System.Linq;
using AutopilotMonitor.Shared.Models.Notifications;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Renders a NotificationAlert as a stable, channel-agnostic JSON payload for generic webhook
    /// consumers (ticketing systems, automation, SMTP gateways such as Postal).
    ///
    /// Payload contract (schemaVersion "1.0"):
    /// {
    ///   "schemaVersion": "1.0",
    ///   "eventType": "enrollment_succeeded",   // omitted when not set
    ///   "severity": "Success",                 // Info | Success | Warning | Error
    ///   "title": "...", "summary": "...", "themeColor": "00B050",
    ///   "primaryUrl": "https://.../sessions/{id}",  // first openUrl action (session/dashboard link), omitted when none
    ///   "facts":    [ { "name": "Device", "value": "..." } ],
    ///   "sections": [ { "title": "...", "text": "..." } ],
    ///   "actions":  [ { "type": "openUrl", "title": "...", "url": "..." } ]
    /// }
    /// </summary>
    public class GenericJsonRenderer : INotificationRenderer
    {
        /// <summary>Bump only on a breaking change to the payload contract above.</summary>
        private const string SchemaVersion = "1.0";

        public WebhookProviderType ProviderType => WebhookProviderType.GenericJson;

        public string RenderToJson(NotificationAlert alert)
        {
            // First openUrl action: a session link for enrollment alerts, a dashboard/portal link for
            // SLA/test alerts. Named generically (primaryUrl) since it is not always a session URL;
            // consumers can still inspect actions[] (each carries its title) to disambiguate.
            var primaryUrl = alert.Actions
                .FirstOrDefault(a => a.Type == "openUrl" && !string.IsNullOrEmpty(a.Url))?.Url;

            var payload = new
            {
                schemaVersion = SchemaVersion,
                eventType = string.IsNullOrEmpty(alert.EventType) ? null : alert.EventType,
                severity = alert.Severity.ToString(),
                title = alert.Title,
                summary = alert.Summary,
                themeColor = string.IsNullOrEmpty(alert.ThemeColor) ? null : alert.ThemeColor,
                primaryUrl,
                facts = alert.Facts.Select(f => new { name = f.Name, value = f.Value }).ToArray(),
                sections = alert.Sections.Select(s => new { title = s.Title, text = s.Text }).ToArray(),
                actions = alert.Actions.Select(a => new { type = a.Type, title = a.Title, url = a.Url }).ToArray(),
            };

            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
            });
        }
    }
}
