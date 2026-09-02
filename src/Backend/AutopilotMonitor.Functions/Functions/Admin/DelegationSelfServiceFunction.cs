using System.Net;
using AutopilotMonitor.Functions.Helpers;
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
/// HTTP surface of self-service delegation for the MANAGING (Pro / MSP) tenant, plus the accept endpoints a
/// customer's tenant admin uses. Every route acts on the caller's JWT tenant (no <c>{tenantId}</c> in the
/// templates — the cross-tenant guard would otherwise deny a managing tenant acting on a customer); the
/// core logic and every audit/hold rule live in <see cref="DelegationSelfService"/>.
/// </summary>
public class DelegationSelfServiceFunction
{
    /// <summary>Managed-tenant MCP usage is resolved for at most this many tenants per listing (Global Admin overrides can be large).</summary>
    internal const int ManagedUsageCap = 25;

    private readonly ILogger<DelegationSelfServiceFunction> _logger;
    private readonly DelegationSelfService _svc;
    private readonly DelegatedSlotService _slots;
    private readonly McpQuotaService _quota;
    private readonly IUserUsageRepository _usage;

    public DelegationSelfServiceFunction(
        ILogger<DelegationSelfServiceFunction> logger,
        DelegationSelfService svc,
        DelegatedSlotService slots,
        McpQuotaService quota,
        IUserUsageRepository usage)
    {
        _logger = logger;
        _svc = svc;
        _slots = slots;
        _quota = quota;
        _usage = usage;
    }

    // ── Slots + managed tenants ──────────────────────────────────────────────────

    /// <summary>GET /api/delegations/slots — the caller's tenant's slot usage. TenantAdminOrGlobalReader.</summary>
    [Function("GetDelegationSlots")]
    [Authorize]
    public async Task<HttpResponseData> GetSlots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delegations/slots")] HttpRequestData req)
    {
        var usage = await _slots.GetUsageAsync(req.GetRequestContext().TenantId);
        return await OkAsync(req, DelegatedSlotManagementFunction.ToResponse(usage));
    }

    /// <summary>GET /api/delegations/managed — the tenants the caller's tenant manages, with their MCP organization budget.</summary>
    [Function("GetDelegationManagedTenants")]
    [Authorize]
    public async Task<HttpResponseData> GetManaged(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delegations/managed")] HttpRequestData req)
    {
        var home = req.GetRequestContext().TenantId;
        var view = await _svc.ListManagedAsync(home);

        var items = new List<ManagedTenantItem>(view.Tenants.Count);
        var resolved = 0;
        foreach (var t in view.Tenants)
        {
            ManagedTenantQuotaUsage? usage = null;
            if (resolved < ManagedUsageCap)
            {
                usage = await ReadManagedUsageAsync(t.TenantId);
                resolved++;
            }
            items.Add(new ManagedTenantItem
            {
                TenantId = t.TenantId,
                Domain = await _svc.DomainAsync(t.TenantId),
                Source = t.Source,
                SinceUtc = t.SinceUtc,
                Removable = t.Removable,
                Usage = usage,
            });
        }

        return await OkAsync(req, new ManagedTenantListResponse
        {
            HomeTenantId = home,
            Slots = DelegatedSlotManagementFunction.ToResponse(view.Slots),
            Tenants = items,
        });
    }

    /// <summary>POST /api/delegations/managed/remove — end a self-service delegation to one managed tenant (24 h slot hold). TenantAdminOrGA.</summary>
    [Function("RemoveDelegationManagedTenant")]
    [Authorize]
    public async Task<HttpResponseData> RemoveManaged(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "delegations/managed/remove")] HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var body = await req.ReadFromJsonAsync<RemoveManagedTenantRequest>();
        if (body == null || !Guid.TryParse(body.TenantId, out _))
            return await BadAsync(req, "a valid tenantId (GUID) is required");

