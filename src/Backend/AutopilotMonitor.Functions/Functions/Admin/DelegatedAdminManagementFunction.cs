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
/// GlobalAdmin-only management of <b>delegated admin</b> assignments — the "scoped global" / "MSP mode" tier
/// that lets a UPN READ a subset of tenants. Mirrors the MCP-user management surface (list / grant / revoke /
/// enable / disable). The platform operator (GlobalAdmin) grants centrally; <see cref="Constants.DelegatedSource.OperatorGranted"/>
/// is stamped as the source. Reads are GlobalReadOrAdmin (a read-only Global Reader can audit who is delegated);
/// all mutations are GlobalAdminOnly (enforced by <c>PolicyEnforcementMiddleware</c> via the route catalog).
///
/// Mutations are audited under the <b>target managed tenant</b>'s trail (not the caller's home tenant), so the
/// customer can see "who was granted access to my tenant" alongside their own admin changes.
/// </summary>
public class DelegatedAdminManagementFunction
{
    private readonly ILogger<DelegatedAdminManagementFunction> _logger;
    private readonly DelegatedAdminService _delegatedAdminService;
    private readonly IMaintenanceRepository _maintenanceRepo;

    public DelegatedAdminManagementFunction(
        ILogger<DelegatedAdminManagementFunction> logger,
        DelegatedAdminService delegatedAdminService,
        IMaintenanceRepository maintenanceRepo)
    {
        _logger = logger;
        _delegatedAdminService = delegatedAdminService;
        _maintenanceRepo = maintenanceRepo;
    }

    /// <summary>GET /api/global/delegated-admins — list every delegated assignment. GlobalReadOrAdmin.</summary>
    [Function("GetDelegatedAdmins")]
    [Authorize]
    public async Task<HttpResponseData> GetDelegatedAdmins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/delegated-admins")] HttpRequestData req)
    {
        var assignments = await _delegatedAdminService.GetAllAsync();
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { assignments });
        return response;
    }

    /// <summary>
    /// POST /api/global/delegated-admins — grant (create/replace) an assignment. GlobalAdminOnly.
    /// Body: { "upn": "user@domain.com", "tenantId": "&lt;guid&gt;", "role": "DelegatedReader" | "DelegatedAdmin" }.
    /// </summary>
    [Function("GrantDelegatedAdmin")]
    [Authorize]
    public async Task<HttpResponseData> GrantDelegatedAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/delegated-admins")] HttpRequestData req,
        FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<GrantDelegatedAdminRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn) || string.IsNullOrWhiteSpace(body.TenantId))
            return await Bad(req, "upn and tenantId are required");

        // Default to the least-privileged role; reject anything we don't recognize (fail-closed) so a typo
        // can never silently widen access.
        var role = string.IsNullOrWhiteSpace(body.Role) ? Constants.DelegatedRoles.DelegatedReader : body.Role;
        if (role != Constants.DelegatedRoles.DelegatedReader && role != Constants.DelegatedRoles.DelegatedAdmin)
            return await Bad(req, $"role must be '{Constants.DelegatedRoles.DelegatedReader}' or '{Constants.DelegatedRoles.DelegatedAdmin}'");

        var entry = await _delegatedAdminService.UpsertAsync(
            body.Upn, body.TenantId, role,
            Constants.DelegatedStatus.Active, Constants.DelegatedSource.OperatorGranted, currentUpn ?? "");

        await _maintenanceRepo.LogAuditEntryAsync(
            entry.TenantId, "CREATE", "DelegatedAdmin", entry.Upn, currentUpn ?? "",
            new Dictionary<string, string>
            {
                { "Role", entry.Role },
                { "Status", entry.Status },
                { "Source", entry.Source },
            });

        _logger.LogInformation("Delegated grant: {Upn} -> {TenantId} ({Role}) by {By}",
            entry.Upn, entry.TenantId, entry.Role, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { assignment = entry });
        return response;
    }

    /// <summary>DELETE /api/global/delegated-admins/{upn}/{tenantId} — revoke (remove) an assignment. GlobalAdminOnly.</summary>
    [Function("RevokeDelegatedAdmin")]
    [Authorize]
    public async Task<HttpResponseData> RevokeDelegatedAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/delegated-admins/{upn}/{tenantId}")] HttpRequestData req,
        string upn, string tenantId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        await _delegatedAdminService.RemoveAsync(upn, tenantId);

        await _maintenanceRepo.LogAuditEntryAsync(
            tenantId.ToLowerInvariant(), "DELETE", "DelegatedAdmin", upn.ToLowerInvariant(), currentUpn ?? "");

        _logger.LogInformation("Delegated revoke: {Upn} -> {TenantId} by {By}", upn, tenantId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Delegated assignment revoked" });
        return response;
    }

    /// <summary>PATCH /api/global/delegated-admins/{upn}/{tenantId}/enable — re-enable a disabled assignment. GlobalAdminOnly.</summary>
    [Function("EnableDelegatedAdmin")]
    [Authorize]
    public Task<HttpResponseData> EnableDelegatedAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/delegated-admins/{upn}/{tenantId}/enable")] HttpRequestData req,
        string upn, string tenantId, FunctionContext context)
        => SetEnabled(req, upn, tenantId, true, context);

    /// <summary>PATCH /api/global/delegated-admins/{upn}/{tenantId}/disable — disable without removing. GlobalAdminOnly.</summary>
    [Function("DisableDelegatedAdmin")]
    [Authorize]
    public Task<HttpResponseData> DisableDelegatedAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/delegated-admins/{upn}/{tenantId}/disable")] HttpRequestData req,
        string upn, string tenantId, FunctionContext context)
        => SetEnabled(req, upn, tenantId, false, context);

    private async Task<HttpResponseData> SetEnabled(
        HttpRequestData req, string upn, string tenantId, bool isEnabled, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        var ok = await _delegatedAdminService.SetEnabledAsync(upn, tenantId, isEnabled);
        if (!ok)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Delegated assignment not found" });
            return notFound;
        }

        await _maintenanceRepo.LogAuditEntryAsync(
            tenantId.ToLowerInvariant(), "UPDATE", "DelegatedAdmin", upn.ToLowerInvariant(), currentUpn ?? "",
            new Dictionary<string, string> { { "IsEnabled", isEnabled.ToString() } });

        _logger.LogInformation("Delegated {Action}: {Upn} -> {TenantId} by {By}",
            isEnabled ? "enable" : "disable", upn, tenantId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = isEnabled ? "Delegated assignment enabled" : "Delegated assignment disabled" });
        return response;
    }

    private static async Task<HttpResponseData> Bad(HttpRequestData req, string error)
    {
        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
        await bad.WriteAsJsonAsync(new { error });
        return bad;
    }
}

public class GrantDelegatedAdminRequest
{
    public string Upn { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? Role { get; set; }
}
