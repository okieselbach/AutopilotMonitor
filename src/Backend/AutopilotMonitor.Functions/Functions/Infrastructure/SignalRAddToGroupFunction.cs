using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Infrastructure
{
    public class SignalRAddToGroupFunction
    {
        private readonly ILogger<SignalRAddToGroupFunction> _logger;
        private readonly DelegatedAdminService _delegatedAdminService;

        public SignalRAddToGroupFunction(
            ILogger<SignalRAddToGroupFunction> logger,
            DelegatedAdminService delegatedAdminService)
        {
            _logger = logger;
            _delegatedAdminService = delegatedAdminService;
        }

        [Function("AddToGroup")]
        public async Task<AddToGroupOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/groups/join")] HttpRequestData req)
        {
            try
            {
                // Authentication + AuthenticatedUser authorization enforced by PolicyEnforcementMiddleware

                // Parse request
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576) // 1 MB limit
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await errorResponse.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return new AddToGroupOutput { HttpResponse = errorResponse };
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<AddToGroupRequest>(requestBody);

                if (string.IsNullOrEmpty(request?.ConnectionId) || string.IsNullOrEmpty(request?.GroupName))
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await errorResponse.WriteAsJsonAsync(new { success = false, message = "ConnectionId and GroupName are required" });
                    return new AddToGroupOutput { HttpResponse = errorResponse };
                }

                // Get user's tenant ID from RequestContext
                var requestCtx = req.GetRequestContext();
                var userTenantId = requestCtx.TenantId;
                var userEmail = requestCtx.UserPrincipalName;

                // Validate tenant access
                // Group names are in format: "tenant-{tenantId}", "session-{tenantId}-{sessionId}", or "global-admins"
                // Users can only join groups for their own tenant (unless they are Global Admin)

                // Explicit validation for the global-admins group. This is a READ broadcast group
                // (cross-tenant new-session/new-event + global-notification live pushes), so any platform
                // scope — Global Admin OR the read-only Global Reader — may join. Mutating actions remain
                // gated elsewhere (the reader only RECEIVES pushes).
                if (request.GroupName == "global-admins")
                {
                    if (!requestCtx.HasGlobalScope)
                    {
                        _logger.LogWarning($"User {userEmail} (tenant {userTenantId}) attempted to join global-admins group without platform scope");
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied: platform scope (Global Admin or Global Reader) required to join this group" });
                        return new AddToGroupOutput { HttpResponse = forbiddenResponse };
                    }
                    _logger.LogInformation($"Platform-scope user {userEmail} (role={requestCtx.UserRole}) joining global-admins group");
                }
                else
                {
                    var requestedTenantId = SignalRGroupHelper.ExtractTenantIdFromGroupName(request.GroupName);
                    if (string.IsNullOrEmpty(requestedTenantId))
                    {
                        _logger.LogWarning($"User {userEmail} attempted to join unrecognized group format: {request.GroupName}");
                        var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "Unrecognized group name format" });
                        return new AddToGroupOutput { HttpResponse = badRequestResponse };
                    }

                    // Check if user is allowed to join this tenant's group. Cross-tenant joins require
                    // platform scope (Global Admin OR read-only Global Reader) — both have cross-tenant
                    // READ scope, and group membership only RECEIVES live updates — OR a delegated ("MSP")
                    // admin whose managed scope includes the requested tenant (so the delegated dashboard
                    // gets the same live session/event pushes a GA gets for that tenant). The admin/member
                    // notification-group checks below still gate by the real tenant role, so a delegated
                    // caller (no tenant role) receives session/event broadcasts but NOT notification pushes.
                    // OrdinalIgnoreCase, matching the middleware's cross-tenant check and the delegated
                    // scope dictionary — a casing difference must never flip which branch authorizes.
                    if (!string.Equals(requestedTenantId, userTenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        var allowedCrossTenant = requestCtx.HasGlobalScope;
                        if (!allowedCrossTenant && !string.IsNullOrEmpty(userEmail))
                        {
                            // realtime/groups/join is not a tenant-scoped route, so the middleware did not
                            // resolve the delegated scope onto RequestContext — resolve it here, bounded to
                            // the requested tenant only.
                            var scope = await _delegatedAdminService.GetScopeAsync(userEmail);
                            allowedCrossTenant = scope.RoleFor(requestedTenantId) != null;
                            if (allowedCrossTenant)
                                _logger.LogInformation($"Delegated user {userEmail} joining managed cross-tenant group: {request.GroupName}");
                        }

                        if (!allowedCrossTenant)
                        {
                            _logger.LogWarning($"User {userEmail} (tenant {userTenantId}) attempted to join group for tenant {requestedTenantId}");
                            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                            await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied: You can only join groups for your own tenant" });
                            return new AddToGroupOutput { HttpResponse = forbiddenResponse };
                        }
                        else if (requestCtx.HasGlobalScope)
                        {
                            _logger.LogInformation($"Platform-scope user {userEmail} (role={requestCtx.UserRole}) joining cross-tenant group: {request.GroupName}");
                        }
                    }

                    // The plain "tenant-{tid}" broadcast group streams MemberRead-tier telemetry (new-session
                    // pushes, per-session "newevents" deltas incl. rule results), but this route's policy
                    // admits ANY authenticated user of the tenant — the Progress Portal's roleless end users,
                    // who only need their own "session-{tid}-{sid}" group. A same-tenant join skipped the
                    // cross-tenant gate above, so bind the broadcast group to a member role here; cross-tenant
                    // callers reaching this point were already admitted as platform-scope or delegated.
                    if (SignalRGroupHelper.IsTenantBroadcastJoinDenied(request.GroupName, requestedTenantId, requestCtx))
                    {
                        _logger.LogWarning($"User {userEmail} (role={requestCtx.UserRole}) attempted to join tenant broadcast group without a member role: {request.GroupName}");
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied: Only tenant members can join the tenant broadcast group" });
                        return new AddToGroupOutput { HttpResponse = forbiddenResponse };
                    }

                    // Notification groups carry the full per-tenant payload (the REST list is Member/Admin-tier
                    // gated), so they are role-gated AGAINST THE GROUP'S TENANT — not the caller's home tenant.
                    // This is leak-critical for a cross-tenant caller admitted above (a delegated "MSP" reader,
                    // or a Global Reader): their home-tenant Admin/member role must NOT let them join a DIFFERENT
                    // (managed) tenant's notify group. See SignalRGroupHelper.CheckNotifyGroupAccess.
                    var notifyDenial = SignalRGroupHelper.CheckNotifyGroupAccess(request.GroupName, requestedTenantId, requestCtx);
                    if (notifyDenial != SignalRGroupHelper.NotifyGroupDenial.None)
                    {
                        var message = notifyDenial == SignalRGroupHelper.NotifyGroupDenial.AdminTier
                            ? "Access denied: Only Tenant Admins can join the admin notification group"
                            : "Access denied: Only tenant members can join the notification group";
                        _logger.LogWarning($"User {userEmail} (role={requestCtx.UserRole}) attempted to join {notifyDenial} notification group for tenant {requestedTenantId}: {request.GroupName}");
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new { success = false, message });
                        return new AddToGroupOutput { HttpResponse = forbiddenResponse };
                    }
                }

                // Extract session ID from group name if it's a session-specific group
                var logPrefix = SignalRGroupHelper.ExtractLogPrefix(request.GroupName);
                _logger.LogInformation($"{logPrefix} AddToGroup: {request.GroupName} (User: {userEmail})");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Added to group {request.GroupName}"
                });

                return new AddToGroupOutput
                {
                    HttpResponse = response,
                    SignalRGroupAction = new SignalRGroupAction(SignalRGroupActionType.Add)
                    {
                        GroupName = request.GroupName,
                        ConnectionId = request.ConnectionId
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding to group");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return new AddToGroupOutput { HttpResponse = errorResponse };
            }
        }

    }

    public class AddToGroupRequest
    {
        public string? ConnectionId { get; set; }
        public string? GroupName { get; set; }
    }

    public class AddToGroupOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRGroupAction? SignalRGroupAction { get; set; }
    }
}
