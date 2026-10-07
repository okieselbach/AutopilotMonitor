using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Push;

/// <summary>
/// Device management from the portal: list (own devices for everyone on the tier, every device
/// of the scope for its admins), remove (wipe push, then the row is gone) and test. Tenant routes
/// under <c>push/devices…</c> (TenantAdminOrOperator), platform routes under
/// <c>global/push/devices…</c> (GlobalAdminOnly).
/// </summary>
public class PushDevicesFunctions
{
    private readonly PushPairingService _pairing;
    private readonly ILogger<PushDevicesFunctions> _logger;

    public PushDevicesFunctions(PushPairingService pairing, ILogger<PushDevicesFunctions> logger)
    {
        _pairing = pairing;
        _logger = logger;
    }

    [Function("ListPushDevices")]
    public Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "push/devices")] HttpRequestData req)
        => ListCoreAsync(req, PushFunctionSupport.TenantContext(req));

    [Function("DeletePushDevice")]
    public Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "push/devices/{deviceId}")] HttpRequestData req,
        string deviceId)
        => DeleteCoreAsync(req, deviceId, PushFunctionSupport.TenantContext(req));

    [Function("TestPushDevice")]
    public Task<HttpResponseData> Test(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/devices/{deviceId}/test")] HttpRequestData req,
        string deviceId)
        => TestCoreAsync(req, deviceId, PushFunctionSupport.TenantContext(req));

    [Function("ListPlatformPushDevices")]
    public Task<HttpResponseData> ListPlatform(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/push/devices")] HttpRequestData req)
        => ListCoreAsync(req, PushFunctionSupport.PlatformContext(req));

    [Function("DeletePlatformPushDevice")]
    public Task<HttpResponseData> DeletePlatform(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/push/devices/{deviceId}")] HttpRequestData req,
        string deviceId)
        => DeleteCoreAsync(req, deviceId, PushFunctionSupport.PlatformContext(req));

    [Function("TestPlatformPushDevice")]
    public Task<HttpResponseData> TestPlatform(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/push/devices/{deviceId}/test")] HttpRequestData req,
        string deviceId)
        => TestCoreAsync(req, deviceId, PushFunctionSupport.PlatformContext(req));

    private async Task<HttpResponseData> ListCoreAsync(HttpRequestData req, (NotificationScope Scope, PushCaller Caller) ctx)
    {
        try
        {
            return await req.OkAsync(await _pairing.ListAsync(ctx.Scope, ctx.Caller));
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "ListPushDevices");
        }
    }

    private async Task<HttpResponseData> DeleteCoreAsync(HttpRequestData req, string deviceId, (NotificationScope Scope, PushCaller Caller) ctx)
    {
        try
        {
            if (!PushFunctionSupport.IsPlausibleId(deviceId))
                return await req.NotFoundAsync("Device not found.");
            var error = await _pairing.DeleteAsync(ctx.Scope, deviceId, ctx.Caller);
            if (error != PushOpError.None)
                return await PushFunctionSupport.ErrorAsync(req, error, "Device not found.");
            return req.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "DeletePushDevice");
        }
    }

    private async Task<HttpResponseData> TestCoreAsync(HttpRequestData req, string deviceId, (NotificationScope Scope, PushCaller Caller) ctx)
    {
        try
        {
            if (!PushFunctionSupport.IsPlausibleId(deviceId))
                return await req.NotFoundAsync("Device not found.");
            var result = await _pairing.TestAsync(ctx.Scope, deviceId, ctx.Caller);
            return result.Ok
                ? await req.OkAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, "Device not found.");
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "TestPushDevice");
        }
    }
}