        var result = await _svc.RemoveManagedAsync(ctx.TenantId, body.TenantId, ctx.UserPrincipalName);
        return result.Ok ? await OkAsync(req, new { message = "Delegation ended" }) : await FailAsync(req, result.Failure!);
    }

    // ── Invitations ──────────────────────────────────────────────────────────────

    /// <summary>GET /api/delegations/invitations — every invitation of the caller's tenant (never the token).</summary>
    [Function("GetDelegationInvitations")]
    [Authorize]
    public async Task<HttpResponseData> GetInvitations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delegations/invitations")] HttpRequestData req)
    {
        var home = req.GetRequestContext().TenantId;
        var rows = await _svc.ListInvitationsAsync(home);
        var items = new List<DelegationInvitationItem>(rows.Count);
        foreach (var r in rows.OrderByDescending(r => r.CreatedAt))
        {
            items.Add(new DelegationInvitationItem
            {
                InvitationId = r.InvitationId,
                Status = _svc.EffectiveStatus(r),
                CreatedBy = r.CreatedBy,
                CreatedUtc = r.CreatedAt,
                ExpiresUtc = r.ExpiresAt,
                AcceptedUtc = r.AcceptedAt,
                AcceptedBy = r.AcceptedBy,
                TenantId = r.TenantId,
                TenantDomain = string.IsNullOrEmpty(r.TenantId) ? null : await _svc.DomainAsync(r.TenantId),
                HoldUntilUtc = r.HoldUntilUtc,
            });
        }
        return await OkAsync(req, new DelegationInvitationListResponse { HomeTenantId = home, Invitations = items });
    }

    /// <summary>POST /api/delegations/invitations — mint a single-use invitation link (token shown once). TenantAdminOrGA.</summary>
    [Function("CreateDelegationInvitation")]
    [Authorize]
    public async Task<HttpResponseData> CreateInvitation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "delegations/invitations")] HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var result = await _svc.CreateInvitationAsync(ctx.TenantId, ctx.UserPrincipalName);
        if (!result.Ok)
            return await FailAsync(req, result.Failure!);

        var (row, token) = result.Value;
        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new CreateDelegationInvitationResponse { InvitationId = row.InvitationId, Token = token, ExpiresUtc = row.ExpiresAt });
        return response;
    }

    /// <summary>DELETE /api/delegations/invitations/{invitationId} — cancel a pending invitation (frees its slot at once).</summary>
    [Function("CancelDelegationInvitation")]
    [Authorize]
    public async Task<HttpResponseData> CancelInvitation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "delegations/invitations/{invitationId}")] HttpRequestData req,
        string invitationId)
    {
        var ctx = req.GetRequestContext();
        var result = await _svc.CancelInvitationAsync(ctx.TenantId, invitationId, ctx.UserPrincipalName);
        return result.Ok ? await OkAsync(req, new { message = "Invitation cancelled" }) : await FailAsync(req, result.Failure!);
    }

    // ── Assignees (the managing tenant's own users) ──────────────────────────────

    /// <summary>GET /api/delegations/assignees — who in the caller's tenant may read the managed tenants.</summary>
    [Function("GetDelegationAssignees")]
    [Authorize]
    public async Task<HttpResponseData> GetAssignees(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delegations/assignees")] HttpRequestData req)
    {
        var home = req.GetRequestContext().TenantId;
        var assignees = await _svc.ListAssigneesAsync(home);
        return await OkAsync(req, new DelegationAssigneeListResponse
        {
            HomeTenantId = home,
            GroupId = Constants.TenantGroupIds.ForHomeTenant(home),
            Assignees = assignees.OrderBy(a => a.Upn, StringComparer.Ordinal).ToList(),
        });
    }

    /// <summary>POST /api/delegations/assignees — assign one of the caller's tenant members (read-only). TenantAdminOrGA.</summary>
    [Function("AssignDelegationAssignee")]
    [Authorize]
    public async Task<HttpResponseData> Assign(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "delegations/assignees")] HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var body = await req.ReadFromJsonAsync<DelegationAssignRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
            return await BadAsync(req, "upn is required");

        var result = await _svc.AssignAsync(ctx.TenantId, body.Upn, ctx.UserPrincipalName);
        if (!result.Ok)
            return await FailAsync(req, result.Failure!);
        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new DelegationAssignResponse { Assignment = result.Value! });
        return response;
    }

    /// <summary>DELETE /api/delegations/assignees/{upn} — unassign. TenantAdminOrGA.</summary>
    [Function("UnassignDelegationAssignee")]
    [Authorize]
    public async Task<HttpResponseData> Unassign(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "delegations/assignees/{upn}")] HttpRequestData req,
        string upn)
    {
        var ctx = req.GetRequestContext();
        var result = await _svc.UnassignAsync(ctx.TenantId, upn, ctx.UserPrincipalName);
        return result.Ok ? await OkAsync(req, new { message = "Unassigned" }) : await FailAsync(req, result.Failure!);
    }

    // ── Accept (the customer's tenant admin) ─────────────────────────────────────

    /// <summary>GET /api/delegations/accept?token= — what accepting would do; no mutation. TenantAdminOrGA.</summary>
    [Function("PreviewDelegationInvitation")]
    [Authorize]
    public async Task<HttpResponseData> Preview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "delegations/accept")] HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var token = req.Query["token"] ?? string.Empty;
        var result = await _svc.PreviewAsync(token, ctx.TenantId);
        if (!result.Ok)
            return await FailAsync(req, result.Failure!);
        var p = result.Value!;
        return await OkAsync(req, new DelegationAcceptPreviewResponse
        {
            HomeTenantId = p.HomeTenantId,
            HomeTenantDomain = p.HomeTenantDomain,
            ExpiresUtc = p.ExpiresUtc,
            Status = p.Status,
            TargetTenantId = p.TargetTenantId,
            TargetTenantDomain = p.TargetTenantDomain,
        });
    }

    /// <summary>POST /api/delegations/accept { token } — the caller's tenant joins the inviting tenant's managed set. TenantAdminOrGA.</summary>
    [Function("AcceptDelegationInvitation")]
    [Authorize]
    public async Task<HttpResponseData> Accept(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "delegations/accept")] HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var body = await req.ReadFromJsonAsync<AcceptDelegationInvitationRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Token))
            return await BadAsync(req, "token is required");

        var result = await _svc.AcceptAsync(body.Token, ctx.TenantId, ctx.UserPrincipalName);
        if (!result.Ok)
            return await FailAsync(req, result.Failure!);
        var o = result.Value!;
        return await OkAsync(req, new AcceptDelegationInvitationResponse { HomeTenantId = o.HomeTenantId, HomeTenantDomain = o.HomeTenantDomain, ManagedTenantId = o.ManagedTenantId });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The managed tenant's organization MCP budget — its own plan's windows and counters.</summary>
    private async Task<ManagedTenantQuotaUsage?> ReadManagedUsageAsync(string tenantId)
    {
        try
        {
            var limits = await _quota.ResolvePlanAsync(null, tenantId);
            var nowUtc = DateTime.UtcNow;
            var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyyMMdd");
            var today = nowUtc.ToString("yyyyMMdd");
            var rows = await _usage.GetTenantUsageAsync(tenantId, monthStart, today);
            return new ManagedTenantQuotaUsage
            {
                TenantPlan = limits.TenantPlan,
                TenantDailyLimit = limits.TenantDailyLimit,
                TenantMonthlyLimit = limits.TenantMonthlyLimit,
                TenantDailyUsed = rows.Where(r => r.Date == today).Sum(r => r.RequestCount),
                TenantMonthlyUsed = rows.Sum(r => r.RequestCount),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Delegation] Managed tenant usage lookup failed for {TenantId}", tenantId);
            return null;
        }
    }

    internal static async Task<HttpResponseData> FailAsync(HttpRequestData req, DelegationFailure failure)
    {
        if (failure.SlotViolation != null)
            return await DelegatedSlotResponses.ConflictAsync(req, failure.SlotViolation);
        var response = req.CreateResponse((HttpStatusCode)failure.Status);
        await response.WriteAsJsonAsync(new { error = failure.Message, code = failure.Code });
        return response;
    }

    private static async Task<HttpResponseData> OkAsync(HttpRequestData req, object body)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(body);
        return response;
    }

    private static async Task<HttpResponseData> BadAsync(HttpRequestData req, string error)
    {
        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
        await bad.WriteAsJsonAsync(new { error });
        return bad;
    }
}
