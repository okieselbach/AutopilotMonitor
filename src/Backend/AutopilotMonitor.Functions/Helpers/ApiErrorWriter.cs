using System.Net;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Http;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The error-envelope writer for the middleware side of the pipeline, where only the ASP.NET Core
/// <see cref="HttpContext"/> exists (no <c>HttpRequestData</c>). Same body, same serializer settings
/// (<see cref="ApiJsonOptions.Instance"/>) as <see cref="ResponseHelper.ErrorAsync"/>, so a 401 from
/// the authentication middleware and a 400 from a function are indistinguishable in shape.
/// The correlation id is stamped here — call sites never fetch it.
/// </summary>
public static class ApiErrorWriter
{
    /// <summary>The status-default <c>code</c> for sites whose status is computed at runtime.</summary>
    public static string DefaultCode(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => Constants.ApiErrorCodes.BadRequest,
        HttpStatusCode.Unauthorized => Constants.ApiErrorCodes.Unauthorized,
        HttpStatusCode.Forbidden => Constants.ApiErrorCodes.Forbidden,
        HttpStatusCode.NotFound => Constants.ApiErrorCodes.NotFound,
        HttpStatusCode.Conflict => Constants.ApiErrorCodes.Conflict,
        HttpStatusCode.Gone => Constants.ApiErrorCodes.Gone,
        HttpStatusCode.RequestEntityTooLarge => Constants.ApiErrorCodes.PayloadTooLarge,
        HttpStatusCode.UnprocessableEntity => Constants.ApiErrorCodes.UnprocessableEntity,
        HttpStatusCode.TooManyRequests => Constants.ApiErrorCodes.RateLimited,
        HttpStatusCode.InternalServerError => Constants.ApiErrorCodes.InternalError,
        HttpStatusCode.BadGateway => Constants.ApiErrorCodes.UpstreamError,
        HttpStatusCode.ServiceUnavailable => Constants.ApiErrorCodes.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout => Constants.ApiErrorCodes.UpstreamTimeout,
        _ => status.ToString(),
    };

    /// <summary>Build the generic envelope. Pure; shared by both writers and by tests.</summary>
    public static ApiErrorResponse Build(
        string correlationId, string code, string message, string? hint = null,
        int? retryAfterSeconds = null, string? operation = null)
        => new()
        {
            Error = message,
            Code = code,
            CorrelationId = correlationId,
            Hint = hint,
            RetryAfterSeconds = retryAfterSeconds,
            Operation = operation,
        };

    /// <summary>Write the generic envelope; sets <c>Retry-After</c> when <paramref name="retryAfterSeconds"/> is given.</summary>
    public static Task WriteAsync(
        HttpContext httpContext, string correlationId, HttpStatusCode status, string code, string message,
        string? hint = null, int? retryAfterSeconds = null, string? operation = null)
        => WriteAsync(httpContext, correlationId, status,
            Build(correlationId, code, message, hint, retryAfterSeconds, operation), retryAfterSeconds);

    /// <summary>Write a specialised error body; stamps its correlation id first.</summary>
    public static async Task WriteAsync<T>(
        HttpContext httpContext, string correlationId, HttpStatusCode status, T body, int? retryAfterSeconds = null)
        where T : class, IApiErrorResponse
    {
        body.CorrelationId = correlationId;
        httpContext.Response.StatusCode = (int)status;
        httpContext.Response.ContentType = "application/json";
        if (retryAfterSeconds is int seconds)
            httpContext.Response.Headers["Retry-After"] = seconds.ToString();
        await httpContext.Response.WriteAsJsonAsync(body, body.GetType(), ApiJsonOptions.Instance);
    }
}
