using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// The MANAGED tenant's side of delegated administration: "who can read my tenant" and the customer's own
/// revoke of a self-service delegation. Always the caller's JWT tenant (no <c>{tenantId}</c> template).
/// Operator-provisioned grants are listed but not revocable here — they were consciously provisioned by
/// platform operators and carry no slot/hold semantics (the trust pages point customers to support).
/// </summary>
public class DelegationManagedTenantFunction
{
    private readonly ILogger<DelegationManagedTenantFunction> _logger;
    private readonly DelegationSelfService _svc;

    public DelegationManagedTenantFunction(ILogger<DelegationManagedTenantFunction> logger, DelegationSelfService svc)
    {
        _logger = logger;
        _svc = svc;
    }

    /// <summary>GET /api/delegations/managers — every party with delegated read access to the caller's tenant. TenantAdminOrGlobalReader.</summary>
    [Function("GetTenantManagers")]
    [Authorize]
    public async Task<HttpResponseData> GetManagers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delegations/managers")] HttpRequestData req)
    {
        var tenantId = req.GetRequestContext().TenantId;
        var managers = await _svc.ListManagersAsync(tenantId);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new TenantManagerListResponse
        {
            TenantId = tenantId,
            Managers = managers.Select(m => new TenantManagerItem
            {
                GroupId = m.GroupId,
                OwnerTenantId = m.OwnerTenantId,
                OwnerDomain = m.OwnerDomain,
                Name = m.Name,
                Source = m.Source,
                Assignees = m.Assignees.Select(a => new TenantManagerAssignee { Upn = a.Upn, Role = a.Role, IsEnabled = a.IsEnabled }).ToList(),
                SinceUtc = m.SinceUtc,
                Revocable = m.Revocable,
            }).ToList(),
        });
        return response;
    }

    /// <summary>POST /api/delegations/managers/revoke { homeTenantId } — end a self-service delegation from the customer's side. TenantAdminOrGA.</summary>
    [Function("RevokeTenantManager")]
    [Authorize]
    public async Task<HttpResponseData> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "delegations/managers/revoke")] HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var body = await req.ReadFromJsonAsync<RevokeTenantManagerRequest>();
        if (body == null || !Guid.TryParse(body.HomeTenantId, out _))
        {
            return await req.BadRequestAsync("a valid homeTenantId (GUID) is required");
        }

        var result = await _svc.RevokeManagerAsync(ctx.TenantId, body.HomeTenantId, ctx.UserPrincipalName);
        if (!result.Ok)
            return await DelegationSelfServiceFunction.FailAsync(req, result.Failure!);

        _logger.LogInformation("[Delegation] {Tenant} revoked self-service access of {Home} by {By}", ctx.TenantId, body.HomeTenantId, ctx.UserPrincipalName);
        return await req.OkAsync(new MessageResponse { Message = "Access revoked" });
    }
}
