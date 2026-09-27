using System.Collections.Generic;
using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Offboarding;
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
    private readonly DelegatedAdminService _delegatedAdminService;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly IMetricsRepository _metricsRepo;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly TelegramNotificationService _telegramNotificationService;
    private readonly GlobalNotificationService _globalNotificationService;
    private readonly McpUserService _mcpUserService;
    private readonly Services.Activation.ITenantAutoApproveEnqueuer _tenantAutoApproveEnqueuer;
    private readonly EntraAppRegistry _appRegistry;
    private readonly AdminIdentityResolver _identityResolver;
    private readonly ISignalRNotificationService _signalRService;
    private readonly AdminConfigurationService _adminConfigService;
    private readonly IOffboardingAuditRepository _offboardingRepo;

    public AuthFunction(
        ILogger<AuthFunction> logger,
        GlobalAdminService globalAdminService,
        DelegatedAdminService delegatedAdminService,
        TenantConfigurationService tenantConfigService,
        TenantAdminsService tenantAdminsService,
        IMetricsRepository metricsRepo,
        PreviewWhitelistService previewWhitelistService,
        TelegramNotificationService telegramNotificationService,
        GlobalNotificationService globalNotificationService,
        McpUserService mcpUserService,
        Services.Activation.ITenantAutoApproveEnqueuer tenantAutoApproveEnqueuer,
        EntraAppRegistry appRegistry,
        AdminIdentityResolver identityResolver,
        ISignalRNotificationService signalRService,
        AdminConfigurationService adminConfigService,
        IOffboardingAuditRepository offboardingRepo)
    {
        _logger = logger;
        _offboardingRepo = offboardingRepo;
        _adminConfigService = adminConfigService;
        _identityResolver = identityResolver;
        _signalRService = signalRService;
        _globalAdminService = globalAdminService;
        _delegatedAdminService = delegatedAdminService;
        _tenantConfigService = tenantConfigService;
        _tenantAdminsService = tenantAdminsService;
        _metricsRepo = metricsRepo;
        _previewWhitelistService = previewWhitelistService;
        _telegramNotificationService = telegramNotificationService;
        _globalNotificationService = globalNotificationService;
        _mcpUserService = mcpUserService;
        _tenantAutoApproveEnqueuer = tenantAutoApproveEnqueuer;
        _appRegistry = appRegistry;
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
            return await req.BadRequestAsync("Missing required claims");
        }

        // --- Parallel data fetch: all independent queries run concurrently ---
        // Fail-fast: if any fetch throws, AggregateException propagates → Azure Functions returns 500.
        // This is intentional — the auth-decision queries are required; the What's-new marker read
        // is fail-soft inside the repository because it is only a UX convenience.
        // Non-creating read: do NOT auto-persist a default config row just because someone authenticated.
        // A delegated-only external MSP login must not phantom-onboard its home tenant (see the side-effect
        // gate below). A genuine first-user still gets their config created by HandleNewTenantDomainAsync.
        var tenantConfigTask = _tenantConfigService.TryGetConfigurationAsync(tenantId);
        // Cross-tenant roles resolve on the FULL identity (upn + tid + oid) against the UPN's identity
        // binding — never on the UPN alone. Null (no oid in the token) ⇒ no platform/delegated role.
        var identity = AdminIdentity.FromPrincipal(principal);
        var globalRoleTask = _globalAdminService.GetGlobalRoleAsync(identity);
        var delegatedScopeTask = _delegatedAdminService.GetScopeAsync(identity);
        var isApprovedTask = _previewWhitelistService.IsApprovedAsync(tenantId);
        var membershipTask = _tenantAdminsService.GetTableMembershipAsync(tenantId, upn);
        // App roles ride along so the AllMembers path sees a claim-derived member the same way the policy
        // middleware does (the table-vs-claim reconciliation happens inside the shared role resolver).
        var mcpCheckTask = _mcpUserService.IsAllowedAsync(upn, tenantId, objectId, principal.GetAppRoles());
        var existingAdminsTask = _tenantAdminsService.GetTenantAdminsAsync(tenantId);
        var whatsNewSeenTask = _metricsRepo.GetUserWhatsNewSeenAsync(tenantId, upn);

        await Task.WhenAll(tenantConfigTask, globalRoleTask, delegatedScopeTask, isApprovedTask,
                           membershipTask, mcpCheckTask, existingAdminsTask, whatsNewSeenTask);

        var (tenantConfig, _) = tenantConfigTask.Result;
        var globalRole = globalRoleTask.Result;
        var isGlobalAdmin = globalRole == Constants.GlobalRoles.GlobalAdmin;
        var isGlobalReader = globalRole == Constants.GlobalRoles.GlobalReader;
        // The tenants this caller manages as a delegated ("MSP") admin (empty for non-delegated users).
        // Surfaced to the web app so it can show fleet/switcher UI and bound it to this set.
        var delegatedTenantIds = delegatedScopeTask.Result.TenantIds;
        var isApproved = isApprovedTask.Result;
        var (tableState, tableRole) = membershipTask.Result;
        var mcpCheck = mcpCheckTask.Result;
        var existingAdmins = existingAdminsTask.Result;
        var whatsNewSeen = whatsNewSeenTask.Result;

        // Reconcile the TenantAdmins table state with any Entra app-role claim. An enabled row
        // wins; a disabled row is an explicit deny (claim ignored); only a missing row falls back
        // to the claim, and only when the tenant has app-roles enabled.
        var memberRole = EntraAppRoleResolver.Resolve(
            tableState, tableRole, principal.GetAppRoles(), tenantConfig.EntraAppRolesEnabled);

        // --- Side-effects that don't affect the auth decision ---
        // Run first-login onboarding side-effects (domain/OnboardedBy write + "new tenant signup"
        // notification, auto-re-enable, and the implicit config-row persistence) ONLY for a genuine
        // home-tenant participant. A delegated-only external MSP caller — who bypassed the preview gate to
        // manage OTHER tenants and has no stake in this home tenant (no membership, no platform role) — is
        // excluded, so a read-only MSP login does not phantom-onboard its home tenant (false signup, default
        // config row). A delegated user who legitimately participates in their own tenant (member/global)
        // still onboards normally; the handlers self-gate when the config already exists.
        var isDelegated = delegatedTenantIds.Count > 0;
        var isHomeTenantParticipant = !isDelegated || isGlobalAdmin || isGlobalReader || memberRole != null;
        // Which app registration minted this login's token (dual app-reg window) — drives the
        // onboarding homing decision and the per-tenant last-seen provenance below.
        var tokenAudience = principal.GetAudience();
        if (isHomeTenantParticipant)
        {
            await HandleNewTenantDomainAsync(tenantConfig, tenantId, upn, tokenAudience);
            await HandleAutoReEnableAsync(tenantConfig, tenantId);
            await HandleAuthClientIdTrackingAsync(tenantConfig, tenantId, tokenAudience);
        }

        // --- Pure decision logic (tested by AuthFunctionTests) ---
        var decision = BuildAuthResult(
            tenantConfig, isGlobalAdmin, isGlobalReader, isApproved,
            memberRole, mcpCheck, existingAdmins.Count > 0,
            tenantId, upn, displayName ?? string.Empty, objectId ?? string.Empty,
            delegatedTenantIds,
            homedApp: _appRegistry.ResolveForTenant(tenantConfig).IsLegacy ? "legacy" : "primary",
            whatsNewSeenPlatformUtc: whatsNewSeen.PlatformUtc,
            whatsNewSeenAgentUtc: whatsNewSeen.AgentUtc,
            mcpClientRegistrationEnabled: (await _adminConfigService.GetConfigurationAsync())?.McpClientRegistrationEnabled ?? false);

        if (!decision.IsSuccess)
        {
            if (decision.StatusCode == HttpStatusCode.Forbidden)
            {
                var bodyType = decision.Body.GetType();
                var errorProp = bodyType.GetProperty("error");
                var errorValue = errorProp?.GetValue(decision.Body) as string;

                if (errorValue == "TenantSuspended")
                    _logger.LogWarning("Login attempt for suspended tenant: {TenantId} by user {Upn}", tenantId, upn);
                else if (errorValue == "PendingActivation")
                    _logger.LogInformation("Tenant {TenantId} blocked by activation gate (user: {Upn})", tenantId, upn);
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
    /// PUT /api/auth/me/whats-new-seen
    /// Marks the caller's What's-new channel as seen.
    /// </summary>
    [Function("MarkWhatsNewSeen")]
    [Authorize]
    public async Task<HttpResponseData> MarkWhatsNewSeen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "auth/me/whats-new-seen")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        var tenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return await req.BadRequestAsync("Missing required claims");

        var read = await req.ReadAsync<WhatsNewSeenRequest>();
        if (read.Error != null) return read.Error;

        if (!TryNormalizeWhatsNewSeen(read.Value, DateTime.UtcNow, out var channel, out var seenUtc, out var error))
            return await req.BadRequestAsync(error);

        await _metricsRepo.MarkUserWhatsNewSeenAsync(tenantId, upn, channel, seenUtc);
        return req.CreateResponse(HttpStatusCode.NoContent);
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
        var isAdmin = await _globalAdminService.IsGlobalAdminAsync(AdminIdentity.FromPrincipal(principal));

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new IsGlobalAdminResponse { IsGlobalAdmin = isAdmin, Upn = upn });
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
        await response.WriteAsJsonAsync(new GetGlobalAdminsResponse { Admins = admins });
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
        var read = await req.ReadAsync<AddGlobalAdminRequest>();
        if (read.Error != null) return read.Error;
        var body = read.Value!;
        if (string.IsNullOrWhiteSpace(body.Upn))
        {
            return await req.BadRequestAsync("UPN is required");
        }
        // The row is inert until the UPN is bound to the identity that may use it. The home tenant is resolved
        // automatically (sign-in history, then UPN domain → onboarded tenant); the body may override it.
        var bindingError = IdentityBindingRules.ValidateOptional(body.HomeTenantId, body.ObjectId);
        if (bindingError != null)
        {
            return await req.BadRequestAsync(bindingError);
        }
        var identity = await IdentityBindingRules.ResolveForGrantAsync(_identityResolver, body.Upn, body.HomeTenantId, body.ObjectId);
        if (identity == null)
        {
            return await req.ErrorAsync(HttpStatusCode.UnprocessableEntity, IdentityBindingRules.HomeTenantUnresolvedCode, IdentityBindingRules.HomeTenantUnresolvedMessage);
        }

        GlobalAdminRow newAdmin;
        try
        {
            newAdmin = await _globalAdminService.AddGlobalAdminAsync(body.Upn, currentUpn!, identity.Value.TenantId, identity.Value.ObjectId);
        }
        catch (IdentityBindingConflictException ex)
        {
            return await req.ConflictAsync(ex.Message);
        }

        _logger.LogInformation($"Global Admin added: {body.Upn} by {currentUpn}");

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new AddGlobalAdminResponse { Admin = newAdmin });
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
            return await req.BadRequestAsync("You cannot remove yourself as a Global Admin");
        }

        await _globalAdminService.RemoveGlobalAdminAsync(upn);

        // SignalR group authorization is join-time only: a removed Global Admin/Reader with a live
        // connection in 'global-admins' would keep receiving cross-tenant pushes until the socket
        // drops. Cut the UPN's connections (negotiate binds userId = lowercased UPN); a reconnect
        // re-runs the join gates against the now-missing platform role.
        await _signalRService.DisconnectUserAsync(upn.ToLowerInvariant());

        _logger.LogInformation($"Global Admin removed: {upn} by {currentUpn}");

        return await req.OkAsync(new MessageResponse { Message = "Global Admin removed successfully" });
    }

    /// <summary>
    /// If the tenant has no domain name yet, extracts it from the UPN, persists it,
    /// and fires best-effort notifications (Telegram + global notification). The write lands
    /// only while the STORED row still has no domain (conditional create or update), and only
    /// the login that wrote it fires the signup side effects.
    /// </summary>
    internal async Task HandleNewTenantDomainAsync(
        TenantConfiguration tenantConfig, string tenantId, string upn, string? tokenAudience = null)
    {
        if (!string.IsNullOrEmpty(tenantConfig.DomainName) || string.IsNullOrEmpty(upn))
            return;

        var domain = ExtractDomainFromUpn(upn);
        if (string.IsNullOrEmpty(domain))
            return;

        // The seeded value is rendered into transactional mails — persist only a strict host name,
        // regardless of what the identity provider's UPN charset happens to allow.
        if (!TenantConfigValidation.IsValidDomainName(domain))
        {
            _logger.LogWarning("Refusing to seed DomainName for tenant {TenantId}: UPN domain is not a valid host name", tenantId);
            return;
        }

        // A returning tenant: the offboarding deleted its configuration, so the pointer is the only
        // record of a consumed self-service trial. Carried over in this same write as OnboardedBy, so
        // no onboarded row (and no auto-promoted admin) ever exists without it (D-288). Unreadable ⇒
        // no write at all; the self-gate above retries on the next login.
        OffboardingByTenantPointer? offboardingPointer;
        try
        {
            (offboardingPointer, _) = await _offboardingRepo.TryGetByTenantPointerAsync(tenantId.ToLowerInvariant());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "First-login offboarding lookup failed for tenant {TenantId} — will retry on next login", tenantId);
            return;
        }

        var onboardingClientId = EntraAppRegistry.NormalizeAudience(tokenAudience);
        var seededAt = DateTime.UtcNow;
        void Seed(TenantConfiguration row)
        {
            if (offboardingPointer is { TrialConsumed: true })
                row.TrialConsumed = true;
            row.DomainName = domain;
            row.UpdatedBy = upn;
            // OnboardedBy is immutable once set: this is the only place that writes it, and the write
            // below lands only while the STORED row has no domain yet (first-ever login for the tenant),
            // so two concurrent first logins cannot both write it. Downstream auto-promote reads
            // OnboardedBy so background sync jobs that mutate UpdatedBy cannot leak sentinel strings
            // into TenantAdmins.
            if (string.IsNullOrWhiteSpace(row.OnboardedBy))
                row.OnboardedBy = upn;
            // The DPA is accepted with the onboarding itself (D-251/D-252): record the version in force,
            // once, in this same first write — later logins never touch it.
            if (string.IsNullOrWhiteSpace(row.DpaVersion))
                row.DpaVersion = Constants.CurrentDpaVersion;
            // Dual app-reg window: home a NEW tenant on the primary app ONLY when its first login
            // actually arrived via the primary app. A first login via the legacy app leaves the field
            // null (= legacy) — keeps the "null = legacy" invariant clean and never routes a tenant
            // to an app it hasn't consented to. Seed the last-seen provenance in the same write.
            if (onboardingClientId != null)
            {
                if (_appRegistry.IsPrimary(onboardingClientId))
                    row.HomedAppClientId = onboardingClientId;
                row.LastAuthClientId = onboardingClientId;
                row.LastAuthClientIdSince = seededAt;
            }
        }

        TenantConfigUpdate update;
        try
        {
            update = await _tenantConfigService.CreateOrUpdateAsync(tenantId, row =>
            {
                if (!string.IsNullOrEmpty(row.DomainName))
                    return false; // another login seeded it since this one read the configuration
                Seed(row);
                return true;
            }, "auth", "first-login domain seed");
        }
        catch (Exception ex)
        {
            // A storage blip must not 500 the login. The handler self-gates on DomainName being
            // empty, so the next login retries — skip the notifications too so they only fire once
            // the write actually stuck.
            _logger.LogWarning(ex, "First-login domain write failed for tenant {TenantId} — will retry on next login", tenantId);
            return;
        }

        if (update.Status != TenantConfigUpdateStatus.Updated)
        {
            // Declined: a concurrent login won the seed and fires the signup side effects itself.
            // Conflict: the next login retries. Tombstone: the tenant is being offboarded.
            _logger.LogInformation("First-login domain seed for tenant {TenantId} not written ({Status})", tenantId, update.Status);
            return;
        }

        _logger.LogInformation("Seeded domain name for tenant {TenantId}: {Domain}", tenantId, domain);
        // This login's view follows what was just stored.
        Seed(tenantConfig);

        // Fire-and-forget: Telegram
        _ = _telegramNotificationService.SendNewTenantSignupAsync(tenantId, upn)
            .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                "Fire-and-forget Telegram notification failed for tenant {TenantId}", tenantId),
                TaskContinuationOptions.OnlyOnFaulted);

        // Fire-and-forget: Global notification
        _ = _globalNotificationService.CreateNotificationAsync(
            "preview_signup",
            "New Tenant Signup",
            $"Tenant {tenantId} ({domain}), UPN: {upn}",
            href: $"/admin/tenants/management?tenantId={Uri.EscapeDataString(tenantId)}");

        // Fire-and-forget: delayed auto-approve. Enqueued unconditionally — the worker is the
        // single decision point (checks AutoApproveNewTenants at processing time). A failed
        // enqueue degrades to manual approval and must never fail the login.
        _ = _tenantAutoApproveEnqueuer.EnqueueAsync(
                new Services.Activation.TenantAutoApproveEnvelope
                {
                    TenantId = tenantId,
                    SignupUpn = upn,
                    EnqueuedAtUtc = DateTime.UtcNow
                },
                Services.Activation.TenantAutoApproveEnvelope.ActivationDelay)
            .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                "Fire-and-forget auto-approve enqueue failed for tenant {TenantId}", tenantId),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Pure observability for the app-reg migration: records which app registration this
    /// tenant's logins actually arrive through (<c>LastAuthClientId</c> + since-when).
    /// Write-on-change only — the common case (same app as last time) is a no-op, so this adds
    /// zero table writes to steady-state logins. Best-effort: a failed persist must never break
    /// auth/me; the next login that still differs retries. Gated on a non-empty DomainName so a
    /// pre-onboarding login (row not persisted yet) can't create a phantom config row.
    /// </summary>
    internal async Task HandleAuthClientIdTrackingAsync(
        TenantConfiguration tenantConfig, string tenantId, string? tokenAudience)
    {
        var clientId = EntraAppRegistry.NormalizeAudience(tokenAudience);
        if (clientId == null || string.IsNullOrEmpty(tenantConfig.DomainName))
            return;
        // Cheap pre-filter on the cached view; the write decides again on the fresh row.
        if (string.Equals(tenantConfig.LastAuthClientId, clientId, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            string? previous = null;
            var update = await _tenantConfigService.UpdateAsync(tenantId, row =>
            {
                if (string.Equals(row.LastAuthClientId, clientId, StringComparison.OrdinalIgnoreCase))
                    return false;
                previous = row.LastAuthClientId;
                row.LastAuthClientId = clientId;
                row.LastAuthClientIdSince = DateTime.UtcNow;
                return true;
            }, "auth", "auth client-id tracking");

            if (update.Status == TenantConfigUpdateStatus.Updated)
            {
                _logger.LogInformation(
                    "Tenant {TenantId} logins now arrive via app registration {ClientId} (was: {Previous})",
                    tenantId, clientId, previous ?? "(none recorded)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auth client-id tracking persist failed for tenant {TenantId} — will retry on a later login", tenantId);
        }
    }

    /// <summary>
    /// If the tenant's suspension has expired (Disabled=true but DisabledUntil is past),
    /// clears the disabled state and persists the change. The cached view only triggers the check:
    /// the decision runs on the FRESH row, so a suspension a Global Admin renewed meanwhile (or the
    /// offboarding tombstone) is never lifted, and this login's view takes over the stored state.
    /// MUST run before BuildAuthResult because it mutates tenantConfig.Disabled.
    /// </summary>
    internal async Task HandleAutoReEnableAsync(
        TenantConfiguration tenantConfig, string tenantId)
    {
        if (!tenantConfig.Disabled || tenantConfig.IsCurrentlyDisabled())
            return;

        try
        {
            var update = await _tenantConfigService.UpdateAsync(tenantId, row =>
            {
                if (!row.Disabled || row.IsCurrentlyDisabled())
                    return false;
                row.Disabled = false;
                row.DisabledReason = null;
                row.DisabledUntil = null;
                row.UpdatedBy = "System (auto-re-enable)";
                return true;
            }, "auth", "auto-re-enable after suspension expiry");

            if (update.Config != null)
            {
                if (update.Status == TenantConfigUpdateStatus.Updated)
                {
                    _logger.LogInformation(
                        "Tenant {TenantId} auto-re-enabled: DisabledUntil ({DisabledUntil}) has expired",
                        tenantId, tenantConfig.DisabledUntil?.ToString("o"));
                }
                tenantConfig.Disabled = update.Config.Disabled;
                tenantConfig.DisabledReason = update.Config.DisabledReason;
                tenantConfig.DisabledUntil = update.Config.DisabledUntil;
                return;
            }
            // NotFound / Conflict: storage could not settle it — fall through.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-re-enable persist failed for tenant {TenantId} — will retry on next login", tenantId);
        }

        // Best-effort: the expired suspension still lets THIS login through; the next login
        // re-runs the auto-re-enable. Must not 500 auth/me.
        tenantConfig.Disabled = false;
        tenantConfig.DisabledReason = null;
        tenantConfig.DisabledUntil = null;
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
        bool isGlobalReader,
        bool isPreviewApproved,
        MemberRoleInfo? memberRole,
        McpAccessCheckResult mcpCheck,
        bool hasTenantAdmins,
        string tenantId, string upn, string displayName, string objectId,
        IReadOnlyCollection<string>? delegatedTenantIds = null,
        string homedApp = "primary",
        DateTime? whatsNewSeenPlatformUtc = null,
        DateTime? whatsNewSeenAgentUtc = null,
        bool mcpClientRegistrationEnabled = false)
    {
        // A delegated ("MSP") admin manages a subset of OTHER tenants. They are explicitly authorized, so —
        // like a Global Admin / Reader — they bypass the private-preview gate even when their own home tenant
        // is not on the waitlist. Empty/null for non-delegated callers.
        var managedTenantIds = delegatedTenantIds ?? System.Array.Empty<string>();
        var isDelegated = managedTenantIds.Count > 0;
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

        // Gate 2: Activation gate (platform roles — GlobalAdmin / GlobalReader — and delegated MSP
        // admins bypass). Wording is mode-neutral on purpose: with auto-approve on, activation
        // completes within a couple of minutes; with it off, the tenant waits for manual approval —
        // the message must hold for both without plumbing the flag into this pure method. The web
        // app accepts the legacy "PrivatePreview" code too, so backend and web deploy in any order.
        if (!isGlobalAdmin && !isGlobalReader && !isDelegated && !isPreviewApproved)
        {
            return AuthDecisionResult.Blocked(HttpStatusCode.Forbidden, new
            {
                error = "PendingActivation",
                message = "Autopilot Monitor is free to use \u2014 every new organization goes through a short activation step. Your tenant is being activated; this usually completes within a couple of minutes."
            });
        }

        // Determine admin status: auto-admin if first user (no existing admins and no role yet).
        // needsAutoAdmin keys off the absence of ANY effective role (table or claim) so a
        // claim-derived Admin/Operator in a claim-only tenant is NOT written into the table.
        // GlobalReader semantics are ADDITIVE (the role adds cross-tenant read; it never removes a
        // user's independent tenant-role write rights). Auto-admin, however, is not an existing right —
        // it SILENTLY grants write on first login. We decline that for a read-only-flagged identity:
        // it removes nothing, and a GlobalReader who should also administer their tenant can be added
        // to TenantAdmins explicitly. (A pure GlobalReader stays read-only everywhere via the write
        // evaluators denying a roleless caller.)
        // Auto-admin is NOT an existing right — it SILENTLY grants write on first login. Decline it for any
        // identity that reached this point on a cross-tenant ticket rather than as a genuine home-tenant
        // first-user: a read-only GlobalReader, AND a delegated ("MSP") admin. A delegated caller logged in
        // to manage OTHER tenants and bypassed the preview gate; auto-admining their own (possibly
        // non-customer) home tenant would convert a read-only delegated assignment into write authority over
        // it. A delegated user who legitimately administers their own tenant has a TenantAdmins row (memberRole
        // != null), so this only suppresses the silent grant, never an existing right.
        bool isTenantAdmin = memberRole?.Role == Constants.TenantRoles.Admin;
        bool needsAutoAdmin = memberRole == null && !hasTenantAdmins && !isGlobalReader && !isDelegated;
        if (needsAutoAdmin)
        {
            isTenantAdmin = true;
        }

        // After auto-admin, re-derive role: the auto-admin gets Admin role,
        // otherwise use the fetched memberRole.
        string? role = needsAutoAdmin ? Constants.TenantRoles.Admin : memberRole?.Role;
        bool canManageBootstrapTokens = needsAutoAdmin || (memberRole?.CanManageBootstrapTokens ?? false);

        return AuthDecisionResult.Success(new AuthMeResponse
        {
            TenantId = tenantId,
            Upn = upn,
            DisplayName = displayName,
            ObjectId = objectId,
            IsGlobalAdmin = isGlobalAdmin,
            IsGlobalReader = isGlobalReader,
            IsTenantAdmin = isTenantAdmin,
            // Delegated ("MSP") scope: the OTHER tenants this caller may manage (read-only this phase) and a
            // convenience flag. The web app uses these for fleet/switcher UI, bounded to this set.
            IsDelegated = isDelegated,
            DelegatedTenantIds = managedTenantIds,
            Role = role,
            CanManageBootstrapTokens = canManageBootstrapTokens,
            HasMcpAccess = mcpCheck.IsAllowed,
            // Dual app-reg window: which app registration this tenant is homed on. The web app
            // stores this per browser (localStorage) so the NEXT login uses the right app —
            // the deferred, no-roundtrip learning mechanism of the parallel operation model.
            HomedApp = homedApp,
            // EFFECTIVE availability flags (drive sidebar/section visibility): bootstrap is
            // included in Pro, Unrestricted Mode requires Pro + the GA on-request gate.
            BootstrapTokenEnabled = TenantEntitlementService.IsBootstrapEnabled(tenantConfig, DateTime.UtcNow),
            UnrestrictedModeEnabled =
                FeatureEntitlementCatalog.Get(TenantEntitlementService.Resolve(tenantConfig, DateTime.UtcNow)).UnrestrictedModeAvailable
                && tenantConfig.UnrestrictedModeEnabled,
            McpClientRegistrationEnabled = mcpClientRegistrationEnabled,
            WhatsNewSeenPlatformUtc = whatsNewSeenPlatformUtc,
            WhatsNewSeenAgentUtc = whatsNewSeenAgentUtc
        }, needsAutoAdmin);
    }

    internal static bool TryNormalizeWhatsNewSeen(
        WhatsNewSeenRequest? body,
        DateTime nowUtc,
        out string channel,
        out DateTime seenUtc,
        out string error)
    {
        channel = string.Empty;
        seenUtc = default;
        error = string.Empty;

        if (body == null)
        {
            error = "Request body is required";
            return false;
        }

        channel = (body.Channel ?? string.Empty).Trim().ToLowerInvariant();
        if (channel is not ("platform" or "agent"))
        {
            error = "channel must be 'platform' or 'agent'";
            return false;
        }

        if (body.SeenUtc == default)
        {
            error = "seenUtc is required";
            return false;
        }

        nowUtc = NormalizeUtc(nowUtc);
        seenUtc = NormalizeUtc(body.SeenUtc);
        if (seenUtc > nowUtc.AddMinutes(5))
        {
            // Client clocks can run ahead; clamp rather than reject so the marker remains harmless.
            seenUtc = nowUtc;
        }

        return true;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

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
