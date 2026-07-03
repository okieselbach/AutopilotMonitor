using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// GlobalAdmin-only management of <b>Tenant Groups</b> — app-internal named bundles of tenants for the
/// delegated-admin ("MSP mode") tier. An operator assigns a delegated UPN to a group instead of to each
/// tenant; the UPN then reads every tenant in the group (resolved by <see cref="DelegatedAdminService"/>).
/// Adding a tenant to the group grants it to all assignees at once.
///
/// Reads are GlobalReadOrAdmin (a read-only Global Reader may audit groups); all mutations are
/// GlobalAdminOnly (enforced by <c>PolicyEnforcementMiddleware</c> via the route catalog), so a delegated
/// caller can never manage groups. ALL mutations go through <see cref="DelegatedAdminService"/> (never the
/// repository directly) so the delegated-scope cache is invalidated in lockstep.
///
/// Audit: access-affecting mutations are logged under the <b>affected managed tenant(s)</b>' trail (so the
/// customer sees "who can read my tenant" / "access removed"). Group create/rename carry no tenant context
/// and are not customer-visible access changes — they are logged operationally only (no AuditLogs partition).
/// </summary>
public class TenantGroupManagementFunction
{
    private readonly ILogger<TenantGroupManagementFunction> _logger;
    private readonly DelegatedAdminService _delegatedAdminService;
    private readonly IMaintenanceRepository _maintenanceRepo;
    private readonly ISignalRNotificationService _signalRService;

    private const string AuditEntity = "DelegatedGroupAccess";

    public TenantGroupManagementFunction(
        ILogger<TenantGroupManagementFunction> logger,
        DelegatedAdminService delegatedAdminService,
        IMaintenanceRepository maintenanceRepo,
        ISignalRNotificationService signalRService)
    {
        _logger = logger;
        _delegatedAdminService = delegatedAdminService;
        _maintenanceRepo = maintenanceRepo;
        _signalRService = signalRService;
    }

