using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Functions.Push;

/// <summary>
/// Shared plumbing of the push functions: the caller as a <see cref="PushCaller"/>, the scope a
/// route addresses, and the one mapping from <see cref="PushOpError"/> to the error envelope.
/// </summary>
internal static class PushFunctionSupport
{
    /// <summary>
    /// The tenant scope of a <c>push/…</c> route: the caller's own tenant. A Global Admin passes
    /// the TenantAdminOrOperator tier by bypass, so the service re-checks the table-backed tenant
    /// role (K2/K5) — a GA without a member row is told to use the platform routes.
    /// </summary>
    public static (NotificationScope Scope, PushCaller Caller) TenantContext(HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var scope = NotificationScope.Tenant(ctx.TargetTenantId);
        var caller = new PushCaller(ctx.UserPrincipalName, ctx.ObjectId, ctx.TenantId,
            IsScopeAdmin: ctx.IsTenantAdmin || ctx.IsGlobalAdmin, IsGlobalAdmin: ctx.IsGlobalAdmin);
        return (scope, caller);
    }

    /// <summary>The platform scope of a <c>global/push/…</c> route (GlobalAdminOnly tier).</summary>
    public static (NotificationScope Scope, PushCaller Caller) PlatformContext(HttpRequestData req)
    {
        var ctx = req.GetRequestContext();
        var caller = new PushCaller(ctx.UserPrincipalName, ctx.ObjectId, ctx.TenantId,
            IsScopeAdmin: ctx.IsGlobalAdmin, IsGlobalAdmin: ctx.IsGlobalAdmin);
        return (NotificationScope.Platform, caller);
    }

    public static Task<HttpResponseData> ErrorAsync(HttpRequestData req, PushOpError error, string? message)
        => error switch
        {
            PushOpError.ChannelRequired => req.ErrorAsync(HttpStatusCode.Conflict, Constants.ApiErrorCodes.PushChannelRequired,
                message ?? "A Push notification channel must be enabled in this scope first."),
            PushOpError.NotEligible => req.ErrorAsync(HttpStatusCode.Forbidden, Constants.ApiErrorCodes.PushNotEligible,
                message ?? "Pairing needs a table-backed Admin or Operator role in this scope."),
            PushOpError.NotFound => req.NotFoundAsync(message ?? "Not found."),
            PushOpError.CodeInvalid => req.ErrorAsync(HttpStatusCode.NotFound, Constants.ApiErrorCodes.PairingCodeInvalid,
                "This pairing code is unknown, expired or already used."),
            PushOpError.CodeUsed => req.ErrorAsync(HttpStatusCode.Conflict, Constants.ApiErrorCodes.PairingCodeUsed,
                "This pairing code was just used by another device."),
            PushOpError.InvalidSubscription => req.ErrorAsync(HttpStatusCode.BadRequest, Constants.ApiErrorCodes.InvalidSubscription,
                message ?? "The push subscription is not acceptable."),
            PushOpError.NotConfigured => req.ErrorAsync(HttpStatusCode.ServiceUnavailable, Constants.ApiErrorCodes.ServiceUnavailable,
                "The push channel is not configured on the platform."),
            PushOpError.Conflict => req.ConflictAsync(message ?? "The operation conflicts with the current state."),
            _ => req.InternalServerErrorAsync(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                new InvalidOperationException("unmapped push error"), "Push"),
        };

    public static bool IsPlausibleId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
}
