using System.Net;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Extension methods on HttpRequestData for creating consistent HTTP responses.
/// Eliminates repeated CreateResponse + WriteAsJsonAsync boilerplate across function handlers.
/// Success bodies are typed (<see cref="IApiResponse"/>): the generic overloads constrain on the
/// marker interface, which anonymous objects cannot implement — so new untyped success shapes
/// fail to compile once the legacy object overloads are gone (ratchet: TypedResponseGuardTests).
/// </summary>
public static class ResponseHelper
{
    /// <summary>200 OK with a typed JSON body.</summary>
    public static Task<HttpResponseData> OkAsync<T>(this HttpRequestData req, T data)
        where T : class, IApiResponse
    {
        return JsonAsync(req, HttpStatusCode.OK, data);
    }

    /// <summary>201 Created with a typed JSON body.</summary>
    public static Task<HttpResponseData> CreatedAsync<T>(this HttpRequestData req, T data)
        where T : class, IApiResponse
    {
        return JsonAsync(req, HttpStatusCode.Created, data);
    }

    /// <summary>Typed JSON body with an explicit status code (e.g. success-flag-dependent 200/500).</summary>
    public static async Task<HttpResponseData> JsonAsync<T>(this HttpRequestData req, HttpStatusCode status, T data)
        where T : class, IApiResponse
    {
        var response = req.CreateResponse(status);
        await response.WriteAsJsonAsync(data);
        return response;
    }

    // ── Error envelope ──────────────────────────────────────────────────────────────────
    // Every non-2xx body is an IApiErrorResponse: { error, code, correlationId, ... }. The
    // correlation id is stamped HERE, never at the call site. ApiErrorWriter writes the same
    // envelope on the middleware side (HttpContext). Codes: Constants.ApiErrorCodes unless a
    // domain class owns the value.

    /// <summary>Generic error envelope with an explicit status; sets <c>Retry-After</c> when given.</summary>
    public static Task<HttpResponseData> ErrorAsync(
        this HttpRequestData req, HttpStatusCode status, string code, string message,
        string? hint = null, int? retryAfterSeconds = null)
    {
        var body = ApiErrorWriter.Build(req.FunctionContext.GetCorrelationId(), code, message, hint, retryAfterSeconds);
        return ErrorAsync(req, status, body, retryAfterSeconds);
    }

    /// <summary>Specialised error body (e.g. <see cref="DelegatedSlotLimitReachedResponse"/>); stamps its correlation id.</summary>
    public static async Task<HttpResponseData> ErrorAsync<T>(
        this HttpRequestData req, HttpStatusCode status, T body, int? retryAfterSeconds = null)
        where T : class, IApiErrorResponse
    {
        body.CorrelationId = req.FunctionContext.GetCorrelationId();
        var response = req.CreateResponse(status);
        if (retryAfterSeconds is int seconds)
            response.Headers.Add("Retry-After", seconds.ToString());
        await response.WriteAsJsonAsync(body);
        return response;
    }

    /// <summary>400 Bad Request envelope.</summary>
    public static Task<HttpResponseData> BadRequestAsync(
        this HttpRequestData req, string message, string code = Constants.ApiErrorCodes.BadRequest, string? hint = null)
        => ErrorAsync(req, HttpStatusCode.BadRequest, code, message, hint);

    /// <summary>401 Unauthorized envelope.</summary>
    public static Task<HttpResponseData> UnauthorizedAsync(
        this HttpRequestData req, string message, string code = Constants.ApiErrorCodes.Unauthorized, string? hint = null)
        => ErrorAsync(req, HttpStatusCode.Unauthorized, code, message, hint);

    /// <summary>403 Forbidden envelope.</summary>
    public static Task<HttpResponseData> ForbiddenAsync(
        this HttpRequestData req, string message, string code = Constants.ApiErrorCodes.Forbidden, string? hint = null)
        => ErrorAsync(req, HttpStatusCode.Forbidden, code, message, hint);

    /// <summary>404 Not Found envelope.</summary>
    public static Task<HttpResponseData> NotFoundAsync(
        this HttpRequestData req, string message, string code = Constants.ApiErrorCodes.NotFound, string? hint = null)
        => ErrorAsync(req, HttpStatusCode.NotFound, code, message, hint);

    /// <summary>409 Conflict envelope.</summary>
    public static Task<HttpResponseData> ConflictAsync(
        this HttpRequestData req, string message, string code = Constants.ApiErrorCodes.Conflict, string? hint = null)
        => ErrorAsync(req, HttpStatusCode.Conflict, code, message, hint);

