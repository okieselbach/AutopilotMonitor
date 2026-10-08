using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications;

/// <summary>
/// Renders a <see cref="NotificationAlert"/> as an e-mail that reads like a message, not like a
/// log line — the difference between the inbox and the junk folder for a transactional sender
/// whose SPF, DKIM and DMARC are already in order. Subject without brackets or shouting, one
/// introductory sentence, the facts as a table, the structured payload (present only when the
/// rule opted in) as a key/value list instead of raw JSON, a visible portal link, and a
/// plain-text part of its own so the multipart message is balanced. Every value from the alert
/// is HTML-escaped; the payload is capped so a large details object cannot bloat the mail.
/// </summary>
public static class EmailAlertRenderer
{
    public const int MaxPayloadChars = 8 * 1024;
    public const int MaxPayloadRows = 40;
    public const string Brand = "Autopilot Monitor";

    private static readonly Regex OpsAlertTitle = new(@"^Ops Alert:\s*(?<category>[^/]+)/(?<event>[A-Za-z0-9_]+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// "Autopilot Monitor: Push delivery failed (Platform)" for an ops alert, "Autopilot Monitor:
    /// &lt;title&gt;" for everything else. No bracketed tags, no severity words — those are the
    /// patterns bulk filters learn to dislike; the severity is the first line of the body.
    /// </summary>
    public static string Subject(NotificationAlert alert)
    {
        var (category, eventName) = SplitOpsTitle(alert.Title);
        var core = eventName != null
            ? $"{Humanize(eventName)} ({category})"
            : OneLine(alert.Title, 120);
        return $"{Brand}: {(core.Length > 0 ? core : "Notification")}";
    }

    public static string Html(NotificationAlert alert)
    {
        var accent = alert.Severity switch
        {
            NotificationSeverity.Error => "#dc2626",
            NotificationSeverity.Warning => "#d97706",
            NotificationSeverity.Success => "#16a34a",
            _ => "#0284c7",
        };
        var (category, eventName) = SplitOpsTitle(alert.Title);
        var heading = eventName != null ? Humanize(eventName) : OneLine(alert.Title, 160);

        var sb = new StringBuilder(2048);
        sb.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:24px;background:#f9fafb;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#111827;font-size:15px;line-height:1.5;\">");
        sb.Append("<div style=\"max-width:640px;margin:0 auto;background:#ffffff;border-radius:8px;border-left:4px solid ").Append(accent).Append(";padding:20px 24px;\">");
        sb.Append("<p style=\"margin:0 0 12px 0;\">").Append(E(Intro(alert, category))).Append("</p>");
        sb.Append("<h1 style=\"font-size:18px;margin:0 0 8px 0;\">").Append(E(heading)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(alert.Summary) && !string.Equals(alert.Summary.Trim(), heading, StringComparison.Ordinal))
            sb.Append("<p style=\"margin:0 0 16px 0;color:#374151;\">").Append(E(alert.Summary)).Append("</p>");

        var facts = Facts(alert);
        if (facts.Count > 0)
            AppendTable(sb, facts);

        foreach (var section in alert.Sections.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Text)))
        {
            if (!string.IsNullOrWhiteSpace(section.Title))
                sb.Append("<h2 style=\"font-size:15px;margin:16px 0 4px 0;\">").Append(E(section.Title)).Append("</h2>");
            sb.Append("<p style=\"margin:0 0 12px 0;white-space:pre-wrap;\">").Append(E(section.Text)).Append("</p>");
        }

        var payload = PayloadRows(alert.DataJson);
        if (payload.Rows.Count > 0)
        {
            sb.Append("<h2 style=\"font-size:15px;margin:16px 0 4px 0;\">Details</h2>");
            AppendTable(sb, payload.Rows);
        }
        else if (payload.Raw != null)
        {
            sb.Append("<h2 style=\"font-size:15px;margin:16px 0 4px 0;\">Details</h2>");
            sb.Append("<pre style=\"margin:0;padding:12px;background:#f3f4f6;border-radius:6px;font-size:12px;overflow:auto;white-space:pre-wrap;\">").Append(E(payload.Raw)).Append("</pre>");
        }

        foreach (var action in Links(alert))
        {
            sb.Append("<p style=\"margin:16px 0 0 0;\">").Append(E(action.Title)).Append(": <a href=\"").Append(E(action.Url))
              .Append("\" style=\"color:#0369a1;\">").Append(E(action.Url)).Append("</a></p>");
        }

