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
/// Tenant Admin Management endpoints
/// Allows tenant admins and global admins to manage admin users for a tenant
/// </summary>
public class TenantAdminManagementFunction
{
    private readonly ILogger<TenantAdminManagementFunction> _logger;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly IMaintenanceRepository _maintenanceRepo;
    private readonly ISignalRNotificationService _signalRService;

    public TenantAdminManagementFunction(
        ILogger<TenantAdminManagementFunction> logger,
        TenantAdminsService tenantAdminsService,
        IMaintenanceRepository maintenanceRepo,
        ISignalRNotificationService signalRService)
    {
        _logger = logger;
        _tenantAdminsService = tenantAdminsService;
        _maintenanceRepo = maintenanceRepo;
        _signalRService = signalRService;
    }

    /// <summary>
    /// SignalR group authorization is evaluated only at join time (SignalRAddToGroupFunction), so a
    /// member whose role was removed, disabled or lowered would keep their already-joined
    /// 'tenant-{tid}' broadcast, notify-group and session-group streams until the socket drops.
    /// Cutting the UPN's connections (negotiate binds userId = lowercased UPN) forces a reconnect that
    /// re-runs the join gates against the new role. Coarse by design — a still-authorized client
    /// simply reconnects — and mirrors the delegated-admin / tenant-group revoke paths.
    /// </summary>
    private Task CutLiveStreamsAsync(string adminUpn) =>
        _signalRService.DisconnectUserAsync(adminUpn.ToLowerInvariant());

    /// <summary>
    /// GET /api/tenants/{tenantId}/admins
    /// Gets all admins for a tenant
    /// Accessible by: Global Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("GetTenantAdmins")]
    [Authorize]
    public async Task<HttpResponseData> GetTenantAdmins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tenants/{tenantId}/admins")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;

        var admins = await _tenantAdminsService.GetTenantAdminsAsync(requestCtx.TargetTenantId);