    /// <summary>GET /api/global/tenant-groups — list every group with tenants + assignee count. GlobalReadOrAdmin.</summary>
    [Function("GetTenantGroups")]
    [Authorize]
    public async Task<HttpResponseData> GetTenantGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/tenant-groups")] HttpRequestData req)
    {
        var groups = await _delegatedAdminService.GetAllGroupsAsync();
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { groups });
        return response;
    }

    /// <summary>POST /api/global/tenant-groups — create a group. GlobalAdminOnly. Body: { "name": "..." }.</summary>
    [Function("CreateTenantGroup")]
    [Authorize]
    public async Task<HttpResponseData> CreateTenantGroup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/tenant-groups")] HttpRequestData req,
        FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<CreateTenantGroupRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Name))
            return await Bad(req, "name is required");

        var groupId = await _delegatedAdminService.CreateGroupAsync(body.Name, currentUpn ?? "");

        _logger.LogInformation("Tenant group created: {GroupId} '{Name}' by {By}", groupId, body.Name, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { groupId, name = body.Name.Trim() });
        return response;
    }

    /// <summary>PATCH /api/global/tenant-groups/{groupId} — rename. GlobalAdminOnly. Body: { "name": "..." }.</summary>
    [Function("RenameTenantGroup")]
    [Authorize]
    public async Task<HttpResponseData> RenameTenantGroup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/tenant-groups/{groupId}")] HttpRequestData req,
        string groupId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<RenameTenantGroupRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Name))
            return await Bad(req, "name is required");

        var ok = await _delegatedAdminService.RenameGroupAsync(groupId, body.Name);
        if (!ok)
            return await NotFound(req);

        _logger.LogInformation("Tenant group renamed: {GroupId} -> '{Name}' by {By}", groupId, body.Name, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Group renamed" });
        return response;
    }

    /// <summary>DELETE /api/global/tenant-groups/{groupId} — delete group + all its assignments. GlobalAdminOnly.</summary>
    [Function("DeleteTenantGroup")]
    [Authorize]
    public async Task<HttpResponseData> DeleteTenantGroup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/tenant-groups/{groupId}")] HttpRequestData req,
        string groupId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        // Capture tenants + assignees BEFORE the cascade delete removes them, so we can audit the
        // bulk access removal under each affected tenant and cut every assignee's live streams.
        var group = await _delegatedAdminService.GetGroupAsync(groupId);
        var assignees = await _delegatedAdminService.GetGroupAssigneesAsync(groupId);
        await _delegatedAdminService.DeleteGroupAsync(groupId);

        // Enforcement: group authorization is join-time only — without the kick, every (former)
        // assignee keeps receiving the managed tenants' live telemetry until their connection drops.
        foreach (var assignee in assignees)
            await _signalRService.DisconnectUserAsync(assignee.Upn);

        if (group != null && group.AssigneeCount > 0)
        {
            await AuditPerTenantAsync(group.TenantIds, "DELETE", "*", currentUpn,
                new Dictionary<string, string>
                {
                    { "Group", group.Name },
                    { "GroupId", groupId },
                    { "Reason", "group-deleted" },
                    { "AssigneeCount", group.AssigneeCount.ToString() },
                });
        }

        _logger.LogInformation("Tenant group deleted: {GroupId} by {By}", groupId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Group deleted" });
        return response;
    }

    /// <summary>POST /api/global/tenant-groups/{groupId}/tenants — add a tenant. GlobalAdminOnly. Body: { "tenantId": "&lt;guid&gt;" }.</summary>
    [Function("AddTenantToGroup")]
    [Authorize]
    public async Task<HttpResponseData> AddTenantToGroup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/tenant-groups/{groupId}/tenants")] HttpRequestData req,
        string groupId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<AddGroupTenantRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.TenantId) || !Guid.TryParse(body.TenantId, out _))
            return await Bad(req, "a valid tenantId (GUID) is required");

        var tenantId = body.TenantId.ToLowerInvariant();
        // The service refuses to add to a non-existent group (no ghost from a lone membership row).
        var added = await _delegatedAdminService.AddTenantToGroupAsync(groupId, tenantId);
        if (!added)
            return await NotFound(req);

        // Every current assignee just gained access to this tenant — audit under that tenant per assignee.
        var assignees = await _delegatedAdminService.GetGroupAssigneesAsync(groupId);
        foreach (var assignee in assignees)
        {
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId, "CREATE", AuditEntity, assignee.Upn, currentUpn ?? "",
                new Dictionary<string, string> { { "GroupId", groupId }, { "Reason", "tenant-added-to-group" } });
        }

        _logger.LogInformation("Tenant {TenantId} added to group {GroupId} by {By}", tenantId, groupId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant added to group" });
        return response;
    }

    /// <summary>DELETE /api/global/tenant-groups/{groupId}/tenants/{tenantId} — remove a tenant. GlobalAdminOnly.</summary>
    [Function("RemoveTenantFromGroup")]
    [Authorize]
    public async Task<HttpResponseData> RemoveTenantFromGroup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/tenant-groups/{groupId}/tenants/{tenantId}")] HttpRequestData req,
        string groupId, string tenantId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        var normalizedTenantId = tenantId.ToLowerInvariant();

        // Snapshot assignees BEFORE the removal so we can audit each one's lost access under this tenant.
        var assignees = await _delegatedAdminService.GetGroupAssigneesAsync(groupId);
        // The service returns false when the group doesn't exist or the tenant isn't a member — so a typo
        // can't 200 and write false "access removed" audit rows. Only audit after a real removal.
        var removed = await _delegatedAdminService.RemoveTenantFromGroupAsync(groupId, normalizedTenantId);
        if (!removed)
            return await NotFound(req);

        foreach (var assignee in assignees)
        {
            await _maintenanceRepo.LogAuditEntryAsync(
                normalizedTenantId, "DELETE", AuditEntity, assignee.Upn, currentUpn ?? "",
                new Dictionary<string, string> { { "GroupId", groupId }, { "Reason", "tenant-removed-from-group" } });
            // Enforcement: cut already-joined live streams for the tenant that just left the group.
            // Coarse by design (connection close, see DisconnectUserAsync) — still-authorized
            // streams recover automatically via the client's reconnect + re-join.
            await _signalRService.DisconnectUserAsync(assignee.Upn);
        }

        _logger.LogInformation("Tenant {TenantId} removed from group {GroupId} by {By}", normalizedTenantId, groupId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant removed from group" });
        return response;
    }

    /// <summary>
    /// POST /api/global/tenant-groups/{groupId}/assignees — assign a UPN to the group. GlobalAdminOnly.
    /// Body: { "upn": "user@domain.com", "role": "DelegatedReader" | "DelegatedAdmin" }.
    /// </summary>
    [Function("AssignTenantGroup")]
    [Authorize]
    public async Task<HttpResponseData> AssignTenantGroup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/tenant-groups/{groupId}/assignees")] HttpRequestData req,
        string groupId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<AssignGroupRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
            return await Bad(req, "upn is required");

        // Fail-closed role handling — mirror the delegated grant: default to least privilege, reject unknowns.
        var role = string.IsNullOrWhiteSpace(body.Role) ? Constants.DelegatedRoles.DelegatedReader : body.Role;
        if (role != Constants.DelegatedRoles.DelegatedReader && role != Constants.DelegatedRoles.DelegatedAdmin)
            return await Bad(req, $"role must be '{Constants.DelegatedRoles.DelegatedReader}' or '{Constants.DelegatedRoles.DelegatedAdmin}'");

        // Guard against assigning to a non-existent group (would create an orphan assignment row).
        var group = await _delegatedAdminService.GetGroupAsync(groupId);
        if (group == null)
            return await NotFound(req);

        var upn = body.Upn.ToLowerInvariant();
        // The service re-checks existence (covers a delete racing this assign) — skip the audit if it no-ops.
        var assigned = await _delegatedAdminService.AssignGroupAsync(upn, groupId, role, true, currentUpn ?? "");
        if (!assigned)
            return await NotFound(req);

        // The UPN just gained read access to every tenant in the group — audit under each.
        await AuditPerTenantAsync(group.TenantIds, "CREATE", upn, currentUpn,
            new Dictionary<string, string>
            {
                { "Group", group.Name },
                { "GroupId", groupId },
                { "Role", role },
                { "Reason", "group-assigned" },
            });

        _logger.LogInformation("Group {GroupId} assigned to {Upn} ({Role}) by {By}", groupId, upn, role, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { message = "Assigned to group" });
        return response;
    }

    /// <summary>DELETE /api/global/tenant-groups/{groupId}/assignees/{upn} — unassign a UPN. GlobalAdminOnly.</summary>
    [Function("UnassignTenantGroup")]
    [Authorize]
    public async Task<HttpResponseData> UnassignTenantGroup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/tenant-groups/{groupId}/assignees/{upn}")] HttpRequestData req,
        string groupId, string upn, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        var normalizedUpn = upn.ToLowerInvariant();

        // Read the group's tenants (unchanged by unassign) so we can audit the UPN's lost access per tenant.
        var group = await _delegatedAdminService.GetGroupAsync(groupId);
        // The service returns false when the UPN was not actually assigned — so a mistyped UPN can't 200 and
        // write false "access removed" audit rows. Only audit after a real unassign.
        var unassigned = await _delegatedAdminService.UnassignGroupAsync(normalizedUpn, groupId);
        if (!unassigned)
            return await NotFound(req);

        // Enforcement: cut the unassigned caller's already-joined live streams (join-time-only authz).
        await _signalRService.DisconnectUserAsync(normalizedUpn);

        if (group != null)
        {
            await AuditPerTenantAsync(group.TenantIds, "DELETE", normalizedUpn, currentUpn,
                new Dictionary<string, string>
                {
                    { "Group", group.Name },
                    { "GroupId", groupId },
                    { "Reason", "group-unassigned" },
                });
        }

        _logger.LogInformation("Group {GroupId} unassigned from {Upn} by {By}", groupId, normalizedUpn, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Unassigned from group" });
        return response;
    }

    /// <summary>Logs one audit entry per tenant (the customer-visible "who can read my tenant" trail).</summary>
    private async Task AuditPerTenantAsync(
        IEnumerable<string> tenantIds, string action, string entityId, string? performedBy, Dictionary<string, string> details)
    {
        foreach (var tenantId in tenantIds)
        {
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId.ToLowerInvariant(), action, AuditEntity, entityId, performedBy ?? "", details);
        }
    }

    private static async Task<HttpResponseData> Bad(HttpRequestData req, string error)
    {
        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
        await bad.WriteAsJsonAsync(new { error });
        return bad;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req)
    {
        var notFound = req.CreateResponse(HttpStatusCode.NotFound);
        await notFound.WriteAsJsonAsync(new { error = "Group not found" });
        return notFound;
    }
}

public class CreateTenantGroupRequest
{
    public string Name { get; set; } = string.Empty;
}

public class RenameTenantGroupRequest
{
    public string Name { get; set; } = string.Empty;
}

public class AddGroupTenantRequest
{
    public string TenantId { get; set; } = string.Empty;
}

public class AssignGroupRequest
{
    public string Upn { get; set; } = string.Empty;
    public string? Role { get; set; }
}
