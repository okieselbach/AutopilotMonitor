using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Push;

/// <summary>
/// PC side of pairing (K7): create a code, poll what the receiver did with it, confirm or reject
/// the Pending device. Tenant routes (<c>push/pairings…</c>, TenantAdminOrOperator, JWT scope)
/// and platform routes (<c>global/push/pairings…</c>, GlobalAdminOnly) share one core.
/// </summary>
public class PushPairingFunctions
{
    private readonly PushPairingService _pairing;
    private readonly ILogger<PushPairingFunctions> _logger;

    public PushPairingFunctions(PushPairingService pairing, ILogger<PushPairingFunctions> logger)
    {
        _pairing = pairing;
        _logger = logger;
    }

    // ── Tenant scope ─────────────────────────────────────────────────────────

    [Function("CreatePushPairing")]
    public Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/pairings")] HttpRequestData req)
        => CreateCoreAsync(req, PushFunctionSupport.TenantContext(req));

    [Function("GetPushPairingStatus")]
    public Task<HttpResponseData> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "push/pairings/{pairingId}")] HttpRequestData req,
        string pairingId)
        => StatusCoreAsync(req, pairingId, PushFunctionSupport.TenantContext(req));

    [Function("ConfirmPushPairing")]
    public Task<HttpResponseData> Confirm(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/pairings/{pairingId}/confirm")] HttpRequestData req,
        string pairingId)
        => ConfirmCoreAsync(req, pairingId, PushFunctionSupport.TenantContext(req));

    [Function("RejectPushPairing")]
    public Task<HttpResponseData> Reject(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/pairings/{pairingId}/reject")] HttpRequestData req,
        string pairingId)
        => RejectCoreAsync(req, pairingId, PushFunctionSupport.TenantContext(req));

    // ── Platform scope ───────────────────────────────────────────────────────

    [Function("CreatePlatformPushPairing")]
    public Task<HttpResponseData> CreatePlatform(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/push/pairings")] HttpRequestData req)
        => CreateCoreAsync(req, PushFunctionSupport.PlatformContext(req));

    [Function("GetPlatformPushPairingStatus")]
    public Task<HttpResponseData> StatusPlatform(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/push/pairings/{pairingId}")] HttpRequestData req,
        string pairingId)
        => StatusCoreAsync(req, pairingId, PushFunctionSupport.PlatformContext(req));

    [Function("ConfirmPlatformPushPairing")]
    public Task<HttpResponseData> ConfirmPlatform(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/push/pairings/{pairingId}/confirm")] HttpRequestData req,
        string pairingId)
        => ConfirmCoreAsync(req, pairingId, PushFunctionSupport.PlatformContext(req));

    [Function("RejectPlatformPushPairing")]
    public Task<HttpResponseData> RejectPlatform(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/push/pairings/{pairingId}/reject")] HttpRequestData req,
        string pairingId)
        => RejectCoreAsync(req, pairingId, PushFunctionSupport.PlatformContext(req));

    // ── Core ─────────────────────────────────────────────────────────────────

    private async Task<HttpResponseData> CreateCoreAsync(HttpRequestData req, (NotificationScope Scope, PushCaller Caller) ctx)
    {
        try
        {
            var result = await _pairing.CreateAsync(ctx.Scope, ctx.Caller);
            return result.Ok
                ? await req.CreatedAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, result.Message);
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "CreatePushPairing");
        }
    }

    private async Task<HttpResponseData> StatusCoreAsync(HttpRequestData req, string pairingId, (NotificationScope Scope, PushCaller Caller) ctx)
    {
        try
        {
            if (!PushFunctionSupport.IsPlausibleId(pairingId))
                return await req.NotFoundAsync("Pairing not found.");
            var result = await _pairing.GetStatusAsync(ctx.Scope, pairingId, ctx.Caller);
            return result.Ok
                ? await req.OkAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, "Pairing not found.");
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "GetPushPairingStatus");
        }
    }

    private async Task<HttpResponseData> ConfirmCoreAsync(HttpRequestData req, string pairingId, (NotificationScope Scope, PushCaller Caller) ctx)
    {
        try
        {
            if (!PushFunctionSupport.IsPlausibleId(pairingId))
                return await req.NotFoundAsync("Pairing not found.");
            var result = await _pairing.ConfirmAsync(ctx.Scope, pairingId, ctx.Caller);
            return result.Ok
                ? await req.OkAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, result.Message ?? "Pairing not found.");
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "ConfirmPushPairing");
        }
    }

    private async Task<HttpResponseData> RejectCoreAsync(HttpRequestData req, string pairingId, (NotificationScope Scope, PushCaller Caller) ctx)
    {
        try
        {
            if (!PushFunctionSupport.IsPlausibleId(pairingId))
                return await req.NotFoundAsync("Pairing not found.");
            var error = await _pairing.RejectAsync(ctx.Scope, pairingId, ctx.Caller);
            if (error != PushOpError.None)
                return await PushFunctionSupport.ErrorAsync(req, error, "Pairing not found.");
            return req.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "RejectPushPairing");
        }
    }
}
