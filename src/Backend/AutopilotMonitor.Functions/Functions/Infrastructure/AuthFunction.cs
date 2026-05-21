using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Infrastructure;

/// <summary>
/// Authentication and authorization endpoints
/// </summary>
public class AuthFunction
{
    private readonly ILogger<AuthFunction> _logger;
    private readonly GlobalAdminService _globalAdminService;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly IMetricsRepository _metricsRepo;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly TelegramNotificationService _telegramNotificationService;
    private readonly GlobalNotificationService _globalNotificationService;
    private readonly McpUserService _mcpUserService;

    public AuthFunction(
        ILogger<AuthFunction> logger,
        GlobalAdminService globalAdminService,
        TenantConfigurationService tenantConfigService,
        TenantAdminsService tenantAdminsService,
        IMetricsRepository metricsRepo,
        PreviewWhitelistService previewWhitelistService,
        TelegramNotificationService telegramNotificationService,
        GlobalNotificationService globalNotificationService,
        McpUserService mcpUserService)
    {
        _logger = logger;
        _globalAdminService = globalAdminService;
        _tenantConfigService = tenantConfigService;
        _tenantAdminsService = tenantAdminsService;
        _metricsRepo = metricsRepo;
        _previewWhitelistService = previewWhitelistService;
        _telegramNotificationService = telegramNotificationService;
        _globalNotificationService = globalNotificationService;
        _mcpUserService = mcpUserService;
    }

    /// <summary>
    /// GET /api/auth/me
    /// Returns information about the currently authenticated user
    /// </summary>
    [Function("GetCurrentUser")]
    [Authorize]
    public async Task<HttpResponseData> GetCurrentUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();

