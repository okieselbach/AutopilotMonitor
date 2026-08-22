using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Sends the product's transactional emails (tenant-activation welcome, post-offboarding
/// farewell). Best-effort: failures are logged as warnings and never propagated.
/// <para>
/// The class and its callers are provider-neutral. Everything provider-specific is confined
/// to the <c>Email:*</c> configuration section and <see cref="SendViaMandrillAsync"/>, so a
/// future provider swap touches the transport method and the app settings only.
/// Current provider: Mailchimp Transactional (Mandrill) — <c>POST {Email:Endpoint}</c> with the
/// API key in the JSON body. Open/click tracking is explicitly disabled so the provider
/// receives nothing beyond the recipient address and the tenant domain (trust-page claim).
/// </para>
/// <para>
/// Configuration (App Settings use <c>Email__Key</c>):
/// <c>Email:ApiKey</c> (required; empty ⇒ every send is a logged no-op),
/// <c>Email:Endpoint</c> (default <see cref="DefaultEndpoint"/>),
/// <c>Email:FromAddress</c> (default <see cref="DefaultFromAddress"/>),
/// <c>Email:FromName</c> (default <see cref="DefaultFromName"/>).
/// </para>
/// </summary>
public class EmailService : IEmailService, IOffboardFarewellEmailSender
{
    public const string ApiKeyConfigKey = "Email:ApiKey";
    public const string DefaultEndpoint = "https://mandrillapp.com/api/1.0/messages/send";
    public const string DefaultFromAddress = "noreply@autopilotmonitor.com";
    public const string DefaultFromName = "Autopilot Monitor";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IEmailTemplateProvider _templates;
    private readonly ILogger<EmailService> _logger;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _fromAddress;
    private readonly string _fromName;

    public EmailService(
        HttpClient http,
        IConfiguration configuration,
        IEmailTemplateProvider templates,
        ILogger<EmailService> logger)
    {
        _http = http;
        _templates = templates;
        _logger = logger;
        _apiKey = configuration[ApiKeyConfigKey] ?? string.Empty;
        _endpoint = FirstNonBlank(configuration["Email:Endpoint"], DefaultEndpoint);
        _fromAddress = FirstNonBlank(configuration["Email:FromAddress"], DefaultFromAddress);
        _fromName = FirstNonBlank(configuration["Email:FromName"], DefaultFromName);
    }

