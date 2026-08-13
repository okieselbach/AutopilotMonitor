using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
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

    public TenantAdminManagementFunction(
        ILogger<TenantAdminManagementFunction> logger,
        TenantAdminsService tenantAdminsService,
        IMaintenanceRepository maintenanceRepo)
    {
        _logger = logger;
        _tenantAdminsService = tenantAdminsService;
        _maintenanceRepo = maintenanceRepo;
    }

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

        // Parse request body
        var body = await req.ReadFromJsonAsync<AddTenantAdminRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "UPN is required" });
            return badRequestResponse;
        }

        // Determine role (default to Admin for backward compat), then validate against the
        // allow-list so arbitrary strings never reach storage.
        var requestedRole = !string.IsNullOrWhiteSpace(body.Role) ? body.Role : AutopilotMonitor.Shared.Constants.TenantRoles.Admin;
        var role = TryCanonicalizeRole(requestedRole);
        if (role == null)
        {
            var badRoleResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRoleResponse.WriteAsJsonAsync(new { error = $"Invalid role '{body.Role}'. Valid roles: Admin, Operator, Viewer." });
            return badRoleResponse;
        }

        var newAdmin = await _tenantAdminsService.AddTenantMemberAsync(requestCtx.TargetTenantId, body.Upn, upn!, role, body.CanManageBootstrapTokens);

        await _maintenanceRepo.LogAuditEntryAsync(
            requestCtx.TargetTenantId,
            "CREATE",
            "TenantAdmin",
            body.Upn,
            upn!,
            new Dictionary<string, string>
            {
                { "Role", role },
                { "CanManageBootstrapTokens", body.CanManageBootstrapTokens.ToString() }
            }
        );

        _logger.LogInformation($"Tenant member added: {body.Upn} with role {role} to tenant {requestCtx.TargetTenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { admin = newAdmin });
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
    public string Upn { get; set; } = string.Empty;
    public string? Role { get; set; }
    public bool CanManageBootstrapTokens { get; set; }
}

public class UpdateMemberPermissionsRequest
{
    public string Role { get; set; } = string.Empty;
    public bool CanManageBootstrapTokens { get; set; }
}
