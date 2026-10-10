using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Push;

/// <summary>What one push carries after projection — the plaintext before encryption.</summary>
public sealed class PushPayload
{
    public string Id { get; init; } = default!;
    public string Type { get; init; } = default!;
    public string Title { get; init; } = default!;
    public string Body { get; init; } = default!;
    /// <summary>error · warning · success · info (the receiver's accent).</summary>
    public string Severity { get; init; } = "info";
    public List<(string Name, string Value)> Facts { get; init; } = new();
    public string? PortalUrl { get; init; }
    /// <summary>Notification tag: same tag = the browser replaces the earlier one where it honours tags.</summary>
    public string Tag { get; init; } = default!;
    public string Scope { get; init; } = "tenant";
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// The lock-screen projection (Korrektur K23): a push carries only what may stand on a lock
/// screen and in an unmanaged phone's history. It reads <see cref="NotificationAlert.Title"/>,
/// <see cref="NotificationAlert.Summary"/>, the allow-listed <see cref="NotificationAlert.Facts"/>,
/// <see cref="NotificationAlert.Severity"/>, <see cref="NotificationAlert.EventType"/> and the
/// portal deep link in <see cref="NotificationAlert.Actions"/> — never <c>Sections</c> (rule
/// explanations interpolated from log lines), never <c>DataJson</c>, never the "Failure Reason"
/// fact (the agent's event message, telemetry free text), and for the platform scope never the
/// ops event's message (it embeds UPNs and mail addresses). A test pins the members read.
/// </summary>
public static class PushAlertProjector
{
    /// <summary>Plaintext budget: well under RFC 8291's 3993 bytes and the ~2.7 KB of bridged routes.</summary>
    public const int MaxPayloadBytes = 2048;
    public const int MaxFactLength = 64;
    public const int MaxTitleLength = 80;
    public const int MaxBodyLength = 160;

    /// <summary>
    /// Fact names a tenant-scope push may carry. "Failure Reason" AND "Last Failure" (the same agent
    /// event message under the consecutive-failures alert's name) are deliberately absent.
    /// </summary>
    public static readonly IReadOnlySet<string> TenantFactAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Device", "Serial", "Hardware", "Manufacturer", "Model", "Duration", "Rule", "Severity", "Category",
        "Confidence", "Breach Type", "Current Rate", "Target Rate", "Current P95", "Target Max", "Current",
        "Target", "Total Sessions", "Failed Sessions", "Period", "Consecutive Failures",
        "Last Device", "Active For", "Started At", "Tenant", "Top Failing App", "Data Quality",
    };

