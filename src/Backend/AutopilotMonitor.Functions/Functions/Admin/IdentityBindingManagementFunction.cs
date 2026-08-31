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
/// GlobalAdmin management of <b>admin identity bindings</b> — the immutable Entra identity (home tenant id +
/// object id) behind every UPN that holds a cross-tenant role (GlobalAdmins, DelegatedAdmins, TenantGroupAssignments).
/// Grants create a binding implicitly and REFUSE to overwrite one (409); this surface is the explicit, audited
/// path for the two legitimate changes: re-homing a UPN in another tenant, and re-pinning the object id after
/// the UPN was re-assigned to a new account (PUT without objectId clears the pin so the next sign-in from the
/// bound tenant re-pins). DELETE makes every role row of the UPN inert without touching the rows.
///
/// Reads are GlobalReadOrAdmin; mutations are GlobalAdminOnly (route catalog). Audited under the binding's
/// HOME tenant with entity <c>AdminIdentityBinding</c>.
/// </summary>
public class IdentityBindingManagementFunction
{
    private readonly ILogger<IdentityBindingManagementFunction> _logger;
    private readonly AdminIdentityBindingService _bindings;
    private readonly IMaintenanceRepository _maintenanceRepo;

    private const string AuditEntity = "AdminIdentityBinding";

    public IdentityBindingManagementFunction(
        ILogger<IdentityBindingManagementFunction> logger,
        AdminIdentityBindingService bindings,
        IMaintenanceRepository maintenanceRepo)
    {
        _logger = logger;
        _bindings = bindings;
        _maintenanceRepo = maintenanceRepo;
    }

    /// <summary>GET /api/global/identity-bindings — every binding. GlobalReadOrAdmin.</summary>
    [Function("GetIdentityBindings")]
    [Authorize]
    public async Task<HttpResponseData> GetIdentityBindings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/identity-bindings")] HttpRequestData req)
    {
        var bindings = await _bindings.GetAllAsync();
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new IdentityBindingListResponse { Bindings = bindings });
        return response;
    }

    /// <summary>
    /// PUT /api/global/identity-bindings/{upn} — create or REPLACE the binding. GlobalAdminOnly.
    /// Body: { "homeTenantId": "&lt;GUID&gt;", "objectId": "&lt;GUID, optional&gt;" }. Omitting objectId clears
    /// any pinned object id (re-pinned on the next sign-in from the home tenant).
    /// </summary>
    [Function("PutIdentityBinding")]
    [Authorize]
    public async Task<HttpResponseData> PutIdentityBinding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "global/identity-bindings/{upn}")] HttpRequestData req,
        string upn, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        upn = upn.ToLowerInvariant();

        var body = await req.ReadFromJsonAsync<IdentityBindingRequest>();
        var error = IdentityBindingRequest.Validate(body?.HomeTenantId, body?.ObjectId);
        if (error != null)
            return await Bad(req, error);

        var previous = await _bindings.GetAsync(upn);
        var binding = await _bindings.RebindAsync(upn, body!.HomeTenantId!, body.ObjectId, currentUpn ?? "");

        var details = new Dictionary<string, string>
        {
            { "HomeTenantId", binding.TenantId },
            { "ObjectId", binding.IsObjectIdPinned ? binding.ObjectId : "(unpinned)" },
        };
        if (previous != null)
        {
            details["PreviousHomeTenantId"] = previous.TenantId;
            details["PreviousObjectId"] = previous.IsObjectIdPinned ? previous.ObjectId : "(unpinned)";
        }
        await _maintenanceRepo.LogAuditEntryAsync(
            binding.TenantId, previous == null ? "CREATE" : "UPDATE", AuditEntity, upn, currentUpn ?? "", details);
        // Warning on purpose: every rebind is a security-relevant, operator-initiated identity change.
        _logger.LogWarning("[IdentityBinding] {Action} binding for {Upn}: tenant {TenantId}, objectId {ObjectId} by {By}",
            previous == null ? "Created" : "Replaced", upn, binding.TenantId,
            binding.IsObjectIdPinned ? binding.ObjectId : "(unpinned)", currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new IdentityBindingResponse { Binding = binding });
        return response;
    }

    /// <summary>DELETE /api/global/identity-bindings/{upn} — remove the binding (all role rows of the UPN become inert). GlobalAdminOnly.</summary>
    [Function("DeleteIdentityBinding")]
    [Authorize]
    public async Task<HttpResponseData> DeleteIdentityBinding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/identity-bindings/{upn}")] HttpRequestData req,
        string upn, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        upn = upn.ToLowerInvariant();

        // Self-removal would lock the acting GA out of every GlobalAdminOnly route, including this one.
        if (string.Equals(upn, currentUpn, StringComparison.OrdinalIgnoreCase))
            return await Bad(req, "You cannot remove your own identity binding");

        var previous = await _bindings.GetAsync(upn);
        if (previous == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Binding not found" });
            return notFound;
        }

        await _bindings.RemoveAsync(upn);
        await _maintenanceRepo.LogAuditEntryAsync(
            previous.TenantId, "DELETE", AuditEntity, upn, currentUpn ?? "",
            new Dictionary<string, string>
            {
                { "HomeTenantId", previous.TenantId },
                { "ObjectId", previous.IsObjectIdPinned ? previous.ObjectId : "(unpinned)" },
            });
        _logger.LogWarning("[IdentityBinding] Removed binding for {Upn} (was tenant {TenantId}) by {By}",
            upn, previous.TenantId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Binding removed" });
        return response;
    }

    private static async Task<HttpResponseData> Bad(HttpRequestData req, string error)
    {
        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
        await bad.WriteAsJsonAsync(new { error });
        return bad;
    }
}

