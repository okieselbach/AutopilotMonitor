using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Emits worker-side <see cref="RequestTelemetry"/> with business context (TenantId, UserId,
/// CorrelationId, UserRole) so the Application Insights <c>requests</c> table is queryable
/// by tenant, user, and correlation ID. Runs first in the pipeline to capture accurate
/// duration including auth and policy evaluation. Non-HTTP triggers are skipped.
/// </summary>
public class RequestTelemetryMiddleware : IFunctionsWorkerMiddleware
{
    private readonly TelemetryClient _telemetryClient;

    // Exact allowlist of device endpoints that READ + validate the X-Tenant-Id header (cert auth
    // for agent/*, bootstrap-token context for bootstrap/*). ONLY these may seed the requests-table
    // TenantId tag from the header. A prefix match (/api/agent/* or /api/bootstrap/*) would also
    // catch routes that never validate the header — agent/config (takes ?tenantId=),
    // agent/register-session + agent/upload-url (tenant from body), bootstrap/validate/{code},
    // bootstrap/sessions{,/{code}}, bootstrap/config, bootstrap/register-session — where a client
    // could send X-Tenant-Id:<valid-guid> and pollute tenant attribution in telemetry.
    private static readonly HashSet<string> TenantHeaderTrustedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/agent/telemetry",
        "/api/agent/ingest",
        "/api/agent/error",
        "/api/agent/distress",
        "/api/bootstrap/ingest",
        "/api/bootstrap/error",
    };

    public RequestTelemetryMiddleware(TelemetryClient telemetryClient)
    {
        _telemetryClient = telemetryClient;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            // Non-HTTP trigger (timer, queue) — nothing to track
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var startTime = DateTimeOffset.UtcNow;
        Exception? caughtException = null;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            caughtException = ex;
            throw;
        }
        finally
        {
            sw.Stop();

            var statusCode = caughtException != null ? 500 : httpContext.Response.StatusCode;
            var functionName = context.FunctionDefinition.Name;
            var method = httpContext.Request.Method;
            var url = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.Path}{httpContext.Request.QueryString}";

            var requestTelemetry = new RequestTelemetry
            {
                Name = $"{method} {functionName}",
                Timestamp = startTime,
                Duration = sw.Elapsed,
                ResponseCode = statusCode.ToString(),
                Success = statusCode < 500,
                Url = new Uri(url),
            };

            // Distributed trace correlation
            var activity = Activity.Current;
            if (activity != null)
            {
                requestTelemetry.Context.Operation.Id = activity.RootId;
                requestTelemetry.Context.Operation.ParentId = activity.Id;
            }

            // Real client IP for the requests table (client_IP). The isolated worker behind
            // Front Door / App Service does not populate it — it defaults to 0.0.0.0, making the
            // column useless. Prefer X-Azure-ClientIP (Front Door sets it to the true client
            // egress IP); fall back to the trusted rightmost X-Forwarded-For hop. Join all header
            // values (matching ClientIpExtractor) so the rightmost trusted hop wins — taking only
            // the first value could surface a spoofable left-side entry. Diagnostic only: the rate
            // limiter independently uses the trusted hop (ClientIpExtractor.GetTrustedClientIp), so
            // a spoofed value here cannot affect throttling.
            // StringValues.ToString() joins multiple header values with "," — same semantics as
            // ClientIpExtractor's string.Join, so the rightmost trusted hop wins.
            var azureClientIp = httpContext.Request.Headers["X-Azure-ClientIP"].ToString();
            var clientIp = !string.IsNullOrWhiteSpace(azureClientIp)
                ? ClientIpExtractor.ExtractTrustedHop(azureClientIp)
                : ClientIpExtractor.ExtractTrustedHop(httpContext.Request.Headers["X-Forwarded-For"].ToString());
            if (clientIp != ClientIpExtractor.Unknown)
                requestTelemetry.Context.Location.Ip = clientIp;

            // Business context from downstream middleware
            requestTelemetry.Properties["Source"] = "WorkerMiddleware";
            requestTelemetry.Properties["FunctionName"] = functionName;
            requestTelemetry.Properties["HttpMethod"] = method;
            requestTelemetry.Properties["HttpPath"] = httpContext.Request.Path.Value ?? "";

            var clientSource = httpContext.Request.Headers["X-Client-Source"].FirstOrDefault();
            if (!string.IsNullOrEmpty(clientSource))
                requestTelemetry.Properties["ClientSource"] = clientSource;

            var mcpToolName = httpContext.Request.Headers["X-MCP-Tool-Name"].FirstOrDefault();
            if (!string.IsNullOrEmpty(mcpToolName))
                requestTelemetry.Properties["McpToolName"] = mcpToolName;

            if (context.Items.TryGetValue("CorrelationId", out var corrId) && corrId is string correlationId)
                requestTelemetry.Properties["CorrelationId"] = correlationId;

            var reqCtx = context.GetRequestContext();
            var tenantId = reqCtx.TenantId;
            if (string.IsNullOrEmpty(tenantId))
            {
                // Device ingest endpoints do not run through PolicyEnforcementMiddleware, so
                // reqCtx.TenantId is empty — but they carry a cert/token-validated tenant in the
                // X-Tenant-Id header. Only honor the header on the exact routes that actually
                // validate it (TenantHeaderTrustedPaths) and only when it is a well-formed GUID, so
                // other/anonymous routes cannot pollute the requests table with arbitrary tenant ids.
                var path = httpContext.Request.Path.Value ?? string.Empty;
                if (TenantHeaderTrustedPaths.Contains(path))
                {
                    var headerTenant = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(headerTenant) && Guid.TryParse(headerTenant, out _))
                        tenantId = headerTenant;
                }
            }
            if (!string.IsNullOrEmpty(tenantId))
                requestTelemetry.Properties["TenantId"] = tenantId;
            if (!string.IsNullOrEmpty(reqCtx.UserPrincipalName))
                requestTelemetry.Properties["UserId"] = reqCtx.UserPrincipalName;
            if (!string.IsNullOrEmpty(reqCtx.UserRole))
                requestTelemetry.Properties["UserRole"] = reqCtx.UserRole;

            if (caughtException != null)
                requestTelemetry.Properties["ExceptionType"] = caughtException.GetType().Name;

            try
            {
                _telemetryClient.TrackRequest(requestTelemetry);
            }
            catch
            {
                // Never let telemetry failures mask the original exception or crash the pipeline
            }
        }
    }
}