        if (principal == null)
        {
            _logger.LogWarning("GetCurrentUser - No authentication found");
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var tenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();
        var displayName = principal.GetDisplayName();
        var objectId = principal.GetObjectId();

        // Validate required claims
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
        {
            _logger.LogWarning("Missing required claims: tenantId or upn");
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "Missing required claims" });
            return badRequestResponse;
        }

        // --- Parallel data fetch: all independent queries run concurrently ---
        // Fail-fast: if any fetch throws, AggregateException propagates → Azure Functions returns 500.
        // This is intentional — all 6 queries are required for the auth decision.
        var tenantConfigTask = _tenantConfigService.GetConfigurationAsync(tenantId);
        var isGlobalAdminTask = _globalAdminService.IsGlobalAdminAsync(upn);
        var isApprovedTask = _previewWhitelistService.IsApprovedAsync(tenantId);
        var memberRoleTask = _tenantAdminsService.GetMemberRoleAsync(tenantId, upn);
        var mcpCheckTask = _mcpUserService.IsAllowedAsync(upn);
        var existingAdminsTask = _tenantAdminsService.GetTenantAdminsAsync(tenantId);

        await Task.WhenAll(tenantConfigTask, isGlobalAdminTask, isApprovedTask,
                           memberRoleTask, mcpCheckTask, existingAdminsTask);

        var tenantConfig = tenantConfigTask.Result;
        var isGlobalAdmin = isGlobalAdminTask.Result;
        var isApproved = isApprovedTask.Result;
        var memberRole = memberRoleTask.Result;
        var mcpCheck = mcpCheckTask.Result;
        var existingAdmins = existingAdminsTask.Result;

        // --- Side-effects that don't affect the auth decision ---
        await HandleNewTenantDomainAsync(tenantConfig, tenantId, upn);
        await HandleAutoReEnableAsync(tenantConfig, tenantId);

        // --- Pure decision logic (tested by AuthFunctionTests) ---
        var decision = BuildAuthResult(
            tenantConfig, isGlobalAdmin, isApproved,
            memberRole, mcpCheck, existingAdmins.Count > 0,
            tenantId, upn, displayName ?? string.Empty, objectId ?? string.Empty);

        if (!decision.IsSuccess)
        {
            if (decision.StatusCode == HttpStatusCode.Forbidden)
            {
                var bodyType = decision.Body.GetType();
                var errorProp = bodyType.GetProperty("error");
                var errorValue = errorProp?.GetValue(decision.Body) as string;

                if (errorValue == "TenantSuspended")
                    _logger.LogWarning("Login attempt for suspended tenant: {TenantId} by user {Upn}", tenantId, upn);
                else if (errorValue == "PrivatePreview")
                    _logger.LogInformation("Tenant {TenantId} blocked by preview gate (user: {Upn})", tenantId, upn);
            }

            var blockedResponse = req.CreateResponse(decision.StatusCode);
            await blockedResponse.WriteAsJsonAsync(decision.Body);
            return blockedResponse;
        }

        // --- Post-decision side-effects ---
        await HandlePostDecisionSideEffectsAsync(decision, tenantId, upn, displayName, objectId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(decision.Body);
        return response;
    }

    /// <summary>
    /// GET /api/auth/is-global-admin
    /// Checks if the current user is a Global Admin
    /// </summary>
    [Function("IsGlobalAdmin")]
    [Authorize]
    public async Task<HttpResponseData> IsGlobalAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/is-global-admin")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var upn = principal.GetUserPrincipalName();
        var isAdmin = await _globalAdminService.IsGlobalAdminAsync(upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { isGlobalAdmin = isAdmin, upn });
        return response;
    }

    /// <summary>
    /// GET /api/auth/global-admins
    /// Lists all Global Admins (only accessible by Global Admins)
    /// </summary>
    [Function("GetGlobalAdmins")]
    [Authorize]
    public async Task<HttpResponseData> GetGlobalAdmins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/global-admins")] HttpRequestData req,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

        var admins = await _globalAdminService.GetAllGlobalAdminsAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { admins });
        return response;
    }

    /// <summary>
    /// POST /api/auth/global-admins
    /// Adds a new Global Admin (only accessible by existing Global Admins)
    /// </summary>
    [Function("AddGlobalAdmin")]
    [Authorize]
    public async Task<HttpResponseData> AddGlobalAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/global-admins")] HttpRequestData req,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var currentUpn = principal?.GetUserPrincipalName();

        // Parse request body
        var body = await req.ReadFromJsonAsync<AddGlobalAdminRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "UPN is required" });
            return badRequestResponse;
        }

        var newAdmin = await _globalAdminService.AddGlobalAdminAsync(body.Upn, currentUpn!);

        _logger.LogInformation($"Global Admin added: {body.Upn} by {currentUpn}");

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { admin = newAdmin });
        return response;
    }

    /// <summary>
    /// DELETE /api/auth/global-admins/{upn}
    /// Removes a Global Admin (only accessible by existing Global Admins)
    /// </summary>
    [Function("RemoveGlobalAdmin")]
    [Authorize]
    public async Task<HttpResponseData> RemoveGlobalAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "auth/global-admins/{upn}")] HttpRequestData req,
        string upn,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var currentUpn = principal?.GetUserPrincipalName();

        // Prevent self-removal
        if (upn.Equals(currentUpn, StringComparison.OrdinalIgnoreCase))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "You cannot remove yourself as a Global Admin" });
            return badRequestResponse;
        }

        await _globalAdminService.RemoveGlobalAdminAsync(upn);

        _logger.LogInformation($"Global Admin removed: {upn} by {currentUpn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Global Admin removed successfully" });
        return response;
    }

    /// <summary>
    /// If the tenant has no domain name yet, extracts it from the UPN, persists it,
    /// and fires best-effort notifications (Telegram + global notification).
    /// </summary>
    internal async Task HandleNewTenantDomainAsync(
        TenantConfiguration tenantConfig, string tenantId, string upn)
    {
        if (!string.IsNullOrEmpty(tenantConfig.DomainName) || string.IsNullOrEmpty(upn))
            return;

        var domain = ExtractDomainFromUpn(upn);
        if (string.IsNullOrEmpty(domain))
            return;

        _logger.LogInformation("Setting domain name for tenant {TenantId}: {Domain}", tenantId, domain);
        tenantConfig.DomainName = domain;
        tenantConfig.UpdatedBy = upn;
        // OnboardedBy is immutable once set: this is the only place that writes it, gated
        // on the same "DomainName is empty" condition (first-ever user login for the tenant).
        // Downstream auto-promote (PreviewWhitelistFunction) reads OnboardedBy so background
        // sync jobs that mutate UpdatedBy cannot leak sentinel strings into TenantAdmins.
        if (string.IsNullOrWhiteSpace(tenantConfig.OnboardedBy))
            tenantConfig.OnboardedBy = upn;
        await _tenantConfigService.SaveConfigurationAsync(tenantConfig);

        // Fire-and-forget: Telegram
        _ = _telegramNotificationService.SendNewTenantSignupAsync(tenantId, upn)
            .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                "Fire-and-forget Telegram notification failed for tenant {TenantId}", tenantId),
                TaskContinuationOptions.OnlyOnFaulted);

        // Fire-and-forget: Global notification
        _ = _globalNotificationService.CreateNotificationAsync(
            "preview_signup",
            "New Preview Signup",
            $"Tenant {tenantId} ({domain}), UPN: {upn}");
    }

    /// <summary>
    /// If the tenant's suspension has expired (Disabled=true but DisabledUntil is past),
    /// clears the disabled state and persists the change.
    /// MUST run before BuildAuthResult because it mutates tenantConfig.Disabled.
    /// </summary>
    internal async Task HandleAutoReEnableAsync(
        TenantConfiguration tenantConfig, string tenantId)
    {
        if (!tenantConfig.Disabled || tenantConfig.IsCurrentlyDisabled())
            return;

        _logger.LogInformation(
            "Tenant {TenantId} auto-re-enabled: DisabledUntil ({DisabledUntil}) has expired",
            tenantId, tenantConfig.DisabledUntil?.ToString("o"));

        tenantConfig.Disabled = false;
        tenantConfig.DisabledReason = null;
        tenantConfig.DisabledUntil = null;
        tenantConfig.UpdatedBy = "System (auto-re-enable)";
        await _tenantConfigService.SaveConfigurationAsync(tenantConfig);
    }

    /// <summary>
    /// Executes post-decision side-effects: auto-admin assignment (awaited)
    /// and metrics recording (fire-and-forget).
    /// </summary>
    internal async Task HandlePostDecisionSideEffectsAsync(
        AuthDecisionResult decision, string tenantId, string upn,
        string? displayName, string? objectId)
    {
        if (decision.NeedsAutoAdmin)
        {
            _logger.LogInformation("First user login for tenant {TenantId}: {Upn} - Auto-assigning as admin", tenantId, upn);
            await _tenantAdminsService.AddTenantAdminAsync(tenantId, upn, "System");
        }

        _ = _metricsRepo.RecordUserLoginAsync(tenantId, upn, displayName, objectId)
            .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                "Fire-and-forget RecordUserLoginAsync failed"),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Pure decision logic for auth/me — no I/O, fully testable.
    /// Takes all pre-fetched data and returns the auth decision.
    /// </summary>
    internal static AuthDecisionResult BuildAuthResult(
        TenantConfiguration tenantConfig,
        bool isGlobalAdmin,
        bool isPreviewApproved,
        MemberRoleInfo? memberRole,
        McpAccessCheckResult mcpCheck,
        bool hasTenantAdmins,
        string tenantId, string upn, string displayName, string objectId)
    {
        // Gate 1: Suspended tenant
        if (tenantConfig.IsCurrentlyDisabled())
        {
            return AuthDecisionResult.Blocked(HttpStatusCode.Forbidden, new
            {
                error = "TenantSuspended",
                message = !string.IsNullOrEmpty(tenantConfig.DisabledReason)
                    ? tenantConfig.DisabledReason
                    : "Your tenant has been suspended. Please contact support for more information.",
                disabledUntil = tenantConfig.DisabledUntil?.ToString("o"),
                contactSupport = true
            });
        }

        // Gate 2: Preview gate (Global Admins bypass)
        if (!isGlobalAdmin && !isPreviewApproved)
        {
            return AuthDecisionResult.Blocked(HttpStatusCode.Forbidden, new
            {
                error = "PrivatePreview",
                message = "Autopilot Monitor is currently in Private Preview. Your organization is on the waitlist \u2014 we'll notify you when access is granted."
            });
        }

        // Determine admin status: auto-admin if first user (no existing admins and no role yet)
        bool isTenantAdmin = memberRole?.Role == Constants.TenantRoles.Admin;
        bool needsAutoAdmin = !isTenantAdmin && !hasTenantAdmins;
        if (needsAutoAdmin)
        {
            isTenantAdmin = true;
        }

        // After auto-admin, re-derive role: the auto-admin gets Admin role,
        // otherwise use the fetched memberRole.
        string? role = needsAutoAdmin ? Constants.TenantRoles.Admin : memberRole?.Role;
        bool canManageBootstrapTokens = needsAutoAdmin || (memberRole?.CanManageBootstrapTokens ?? false);

        return AuthDecisionResult.Success(new
        {
            tenantId,
            upn,
            displayName,
            objectId,
            isGlobalAdmin,
            isTenantAdmin,
            role,
            canManageBootstrapTokens,
            hasMcpAccess = mcpCheck.IsAllowed,
            bootstrapTokenEnabled = tenantConfig.BootstrapTokenEnabled,
            unrestrictedModeEnabled = tenantConfig.UnrestrictedModeEnabled
        }, needsAutoAdmin);
    }

    /// <summary>
    /// Extracts domain name from UPN (e.g., user@contoso.com -> contoso.com)
    /// </summary>
    internal static string ExtractDomainFromUpn(string upn)
    {
        if (string.IsNullOrEmpty(upn))
            return string.Empty;

        var atIndex = upn.IndexOf('@');
        if (atIndex > 0 && atIndex < upn.Length - 1)
        {
            return upn.Substring(atIndex + 1);
        }

        return string.Empty;
    }
}

/// <summary>
/// Result of the pure auth decision logic — no HTTP concerns.
/// </summary>
internal class AuthDecisionResult
{
    public HttpStatusCode StatusCode { get; init; }
    public object Body { get; init; } = default!;
    public bool IsSuccess => StatusCode == HttpStatusCode.OK;

    /// <summary>True when the user should be auto-promoted to tenant admin (first user).</summary>
    public bool NeedsAutoAdmin { get; init; }

    public static AuthDecisionResult Success(object body, bool needsAutoAdmin = false) => new()
    {
        StatusCode = HttpStatusCode.OK,
        Body = body,
        NeedsAutoAdmin = needsAutoAdmin
    };

    public static AuthDecisionResult Blocked(HttpStatusCode statusCode, object body) => new()
    {
        StatusCode = statusCode,
        Body = body
    };
}

public class AddGlobalAdminRequest
{
    public string Upn { get; set; } = string.Empty;
}