/// <summary>Body of PUT global/identity-bindings/{upn}; also the shared validator for the binding fields every grant carries.</summary>
public class IdentityBindingRequest
{
    /// <summary>
    /// The identity a grant binds: the operator-supplied values when present, otherwise the resolver's answer.
    /// Returns null when neither yields a home tenant — the endpoint answers 422 <see cref="HomeTenantUnresolvedCode"/>
    /// and the UI offers a tenant picker. A supplied object id always wins over a resolved one.
    /// </summary>
    public static async Task<(string TenantId, string? ObjectId)?> ResolveForGrantAsync(
        AdminIdentityResolver resolver, string upn, string? homeTenantId, string? objectId)
    {
        objectId = string.IsNullOrWhiteSpace(objectId) ? null : objectId;
        if (!string.IsNullOrWhiteSpace(homeTenantId))
            return (homeTenantId, objectId);

        var resolved = await resolver.ResolveAsync(upn);
        return resolved == null ? null : (resolved.TenantId, objectId ?? resolved.ObjectId);
    }

    public string? HomeTenantId { get; set; }
    public string? ObjectId { get; set; }

    /// <summary>Returns the 400 message, or null when the fields are valid (home tenant = GUID required; object id = GUID or absent).</summary>
    public static string? Validate(string? homeTenantId, string? objectId)
    {
        if (string.IsNullOrWhiteSpace(homeTenantId))
            return "a valid homeTenantId (GUID — the Entra tenant the person signs in from) is required";
        return ValidateOptional(homeTenantId, objectId);
    }

    /// <summary>
    /// Grant-time variant: the home tenant MAY be absent (the backend resolves it from sign-in history or the
    /// UPN domain — see <see cref="AdminIdentityResolver"/>); when supplied it must be a GUID, as must the object id.
    /// </summary>
    public static string? ValidateOptional(string? homeTenantId, string? objectId)
    {
        if (!string.IsNullOrWhiteSpace(homeTenantId) && !Guid.TryParse(homeTenantId, out _))
            return "homeTenantId must be a GUID (the Entra tenant the person signs in from) when supplied";
        if (!string.IsNullOrWhiteSpace(objectId) && !Guid.TryParse(objectId, out _))
            return "objectId must be a GUID (the person's Entra object id) when supplied";
        return null;
    }

    /// <summary>Error code the grant endpoints return (HTTP 422) when the home tenant could not be resolved automatically.</summary>
    public const string HomeTenantUnresolvedCode = "HomeTenantUnresolved";
    public const string HomeTenantUnresolvedMessage =
        "The person's home tenant could not be resolved automatically (no previous sign-in, and the UPN domain does not belong to an onboarded tenant). Select the tenant they sign in from.";
}
