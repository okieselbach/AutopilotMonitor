using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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

    public McpUserFunction(
        ILogger<McpUserFunction> logger,
        McpUserService mcpUserService,
        AdminIdentityResolver identityResolver)
    {
        _logger = logger;
        _mcpUserService = mcpUserService;
        _identityResolver = identityResolver;
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
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new { error = "UPN is required" });
            return badResponse;
        }
        var bindingError = IdentityBindingRequest.ValidateOptional(body.HomeTenantId, body.ObjectId);
        if (bindingError != null)
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new { error = bindingError });
            return badResponse;
        }
        var identity = await IdentityBindingRequest.ResolveForGrantAsync(_identityResolver, body.Upn, body.HomeTenantId, body.ObjectId);
        if (identity == null)
        {
            var unresolved = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await unresolved.WriteAsJsonAsync(new { error = IdentityBindingRequest.HomeTenantUnresolvedMessage, code = IdentityBindingRequest.HomeTenantUnresolvedCode });
            return unresolved;
        }

        McpUserEntry user;
        try
        {
            user = await _mcpUserService.AddMcpUserAsync(body.Upn, currentUpn!, identity.Value.TenantId, identity.Value.ObjectId);
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
    public string Upn { get; set; } = string.Empty;
    /// <summary>The grantee's HOME Entra tenant id (optional override) — resolved from sign-in history / UPN domain when omitted.</summary>
    public string? HomeTenantId { get; set; }
    /// <summary>The grantee's Entra object id (optional) — taken from sign-in history, else pinned on their first sign-in.</summary>
    public string? ObjectId { get; set; }
}

public class SetUsagePlanRequest
{
    public string? UsagePlan { get; set; }
}