    /// <summary>
    /// 500 Internal Server Error envelope (<c>code = InternalError</c>) with a sanitized message.
    /// MCP clients (identified by <c>X-Client-Source: mcp</c>) additionally receive the failing
    /// operation and a recovery hint so the AI can self-correct. Stack traces, CLR type names and
    /// infrastructure secrets are never exposed — the correlation id is the handle for the log.
    /// </summary>
    public static Task<HttpResponseData> InternalServerErrorAsync(
        this HttpRequestData req,
        ILogger logger,
        Exception ex,
        string operation)
    {
        var correlationId = req.FunctionContext.GetCorrelationId();
        logger.LogError(ex, "{Operation} failed [CorrelationId={CorrelationId}]", operation, correlationId);

        var isMcpClient = IsMcpRequest(req);
        var body = ApiErrorWriter.Build(
            correlationId,
            Constants.ApiErrorCodes.InternalError,
            SanitizeErrorMessage(ex, operation),
            hint: isMcpClient ? GetRecoveryHint(ex) : null,
            operation: isMcpClient ? operation : null);

        return ErrorAsync(req, HttpStatusCode.InternalServerError, body);
    }

    /// <summary>
    /// 500 Internal Server Error (simple overload for backward compatibility).
    /// Delegates to the enhanced overload using logMessage as the operation name.
    /// </summary>
    public static Task<HttpResponseData> InternalServerErrorAsync(
        this HttpRequestData req,
        ILogger logger,
        Exception ex)
    {
        return InternalServerErrorAsync(req, logger, ex, operation: "Unhandled error");
    }

    /// <summary>Detect MCP clients via the X-Client-Source header set by the MCP server.</summary>
    internal static bool IsMcpRequest(HttpRequestData req)
    {
        var httpContext = req.FunctionContext.GetHttpContext();
        if (httpContext == null) return false;
        return string.Equals(
            httpContext.Request.Headers["X-Client-Source"].FirstOrDefault(),
            "mcp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extract a safe, human-readable error message from known exception types.
    /// Unknown exceptions get a generic message that points to the correlation ID.
    /// </summary>
    internal static string SanitizeErrorMessage(Exception ex, string operation)
    {
        return ex switch
        {
            RequestFailedException rfe when rfe.ErrorCode != null =>
                $"{operation}: {rfe.ErrorCode} — {FirstLine(rfe.Message)}",
            RequestFailedException rfe =>
                $"{operation}: Azure error (HTTP {rfe.Status}). Use correlationId to investigate in backend logs.",
            ArgumentException ae =>
                $"{operation}: Invalid argument — {ae.Message}",
            InvalidOperationException =>
                $"{operation}: Invalid operation. Use correlationId to investigate in backend logs.",
            TimeoutException =>
                $"{operation}: The operation timed out",
            TaskCanceledException =>
                $"{operation}: The operation timed out or was cancelled",
            HttpRequestException hre =>
                $"{operation}: External service call failed ({hre.StatusCode})",
            FormatException fe =>
                $"{operation}: Invalid format — {fe.Message}",
            _ =>
                $"{operation} failed. Use correlationId to investigate in backend logs.",
        };
    }

    /// <summary>
    /// Extract only the first line from a (potentially multi-line) Azure SDK error message.
    /// Azure SDK messages dump the full HTTP response (RequestId, headers, body) after the
    /// first line — those details leak infrastructure info and must not be exposed.
    /// </summary>
    private static string FirstLine(string message)
    {
        var idx = message.IndexOf('\n');
        return idx > 0 ? message[..idx].TrimEnd('\r') : message;
    }

    /// <summary>
    /// Provide AI-targeted recovery hints based on exception type.
    /// Returns null when no specific guidance is available.
    /// </summary>
    internal static string? GetRecoveryHint(Exception ex)
    {
        return ex switch
        {
            RequestFailedException { ErrorCode: "InvalidInput" or "BadRequest" or "NotImplemented" } =>
                "The OData filter expression may be malformed. Check syntax: string values need single quotes, property names are case-sensitive. Example: \"Status eq 'Failed'\".",
            RequestFailedException { Status: 404 } =>
                "The requested resource was not found. Verify the table name, partition key, or entity exists.",
            RequestFailedException { Status: 409 } =>
                "Conflict — the entity was modified concurrently. Retry the operation.",
            RequestFailedException { Status: 429 } =>
                "Rate limited by Azure Storage. Wait a moment and retry with a smaller query.",
            TimeoutException or TaskCanceledException =>
                "The backend timed out. Try reducing the query scope (fewer results, narrower time range, more specific filters).",
            ArgumentException =>
                "One or more parameters are invalid. Check parameter types and required fields.",
            _ => null,
        };
    }
}
