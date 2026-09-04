using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Middleware that ensures every request carries a Correlation ID.
/// Reads <c>X-Correlation-ID</c> from the incoming request; generates a new compact GUID if absent.
/// The ID is stored in <c>FunctionContext.Items["CorrelationId"]</c> (retrieve via
/// <c>context.GetCorrelationId()</c>), echoed back in the <c>X-Correlation-ID</c> response header,
/// and injected into the logging scope so all log entries for the request carry it automatically.
/// </summary>
public class CorrelationIdMiddleware : IFunctionsWorkerMiddleware
{
    private const string HeaderName = "X-Correlation-ID";
    private const string ItemsKey = "CorrelationId";

    /// <summary>
    /// Non-safelisted response headers a browser script may read cross-origin. Platform CORS
    /// (App Service) answers the preflight but has no setting for this header, so the app names
    /// them itself; whether the platform layer forwards it is verified after deploy — nothing
    /// depends on it (the portal mints its own X-Correlation-ID and every error body carries it).
    /// </summary>
    internal const string ExposedHeaders = "X-Correlation-ID, Retry-After, X-RateLimit-Limit, X-RateLimit-Remaining, X-RateLimit-Reset";

    // Strict allow-list gate against CRLF / log-forgery / App-Insights row-injection via inbound header.
    private static readonly Regex CorrelationIdPattern = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(ILogger<CorrelationIdMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();

        string correlationId;
        if (httpContext != null
            && httpContext.Request.Headers.TryGetValue(HeaderName, out var existingId)
            && !string.IsNullOrEmpty(existingId)
            && CorrelationIdPattern.IsMatch(existingId.ToString()))
        {
            correlationId = existingId.ToString();
        }
        else
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Items[ItemsKey] = correlationId;

        if (httpContext != null && !httpContext.Response.Headers.ContainsKey(HeaderName))
        {
            // Write directly before next() — OnStarting() is not reliably
            // triggered in the .NET 8 isolated worker (the host bridges the
            // worker's response, so the hook fires on a shadow object that
            // never reaches the wire). Without this, the correlation ID
            // never made it back to the client even though it was logged
            // on the server side.
            httpContext.Response.Headers[HeaderName] = correlationId;
        }

        if (httpContext != null && !httpContext.Response.Headers.ContainsKey("Access-Control-Expose-Headers"))
        {
            httpContext.Response.Headers["Access-Control-Expose-Headers"] = ExposedHeaders;
        }

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
