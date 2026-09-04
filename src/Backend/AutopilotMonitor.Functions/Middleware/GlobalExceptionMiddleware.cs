using System.Net;
using Azure;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Safety-net middleware that catches any exception escaping past function-level catch blocks
/// and returns a structured error response instead of a raw 500. Placed after
/// <see cref="CorrelationIdMiddleware"/> (needs correlation ID) and before
/// <see cref="AuthenticationMiddleware"/> (catches auth-layer exceptions too).
/// MCP clients receive richer error details via the X-Client-Source header detection.
/// </summary>
public class GlobalExceptionMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(ILogger<GlobalExceptionMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext == null)
                throw; // Non-HTTP triggers: let it propagate to the Functions runtime

            if (httpContext.Response.HasStarted)
                throw; // Response already streaming: can't write a JSON body

            var correlationId = context.GetCorrelationId();
            var functionName = context.FunctionDefinition.Name;
            _logger.LogError(ex, "Unhandled exception in {Function} [CorrelationId={CorrelationId}]",
                functionName, correlationId);

            var isMcp = string.Equals(
                httpContext.Request.Headers["X-Client-Source"].FirstOrDefault(),
                "mcp", StringComparison.OrdinalIgnoreCase);

            // Same envelope as ResponseHelper.InternalServerErrorAsync: MCP clients get the
            // operation + recovery hint; the CLR exception type never leaves the process.
            await ApiErrorWriter.WriteAsync(
                httpContext, correlationId, HttpStatusCode.InternalServerError,
                Constants.ApiErrorCodes.InternalError,
                ResponseHelper.SanitizeErrorMessage(ex, functionName),
                hint: isMcp ? ResponseHelper.GetRecoveryHint(ex) : null,
                operation: isMcp ? functionName : null);
        }
    }
}
