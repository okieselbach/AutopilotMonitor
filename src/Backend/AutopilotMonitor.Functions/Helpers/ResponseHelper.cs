using System.Net;
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

    /// <summary>400 Bad Request with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> BadRequestAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>401 Unauthorized with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> UnauthorizedAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.Unauthorized);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>403 Forbidden with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> ForbiddenAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.Forbidden);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>404 Not Found with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> NotFoundAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>
    /// 500 Internal Server Error with structured, AI-consumable error details.
    /// Returns exception type, sanitized message, correlation ID, and operation context.
    /// MCP clients (identified by <c>X-Client-Source: mcp</c>) receive richer details
    /// including recovery hints and request context to help the AI self-correct.
    /// Stack traces and infrastructure secrets are never exposed.
    /// </summary>
    public static async Task<HttpResponseData> InternalServerErrorAsync(
        this HttpRequestData req,
        ILogger logger,
        Exception ex,
        string operation,
        object? context = null)
    {
        var correlationId = req.FunctionContext.GetCorrelationId();
        logger.LogError(ex, "{Operation} failed [CorrelationId={CorrelationId}]", operation, correlationId);

        var isMcpClient = IsMcpRequest(req);

        var errorBody = new Dictionary<string, object?>
        {
            ["error"] = SanitizeErrorMessage(ex, operation),
            ["correlationId"] = correlationId,
            ["exceptionType"] = ex.GetType().Name,
        };

        // MCP clients get richer details to help the AI self-correct
        if (isMcpClient)
        {
            errorBody["operation"] = operation;
            if (context != null) errorBody["context"] = context;
            var hint = GetRecoveryHint(ex);
            if (hint != null) errorBody["hint"] = hint;
        }

        var response = req.CreateResponse(HttpStatusCode.InternalServerError);
        await response.WriteAsJsonAsync(errorBody);
        return response;
    }

    /// <summary>
    /// 500 Internal Server Error (simple overload for backward compatibility).
    /// Delegates to the enhanced overload using logMessage as the operation name.
    /// </summary>
    public static async Task<HttpResponseData> InternalServerErrorAsync(
        this HttpRequestData req,
        ILogger logger,
        Exception ex,
        string logMessage = "Unhandled error")
    {
        return await InternalServerErrorAsync(req, logger, ex, operation: logMessage);
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
