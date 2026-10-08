using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Push;

/// <summary>
/// The receiver's own routes — the phone never signs in (that is the point: Conditional Access
/// on the portal must not decide whether an admin's phone gets alerts). Two are anonymous and
/// gated by the one-shot pairing code (K8–K10), three by the device token in
/// <see cref="Constants.Push.DeviceTokenHeader"/> (K11). Per-IP throttle on the anonymous pair,
/// one identical 404 for every unusable code, the code is never logged, and no response names
/// an endpoint.
/// </summary>
public class PushReceiverFunctions
{
    private const int RedeemRateLimitPerMinute = 10;
    /// <summary>Per device: the token holder is one phone; its routes reach storage and (on a refused endpoint) an ops event.</summary>
    private const int DeviceRouteLimitPerMinute = 10;
    private const long MaxBodyBytes = 8 * 1024;

    private readonly PushPairingService _pairing;
    private readonly RateLimitService _rateLimit;
    private readonly ILogger<PushReceiverFunctions> _logger;

    public PushReceiverFunctions(PushPairingService pairing, RateLimitService rateLimit, ILogger<PushReceiverFunctions> logger)
    {
        _pairing = pairing;
        _rateLimit = rateLimit;
        _logger = logger;
    }

    [Function("BeginPushPair")]
    public async Task<HttpResponseData> Begin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/pair/begin")] HttpRequestData req)
    {
        try
        {
            if (await ThrottleAsync(req) is { } throttled) return throttled;

            var read = await req.ReadAsync<BeginPairRequest>(MaxBodyBytes);
            if (read.Error != null) return read.Error;

            var result = await _pairing.BeginAsync(read.Value!.Code);
            return result.Ok
                ? await req.OkAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, result.Message);
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "BeginPushPair");
        }
    }

    [Function("RedeemPushPair")]
    public async Task<HttpResponseData> Redeem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/pair")] HttpRequestData req)
    {
        try
        {
            if (await ThrottleAsync(req) is { } throttled) return throttled;

            var read = await req.ReadAsync<RedeemPairRequest>(MaxBodyBytes);
            if (read.Error != null) return read.Error;

            var result = await _pairing.RedeemAsync(read.Value!);
            return result.Ok
                ? await req.CreatedAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, result.Message);
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "RedeemPushPair");
        }
    }

    [Function("GetPushDevice")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "push/device")] HttpRequestData req)
    {
        try
        {
            var device = await _pairing.ResolveDeviceAsync(DeviceToken(req));
            if (device == null)
                return await req.ErrorAsync(HttpStatusCode.Unauthorized, Constants.ApiErrorCodes.InvalidDeviceToken, "Unknown device token.");
            if (await ThrottleDeviceAsync(req, device) is { } throttled) return throttled;

            return await req.OkAsync(await _pairing.GetDeviceStatusAsync(device));
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "GetPushDevice");
        }
    }

    [Function("ResubscribePushDevice")]
    public async Task<HttpResponseData> Resubscribe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "push/device")] HttpRequestData req)
    {
        try
        {
            var device = await _pairing.ResolveDeviceAsync(DeviceToken(req));
            if (device == null)
                return await req.ErrorAsync(HttpStatusCode.Unauthorized, Constants.ApiErrorCodes.InvalidDeviceToken, "Unknown device token.");
            if (await ThrottleDeviceAsync(req, device) is { } throttled) return throttled;

            var read = await req.ReadAsync<ResubscribeRequest>(MaxBodyBytes);
            if (read.Error != null) return read.Error;

            var result = await _pairing.ResubscribeAsync(device, read.Value!);
            return result.Ok
                ? await req.OkAsync(result.Value!)
                : await PushFunctionSupport.ErrorAsync(req, result.Error, result.Message ?? "This device is no longer paired.");
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "ResubscribePushDevice");
        }
    }

    [Function("UnpairPushDevice")]
    public async Task<HttpResponseData> Unpair(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "push/device")] HttpRequestData req)
    {
        try
        {
            var device = await _pairing.ResolveDeviceAsync(DeviceToken(req));
            if (device == null)
                return await req.ErrorAsync(HttpStatusCode.Unauthorized, Constants.ApiErrorCodes.InvalidDeviceToken, "Unknown device token.");
            if (await ThrottleDeviceAsync(req, device) is { } throttled) return throttled;

            await _pairing.UnpairAsync(device);
            return req.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "UnpairPushDevice");
        }
    }

    private static string? DeviceToken(HttpRequestData req)
        => req.Headers.TryGetValues(Constants.Push.DeviceTokenHeader, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Per device, after the token matched: one phone is one device, and the PUT reaches storage
    /// and (for a refused endpoint host) an ops event, so a leaked token must not turn into a
    /// write loop.
    /// </summary>
    private async Task<HttpResponseData?> ThrottleDeviceAsync(HttpRequestData req, Shared.DataAccess.PushDevice device)
    {
        var result = _rateLimit.CheckRateLimit($"push-device:{device.Scope}/{device.DeviceId}", DeviceRouteLimitPerMinute);
        if (result.IsAllowed)
            return null;

        return await req.ErrorAsync(HttpStatusCode.TooManyRequests, Constants.ApiErrorCodes.RateLimited,
            "Too many requests for this device. Try again in a minute.",
            retryAfterSeconds: result.RetryAfter is { } retryAfter ? (int)Math.Ceiling(retryAfter.TotalSeconds) : 60);
    }

    /// <summary>
    /// Second layer only (K9): the first is the ≥50-bit single-use code with its five-failure
    /// burn. Generous per IP because carriers put thousands of phones behind one address.
    /// </summary>
    private async Task<HttpResponseData?> ThrottleAsync(HttpRequestData req)
    {
        var clientIp = ClientIpExtractor.GetTrustedClientIp(req);
        var result = _rateLimit.CheckRateLimit($"push-pair:{clientIp}", RedeemRateLimitPerMinute);
        if (result.IsAllowed)
            return null;

        return await req.ErrorAsync(HttpStatusCode.TooManyRequests, Constants.ApiErrorCodes.RateLimited,
            "Too many pairing attempts. Try again in a minute.",
            retryAfterSeconds: result.RetryAfter is { } retryAfter ? (int)Math.Ceiling(retryAfter.TotalSeconds) : 60);
    }
}