        sb.Append("<p style=\"margin:20px 0 0 0;font-size:12px;color:#6b7280;\">This message was sent automatically by ").Append(Brand)
          .Append(" because an alert rule in your platform settings is bound to an e-mail channel. Change the rule or the channel in the portal to stop it.</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>The plain-text twin of <see cref="Html"/>: same content, no markup.</summary>
    public static string Text(NotificationAlert alert)
    {
        var (category, eventName) = SplitOpsTitle(alert.Title);
        var heading = eventName != null ? Humanize(eventName) : OneLine(alert.Title, 160);
        var sb = new StringBuilder(1024);
        sb.Append(Intro(alert, category)).Append("\n\n");
        sb.Append(heading).Append('\n');
        if (!string.IsNullOrWhiteSpace(alert.Summary) && !string.Equals(alert.Summary.Trim(), heading, StringComparison.Ordinal))
            sb.Append(alert.Summary.Trim()).Append('\n');
        sb.Append('\n');
        foreach (var (name, value) in Facts(alert))
            sb.Append(name).Append(": ").Append(value).Append('\n');
        foreach (var section in alert.Sections.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Text)))
        {
            sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(section.Title))
                sb.Append(section.Title.Trim()).Append('\n');
            sb.Append(section.Text.Trim()).Append('\n');
        }
        var payload = PayloadRows(alert.DataJson);
        if (payload.Rows.Count > 0 || payload.Raw != null)
        {
            sb.Append("\nDetails\n");
            foreach (var (name, value) in payload.Rows)
                sb.Append(name).Append(": ").Append(value).Append('\n');
            if (payload.Raw != null)
                sb.Append(payload.Raw).Append('\n');
        }
        foreach (var action in Links(alert))
            sb.Append('\n').Append(action.Title).Append(": ").Append(action.Url).Append('\n');
        sb.Append("\n--\nThis message was sent automatically by ").Append(Brand)
          .Append(" because an alert rule in your platform settings is bound to an e-mail channel. Change the rule or the channel in the portal to stop it.\n");
        return sb.ToString();
    }

    // ── pieces ──────────────────────────────────────────────────────────────

    private static string Intro(NotificationAlert alert, string? category)
    {
        var severity = alert.Severity switch
        {
            NotificationSeverity.Error => "an error",
            NotificationSeverity.Warning => "a warning",
            NotificationSeverity.Success => "a success notification",
            _ => "an informational notification",
        };
        return category != null
            ? $"{Brand} raised {severity} in the {category} category."
            : $"{Brand} sent you {severity}.";
    }

    private static (string? Category, string? Event) SplitOpsTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, null);
        var m = OpsAlertTitle.Match(title.Trim());
        return m.Success ? (m.Groups["category"].Value.Trim(), m.Groups["event"].Value) : (null, null);
    }

    /// <summary>"PushDeliveryFailed" → "Push delivery failed"; "SLA" stays upper-case; snake_case is split too.</summary>
    public static string Humanize(string value)
    {
        var words = Regex.Replace(value.Replace('_', ' '), @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Trim();
        if (words.Length == 0) return value;
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => w.All(char.IsUpper) && w.Length > 1 ? w : i == 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToLowerInvariant());
        return string.Join(" ", parts);
    }

    private static List<(string Name, string Value)> Facts(NotificationAlert alert)
        => alert.Facts.Where(f => f != null && !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => (f.Name.Trim(), f.Value.Trim())).ToList();

    private static List<NotificationAction> Links(NotificationAlert alert)
        => alert.Actions.Where(a => a != null && IsHttpUrl(a.Url))
            .Select(a => new NotificationAction { Type = a.Type, Url = a.Url, Title = string.IsNullOrWhiteSpace(a.Title) ? "Open in the portal" : a.Title.Trim() })
            .ToList();

    /// <summary>
    /// A JSON object becomes key/value rows (nested values stay compact JSON); anything else is
    /// kept as capped raw text. Rows are capped too, so a details object never turns into a wall.
    /// </summary>
    private static (List<(string Name, string Value)> Rows, string? Raw) PayloadRows(string? dataJson)
    {
        var rows = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(dataJson))
            return (rows, null);
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (rows.Count >= MaxPayloadRows) { rows.Add(("…", "further values omitted")); break; }
                    var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : prop.Value.GetRawText();
                    rows.Add((prop.Name, OneLine(value, 400)));
                }
                return (rows, null);
            }
        }
        catch (JsonException)
        {
            // Not JSON: shown as text below.
        }
        var raw = dataJson.Length > MaxPayloadChars ? dataJson[..MaxPayloadChars] + "\n… (truncated)" : dataJson;
        return (rows, raw);
    }

    private static void AppendTable(StringBuilder sb, List<(string Name, string Value)> rows)
    {
        sb.Append("<table style=\"border-collapse:collapse;margin:0 0 16px 0;\">");
        foreach (var (name, value) in rows)
        {
            sb.Append("<tr><td style=\"padding:3px 16px 3px 0;color:#6b7280;vertical-align:top;white-space:nowrap;\">").Append(E(name))
              .Append("</td><td style=\"padding:3px 0;\">").Append(E(value)).Append("</td></tr>");
        }
        sb.Append("</table>");
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
