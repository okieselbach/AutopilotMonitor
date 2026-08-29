using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Config;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Single source for tenant-configuration model validation, shared by the full-model
    /// PUT (<c>UpdateTenantConfigurationFunction</c>) and the transactional field-patch
    /// flow (<c>TenantConfigPatchService</c>). The individual validators moved here from
    /// the PUT function; thin forwarding shims remain there for existing callers/tests.
    /// </summary>
    internal static class TenantConfigValidation
    {
        private const int MaxCustomHeadersJsonLength = 8192;
        private const int MaxCustomHeaderCount = 25;
        private const int MaxNotificationChannelsJsonLength = 65536;

        // RFC 5321 caps a forward path at 254 characters.
        internal const int MaxContactEmailLength = 254;

        // RFC 1035: a host name is at most 253 characters, labels at most 63.
        internal const int MaxDomainNameLength = 253;

        /// <summary>
        /// True when <paramref name="domainName"/> is a strict DNS host name: dotted labels of
        /// ASCII letters, digits and inner hyphens only. This is the ONLY shape DomainName may
        /// ever be persisted in — it is rendered into transactional mails and admin views.
        /// </summary>
        internal static bool IsValidDomainName(string? domainName)
        {
            if (string.IsNullOrEmpty(domainName) || domainName.Length > MaxDomainNameLength)
                return false;

            var labels = domainName.Split('.');
            if (labels.Length < 2)
                return false;

            foreach (var label in labels)
            {
                if (label.Length == 0 || label.Length > 63)
                    return false;
                if (label[0] == '-' || label[label.Length - 1] == '-')
                    return false;
                foreach (var ch in label)
                {
                    var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-';
                    if (!ok)
                        return false;
                }
            }

            return true;
        }

        private const int MaxWhitelistEntries = 200;
        private const int MaxWhitelistEntryLength = 128;

        /// <summary>
        /// Validates a hardware whitelist CSV (<see cref="TenantConfiguration.ManufacturerWhitelist"/> /
        /// <see cref="TenantConfiguration.ModelWhitelist"/>). The CSV is split on ',' at enforcement
        /// time, so this is the last line of defence against a value that was meant as ONE entry but
        /// alters the list structure: blank items (",,", ", ,") are rejected rather than silently
        /// dropped, and every item must be a printable, bounded pattern. Returns a user-facing
        /// reason, or null when the list is well-formed. Empty/whitespace = allow all (documented).
        /// </summary>
        internal static string? ValidateHardwareWhitelist(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return null;

            var items = csv!.Split(',');
            if (items.Length > MaxWhitelistEntries)
                return $"at most {MaxWhitelistEntries} entries are allowed.";

            foreach (var raw in items)
            {
                var entry = raw.Trim();
                if (entry.Length == 0)
                    return "entries must not be empty (check for a stray or doubled comma).";
                if (entry.Length > MaxWhitelistEntryLength)
                    return $"each entry must be at most {MaxWhitelistEntryLength} characters.";
                foreach (var ch in entry)
                {
                    if (char.IsControl(ch))
                        return "entries must not contain control characters.";
                }
            }

            return null;
        }

        // Generous for a CDN asset URL; bounds the command line the agent composes from it.
        internal const int MaxBrandingImageUrlLength = 2048;

        /// <summary>
        /// Validates <see cref="TenantConfiguration.EnrollmentSummaryBrandingImageUrl"/>. The value
        /// is delivered verbatim to every enrolling device, fetched from the signed-in user's
        /// session and spliced into the summary-dialog command line, so it is the one piece of
        /// tenant config that is both a device egress target and a process argument. It must be a
        /// plain absolute HTTPS URL to a DNS host — no quotes, whitespace or control characters
        /// that could terminate a quoted argument. Returns an error message, or null when
        /// valid/empty (empty = no branding image).
        /// </summary>
        internal static string? ValidateBrandingImageUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (url!.Length > MaxBrandingImageUrlLength)
                return $"must be at most {MaxBrandingImageUrlLength} characters.";

            foreach (var ch in url)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                    return "must not contain whitespace or control characters.";
                if (ch == '"' || ch == '\'')
                    return "must not contain quotes.";
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "is not a valid absolute URL.";

            if (uri.Scheme != Uri.UriSchemeHttps)
                return "must use HTTPS.";

            if (uri.IsLoopback || string.IsNullOrEmpty(uri.Host))
                return "must not target localhost.";

            if (System.Net.IPAddress.TryParse(uri.Host, out _))
                return "must use a DNS hostname, not an IP address.";

            return null;
        }

        /// <summary>
        /// Validates a candidate configuration against the stored one. Returns a
        /// user-facing error message (same wording the PUT endpoint always produced),
        /// or null when the candidate is saveable. Does NOT mutate either argument.
        /// </summary>
        internal static string? ValidateModel(
            TenantConfiguration candidate, TenantConfiguration existing, bool isGlobalAdmin)
        {
            // Per-tenant rate-limit overrides are optional (null = inherit global), but if provided
            // they must be positive — a zero/negative override would throttle every request.
            var customLimitError =
                candidate.CustomRateLimitRequestsPerMinute is int dev && dev < 1 ? "Device API Rate Limit" :
                candidate.CustomUserRateLimitRequestsPerMinute is int usr && usr < 1 ? "User API Rate Limit" :
                null;
            if (customLimitError != null)
                return $"{customLimitError} override must be at least 1 request per minute (or left blank to inherit the global default).";

            var contactEmailError = ValidateContactEmail(candidate.ContactEmail);
            if (contactEmailError != null)
                return $"Invalid contact email: {contactEmailError}";

            var webhookUrlError = SsrfGuard.ValidateWebhookUrlFormat(candidate.WebhookUrl);
            if (webhookUrlError != null)
                return $"Invalid Webhook URL: {webhookUrlError}";

            var teamsUrlError = SsrfGuard.ValidateWebhookUrlFormat(candidate.TeamsWebhookUrl);
            if (teamsUrlError != null)
                return $"Invalid Teams Webhook URL: {teamsUrlError}";

            var headersError = ValidateWebhookCustomHeaders(candidate.WebhookCustomHeadersJson);
            if (headersError != null)
                return $"Invalid custom headers: {headersError}";

            var channelsError = ValidateNotificationChannels(candidate.NotificationChannelsJson);
            if (channelsError != null)
                return $"Invalid notification channels: {channelsError}";

            var brandingUrlError = ValidateBrandingImageUrl(candidate.EnrollmentSummaryBrandingImageUrl);
            if (brandingUrlError != null)
                return $"Invalid enrollment summary branding image URL: {brandingUrlError}";

            var manufacturerWhitelistError = ValidateHardwareWhitelist(candidate.ManufacturerWhitelist);
            if (manufacturerWhitelistError != null)
                return $"Invalid manufacturer whitelist: {manufacturerWhitelistError}";

            var modelWhitelistError = ValidateHardwareWhitelist(candidate.ModelWhitelist);
            if (modelWhitelistError != null)
                return $"Invalid model whitelist: {modelWhitelistError}";

            // Only validate the customer-supplied SAS URL when the tenant has actually selected
            // the CustomerSas destination — a stale value left over from a prior CustomerSas
            // configuration must not block a Hosted save (mirrors GetDiagnosticsUploadUrlFunction).
            var diagDestination = Functions.Diagnostics.GetDiagnosticsUploadUrlFunction
                .NormalizeDestination(candidate.DiagnosticsUploadDestination);
            if (diagDestination == Functions.Diagnostics.GetDiagnosticsUploadUrlFunction.DestinationCustomerSas)
            {
                var diagSasError = SsrfGuard.ValidateAzureBlobSasUrlFormat(candidate.DiagnosticsBlobSasUrl);
                if (diagSasError != null)
                    return $"Invalid Diagnostics SAS URL: {diagSasError}";
            }

            // Retention cap (edition entitlement): non-GA callers may only set 7..cap days, and
            // only when they actually CHANGED the value — a stored value predating the cap must
            // not block unrelated saves. 0 (= infinite) is a GA-only escape hatch. Edition
            // resolves from the STORED config.
            if (!isGlobalAdmin && candidate.DataRetentionDays != existing.DataRetentionDays)
            {
                var cap = FeatureEntitlementCatalog
                    .Get(TenantEntitlementService.ResolveEdition(existing, DateTime.UtcNow))
                    .RetentionCapDays;
                if (candidate.DataRetentionDays < 7 || candidate.DataRetentionDays > cap)
                    return $"Data retention must be between 7 and {cap} days for your plan. Upgrade to Pro for up to 365 days.";
            }

            return null;
        }

        /// <summary>
        /// Validates the tenant contact address. Returns an error message, or null when valid/empty.
        /// Empty is legitimate — it means we have no way to reach the tenant.
        /// <para>
        /// Deliberately not an RFC 5322 parser: the job is to reject values that are not addresses
        /// at all. Specifically it rejects recipient lists (a comma would silently widen who receives
        /// service notices), display-name forms, and control characters (which would let a caller
        /// forge mail headers once this address is actually mailed).
        /// </para>
        /// </summary>
        internal static string? ValidateContactEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return null;

            var trimmed = email.Trim();

            if (trimmed.Length > MaxContactEmailLength)
                return $"must be at most {MaxContactEmailLength} characters.";

            foreach (var ch in trimmed)
            {
                if (char.IsControl(ch))
                    return "must not contain control characters.";
                if (char.IsWhiteSpace(ch) || ch == ',' || ch == ';' || ch == '<' || ch == '>')
                    return "must be a single address, without spaces, separators or angle brackets.";
            }

            var at = trimmed.IndexOf('@');
            if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
                return "must contain a single \"@\" with text on both sides.";

            // A bare host with no dot is unreachable from our sender, so it is a typo, not an address.
            var domain = trimmed.Substring(at + 1);
            if (!domain.Contains('.') || domain.StartsWith(".", StringComparison.Ordinal)
                || domain.EndsWith(".", StringComparison.Ordinal))
            {
                return "the domain part must be a dotted host name.";
            }

            return null;
        }

        /// <summary>
        /// Validates the notification-channel list JSON. Returns an error message, or null when
        /// valid/empty. Strict counterpart of the fail-soft <c>NotificationChannel.ParseList</c>:
        /// entries the parser would silently drop (missing id, unknown provider) are rejected here
        /// so a tenant admin gets feedback instead of a channel that never fires. Each channel's
        /// URL and custom headers pass the same gates as the legacy single-webhook fields.
        /// </summary>
        internal static string? ValidateNotificationChannels(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            if (json.Length > MaxNotificationChannelsJsonLength)
                return $"too large (max {MaxNotificationChannelsJsonLength} characters).";

            List<Shared.Models.Notifications.NotificationChannel>? channels;
            try
            {
                channels = System.Text.Json.JsonSerializer.Deserialize<List<Shared.Models.Notifications.NotificationChannel>>(
                    json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException)
            {
                return "not valid JSON.";
            }

            if (channels == null)
                return "must be a JSON array of channels.";

            if (channels.Count > Shared.Models.Notifications.NotificationChannel.MaxChannelsPerTenant)
                return $"too many channels (max {Shared.Models.Notifications.NotificationChannel.MaxChannelsPerTenant}).";

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in channels)
            {
                if (channel == null || string.IsNullOrWhiteSpace(channel.Id))
                    return "every channel needs an id.";
                if (!ids.Add(channel.Id))
                    return $"duplicate channel id \"{channel.Id}\".";

                var label = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name;

                if (!Enum.IsDefined(typeof(Shared.Models.Notifications.WebhookProviderType), channel.ProviderType)
                    || channel.ProviderType == (int)Shared.Models.Notifications.WebhookProviderType.None)
                    return $"channel \"{label}\" has an invalid provider type.";

                var urlError = SsrfGuard.ValidateWebhookUrlFormat(channel.Url);
                if (urlError != null)
                    return $"channel \"{label}\": {urlError}";

                var headerError = ValidateWebhookCustomHeaders(channel.CustomHeadersJson);
                if (headerError != null)
                    return $"channel \"{label}\" headers: {headerError}";

                var secretError = ValidateWebhookSigningSecret(channel.SigningSecret);
                if (secretError != null)
                    return $"channel \"{label}\" signing secret: {secretError}";
            }

            return null;
        }

        /// <summary>
        /// Validates a generic-webhook HMAC signing secret. Returns an error message, or null when
        /// valid/absent. A short key is brute-forceable as an HMAC key; the printable-ASCII rule
        /// keeps the secret copy-paste safe and (as a side effect) rejects a stray unrestored
        /// redaction sentinel, which is shorter than the minimum. No provider gate here — a secret
        /// on a non-generic channel is stored but inert at dispatch (GetSigningSecret), matching
        /// how CustomHeadersJson behaves.
        /// </summary>
        internal static string? ValidateWebhookSigningSecret(string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret))
                return null;

            if (secret.Length < 16 || secret.Length > 128)
                return "must be 16-128 characters.";

            foreach (var ch in secret)
            {
                if (ch < 0x21 || ch > 0x7E)
                    return "must be printable ASCII without spaces or line breaks.";
            }

            return null;
        }

        /// <summary>
        /// Validates the generic-webhook custom-headers JSON. Returns an error message, or null when
        /// valid/empty. Enforces a JSON object of string values, valid HTTP token names, no CR/LF
        /// header-injection, and size caps. Restricted (framing/host/content) headers are not rejected
        /// here — they are silently ignored at dispatch by TenantConfiguration.GetGenericWebhookHeaders().
        /// </summary>
        internal static string? ValidateWebhookCustomHeaders(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            if (json.Length > MaxCustomHeadersJsonLength)
                return $"too large (max {MaxCustomHeadersJsonLength} characters).";

            System.Text.Json.JsonDocument doc;
            try
            {
                doc = System.Text.Json.JsonDocument.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                return "not valid JSON.";
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return "must be a JSON object of header name/value pairs.";

                var count = 0;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (++count > MaxCustomHeaderCount)
                        return $"too many headers (max {MaxCustomHeaderCount}).";

                    if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                        return $"header \"{prop.Name}\" must have a string value.";

                    if (!IsValidHeaderName(prop.Name))
                        return $"\"{prop.Name}\" is not a valid HTTP header name.";

                    var value = prop.Value.GetString();
                    if (value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0))
                        return $"value for \"{prop.Name}\" must not contain line breaks.";
                }
            }

            return null;
        }

        /// <summary>Validates an HTTP header name as an RFC 7230 token (no whitespace, controls, or separators).</summary>
        private static bool IsValidHeaderName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (var ch in name)
            {
                var isTokenChar =
                    (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') ||
                    "!#$%&'*+-.^_`|~".IndexOf(ch) >= 0;
                if (!isTokenChar)
                    return false;
            }

            return true;
        }
    }
}
