using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AutopilotMonitor.Shared.Models.Notifications;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Renders a NotificationAlert as a Discord webhook payload (single embed).
    /// Discord webhooks cannot post buttons, so openUrl actions are appended to the
    /// embed description as markdown links.
    /// </summary>
    public class DiscordRenderer : INotificationRenderer
    {
        // Discord embed limits: https://discord.com/developers/docs/resources/message#embed-object-embed-limits
        private const int MaxTitleLength = 256;
        private const int MaxDescriptionLength = 4096;
        private const int MaxFields = 25;
        private const int MaxFieldNameLength = 256;
        private const int MaxFieldValueLength = 1024;
        private const int MaxEmbedTotalLength = 6000;

        public WebhookProviderType ProviderType => WebhookProviderType.Discord;

        public string RenderToJson(NotificationAlert alert)
        {
            var title = Truncate($"{GetSeverityEmoji(alert.Severity)} {alert.Title}", MaxTitleLength);
            var description = Truncate(BuildDescription(alert), MaxDescriptionLength);

            // Facts as inline fields. Discord rejects fields with an empty name or value.
            var facts = alert.Facts
                .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
                .Take(MaxFields)
                .Select(f => (Name: Truncate(f.Name, MaxFieldNameLength), Value: Truncate(f.Value, MaxFieldValueLength)))
                .ToList();

            // Enforce the 6000-char total cap across title + description + fields:
            // drop trailing fields first, then shrink the description to the remaining budget.
            var fixedLength = title.Length + description.Length;
            while (facts.Count > 0 && fixedLength + facts.Sum(f => f.Name.Length + f.Value.Length) > MaxEmbedTotalLength)
                facts.RemoveAt(facts.Count - 1);
            if (fixedLength > MaxEmbedTotalLength)
                description = Truncate(description, MaxEmbedTotalLength - title.Length);

            var fields = facts
                .Select(f => new { name = f.Name, value = f.Value, inline = true })
                .ToArray();

            var embed = new
            {
                title,
                description = description.Length > 0 ? description : null,
                color = GetColor(alert),
                fields = fields.Length > 0 ? fields : null
            };

            var payload = new { embeds = new[] { embed } };

            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                {
                    NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()
                }
            });
        }

        private static string BuildDescription(NotificationAlert alert)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(alert.Summary))
                sb.Append(alert.Summary);

            foreach (var section in alert.Sections)
            {
                var text = "";
                if (!string.IsNullOrEmpty(section.Title))
                    text += $"**{section.Title}**\n";
                if (!string.IsNullOrEmpty(section.Text))
                    text += section.Text;

                if (text.Length > 0)
                {
                    if (sb.Length > 0)
                        sb.Append("\n\n");
                    sb.Append(text);
                }
            }

            foreach (var action in alert.Actions.Where(a => a.Type == "openUrl" && !string.IsNullOrEmpty(a.Url)))
            {
                if (sb.Length > 0)
                    sb.Append("\n\n");
                sb.Append($"[{action.Title}]({action.Url})");
            }

            return sb.ToString();
        }

        private static int GetColor(NotificationAlert alert)
        {
            if (!string.IsNullOrEmpty(alert.ThemeColor)
                && int.TryParse(alert.ThemeColor.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
                return color;

            return alert.Severity switch
            {
                NotificationSeverity.Success => 0x2ECC71,
                NotificationSeverity.Error => 0xE74C3C,
                NotificationSeverity.Warning => 0xF1C40F,
                _ => 0x3498DB
            };
        }

        private static string GetSeverityEmoji(NotificationSeverity severity)
        {
            return severity switch
            {
                NotificationSeverity.Success => "\u2705",
                NotificationSeverity.Error => "\u274c",
                NotificationSeverity.Warning => "\u26a0\ufe0f",
                NotificationSeverity.Info => "\u2139\ufe0f",
                _ => "\u2139\ufe0f"
            };
        }

        private static string Truncate(string value, int maxLength)
        {
            if (maxLength <= 0)
                return "";
            if (value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength - 1) + "\u2026";
        }
    }
}
