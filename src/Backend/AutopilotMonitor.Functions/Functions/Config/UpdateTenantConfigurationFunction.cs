using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class UpdateTenantConfigurationFunction
    {
        private readonly ILogger<UpdateTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public UpdateTenantConfigurationFunction(
            ILogger<UpdateTenantConfigurationFunction> logger,
            TenantConfigurationService configService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _configService = configService;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("UpdateTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", "post", Route = "config/{tenantId}")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;

                _logger.LogInformation("UpdateTenantConfiguration: {TenantId} by user {User}", requestCtx.TargetTenantId, userIdentifier);

                // Parse request body
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576) // 1 MB limit
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return badRequest;
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var config = JsonConvert.DeserializeObject<TenantConfiguration>(requestBody);

                if (config == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Invalid configuration" });
                    return badRequest;
                }

                // Per-tenant rate-limit overrides are optional (null = inherit global), but if provided
                // they must be positive — a zero/negative override would throttle every request.
                var customLimitError =
                    config.CustomRateLimitRequestsPerMinute is int dev && dev < 1 ? "Device API Rate Limit" :
                    config.CustomUserRateLimitRequestsPerMinute is int usr && usr < 1 ? "User API Rate Limit" :
                    null;
                if (customLimitError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = $"{customLimitError} override must be at least 1 request per minute (or left blank to inherit the global default)." });
                    return badRequest;
                }

                // The contact address is where enforcement actions and service notices are sent,
                // so it must be an address rather than whatever string a direct API caller posts.
                var contactEmailError = ValidateContactEmail(config.ContactEmail);
                if (contactEmailError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = $"Invalid contact email: {contactEmailError}" });
                    return badRequest;
                }
                // Normalize so the stored value never carries surrounding whitespace, and an
                // all-whitespace submission clears the field instead of masquerading as a value.
                config.ContactEmail = string.IsNullOrWhiteSpace(config.ContactEmail) ? null : config.ContactEmail.Trim();

                // Load the stored config up-front so we can (1) restore any redacted secret placeholders
                // before validation/save and (2) protect GA-only fields below.
                var existingConfig = await _configService.GetConfigurationAsync(requestCtx.TargetTenantId);

                // Defense-in-depth: a read-only GlobalReader is served a redacted config (secrets replaced
                // with the ***REDACTED*** sentinel). If such a view is ever round-tripped back on a save,
                // never persist the placeholder — restore the real secret from the stored config. (Own-tenant
                // admins are served the FULL config so this is normally a no-op for them.)
                config.RestoreRedactedSecretsFrom(existingConfig);

                // Validate webhook URLs (SSRF protection)
                var webhookUrlError = SsrfGuard.ValidateWebhookUrlFormat(config.WebhookUrl);
                if (webhookUrlError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = $"Invalid Webhook URL: {webhookUrlError}" });
                    return badRequest;
                }
                var teamsUrlError = SsrfGuard.ValidateWebhookUrlFormat(config.TeamsWebhookUrl);
                if (teamsUrlError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = $"Invalid Teams Webhook URL: {teamsUrlError}" });
                    return badRequest;
                }
                var headersError = ValidateWebhookCustomHeaders(config.WebhookCustomHeadersJson);
                if (headersError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = $"Invalid custom headers: {headersError}" });
                    return badRequest;
                }
                // Per-channel counterpart of the two checks above: every channel's URL and custom
                // headers must pass the same format/SSRF gates as the legacy single-webhook fields.
                var channelsError = ValidateNotificationChannels(config.NotificationChannelsJson);
                if (channelsError != null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = $"Invalid notification channels: {channelsError}" });
                    return badRequest;
                }
                // Only validate the customer-supplied SAS URL when the tenant has actually
                // selected the CustomerSas destination. In Hosted mode the field is unused at
                // runtime (see GetDiagnosticsUploadUrlFunction), so a stale/legacy value left
                // over from a prior CustomerSas configuration must not block a Hosted save.
                // Mirrors the runtime branching in GetDiagnosticsUploadUrlFunction.Run.
                var diagDestination = AutopilotMonitor.Functions.Functions.Diagnostics.GetDiagnosticsUploadUrlFunction
                    .NormalizeDestination(config.DiagnosticsUploadDestination);
                if (diagDestination == AutopilotMonitor.Functions.Functions.Diagnostics.GetDiagnosticsUploadUrlFunction.DestinationCustomerSas)
                {
                    var diagSasError = SsrfGuard.ValidateAzureBlobSasUrlFormat(config.DiagnosticsBlobSasUrl);
                    if (diagSasError != null)
                    {
                        var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequest.WriteAsJsonAsync(new { success = false, message = $"Invalid Diagnostics SAS URL: {diagSasError}" });
                        return badRequest;
                    }
                }

                // Ensure tenant ID matches
                config.TenantId = requestCtx.TargetTenantId;

                // Set the actual user identifier for audit logging
                config.UpdatedBy = userIdentifier;

                // Protect GA-only fields from non-Global-Admin callers (existingConfig loaded above).
                if (!requestCtx.IsGlobalAdmin)
                {
                    if (config.AllowInsecureAgentRequests != existingConfig.AllowInsecureAgentRequests ||
                        config.BootstrapTokenEnabled != existingConfig.BootstrapTokenEnabled ||
                        config.UnrestrictedModeEnabled != existingConfig.UnrestrictedModeEnabled ||
                        config.EntraAppRolesEnabled != existingConfig.EntraAppRolesEnabled ||
                        config.CustomRateLimitRequestsPerMinute != existingConfig.CustomRateLimitRequestsPerMinute ||
                        config.CustomUserRateLimitRequestsPerMinute != existingConfig.CustomUserRateLimitRequestsPerMinute ||
                        config.Disabled != existingConfig.Disabled ||
                        config.ValidateDeviceAssociation != existingConfig.ValidateDeviceAssociation)
                    {
                        _logger.LogWarning(
                            "Tenant Admin {User} attempted to modify GA-only fields for tenant {TenantId}",
                            userIdentifier, requestCtx.TargetTenantId);
                    }

                    config.AllowInsecureAgentRequests = existingConfig.AllowInsecureAgentRequests;
                    config.BootstrapTokenEnabled = existingConfig.BootstrapTokenEnabled;
                    config.UnrestrictedModeEnabled = existingConfig.UnrestrictedModeEnabled;
                    config.EntraAppRolesEnabled = existingConfig.EntraAppRolesEnabled;
                    config.CustomRateLimitRequestsPerMinute = existingConfig.CustomRateLimitRequestsPerMinute;
                    config.CustomUserRateLimitRequestsPerMinute = existingConfig.CustomUserRateLimitRequestsPerMinute;
                    config.Disabled = existingConfig.Disabled;
                    config.DisabledReason = existingConfig.DisabledReason;
                    config.DisabledUntil = existingConfig.DisabledUntil;
                    // DevPrep "Device association" toggle is GA-only during Private Preview.
                    // TODO(devprep-followup): missing xUnit test for this GA-gate — needs
                    // UpdateTenantConfigurationFunction test harness (mock HttpRequestData +
                    // RequestContext + TenantConfigurationService). Tracked in
                    // memory/project_devprep_followups.md.
                    config.ValidateDeviceAssociation = existingConfig.ValidateDeviceAssociation;
                }

                // Safety: if GA gate is off, force UnrestrictedMode to false
                if (!config.UnrestrictedModeEnabled)
                {
                    config.UnrestrictedMode = false;
                }

                // MaxNdjsonPayloadSizeMB is table-only — always preserve existing value
                config.MaxNdjsonPayloadSizeMB = existingConfig.MaxNdjsonPayloadSizeMB;

                // Plan/trial fields are mutable ONLY via the dedicated plan/trial endpoints
                // (PATCH config/{tenantId}/plan, POST config/{tenantId}/trial). The generic PUT
                // deserializes the full model, so without this preserve a round-tripped stale
                // view would silently reset the tenant's edition/trial — for ALL callers, GA included.
                config.PlanTier = existingConfig.PlanTier;
                config.TrialExpiresUtc = existingConfig.TrialExpiresUtc;
                config.TrialStartedUtc = existingConfig.TrialStartedUtc;
                config.TrialConsumed = existingConfig.TrialConsumed;
                config.TrialGrantedBy = existingConfig.TrialGrantedBy;

                // Retention cap (edition entitlement): non-GA callers may only set 7..cap days.
                // Enforced only when the caller actually CHANGED the value, so a tenant whose
                // stored value predates the cap (e.g. 180 on Community) can still save unrelated
                // settings. 0 (= infinite) is a GA-only escape hatch. Edition resolves from the
                // STORED config — the client-sent plan fields were just discarded above.
                if (!requestCtx.IsGlobalAdmin && config.DataRetentionDays != existingConfig.DataRetentionDays)
                {
                    var cap = FeatureEntitlementCatalog
                        .Get(TenantEntitlementService.ResolveEdition(existingConfig, DateTime.UtcNow))
                        .RetentionCapDays;
                    if (config.DataRetentionDays < 7 || config.DataRetentionDays > cap)
                    {
                        var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequest.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Data retention must be between 7 and {cap} days for your plan. Upgrade to Enterprise for up to 365 days."
                        });
                        return badRequest;
                    }
                }

                // Save configuration
                await _configService.SaveConfigurationAsync(config);

                var changes = ConfigDiffHelper.GetChanges(existingConfig, config);
                await _maintenanceRepo.LogAuditEntryAsync(
                    requestCtx.TargetTenantId,
                    "UPDATE",
                    "TenantConfiguration",
                    requestCtx.TargetTenantId,
                    userIdentifier,
                    changes.Count > 0 ? changes : null
                );

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Configuration updated successfully",
                    config = config
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating configuration for tenant {tenantId}");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        private const int MaxCustomHeadersJsonLength = 8192;
        private const int MaxCustomHeaderCount = 25;
        private const int MaxNotificationChannelsJsonLength = 65536;

        // RFC 5321 caps a forward path at 254 characters.
        internal const int MaxContactEmailLength = 254;

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
