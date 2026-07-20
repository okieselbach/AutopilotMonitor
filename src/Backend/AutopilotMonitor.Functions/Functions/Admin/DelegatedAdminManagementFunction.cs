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
    private readonly ISignalRNotificationService _signalRService;

    public DelegatedAdminManagementFunction(
        ILogger<DelegatedAdminManagementFunction> logger,
        DelegatedAdminService delegatedAdminService,
        IMaintenanceRepository maintenanceRepo,
        ISignalRNotificationService signalRService)
    {
        _logger = logger;
        _delegatedAdminService = delegatedAdminService;
        _maintenanceRepo = maintenanceRepo;
        _signalRService = signalRService;
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
        var validationError = ValidateGrantRequest(body, out var role);
        if (validationError != null)
            return await Bad(req, validationError);

        // Edition note: TARGET tenants may be any edition — an MSP on Enterprise may manage Community
        // customers. The Enterprise requirement applies to the delegated admin's HOME tenant and is
        // enforced at resolve time (DelegatedAdminService.GetScopeAsync gates on the JWT tid). It cannot
        // be checked here: at grant time only the UPN is known, and UPN-domain → tenant mapping is not
        // reliable (multi-domain tenants). Grants to non-Enterprise-homed admins are simply inert.
        var entry = await _delegatedAdminService.UpsertAsync(
            body!.Upn, body.TenantId, role,
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
        var removed = await RevokeCoreAsync(upn, tenantId, currentUpn);
        if (!removed)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Delegated assignment not found" });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Delegated assignment revoked" });
        return response;
    }

    /// <summary>
    /// Testable core of the revoke flow (no HTTP plumbing — see GetAllBlockedDevicesFunctionTests for the
    /// convention). Returns false when no assignment row existed (caller 404s): a mistyped upn/tenantId
    /// must not 200 and write a false customer-visible "access removed" audit row while the real grant
    /// stays live (mirrors the tenant-group revoke semantics). On a real removal writes the audit entry
    /// and cuts the revoked UPN's live streams — group authorization is join-time only, so without the
    /// kick they keep receiving tenant telemetry until the connection drops.
    /// </summary>
    internal async Task<bool> RevokeCoreAsync(string upn, string tenantId, string? currentUpn)
    {
        var removed = await _delegatedAdminService.RemoveAsync(upn, tenantId);
        if (!removed)
            return false;

        await _maintenanceRepo.LogAuditEntryAsync(
            tenantId.ToLowerInvariant(), "DELETE", "DelegatedAdmin", upn.ToLowerInvariant(), currentUpn ?? "");

        await _signalRService.DisconnectUserAsync(upn.ToLowerInvariant());

        _logger.LogInformation("Delegated revoke: {Upn} -> {TenantId} by {By}", upn, tenantId, currentUpn);
        return true;
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
        var ok = await SetEnabledCoreAsync(upn, tenantId, isEnabled, currentUpn);
        if (!ok)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Delegated assignment not found" });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = isEnabled ? "Delegated assignment enabled" : "Delegated assignment disabled" });
        return response;
    }

    /// <summary>
    /// Testable core of enable/disable (no HTTP plumbing). Returns false when no assignment row
    /// existed (caller 404s — no audit, no disconnect). Disable is a revocation too, so it cuts the
    /// UPN's already-joined live streams (see <see cref="RevokeCoreAsync"/>); enable never disconnects.
    /// </summary>
    internal async Task<bool> SetEnabledCoreAsync(string upn, string tenantId, bool isEnabled, string? currentUpn)
    {
        var ok = await _delegatedAdminService.SetEnabledAsync(upn, tenantId, isEnabled);
        if (!ok)
            return false;

        await _maintenanceRepo.LogAuditEntryAsync(
            tenantId.ToLowerInvariant(), "UPDATE", "DelegatedAdmin", upn.ToLowerInvariant(), currentUpn ?? "",
            new Dictionary<string, string> { { "IsEnabled", isEnabled.ToString() } });

        if (!isEnabled)
            await _signalRService.DisconnectUserAsync(upn.ToLowerInvariant());

        _logger.LogInformation("Delegated {Action}: {Upn} -> {TenantId} by {By}",
            isEnabled ? "enable" : "disable", upn, tenantId, currentUpn);
        return true;
    }

    /// <summary>
    /// Pure grant-request validation (testable seam). Returns the 400 error message, or null when valid —
    /// then <paramref name="role"/> carries the effective role. A mistyped tenantId must not create a
    /// garbage scope entry that pollutes the allow-list and seeds a per-tenant audit trail keyed on the
    /// typo; the role defaults to least-privileged and unknown roles are rejected (fail-closed) so a typo
    /// can never silently widen access.
    /// </summary>
    internal static string? ValidateGrantRequest(GrantDelegatedAdminRequest? body, out string role)
    {
        role = Constants.DelegatedRoles.DelegatedReader;

        if (body == null || string.IsNullOrWhiteSpace(body.Upn) || string.IsNullOrWhiteSpace(body.TenantId))
            return "upn and tenantId are required";

        if (!Guid.TryParse(body.TenantId, out _))
            return "a valid tenantId (GUID) is required";

        role = string.IsNullOrWhiteSpace(body.Role) ? Constants.DelegatedRoles.DelegatedReader : body.Role!;
        if (role != Constants.DelegatedRoles.DelegatedReader && role != Constants.DelegatedRoles.DelegatedAdmin)
            return $"role must be '{Constants.DelegatedRoles.DelegatedReader}' or '{Constants.DelegatedRoles.DelegatedAdmin}'";

        return null;
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
