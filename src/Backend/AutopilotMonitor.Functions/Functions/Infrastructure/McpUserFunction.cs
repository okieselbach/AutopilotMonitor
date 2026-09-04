using System.Net;
using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Infrastructure;

/// <summary>
/// CRUD endpoints for MCP user whitelist management + access check for the remote MCP server.
/// </summary>
public class McpUserFunction
{
    private readonly ILogger<McpUserFunction> _logger;
    private readonly McpUserService _mcpUserService;
    private readonly AdminIdentityResolver _identityResolver;
    private readonly IMetricsRepository _metricsRepo;
    private readonly OpsEventService _opsEvents;
    private readonly IMemoryCache _cache;

    public McpUserFunction(
        ILogger<McpUserFunction> logger,
        McpUserService mcpUserService,
        AdminIdentityResolver identityResolver,
        IMetricsRepository metricsRepo,
        OpsEventService opsEvents,
        IMemoryCache cache)
    {
        _logger = logger;
        _mcpUserService = mcpUserService;
        _identityResolver = identityResolver;
        _metricsRepo = metricsRepo;
        _opsEvents = opsEvents;
        _cache = cache;
    }

    /// <summary>
    /// GET /api/admin/mcp-users
    /// Lists all MCP users + current policy. GlobalAdminOnly.
    /// </summary>
    [Function("GetMcpUsers")]
    [Authorize]
    public async Task<HttpResponseData> GetMcpUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/mcp-users")] HttpRequestData req)
    {
        var users = await _mcpUserService.GetAllMcpUsersAsync();
        var policy = await _mcpUserService.GetPolicyAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new GetMcpUsersResponse { Policy = policy.ToString(), Users = users });
        return response;
    }

    /// <summary>
    /// POST /api/global/mcp-users
    /// Adds a user to the MCP whitelist. GlobalAdminOnly.
    /// Body: { "upn": "user@domain.com", "homeTenantId"?: "&lt;guid&gt;", "objectId"?: "&lt;guid&gt;" }.
    /// The row is inert until the UPN is bound to the identity that may use it; the home tenant is resolved
    /// automatically (sign-in history, then UPN domain → onboarded tenant) and the body may override it —
    /// 422 HomeTenantUnresolved when neither works, 409 when the UPN is already bound elsewhere. Same
    /// contract as POST auth/global-admins and POST global/delegated-admins.
    /// </summary>
    [Function("AddMcpUser")]
    [Authorize]
    public async Task<HttpResponseData> AddMcpUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/mcp-users")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        var currentUpn = principal?.GetUserPrincipalName();

        var body = await req.ReadFromJsonAsync<AddMcpUserRequest>();
        var isApplication = !string.IsNullOrWhiteSpace(body?.ApplicationId);
        if (body == null || (string.IsNullOrWhiteSpace(body.Upn) && !isApplication))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new { error = "UPN or applicationId is required" });
            return badResponse;
        }
        if (isApplication && !Guid.TryParse(body.ApplicationId, out _))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new { error = "applicationId must be the application's (client) id GUID" });
            return badResponse;
        }
        // An application has no sign-in history and no UPN domain to resolve a home tenant from: the tenant
        // its service principal lives in must be named. The object id (that tenant's SP object id) is pinned
        // on the first call, exactly like a person's.
        var bindingError = isApplication
            ? IdentityBindingRequest.Validate(body.HomeTenantId, body.ObjectId)
            : IdentityBindingRequest.ValidateOptional(body.HomeTenantId, body.ObjectId);
        if (bindingError != null)
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new { error = bindingError });
            return badResponse;
        }
        var principalKey = isApplication ? Constants.PrincipalKeys.ForApplication(body.ApplicationId!) : body.Upn;
        var identity = await IdentityBindingRequest.ResolveForGrantAsync(_identityResolver, principalKey, body.HomeTenantId, body.ObjectId);
        if (identity == null)
        {
            var unresolved = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await unresolved.WriteAsJsonAsync(new { error = IdentityBindingRequest.HomeTenantUnresolvedMessage, code = IdentityBindingRequest.HomeTenantUnresolvedCode });
            return unresolved;
        }

        McpUserEntry user;
        try
        {
            user = await _mcpUserService.AddMcpUserAsync(principalKey, currentUpn!, identity.Value.TenantId, identity.Value.ObjectId);
        }
        catch (IdentityBindingConflictException ex)
        {
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteAsJsonAsync(new { error = ex.Message });
            return conflict;
        }

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new AddMcpUserResponse { User = user });
        return response;
    }

    /// <summary>
    /// A service principal never signs in to the portal, so the MCP front door is where its presence is
    /// recorded: the sign-in history row (its "last seen", and what the per-user usage view keys the
    /// display on) plus, when the key has no history yet, the once-only
    /// <see cref="OpsEventTypes.McpServicePrincipalFirstSeen"/> so new automation is noticed the moment it
    /// starts. Fire-and-forget: observation must never delay or fail the access check.
    /// </summary>
    private void ObserveApplicationSession(ClaimsPrincipal principal, McpAccessCheckResult result)
    {
        var tenantId = principal.GetTenantId() ?? string.Empty;
        var objectId = principal.GetObjectId() ?? string.Empty;
        var applicationId = principal.GetApplicationId() ?? string.Empty;
        var principalKey = result.Upn;
        // The history read is a filtered scan meant for grant time; one per application per instance is
        // enough — after that the memory cache says "seen" and only the cheap login upsert remains.
        var seenKey = $"mcp-app-seen:{principalKey}";
        var alreadySeen = _cache.TryGetValue(seenKey, out _);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!alreadySeen)
                {
                    var history = await _metricsRepo.GetSignInIdentitiesByUpnAsync(principalKey);
                    if (history.Count == 0)
                        await _opsEvents.RecordMcpServicePrincipalFirstSeenAsync(tenantId, principalKey, applicationId, objectId, result.AccessGrant);
                    _cache.Set(seenKey, true, TimeSpan.FromHours(24));
                }
                await _metricsRepo.RecordUserLoginAsync(tenantId, principalKey, displayName: $"Service principal {applicationId}", objectId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[McpAccess] Failed to record the application session for {PrincipalKey}", principalKey);
            }
        });
    }

    /// <summary>
    /// DELETE /api/admin/mcp-users/{upn}
    /// Removes a user from the MCP whitelist. GlobalAdminOnly.
    /// </summary>
    [Function("RemoveMcpUser")]
    [Authorize]
    public async Task<HttpResponseData> RemoveMcpUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/mcp-users/{upn}")] HttpRequestData req,
        string upn)
    {
        await _mcpUserService.RemoveMcpUserAsync(upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "MCP user removed" });
        return response;
    }

    /// <summary>
    /// PATCH /api/admin/mcp-users/{upn}/enable
    /// Enables a previously disabled MCP user. GlobalAdminOnly.
    /// </summary>
    [Function("EnableMcpUser")]
    [Authorize]
    public async Task<HttpResponseData> EnableMcpUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/mcp-users/{upn}/enable")] HttpRequestData req,
        string upn)
    {
        await _mcpUserService.SetMcpUserEnabledAsync(upn, true);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "MCP user enabled" });
        return response;
    }

    /// <summary>
    /// PATCH /api/admin/mcp-users/{upn}/disable
    /// Disables an MCP user without removing them. GlobalAdminOnly.
    /// </summary>
    [Function("DisableMcpUser")]
    [Authorize]
    public async Task<HttpResponseData> DisableMcpUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/mcp-users/{upn}/disable")] HttpRequestData req,
        string upn)
    {
        await _mcpUserService.SetMcpUserEnabledAsync(upn, false);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "MCP user disabled" });
        return response;
    }

    /// <summary>
    /// PATCH /api/global/mcp-users/{upn}/usage-plan
    /// Sets the usage plan for an MCP user. GlobalAdminOnly.
    /// Body: { "usagePlan": "pro" } — null or empty to inherit tenant default.
    /// </summary>
    [Function("SetMcpUserUsagePlan")]
    [Authorize]
    public async Task<HttpResponseData> SetMcpUserUsagePlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/mcp-users/{upn}/usage-plan")] HttpRequestData req,
        string upn)
    {
        var body = await req.ReadFromJsonAsync<SetUsagePlanRequest>();
        var usagePlan = string.IsNullOrWhiteSpace(body?.UsagePlan) ? null : body.UsagePlan.ToLowerInvariant();

        var success = await _mcpUserService.SetMcpUserUsagePlanAsync(upn, usagePlan);
        if (!success)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "MCP user not found" });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new SetMcpUserUsagePlanResponse { Upn = upn, UsagePlan = usagePlan ?? "(inherit)" });
        return response;
    }

    /// <summary>
    /// GET /api/auth/mcp
    /// Lightweight access check for the remote MCP server.
    /// Called by MCP server auth middleware to validate if a user can access MCP.
    /// AuthenticatedUser policy — the endpoint itself checks MCP access via service.
    /// </summary>
    [Function("CheckMcpAccess")]
    [Authorize]
    public async Task<HttpResponseData> CheckMcpAccess(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/mcp")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        var upn = principal?.GetUserPrincipalName();

        // JWT tid + oid complete the caller's identity: platform-role and delegated (MSP) grants resolve on
        // the identity binding (never the UPN alone); the delegated seat additionally requires a Pro home tenant.
        // The token's app roles feed the AllMembers member check (claim-derived Admin/Operator when the tenant opted in).
        var result = await _mcpUserService.IsAllowedAsync(
            upn, principal?.GetTenantId(), principal?.GetObjectId(), principal?.GetAppRoles());

        if (result.IsAllowed && principal != null && principal.IsApplicationPrincipal())
            ObserveApplicationSession(principal, result);

        var response = req.CreateResponse(result.IsAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        // Only surface platform-role flags when the caller actually has one (null ⇒ key omitted).
        // A normal tenant user gets neither field: the MCP access-guard reads `globalRole` to decide
        // cross-tenant routing (and keeps reading `isGlobalAdmin === true` for back-compat /
        // write-tier hints), and we avoid hinting to ordinary callers that a platform tier even
        // exists. delegatedTenantIds bounds cross-tenant routing (/api/global/*?tenantId=<managed>)
        // to exactly these tenants and is only emitted for a caller holding a delegated assignment.
        await response.WriteAsJsonAsync(new CheckMcpAccessResponse
        {
            Allowed = result.IsAllowed,
            Upn = result.Upn,
            AccessGrant = result.AccessGrant,
            Reason = result.Reason,
            IsGlobalAdmin = result.IsGlobalAdmin ? true : null,
            GlobalRole = string.IsNullOrEmpty(result.GlobalRole) ? null : result.GlobalRole,
            DelegatedTenantIds = result.DelegatedTenantIds is { Count: > 0 } ? result.DelegatedTenantIds : null,
            DelegatedRole = string.IsNullOrEmpty(result.DelegatedRole) ? null : result.DelegatedRole,
        });
        return response;
    }
}

public class AddMcpUserRequest
{
    /// <summary>The person's UPN. Leave empty when adding an application (<see cref="ApplicationId"/>).</summary>
    public string Upn { get; set; } = string.Empty;
    /// <summary>
    /// The Entra application (client) id of a service principal; stored under the <c>app:&lt;client-id&gt;</c>
    /// key. <see cref="HomeTenantId"/> is then required (no sign-in history to resolve it from).
    /// </summary>
    public string? ApplicationId { get; set; }
    /// <summary>The grantee's HOME Entra tenant id (optional override) — resolved from sign-in history / UPN domain when omitted.</summary>
    public string? HomeTenantId { get; set; }
    /// <summary>The grantee's Entra object id (optional) — taken from sign-in history, else pinned on their first sign-in.</summary>
    public string? ObjectId { get; set; }
}

public class SetUsagePlanRequest
{
    public string? UsagePlan { get; set; }
}
