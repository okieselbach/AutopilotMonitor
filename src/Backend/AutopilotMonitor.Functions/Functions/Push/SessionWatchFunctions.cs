using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Push;

/// <summary>
/// "Notify me when this session ends" on the session page (TenantAdminOrOperator, QueryParam
/// scoping like the annotations route so a Global Admin can watch a customer's session from
/// their own paired devices — the devices stay in the caller's own scope).
/// </summary>
public class SessionWatchFunctions
{
    private readonly PushSessionWatchService _watches;
    private readonly ISessionRepository _sessionRepo;
    private readonly ILogger<SessionWatchFunctions> _logger;

    public SessionWatchFunctions(PushSessionWatchService watches, ISessionRepository sessionRepo, ILogger<SessionWatchFunctions> logger)
    {
        _watches = watches;
        _sessionRepo = sessionRepo;
        _logger = logger;
    }

    [Function("GetSessionWatch")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/watch")] HttpRequestData req,
        string sessionId)
    {
        try
        {
            var (tenantId, caller) = await ScopeAsync(req, sessionId);
            return await req.OkAsync(await _watches.GetAsync(tenantId, sessionId, caller));
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "GetSessionWatch");
        }
    }

    [Function("WatchSession")]
    public async Task<HttpResponseData> Watch(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sessions/{sessionId}/watch")] HttpRequestData req,
        string sessionId)
    {
        try
        {
            if (!PushFunctionSupport.IsPlausibleId(sessionId))
                return await req.NotFoundAsync("Session not found.");
            var (tenantId, caller) = await ScopeAsync(req, sessionId);
            if (await _sessionRepo.GetSessionAsync(tenantId, sessionId) == null)
                return await req.NotFoundAsync("Session not found.");

            var result = await _watches.WatchAsync(tenantId, sessionId, caller);
            return result.Ok
                ? await req.OkAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, result.Message);
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "WatchSession");
        }
    }

    [Function("UnwatchSession")]
    public async Task<HttpResponseData> Unwatch(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}/watch")] HttpRequestData req,
        string sessionId)
    {
        try
        {
            var (tenantId, caller) = await ScopeAsync(req, sessionId);
            return await req.OkAsync(await _watches.UnwatchAsync(tenantId, sessionId, caller));
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "UnwatchSession");
        }
    }

    /// <summary>
    /// The watch lives in the SESSION's tenant; the devices it fires on are the caller's own
    /// (home-tenant scope, plus the platform scope for a Global Admin), resolved at terminal status
    /// by owner oid.
    /// </summary>
    private async Task<(string TenantId, PushCaller Caller)> ScopeAsync(HttpRequestData req, string sessionId)
    {
        var ctx = req.GetRequestContext();
        var tenantId = await ctx.ResolveSessionScopeAsync(_sessionRepo, sessionId, requireGlobalAdmin: true);
        var caller = new PushCaller(ctx.UserPrincipalName, ctx.ObjectId, ctx.TenantId,
            IsScopeAdmin: ctx.IsTenantAdmin || ctx.IsGlobalAdmin, IsGlobalAdmin: ctx.IsGlobalAdmin);
        return (tenantId, caller);
    }
}
