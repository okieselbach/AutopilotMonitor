using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Self-hosted MCP client registrations: Tenant Admins (or a Global Admin) manage the exact callbacks of
/// clients their organization runs itself; the MCP server's OAuth proxy resolves a client id
/// <c>amc_&lt;id&gt;</c> through the anonymous lookup. The lookup sits under <c>/api/auth</c> because the
/// Function App requires client certificates outside a fixed list of path prefixes.
/// </summary>
public class McpClientRegistrationFunction
{
    /// <summary>Per source IP and minute. Every lookup comes from the MCP server's egress IP, which caches answers for a minute.</summary>
    internal const int LookupRateLimitPerMinute = 120;

    private readonly ILogger<McpClientRegistrationFunction> _logger;
    private readonly McpClientRegistrationService _service;
    private readonly RateLimitService _rateLimitService;

    public McpClientRegistrationFunction(
        ILogger<McpClientRegistrationFunction> logger,
        McpClientRegistrationService service,
        RateLimitService rateLimitService)
    {
        _logger = logger;
        _service = service;
        _rateLimitService = rateLimitService;
    }

    /// <summary>GET /api/tenants/{tenantId}/mcp-client-registrations</summary>
    [Function("GetMcpClientRegistrations")]
    [Authorize]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tenants/{tenantId}/mcp-client-registrations")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var requestCtx = context.GetRequestContext();
        var registrations = await _service.ListAsync(requestCtx.TargetTenantId);
        return await req.OkAsync(new McpClientRegistrationListResponse
        {
            Enabled = await _service.IsEnabledAsync(),
            MaxRegistrations = McpClientRegistrationService.MaxRegistrationsPerTenant,
            ServerUrl = $"{Constants.McpServerBaseUrl}/mcp",
            Registrations = registrations.Select(ToItem).ToList(),
        });
    }

    /// <summary>POST /api/tenants/{tenantId}/mcp-client-registrations</summary>
    [Function("CreateMcpClientRegistration")]
    [Authorize]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/mcp-client-registrations")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var requestCtx = context.GetRequestContext();
        var read = await req.ReadAsync<CreateMcpClientRegistrationRequest>();
        if (read.Error != null) return read.Error;

        var result = await _service.CreateAsync(
            requestCtx.TargetTenantId, read.Value!.Name, read.Value.RedirectUri, requestCtx.UserPrincipalName);
        if (result.Registration == null)
        {
            var code = result.Status switch
            {
                HttpStatusCode.Forbidden => Constants.ApiErrorCodes.Forbidden,
                HttpStatusCode.Conflict => Constants.ApiErrorCodes.Conflict,
                HttpStatusCode.InternalServerError => Constants.ApiErrorCodes.InternalError,
                _ => Constants.ApiErrorCodes.BadRequest,
            };
            return await req.ErrorAsync(result.Status, code, result.Error!);
        }
        return await req.CreatedAsync(new CreateMcpClientRegistrationResponse { Registration = ToItem(result.Registration) });
    }

    /// <summary>DELETE /api/tenants/{tenantId}/mcp-client-registrations/{registrationId}</summary>
    [Function("DeleteMcpClientRegistration")]
    [Authorize]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "tenants/{tenantId}/mcp-client-registrations/{registrationId}")] HttpRequestData req,
        string tenantId,
        string registrationId,
        FunctionContext context)
    {
        var requestCtx = context.GetRequestContext();
        var deleted = await _service.DeleteAsync(requestCtx.TargetTenantId, registrationId, requestCtx.UserPrincipalName);
        return deleted
            ? await req.OkAsync(new MessageResponse { Message = "Registration deleted" })
            : await req.NotFoundAsync("Registration not found");
    }

    /// <summary>
    /// GET /api/auth/mcp/client-registrations/{registrationId} — anonymous; the MCP server's OAuth proxy
    /// resolves a tenant-bound client id before any user token exists. 404 for an unknown id and while the
    /// operator switch is off; the id is 128 random bits, the answer names nothing secret.
    /// </summary>
    [Function("LookupMcpClientRegistration")]
    public async Task<HttpResponseData> Lookup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/mcp/client-registrations/{registrationId}")] HttpRequestData req,
        string registrationId)
    {
        var clientIp = ClientIpExtractor.GetTrustedClientIp(req);
        var rateLimit = _rateLimitService.CheckRateLimit($"mcp-client-lookup:{clientIp}", LookupRateLimitPerMinute);
        if (!rateLimit.IsAllowed)
        {
            return await req.ErrorAsync(HttpStatusCode.TooManyRequests, Constants.ApiErrorCodes.RateLimited,
                "Rate limit exceeded. Try again later.",
                retryAfterSeconds: rateLimit.RetryAfter is { } retryAfter ? (int)retryAfter.TotalSeconds : null);
        }

        var registration = await _service.LookupAsync(registrationId);
        if (registration == null)
            return await req.NotFoundAsync("Registration not found");

        return await req.OkAsync(new McpClientRegistrationLookupResponse
        {
            RegistrationId = registration.RegistrationId,
            TenantId = registration.TenantId,
            RedirectUri = registration.RedirectUri,
            Name = registration.Name,
        });
    }

    internal static McpClientRegistrationItem ToItem(McpClientRegistration r) => new()
    {
        RegistrationId = r.RegistrationId,
        ClientId = McpClientRegistrationService.ClientIdOf(r.RegistrationId),
        Name = r.Name,
        RedirectUri = r.RedirectUri,
        CreatedBy = r.CreatedBy,
        CreatedUtc = r.CreatedAt,
    };
}