    /// <summary>Fact names a platform-scope (ops) push may carry — the baseline only, payload facts never.</summary>
    public static readonly IReadOnlySet<string> PlatformFactAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Category", "Event", "Severity", "Tenant",
    };

    private const string HardwareRejectedEventType = "hardware_rejected";

    public static PushPayload Project(NotificationAlert alert, NotificationScope scope, string entryId, DateTime nowUtc)
    {
        var eventType = string.IsNullOrWhiteSpace(alert.EventType) ? "alert" : alert.EventType!;
        var portalUrl = FindPortalUrl(alert);
        var allow = scope.IsPlatform ? PlatformFactAllowList : TenantFactAllowList;

        var facts = new List<(string, string)>();
        foreach (var fact in alert.Facts)
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.Name) || !allow.Contains(fact.Name))
                continue;

            var value = string.Equals(fact.Name, "Serial", StringComparison.OrdinalIgnoreCase)
                ? MaskSerial(fact.Value)
                : Clean(fact.Value, MaxFactLength);
            if (value.Length == 0)
                continue;

            facts.Add((fact.Name, value));
        }

        string title;
        string body;
        if (scope.IsPlatform)
        {
            // The ops message is the alert's Summary: category/event/severity is all a lock screen gets.
            var category = facts.FirstOrDefault(f => f.Item1.Equals("Category", StringComparison.OrdinalIgnoreCase)).Item2;
            var severity = facts.FirstOrDefault(f => f.Item1.Equals("Severity", StringComparison.OrdinalIgnoreCase)).Item2;
            title = "Ops alert";
            body = Clean(string.IsNullOrEmpty(category) ? eventType : $"{category}/{eventType}", MaxBodyLength)
                   + (string.IsNullOrEmpty(severity) ? string.Empty : $" · {severity}");
        }
        else if (string.Equals(eventType, HardwareRejectedEventType, StringComparison.OrdinalIgnoreCase))
        {
            // Pre-auth distress strings are whoever-sent-them text; a fixed template keeps the
            // lock screen free of them, the portal keeps the full strings.
            var serial = facts.FirstOrDefault(f => f.Item1.Equals("Serial", StringComparison.OrdinalIgnoreCase)).Item2;
            title = "Hardware rejected";
            body = string.IsNullOrEmpty(serial) ? "A device outside the hardware whitelist tried to enroll." : $"Serial {serial} is not in the hardware whitelist.";
            facts.RemoveAll(f => !f.Item1.Equals("Serial", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            title = Clean(alert.Title, MaxTitleLength);
            body = Clean(alert.Summary, MaxBodyLength);
        }

        if (title.Length == 0)
            title = "Autopilot Monitor";

        return new PushPayload
        {
            Id = entryId,
            Type = eventType,
            Title = title,
            Body = body,
            Severity = alert.Severity switch
            {
                NotificationSeverity.Error => "error",
                NotificationSeverity.Warning => "warning",
                NotificationSeverity.Success => "success",
                _ => "info",
            },
            Facts = facts,
            PortalUrl = portalUrl,
            Tag = BuildTag(portalUrl, entryId),
            Scope = scope.IsPlatform ? "platform" : "tenant",
            TimestampUtc = nowUtc,
        };
    }

    /// <summary>
    /// A system message to one device (test, paired, revoked, muted, paused, session watch). Its
    /// tag is its id: a system message never replaces an earlier notification where the browser
    /// honours tags, so two watched sessions finishing minutes apart stay two notifications
    /// (alerts keep the per-session tag, so a later verdict for the same session replaces the
    /// earlier one on the lock screen; the history keeps every push either way, keyed by id).
    /// </summary>
    public static PushPayload SystemMessage(string type, string title, string body, string severity, NotificationScope scope, DateTime nowUtc, string? portalUrl = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return new()
        {
            Id = id,
            Type = type,
            Title = Clean(title, MaxTitleLength),
            Body = Clean(body, MaxBodyLength),
            Severity = severity,
            PortalUrl = portalUrl,
            Tag = id,
            Scope = scope.IsPlatform ? "platform" : "tenant",
            TimestampUtc = nowUtc,
        };
    }

    /// <summary>
    /// Declarative Web Push JSON (<c>web_push: 8030</c>): Safari ≥ 18.4 shows it without the
    /// service worker when the worker fails; <c>mutable</c> wakes the worker so it can store the
    /// history entry; every other browser hands the same object to the worker's push event.
    /// Over budget, facts are dropped from the end, then the body is shortened.
    /// </summary>
    public static byte[] ToJsonBytes(PushPayload payload)
    {
        var facts = new List<(string Name, string Value)>(payload.Facts);
        var body = payload.Body;
        while (true)
        {
            var bytes = Serialize(payload, facts, body);
            if (bytes.Length <= MaxPayloadBytes)
                return bytes;
            if (facts.Count > 0)
                facts.RemoveAt(facts.Count - 1);
            else if (body.Length > 32)
                body = body.Substring(0, body.Length / 2);
            else
                return bytes;
        }
    }

    private static byte[] Serialize(PushPayload p, List<(string Name, string Value)> facts, string body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("web_push", 8030);
            w.WriteBoolean("mutable", true);
            w.WriteStartObject("notification");
            w.WriteString("title", p.Title);
            w.WriteString("body", body);
            w.WriteString("navigate", Constants.PortalPushHistoryUrl(p.Id));
            w.WriteString("tag", p.Tag);
            w.WriteStartObject("data");
            w.WriteNumber("v", 1);
            w.WriteString("id", p.Id);
            w.WriteString("type", p.Type);
            w.WriteString("ts", p.TimestampUtc.ToString("O"));
            w.WriteString("severity", p.Severity);
            w.WriteStartArray("facts");
            foreach (var (name, value) in facts)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("value", value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (p.PortalUrl != null)
                w.WriteString("portalUrl", p.PortalUrl);
            else
                w.WriteNull("portalUrl");
            w.WriteString("scope", p.Scope);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Last four characters only: the full serial is the Progress Portal's access proof (F13).</summary>
    public static string MaskSerial(string? serial)
    {
        var s = Clean(serial, 64);
        if (s.Length == 0 || s == "–" || s == "-")
            return string.Empty;
        return s.Length <= 4 ? "…" + s : "…" + s.Substring(s.Length - 4);
    }

    /// <summary>Control and format characters out, whitespace collapsed, hard length cap.</summary>
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch) || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format)
                continue;
            if (char.IsWhiteSpace(ch))
            {
                if (lastWasSpace) continue;
                sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(ch);
            lastWasSpace = false;
        }

        // A removed trailing control character can leave one collapsed space behind.
        var result = sb.ToString().TrimEnd();
        return result.Length > maxLength ? result.Substring(0, maxLength - 1).TrimEnd() + "…" : result;
    }

    private static string? FindPortalUrl(NotificationAlert alert)
    {
        foreach (var action in alert.Actions)
        {
            if (action?.Url != null && action.Url.StartsWith(Constants.PortalBaseUrl, StringComparison.OrdinalIgnoreCase))
                return action.Url;
        }
        return null;
    }

    /// <summary>
    /// Session-bound alerts collapse per session where the browser honours tags (best effort,
    /// K21); everything else is its own notification.
    /// </summary>
    private static string BuildTag(string? portalUrl, string entryId)
    {
        if (portalUrl == null || !portalUrl.Contains("id=", StringComparison.Ordinal))
            return entryId;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(portalUrl));
        return "s-" + Convert.ToHexString(digest, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// The push-service Topic for a session-bound alert: 24 HMAC bytes = exactly 32 base64url
    /// characters (Apple's cap), keyed with the platform's signing key so the push services see
    /// an opaque value, never a session id. Null without a session or a key.
    /// </summary>
    public static string? BuildTopic(string? portalUrl, byte[]? hmacKey)
    {
        if (portalUrl == null || hmacKey == null || hmacKey.Length == 0 || !portalUrl.Contains("id=", StringComparison.Ordinal))
            return null;
        var mac = HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(portalUrl));
        return System.Buffers.Text.Base64Url.EncodeToString(mac.AsSpan(0, 24));
    }
}
