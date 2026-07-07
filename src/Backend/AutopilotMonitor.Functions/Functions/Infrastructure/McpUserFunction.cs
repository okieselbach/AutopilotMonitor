using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Services;
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

    public McpUserFunction(
        ILogger<McpUserFunction> logger,
        McpUserService mcpUserService)
    {
        _logger = logger;
        _mcpUserService = mcpUserService;
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
        await response.WriteAsJsonAsync(new { policy = policy.ToString(), users });
        return response;
    }

    /// <summary>
    /// POST /api/admin/mcp-users
    /// Adds a user to the MCP whitelist. GlobalAdminOnly.
    /// Body: { "upn": "user@domain.com" }
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

        var user = await _mcpUserService.AddMcpUserAsync(body.Upn, currentUpn!);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { user });
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
        await response.WriteAsJsonAsync(new { upn, usagePlan = usagePlan ?? "(inherit)" });
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

        // JWT tid = the caller's home tenant — gates the delegated (MSP) auto-grant (Enterprise-only seat).
        var result = await _mcpUserService.IsAllowedAsync(upn, principal?.GetTenantId());

        var response = req.CreateResponse(result.IsAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        var payload = new Dictionary<string, object?>
        {
            ["allowed"] = result.IsAllowed,
            ["upn"] = result.Upn,
            ["accessGrant"] = result.AccessGrant,
            ["reason"] = result.Reason,
        };
        // Only surface platform-role flags when the caller actually has one. A normal tenant user gets
        // neither field: the MCP access-guard reads `globalRole` to decide cross-tenant routing (and
        // keeps reading `isGlobalAdmin === true` for back-compat / write-tier hints), and we avoid
        // hinting to ordinary callers that a platform tier even exists.
        if (result.IsGlobalAdmin)
        {
            payload["isGlobalAdmin"] = true;
        }
        if (!string.IsNullOrEmpty(result.GlobalRole))
        {
            payload["globalRole"] = result.GlobalRole; // "GlobalAdmin" | "GlobalReader"
        }
        // Delegated (scoped-global / MSP) scope, when present. The MCP access-guard reads
        // delegatedTenantIds to route the caller cross-tenant (/api/global/*?tenantId=<managed>) bounded to
        // exactly these tenants, and to reject any tool call that does not name one of them. Only emitted
        // for a caller that actually holds a delegated assignment — ordinary tenant users get neither field.
        if (result.DelegatedTenantIds is { Count: > 0 })
        {
            payload["delegatedTenantIds"] = result.DelegatedTenantIds; // string[] (lowercase)
        }
        if (!string.IsNullOrEmpty(result.DelegatedRole))
        {
            payload["delegatedRole"] = result.DelegatedRole; // "DelegatedAdmin" | "DelegatedReader"
        }
        await response.WriteAsJsonAsync(payload);
        return response;
    }
}

public class AddMcpUserRequest
{
    public string Upn { get; set; } = string.Empty;
}

public class SetUsagePlanRequest
{
    public string? UsagePlan { get; set; }
}
