using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules;

/// <summary>
/// CRUD endpoints for managing the tenant-activation whitelist (table: PreviewWhitelist).
/// All endpoints are Global Admin only (except notification-email which is AuthenticatedUser).
/// </summary>
public class PreviewWhitelistFunction
{
    private readonly ILogger<PreviewWhitelistFunction> _logger;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly TenantApprovalService _tenantApprovalService;
    private readonly TenantConfigurationService _tenantConfigurationService;
    private readonly ResendEmailService _resendEmailService;

    public PreviewWhitelistFunction(
        ILogger<PreviewWhitelistFunction> logger,
        PreviewWhitelistService previewWhitelistService,
        TenantApprovalService tenantApprovalService,
        TenantConfigurationService tenantConfigurationService,
        ResendEmailService resendEmailService)
    {
        _logger = logger;
        _previewWhitelistService = previewWhitelistService;
        _tenantApprovalService = tenantApprovalService;
        _tenantConfigurationService = tenantConfigurationService;
        _resendEmailService = resendEmailService;
    }

    /// <summary>
    /// GET /api/preview/whitelist
    /// Returns all approved tenants.
    /// </summary>
    [Function("GetPreviewWhitelist")]
    [Authorize]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/whitelist")] HttpRequestData req,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

        var approved = await _previewWhitelistService.GetAllApprovedAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { tenants = approved });
        return response;
    }

    /// <summary>
    /// POST /api/preview/whitelist/{tenantId}
    /// Activates a tenant (adds it to the whitelist).
    /// </summary>
    [Function("ApprovePreviewTenant")]
    [Authorize]
    public async Task<HttpResponseData> Approve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "preview/whitelist/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var upn = principal?.GetUserPrincipalName();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "tenantId is required" });
            return bad;
        }

        // Whitelist add + auto-promote + welcome email — shared with the auto-approve worker.
        // False = already activated: idempotent success, but no duplicate mail/promote.
        var newlyApproved = await _tenantApprovalService.ApproveWithSideEffectsAsync(tenantId, upn!);

        var response = req.CreateResponse(newlyApproved ? HttpStatusCode.Created : HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            message = newlyApproved ? "Tenant approved for preview" : "Tenant was already approved",
            tenantId,
            alreadyApproved = !newlyApproved,
        });
        return response;
    }

    /// <summary>
    /// DELETE /api/preview/whitelist/{tenantId}
    /// Revokes a tenant's activation (removes it from the whitelist).
    /// </summary>
    [Function("RevokePreviewTenant")]
    [Authorize]
    public async Task<HttpResponseData> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "preview/whitelist/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var upn = principal?.GetUserPrincipalName();

        await _previewWhitelistService.RevokeAsync(tenantId);

        _logger.LogInformation("Preview tenant revoked: {TenantId} by {Upn}", tenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant removed from preview", tenantId });
        return response;
    }

    /// <summary>
    /// GET /api/preview/notification-email/{tenantId}
    /// Returns the notification email for a tenant. Global Admin only.
    /// </summary>
    [Function("GetPreviewNotificationEmail")]
    [Authorize]
    public async Task<HttpResponseData> GetNotificationEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/notification-email/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var email = await _previewWhitelistService.GetNotificationEmailAsync(tenantId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { email = email ?? "" });
        return response;
    }

    /// <summary>
    /// PUT /api/preview/notification-email
    /// Saves the caller's notification email for the activation notice.
    /// AuthenticatedUser policy — preview-blocked users can call this.
    /// </summary>
    [Function("SavePreviewNotificationEmail")]
    [Authorize]
    public async Task<HttpResponseData> SaveNotificationEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "preview/notification-email")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        var tenantId = principal?.GetTenantId();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Could not determine tenant" });
            return bad;
        }

        var body = await req.ReadFromJsonAsync<SaveNotificationEmailRequest>();
        var email = body?.Email?.Trim();

        if (!string.IsNullOrEmpty(email) && !email.Contains('@'))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Invalid email address" });
            return bad;
        }

        await _previewWhitelistService.SaveNotificationEmailAsync(tenantId, email);

        _logger.LogInformation(
            "Preview notification email updated for tenant {TenantId}: {Email}",
            tenantId, string.IsNullOrEmpty(email) ? "(cleared)" : email);

        // Send-on-save half of the welcome-mail race: with auto-approve, activation
        // typically finishes before the user has typed this address on the activation
        // page — the approval path then found no address and deferred to us. Fresh
        // approval read: a cached "not approved" can be stale for exactly this window.
        // The sent-marker inside TrySendWelcomeEmailAsync dedupes against the approval
        // path and against repeated saves.
        var welcomeEmailSent = false;
        if (!string.IsNullOrWhiteSpace(email) &&
            await _previewWhitelistService.IsApprovedFreshAsync(tenantId))
        {
            welcomeEmailSent = await _tenantApprovalService.TrySendWelcomeEmailAsync(tenantId);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Notification email saved", email, welcomeEmailSent });
        return response;
    }

    /// <summary>
    /// POST /api/preview/send-welcome-email/{tenantId}
    /// Sends (or resends) the activation welcome email. Global Admin only.
    /// Accepts optional { email } in body — if provided, saves it to PreviewWhitelist table before sending.
    /// </summary>
    [Function("SendPreviewWelcomeEmail")]
    [Authorize]
    public async Task<HttpResponseData> SendWelcomeEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "preview/send-welcome-email/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

        // If the caller provides an email in the body, save it first
        var body = await req.ReadFromJsonAsync<SaveNotificationEmailRequest>();
        var bodyEmail = body?.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(bodyEmail))
        {
            await _previewWhitelistService.SaveNotificationEmailAsync(tenantId, bodyEmail);
        }

        var email = !string.IsNullOrWhiteSpace(bodyEmail)
            ? bodyEmail
            : await _previewWhitelistService.GetNotificationEmailAsync(tenantId);

        if (string.IsNullOrWhiteSpace(email))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "No notification email configured for this tenant" });
            return bad;
        }

        var tenantConfig = await _tenantConfigurationService.GetConfigurationAsync(tenantId);
        await _resendEmailService.SendPreviewApprovedEmailAsync(email, tenantConfig.DomainName);

        // Explicit GA send always sends; consume the once-only marker (best-effort) so
        // the automatic paths won't produce a duplicate afterwards.
        try { await _previewWhitelistService.TryMarkWelcomeEmailSentAsync(tenantId); }
        catch { /* best-effort — a failed marker write must not fail the explicit send */ }

        var principal = context.GetUser();
        var upn = principal?.GetUserPrincipalName();
        _logger.LogInformation(
            "Welcome email sent to {Email} for tenant {TenantId} by {Upn}",
            email, tenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Welcome email sent", email });
        return response;
    }

}

public class SaveNotificationEmailRequest
{
    public string Email { get; set; } = string.Empty;
}
