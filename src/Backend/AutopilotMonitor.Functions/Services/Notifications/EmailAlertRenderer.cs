using System.Net;
using System.Text;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications;

/// <summary>
/// Renders a <see cref="NotificationAlert"/> as an e-mail: subject and a self-contained HTML body
/// (inline styles, no images, no scripts) that reads well in every mail client and in its
/// auto-generated plain-text form. Every value from the alert is HTML-escaped; the structured
/// payload (<see cref="NotificationAlert.DataJson"/>, present only when the rule opted in) is
/// shown as preformatted text, capped so a large details object cannot bloat the mail.
/// </summary>
public static class EmailAlertRenderer
{
    public const int MaxPayloadChars = 8 * 1024;
    public const string SubjectPrefix = "[Autopilot Monitor]";

    public static string Subject(NotificationAlert alert)
    {
        var severity = alert.Severity switch
        {
            NotificationSeverity.Error => "Error: ",
            NotificationSeverity.Warning => "Warning: ",
            _ => string.Empty,
        };
        var title = OneLine(alert.Title, 160);
        return $"{SubjectPrefix} {severity}{(title.Length > 0 ? title : "Notification")}";
    }

    public static string Html(NotificationAlert alert)
    {
        var accent = alert.Severity switch
        {
            NotificationSeverity.Error => "#dc2626",
            NotificationSeverity.Warning => "#f59e0b",
            NotificationSeverity.Success => "#16a34a",
            _ => "#0284c7",
        };

        var sb = new StringBuilder(2048);
        sb.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f9fafb;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#111827;\">");
        sb.Append("<div style=\"max-width:640px;margin:0 auto;background:#ffffff;border-radius:8px;border-left:4px solid ").Append(accent).Append(";padding:20px 24px;\">");
        sb.Append("<h1 style=\"font-size:18px;margin:0 0 8px 0;\">").Append(E(alert.Title)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(alert.Summary))
            sb.Append("<p style=\"margin:0 0 16px 0;font-size:14px;color:#374151;\">").Append(E(alert.Summary)).Append("</p>");

        var facts = alert.Facts.Where(f => f != null && !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (facts.Count > 0)
        {
            sb.Append("<table style=\"border-collapse:collapse;font-size:14px;margin:0 0 16px 0;\">");
            foreach (var fact in facts)
            {
                sb.Append("<tr><td style=\"padding:3px 16px 3px 0;color:#6b7280;vertical-align:top;white-space:nowrap;\">").Append(E(fact.Name))
                  .Append("</td><td style=\"padding:3px 0;\">").Append(E(fact.Value)).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        foreach (var section in alert.Sections.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Text)))
        {
            if (!string.IsNullOrWhiteSpace(section.Title))
                sb.Append("<h2 style=\"font-size:14px;margin:16px 0 4px 0;\">").Append(E(section.Title)).Append("</h2>");
            sb.Append("<p style=\"margin:0 0 12px 0;font-size:14px;white-space:pre-wrap;\">").Append(E(section.Text)).Append("</p>");
        }

        var links = alert.Actions.Where(a => a != null && IsHttpUrl(a.Url)).ToList();
        if (links.Count > 0)
        {
            sb.Append("<p style=\"margin:16px 0 0 0;font-size:14px;\">");
            foreach (var action in links)
            {
                sb.Append("<a href=\"").Append(E(action.Url)).Append("\" style=\"color:#0369a1;margin-right:16px;\">")
                  .Append(E(string.IsNullOrWhiteSpace(action.Title) ? action.Url : action.Title)).Append("</a>");
            }
            sb.Append("</p>");
        }

        if (!string.IsNullOrWhiteSpace(alert.DataJson))
        {
            var payload = alert.DataJson.Length > MaxPayloadChars ? alert.DataJson[..MaxPayloadChars] + "\n… (truncated)" : alert.DataJson;
            sb.Append("<h2 style=\"font-size:14px;margin:16px 0 4px 0;\">Details</h2>");
            sb.Append("<pre style=\"margin:0;padding:12px;background:#f3f4f6;border-radius:6px;font-size:12px;overflow:auto;white-space:pre-wrap;\">").Append(E(payload)).Append("</pre>");
        }

        sb.Append("<p style=\"margin:20px 0 0 0;font-size:12px;color:#9ca3af;\">Sent by Autopilot Monitor");
        if (!string.IsNullOrWhiteSpace(alert.EventType))
            sb.Append(" · ").Append(E(alert.EventType));
        sb.Append("</p></div></body></html>");
        return sb.ToString();
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string OneLine(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var flat = string.Join(" ", value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length > max ? flat[..max] + "…" : flat;
    }

    private static bool IsHttpUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