    /// <inheritdoc />
    public async Task SendPreviewApprovedEmailAsync(string toEmail, string domainName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug("{ConfigKey} not configured — skipping preview approval email", ApiKeyConfigKey);
            return;
        }

        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogDebug("No notification email set — skipping preview approval email for {Domain}", domainName);
            return;
        }

        var sent = await SendViaMandrillAsync(
            toEmail,
            EmailTemplates.PreviewApprovedSubject,
            await _templates.GetHtmlAsync(EmailTemplateKind.Welcome, domainName, ct),
            tag: "welcome",
            ct);

        if (sent)
        {
            _logger.LogInformation(
                "Preview approval email sent to {ToEmail} for domain {Domain}",
                toEmail, domainName);
        }
    }

    /// <summary>
    /// Sends the post-offboarding "sorry to see you go" farewell email.
    /// No-op if the API key or recipient email is not configured. Best-effort: failures
    /// are logged as warnings and never propagated (the offboarding correctness contract
    /// does not depend on email delivery).
    /// </summary>
    public async Task SendAsync(string toEmail, string domainName, string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug(
                "{ConfigKey} not configured — skipping offboard farewell email for tenant {TenantId}",
                ApiKeyConfigKey, tenantId);
            return;
        }

        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogDebug(
                "No notification email captured — skipping offboard farewell email for tenant {TenantId} ({Domain})",
                tenantId, domainName);
            return;
        }

        var sent = await SendViaMandrillAsync(
            toEmail,
            EmailTemplates.OffboardingFarewellSubject,
            await _templates.GetHtmlAsync(EmailTemplateKind.Farewell, domainName, ct),
            tag: "offboarding-farewell",
            ct);

        if (sent)
        {
            _logger.LogInformation(
                "Offboard farewell email sent to {ToEmail} for tenant {TenantId} ({Domain})",
                toEmail, tenantId, domainName);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendTestAsync(EmailTemplateKind kind, string toEmail, string domainName, string? draftHtml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("{ConfigKey} not configured — cannot send {Kind} test email", ApiKeyConfigKey, kind);
            return false;
        }

        var html = draftHtml is null
            ? await _templates.GetHtmlAsync(kind, domainName, ct)
            : EmailTemplateService.Render(draftHtml, domainName);

        var sent = await SendViaMandrillAsync(toEmail, EmailTemplateService.Subject(kind), html, tag: "test", ct);
        if (sent)
            _logger.LogInformation("{Kind} test email sent to {ToEmail} (draft={IsDraft})", kind, toEmail, draftHtml is not null);
        return sent;
    }

    /// <summary>
    /// The only provider-specific code path. Posts a Mandrill <c>messages/send</c> request and
    /// interprets the per-recipient result array: <c>sent</c>/<c>queued</c>/<c>scheduled</c>
    /// count as success, <c>rejected</c>/<c>invalid</c> as failure (with the provider's
    /// <c>reject_reason</c> in the warning). Never throws.
    /// </summary>
    private async Task<bool> SendViaMandrillAsync(string toEmail, string subject, string html, string tag, CancellationToken ct)
    {
        try
        {
            var request = new MandrillSendRequest
            {
                Key = _apiKey,
                Message = new MandrillMessage
                {
                    FromEmail = _fromAddress,
                    FromName = _fromName,
                    To = new[] { new MandrillRecipient { Email = toEmail, Type = "to" } },
                    Subject = subject,
                    Html = html,
                    AutoText = true,
                    TrackOpens = false,
                    TrackClicks = false,
                    Tags = new[] { tag },
                },
            };

            using var response = await _http.PostAsJsonAsync(_endpoint, request, JsonOptions, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Email provider returned {StatusCode} for {Tag} mail to {ToEmail}: {Body}",
                    (int)response.StatusCode, tag, toEmail, Truncate(body));
                return false;
            }

            var results = JsonSerializer.Deserialize<MandrillSendResult[]>(body, JsonOptions);
            var result = results?.FirstOrDefault();
            if (result is null)
            {
                _logger.LogWarning(
                    "Email provider returned an empty result for {Tag} mail to {ToEmail}: {Body}",
                    tag, toEmail, Truncate(body));
                return false;
            }

            if (!IsAcceptedStatus(result.Status))
            {
                _logger.LogWarning(
                    "Email provider did not accept {Tag} mail to {ToEmail}: status={Status} reason={Reason}",
                    tag, toEmail, result.Status, result.RejectReason ?? "n/a");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Tag} mail to {ToEmail}", tag, toEmail);
            return false;
        }
    }

    private static bool IsAcceptedStatus(string? status)
        => status is "sent" or "queued" or "scheduled";

    private static string FirstNonBlank(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500] + "…";

    // --- Mandrill wire format (https://mailchimp.com/developer/transactional/api/messages/send-new-message/) ---

    private sealed class MandrillSendRequest
    {
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("message")] public MandrillMessage Message { get; set; } = new();
    }

    private sealed class MandrillMessage
    {
        [JsonPropertyName("from_email")] public string FromEmail { get; set; } = string.Empty;
        [JsonPropertyName("from_name")] public string FromName { get; set; } = string.Empty;
        [JsonPropertyName("to")] public MandrillRecipient[] To { get; set; } = Array.Empty<MandrillRecipient>();
        [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;
        [JsonPropertyName("html")] public string Html { get; set; } = string.Empty;
        [JsonPropertyName("auto_text")] public bool AutoText { get; set; }
        [JsonPropertyName("track_opens")] public bool TrackOpens { get; set; }
        [JsonPropertyName("track_clicks")] public bool TrackClicks { get; set; }
        [JsonPropertyName("tags")] public string[] Tags { get; set; } = Array.Empty<string>();
    }

    private sealed class MandrillRecipient
    {
        [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = "to";
    }

    private sealed class MandrillSendResult
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reject_reason")] public string? RejectReason { get; set; }
        [JsonPropertyName("_id")] public string? Id { get; set; }
    }
}
