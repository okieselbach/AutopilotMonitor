using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Global-Admin management of the two transactional email templates (welcome, farewell):
/// read the effective template, store/reset an HTML override, and send a test mail.
/// All routes are GlobalAdminOnly via <c>EndpointAccessPolicyCatalog</c>.
/// </summary>
public class EmailTemplatesFunction
{
    private readonly ILogger<EmailTemplatesFunction> _logger;
    private readonly EmailTemplateService _templates;
    private readonly IEmailService _email;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly TenantConfigurationService _tenantConfigurationService;

    public EmailTemplatesFunction(
        ILogger<EmailTemplatesFunction> logger,
        EmailTemplateService templates,
        IEmailService email,
        PreviewWhitelistService previewWhitelistService,
        TenantConfigurationService tenantConfigurationService)
    {
        _logger = logger;
        _templates = templates;
        _email = email;
        _previewWhitelistService = previewWhitelistService;
        _tenantConfigurationService = tenantConfigurationService;
    }

    /// <summary>
    /// GET /api/global/email-templates/{kind}
    /// Effective template: the override when stored, otherwise the built-in (raw, with the
    /// {{domainName}} placeholder so it can be edited as-is), plus the caller's default test recipient.
    /// </summary>
    [Function("GetEmailTemplate")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/email-templates/{kind}")] HttpRequestData req,
        string kind)
    {
        if (!EmailTemplateService.TryParseKind(kind, out var templateKind))
            return await BadRequest(req, "Unknown template kind. Use 'welcome' or 'farewell'.");

        var overrideEntry = await _templates.GetOverrideAsync(templateKind);
        var tenantId = TenantHelper.GetTenantId(req);
        var tenantConfig = await _tenantConfigurationService.GetConfigurationAsync(tenantId);
        var testRecipient = await DefaultTestRecipientAsync(tenantId, TenantHelper.GetUserIdentifier(req), tenantConfig);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new EmailTemplateResponse
        {
            Kind = EmailTemplateService.KindKey(templateKind),
            Subject = EmailTemplateService.Subject(templateKind),
            IsOverridden = overrideEntry is not null,
            Html = overrideEntry?.Html ?? EmailTemplateService.BuiltInRaw(templateKind),
            BuiltInHtml = EmailTemplateService.BuiltInRaw(templateKind),
            UpdatedBy = overrideEntry?.UpdatedBy,
            UpdatedUtc = overrideEntry?.UpdatedUtc,
            Placeholder = EmailTemplateService.DomainPlaceholder,
            MaxLength = EmailTemplateService.MaxHtmlLength,
            TestRecipient = testRecipient.Address,
            TestRecipientSource = testRecipient.Source,
        });
        return response;
    }

    /// <summary>PUT /api/global/email-templates/{kind} — body { html } stores an override.</summary>
    [Function("SaveEmailTemplate")]
    public async Task<HttpResponseData> Save(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "global/email-templates/{kind}")] HttpRequestData req,
        string kind)
    {
        if (!EmailTemplateService.TryParseKind(kind, out var templateKind))
            return await BadRequest(req, "Unknown template kind. Use 'welcome' or 'farewell'.");

        var read = await req.ReadAsync<EmailTemplateRequest>();
        if (read.Error != null) return read.Error;
        var body = read.Value!;
        var error = EmailTemplateService.Validate(body.Html);
        if (error is not null)
            return await BadRequest(req, error);

        var upn = TenantHelper.GetUserIdentifier(req);
        var saved = await _templates.SaveOverrideAsync(templateKind, body!.Html!, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new EmailTemplateSaveResponse { Kind = saved.Kind, IsOverridden = true, UpdatedBy = saved.UpdatedBy, UpdatedUtc = saved.UpdatedUtc });
        return response;
    }

    /// <summary>DELETE /api/global/email-templates/{kind} — resets to the built-in template.</summary>
    [Function("ResetEmailTemplate")]
    public async Task<HttpResponseData> Reset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/email-templates/{kind}")] HttpRequestData req,
        string kind)
    {
        if (!EmailTemplateService.TryParseKind(kind, out var templateKind))
            return await BadRequest(req, "Unknown template kind. Use 'welcome' or 'farewell'.");

        await _templates.DeleteOverrideAsync(templateKind, TenantHelper.GetUserIdentifier(req));

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new EmailTemplateResetResponse { Kind = EmailTemplateService.KindKey(templateKind), IsOverridden = false });
        return response;
    }

    /// <summary>
    /// POST /api/global/email-templates/{kind}/test — body { html?, toEmail? } sends the effective
    /// template (or the unsaved draft) to <c>toEmail</c> when given, otherwise to the caller's
    /// default test recipient (<see cref="ResolveDefaultTestRecipient"/>). Domain = the caller's
    /// tenant domain.
    /// </summary>
    [Function("SendEmailTemplateTest")]
    public async Task<HttpResponseData> SendTest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/email-templates/{kind}/test")] HttpRequestData req,
        string kind)
    {
        if (!EmailTemplateService.TryParseKind(kind, out var templateKind))
            return await BadRequest(req, "Unknown template kind. Use 'welcome' or 'farewell'.");

        var read = await req.ReadOptionalAsync<EmailTemplateTestSendRequest>();
        if (read.Error != null) return read.Error;
        var draft = string.IsNullOrWhiteSpace(read.Value?.Html) ? null : read.Value!.Html;
        if (draft is not null)
        {
            var error = EmailTemplateService.Validate(draft);
            if (error is not null) return await BadRequest(req, error);
        }
        var namedRecipient = string.IsNullOrWhiteSpace(read.Value?.ToEmail) ? null : read.Value!.ToEmail!.Trim();
        if (namedRecipient is not null)
        {
            var error = TenantConfigValidation.ValidateEmailAddress(namedRecipient);
            if (error is not null) return await BadRequest(req, error);
        }

        var tenantId = TenantHelper.GetTenantId(req);
        var upn = TenantHelper.GetUserIdentifier(req);
        var tenantConfig = await _tenantConfigurationService.GetConfigurationAsync(tenantId);

        var toEmail = namedRecipient ?? (await DefaultTestRecipientAsync(tenantId, upn, tenantConfig)).Address;
        if (toEmail is null)
            return await BadRequest(req, "No default test recipient for your tenant — enter a recipient, or set a notification email or ContactEmail first.");

        var sent = await _email.SendTestAsync(templateKind, toEmail, tenantConfig.DomainName, draft);
        _logger.LogInformation("Email template test {Kind} requested by {Upn} → {ToEmail} ({Recipient}): {Result}",
            templateKind, upn, toEmail, namedRecipient is null ? "default" : "named", sent ? "accepted" : "failed");

        if (!sent)
        {
            return await req.ErrorAsync(HttpStatusCode.BadGateway, Constants.ApiErrorCodes.UpstreamError,
                "The email provider did not accept the message. Check the backend log for the provider response.");
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new EmailTemplateTestSendResponse { SentTo = toEmail, DomainName = tenantConfig.DomainName, Draft = draft is not null });
        return response;
    }

    /// <summary>
    /// The caller's default test recipient: where the tenant's real welcome and farewell mails go
    /// (the activation notification address), else its ContactEmail, else the caller's UPN when it
    /// is an address. Source = the wire value of <see cref="EmailTemplateResponse.TestRecipientSource"/>.
    /// </summary>
    internal static (string? Address, string? Source) ResolveDefaultTestRecipient(string? notificationEmail, string? contactEmail, string? upn)
    {
        if (!string.IsNullOrWhiteSpace(notificationEmail)) return (notificationEmail, "notificationEmail");
        if (!string.IsNullOrWhiteSpace(contactEmail)) return (contactEmail, "contactEmail");
        if (!string.IsNullOrWhiteSpace(upn) && upn.Contains('@')) return (upn, "userPrincipalName");
        return (null, null);
    }

    private async Task<(string? Address, string? Source)> DefaultTestRecipientAsync(string tenantId, string upn, TenantConfiguration tenantConfig)
        => ResolveDefaultTestRecipient(await _previewWhitelistService.GetNotificationEmailAsync(tenantId), tenantConfig.ContactEmail, upn);

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string error)
    {
        return await req.BadRequestAsync(error);
    }
}