        _logger.LogInformation($"Retrieved {admins.Count} admins for tenant {requestCtx.TargetTenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(admins);
        return response;
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/admins
    /// Adds a new admin to a tenant
    /// Accessible by: Global Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("AddTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> AddTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/admins")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;

        // Parse request body. A member is either a person (upn) or an application (applicationId — the
        // Entra client id of a service principal in THIS tenant, stored under the app:<client-id> key).
        var body = await req.ReadFromJsonAsync<AddTenantAdminRequest>();
        var isApplication = !string.IsNullOrWhiteSpace(body?.ApplicationId);
        if (body == null || (string.IsNullOrWhiteSpace(body.Upn) && !isApplication))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "UPN or applicationId is required" });
            return badRequestResponse;
        }
        if (isApplication && !Guid.TryParse(body.ApplicationId, out _))
        {
            var badAppResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badAppResponse.WriteAsJsonAsync(new { error = "applicationId must be the application's (client) id GUID" });
            return badAppResponse;
        }
        var memberKey = isApplication
            ? AutopilotMonitor.Shared.Constants.PrincipalKeys.ForApplication(body.ApplicationId!)
            : body.Upn;

        // Determine role (default to Admin for backward compat, Viewer for an application), then validate
        // against the allow-list so arbitrary strings never reach storage. An application is read-only:
        // Viewer is the only role it may hold (the resolver caps it there regardless of the row).
        var requestedRole = !string.IsNullOrWhiteSpace(body.Role)
            ? body.Role
            : isApplication ? AutopilotMonitor.Shared.Constants.TenantRoles.Viewer : AutopilotMonitor.Shared.Constants.TenantRoles.Admin;
        var role = TryCanonicalizeRole(requestedRole);
        if (role == null)
        {
            var badRoleResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRoleResponse.WriteAsJsonAsync(new { error = $"Invalid role '{body.Role}'. Valid roles: Admin, Operator, Viewer." });
            return badRoleResponse;
        }
        if (isApplication && role != AutopilotMonitor.Shared.Constants.TenantRoles.Viewer)
        {
            var badRoleResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRoleResponse.WriteAsJsonAsync(new { error = ApplicationRoleError });
            return badRoleResponse;
        }
        var canManageBootstrapTokens = !isApplication && body.CanManageBootstrapTokens;

        var newAdmin = await _tenantAdminsService.AddTenantMemberAsync(requestCtx.TargetTenantId, memberKey, upn!, role, canManageBootstrapTokens);

        await _maintenanceRepo.LogAuditEntryAsync(
            requestCtx.TargetTenantId,
            "CREATE",
            "TenantAdmin",
            memberKey,
            upn!,
            new Dictionary<string, string>
            {
                { "Role", role },
                { "CanManageBootstrapTokens", canManageBootstrapTokens.ToString() },
                { "PrincipalType", isApplication ? "Application" : "User" }
            }
        );

        _logger.LogInformation($"Tenant member added: {memberKey} with role {role} to tenant {requestCtx.TargetTenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new TenantAdminCreatedResponse { Admin = newAdmin });
        return response;
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/admins/{adminUpn}
    /// Removes an admin from a tenant
    /// Accessible by: Global Admins OR Tenant Admins of the same tenant
    /// Note: Cannot remove yourself if you're the last admin
    /// </summary>
    [Function("RemoveTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> RemoveTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "tenants/{tenantId}/admins/{adminUpn}")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;

        // Check if trying to remove self
        if (adminUpn.Equals(upn, StringComparison.OrdinalIgnoreCase))
        {
            // Check if this is the last Admin-role member (only for non-Global-Admins)
            if (!requestCtx.IsGlobalAdmin)
            {
                var members = await _tenantAdminsService.GetTenantAdminsAsync(requestCtx.TargetTenantId);
                var adminCount = members.Count(m => m.IsEnabled && (m.Role == null || m.Role == AutopilotMonitor.Shared.Constants.TenantRoles.Admin));
                if (adminCount <= 1)
                {
                    var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteAsJsonAsync(new { error = "Cannot remove yourself as the last admin. Please add another admin first." });
                    return badRequestResponse;
                }
            }
        }

        // Remove the admin
        await _tenantAdminsService.RemoveTenantAdminAsync(requestCtx.TargetTenantId, adminUpn);

        await _maintenanceRepo.LogAuditEntryAsync(
            requestCtx.TargetTenantId,
            "DELETE",
            "TenantAdmin",
            adminUpn,
            upn!
        );

        await CutLiveStreamsAsync(adminUpn);

        _logger.LogInformation($"Tenant Admin removed: {adminUpn} from tenant {requestCtx.TargetTenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant Admin removed successfully" });
        return response;
    }

    /// <summary>
    /// PATCH /api/tenants/{tenantId}/admins/{adminUpn}/disable
    /// Disables an admin for a tenant
    /// Accessible by: Global Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("DisableTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> DisableTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenants/{tenantId}/admins/{adminUpn}/disable")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;

        // Disable the admin
        await _tenantAdminsService.DisableTenantAdminAsync(requestCtx.TargetTenantId, adminUpn);

        await _maintenanceRepo.LogAuditEntryAsync(
            requestCtx.TargetTenantId,
            "UPDATE",
            "TenantAdmin",
            adminUpn,
            upn!,
            new Dictionary<string, string> { { "Action", "Disable" } }
        );

        await CutLiveStreamsAsync(adminUpn);

        _logger.LogInformation($"Tenant Admin disabled: {adminUpn} for tenant {requestCtx.TargetTenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant Admin disabled successfully" });
        return response;
    }

    /// <summary>
    /// PATCH /api/tenants/{tenantId}/admins/{adminUpn}/enable
    /// Enables an admin for a tenant
    /// Accessible by: Global Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("EnableTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> EnableTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenants/{tenantId}/admins/{adminUpn}/enable")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;

        // Enable the admin
        await _tenantAdminsService.EnableTenantAdminAsync(requestCtx.TargetTenantId, adminUpn);

        await _maintenanceRepo.LogAuditEntryAsync(
            requestCtx.TargetTenantId,
            "UPDATE",
            "TenantAdmin",
            adminUpn,
            upn!,
            new Dictionary<string, string> { { "Action", "Enable" } }
        );

        _logger.LogInformation($"Tenant Admin enabled: {adminUpn} for tenant {requestCtx.TargetTenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant Admin enabled successfully" });
        return response;
    }
    /// <summary>
    /// PATCH /api/tenants/{tenantId}/admins/{adminUpn}/permissions
    /// Updates role and permissions for a tenant member
    /// Accessible by: Global Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("UpdateMemberPermissions")]
    [Authorize]
    public async Task<HttpResponseData> UpdateMemberPermissions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenants/{tenantId}/admins/{adminUpn}/permissions")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = context.GetRequestContext();
        var upn = requestCtx.UserPrincipalName;

        var body = await req.ReadFromJsonAsync<UpdateMemberPermissionsRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Role))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "Role is required" });
            return badRequestResponse;
        }

        // Validate against the allow-list so arbitrary strings never reach storage
        var role = TryCanonicalizeRole(body.Role);
        if (role == null)
        {
            var badRoleResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRoleResponse.WriteAsJsonAsync(new { error = $"Invalid role '{body.Role}'. Valid roles: Admin, Operator, Viewer." });
            return badRoleResponse;
        }
        if (AutopilotMonitor.Shared.Constants.PrincipalKeys.IsApplication(adminUpn) && role != AutopilotMonitor.Shared.Constants.TenantRoles.Viewer)
        {
            var badRoleResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRoleResponse.WriteAsJsonAsync(new { error = ApplicationRoleError });
            return badRoleResponse;
        }

        // Prevent demoting yourself if you're the last Admin
        if (adminUpn.Equals(upn, StringComparison.OrdinalIgnoreCase) && role != AutopilotMonitor.Shared.Constants.TenantRoles.Admin)
        {
            if (!requestCtx.IsGlobalAdmin)
            {
                var members = await _tenantAdminsService.GetTenantAdminsAsync(requestCtx.TargetTenantId);
                var adminCount = members.Count(m => m.IsEnabled && (m.Role == null || m.Role == AutopilotMonitor.Shared.Constants.TenantRoles.Admin));
                if (adminCount <= 1)
                {
                    var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteAsJsonAsync(new { error = "Cannot demote yourself as the last admin. Please add another admin first." });
                    return badRequestResponse;
                }
            }
        }

        var updated = await _tenantAdminsService.UpdateMemberPermissionsAsync(requestCtx.TargetTenantId, adminUpn, role, body.CanManageBootstrapTokens);
        if (!updated)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteAsJsonAsync(new { error = "Member not found" });
            return notFoundResponse;
        }

        await _maintenanceRepo.LogAuditEntryAsync(
            requestCtx.TargetTenantId,
            "UPDATE",
            "TenantAdmin",
            adminUpn,
            upn!,
            new Dictionary<string, string>
            {
                { "Action", "UpdatePermissions" },
                { "Role", role },
                { "CanManageBootstrapTokens", body.CanManageBootstrapTokens.ToString() }
            }
        );

        // Only Admin holds the admin-tier notify group; any other target role may have lost it.
        if (role != AutopilotMonitor.Shared.Constants.TenantRoles.Admin)
            await CutLiveStreamsAsync(adminUpn);

        _logger.LogInformation("Member permissions updated: {AdminUpn} -> role={Role} in tenant {TenantId} by {Upn}", adminUpn, role, requestCtx.TargetTenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Member permissions updated successfully" });
        return response;
    }

    /// <summary>
    /// Validates a requested tenant role against the allow-list (Admin/Operator/Viewer),
    /// matching case-insensitively and canonicalizing to the exact constant casing so only
    /// canonical values are ever persisted. Returns null for anything not in the allow-list.
    /// </summary>
    internal const string ApplicationRoleError =
        "A service principal can only hold the Viewer role (read-only). Grant Operator or Admin to a person instead.";

    internal static string? TryCanonicalizeRole(string role)
    {
        string[] validRoles =
        {
            AutopilotMonitor.Shared.Constants.TenantRoles.Admin,
            AutopilotMonitor.Shared.Constants.TenantRoles.Operator,
            AutopilotMonitor.Shared.Constants.TenantRoles.Viewer
        };
        return validRoles.FirstOrDefault(v => string.Equals(v, role, StringComparison.OrdinalIgnoreCase));
    }
}

public class AddTenantAdminRequest
{
    /// <summary>The person's UPN. Leave empty when adding an application (<see cref="ApplicationId"/>).</summary>
    public string Upn { get; set; } = string.Empty;
    /// <summary>
    /// The Entra application (client) id of a service principal in this tenant. Stored under the
    /// <c>app:&lt;client-id&gt;</c> member key; the role is fixed to Viewer.
    /// </summary>
    public string? ApplicationId { get; set; }
    public string? Role { get; set; }
    public bool CanManageBootstrapTokens { get; set; }
}

public class UpdateMemberPermissionsRequest
{
    public string Role { get; set; } = string.Empty;
    public bool CanManageBootstrapTokens { get; set; }
}
