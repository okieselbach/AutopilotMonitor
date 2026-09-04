using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Operator surface for a managing (MSP) tenant's <b>delegated tenant slots</b> — how many distinct
/// customer tenants its users may manage against its plan limit or Global Admin override — and the escape
/// hatch that ends a 24-hour release hold early (support call: "I need one back in today"). Platform-operational
/// (excludeDelegated): a delegated caller never reads another tenant's slot accounting here.
/// </summary>
public class DelegatedSlotManagementFunction
{
    private readonly ILogger<DelegatedSlotManagementFunction> _logger;
    private readonly DelegatedSlotService _slots;
    private readonly IMaintenanceRepository _audit;

    public DelegatedSlotManagementFunction(ILogger<DelegatedSlotManagementFunction> logger, DelegatedSlotService slots, IMaintenanceRepository audit)
    {
        _logger = logger;
        _slots = slots;
        _audit = audit;
    }

    /// <summary>GET /api/global/delegated-slots/{tenantId} — slot usage of the managing tenant. GlobalReadOrAdmin.</summary>
    [Function("GetDelegatedSlotUsage")]
    [Authorize]
    public async Task<HttpResponseData> GetUsage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/delegated-slots/{tenantId}")] HttpRequestData req,
        string tenantId)
    {
        var target = req.GetRequestContext().TargetTenantId;
        _logger.LogInformation("Delegated slot usage requested for {TenantId}", target);

        var usage = await _slots.GetUsageAsync(target);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(ToResponse(usage));
        return response;
    }

    /// <summary>
    /// POST /api/global/delegated-slots/{tenantId}/release-hold — end one hold ({ "invitationId" }) or every
    /// active hold ({ "all": true }) now. GlobalAdminOnly. Audited under the managing tenant.
    /// </summary>
    [Function("ReleaseDelegatedSlotHold")]
    [Authorize]
    public async Task<HttpResponseData> ReleaseHold(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/delegated-slots/{tenantId}/release-hold")] HttpRequestData req,
        string tenantId)
    {
        var ctx = req.GetRequestContext();
        var target = ctx.TargetTenantId;
        var body = await req.ReadFromJsonAsync<ReleaseDelegatedSlotHoldRequest>();
        if (body == null || (!body.All && string.IsNullOrWhiteSpace(body.InvitationId)))
        {
            return await req.BadRequestAsync("invitationId or all=true is required");
        }

        var released = await _slots.ReleaseHoldAsync(target, body.InvitationId, body.All, ctx.UserPrincipalName);
        if (released > 0)
        {
            await _audit.LogAuditEntryAsync(target, "UPDATE", "DelegatedSlotHold", body.All ? "*" : body.InvitationId!, ctx.UserPrincipalName,
                new Dictionary<string, string> { { "Reason", "hold-released-early" }, { "Released", released.ToString() } });
        }
        _logger.LogInformation("Delegated slot holds released for {TenantId}: {Count} by {By}", target, released, ctx.UserPrincipalName);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new ReleaseDelegatedSlotHoldResponse { HomeTenantId = target, Released = released });
        return response;
    }

    internal static DelegatedSlotUsageResponse ToResponse(DelegatedSlotUsage usage) => new()
    {
        HomeTenantId = usage.HomeTenantId,
        Limit = usage.Limit,
        CatalogLimit = usage.CatalogLimit,
        OverrideLimit = usage.OverrideLimit,
        Used = usage.Used,
        ManagedTenantIds = usage.ManagedTenantIds.OrderBy(t => t, StringComparer.Ordinal).ToList(),
        PendingInvitations = usage.PendingInvitations,
        Holds = usage.Holds.Select(h => new DelegatedSlotHold
        {
            InvitationId = h.InvitationId,
            TenantId = h.TenantId,
            HoldUntilUtc = h.HoldUntilUtc ?? DateTime.MinValue,
            ReleasedBy = h.ReleasedBy ?? string.Empty,
        }).ToList(),
    };
}
