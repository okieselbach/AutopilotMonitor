using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using AutopilotMonitor.Functions.Extensions;
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
    /// <summary>GC pause during the request below this is not worth a column on the row.</summary>
    internal static readonly TimeSpan GcPauseStampThreshold = TimeSpan.FromMilliseconds(100);

    private readonly TelemetryClient _telemetryClient;

    // Exact allowlist of device endpoints that READ + validate the X-Tenant-Id header (cert auth
    // for agent/*, bootstrap-token context for bootstrap/*). ONLY these may seed the requests-table
    // TenantId tag from the header. A prefix match (/api/agent/* or /api/bootstrap/*) would also
    // catch routes that never validate the header — agent/config (takes ?tenantId=),
    // agent/register-session + agent/upload-url (tenant from body), bootstrap/validate/{code},
    // bootstrap/sessions{,/{code}}, bootstrap/config, bootstrap/register-session — where a client
    // could send X-Tenant-Id:<valid-guid> and pollute tenant attribution in telemetry.
    // Those routes get their TenantId from RequestRowMarkers.ValidatedTenantKey instead, which
    // SecurityValidator stamps only once the tenant is proven (see ResolveTenantId). The list
    // stays for the routes that never reach the validator (distress is pre-auth).
    private static readonly HashSet<string> TenantHeaderTrustedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/agent/telemetry",
        "/api/agent/error",
        "/api/agent/distress",
        "/api/bootstrap/error",
    };

    public RequestTelemetryMiddleware(TelemetryClient telemetryClient)
    {
        _telemetryClient = telemetryClient;
    }

    /// <summary>
    /// Tenant attribution for the request row, most trusted source first: the policy pipeline's
    /// tenant (portal / MCP routes), then the tenant <c>SecurityValidator</c> proved for an agent
    /// request (<see cref="RequestRowMarkers.ValidatedTenantKey"/> — the only source for
    /// register / config / upload-url, which validate no tenant header), and last the
    /// <c>X-Tenant-Id</c> header on the exact routes that validate it themselves
    /// (<see cref="TenantHeaderTrustedPaths"/>), when it is a well-formed GUID. Any other route
    /// gets no tenant: an anonymous caller must not be able to pollute the attribution.
    /// </summary>
    internal static string? ResolveTenantId(
        string? policyTenantId,
        IDictionary<object, object> items,
        string path,
        string? headerTenantId)
    {
        if (!string.IsNullOrEmpty(policyTenantId))
            return policyTenantId;

        if (items.TryGetValue(RequestRowMarkers.ValidatedTenantKey, out var validated)
            && validated is string validatedTenantId && !string.IsNullOrEmpty(validatedTenantId))
            return validatedTenantId;

        if (TenantHeaderTrustedPaths.Contains(path)
            && !string.IsNullOrEmpty(headerTenantId) && Guid.TryParse(headerTenantId, out _))
            return headerTenantId;

        return null;
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
        var gcPauseAtStart = GC.GetTotalPauseDuration();
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

            // NOTE: client_IP is deliberately NOT populated here. App Insights uses the request IP
            // only for geo-lookup and then masks the stored client_IP to 0.0.0.0 (verified: every
            // request in the resource shows 0.0.0.0), so setting Context.Location.Ip has no effect
            // on the column. On top of that, the only IP reaching the isolated worker is an Azure-
            // infra hop (West Europe), not the device's real egress IP — the device IP would need a
            // non-masked custom property AND the correct forwarded header (Front-Door-topology
            // dependent). Not worth it: TenantId (above) + CorrelationId already isolate a device.
            // See memory project_ingest_request_multiplication_429 for the full diagnosis.

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

            // APPLICATION PRINCIPALS — the request-row dimension that lets KQL separate automation
            // (app-only tokens) from people: "app" plus the calling client id, nothing for a person.
            if (context.GetUser() is { } principal && principal.IsApplicationPrincipal())
            {
                requestTelemetry.Properties["PrincipalKind"] = "app";
                var applicationId = principal.GetApplicationId();
                if (applicationId != null)
                    requestTelemetry.Properties["ApplicationId"] = applicationId;
            }

            if (context.Items.TryGetValue("CorrelationId", out var corrId) && corrId is string correlationId)
                requestTelemetry.Properties["CorrelationId"] = correlationId;

            // THROTTLE SURFACE — set by UserRateLimitMiddleware from the signed client-auth claim
            // (portal = public client, integration = confidential client / app-only). Next to the
            // self-declared ClientSource header it makes the classification auditable in KQL:
            // ClientSource == 'mcp' must always land on 'integration'.
            if (context.Items.TryGetValue(UserRateLimitMiddleware.ThrottleSurfaceItemKey, out var surface) && surface is string throttleSurface)
                requestTelemetry.Properties["ThrottleSurface"] = throttleSurface;

            // CERT-TENANT-BINDING-SHADOW — set by SecurityValidator.ObserveCertTenantBinding.
            // Carried on the request row rather than a trace line: worker-side LogInformation never
            // reaches App Insights (provider default rule is Warning+), so the bulk "Match" outcome
            // would be invisible and the shadow telemetry would have no denominator. This row already
            // exists per request and is unsampled, so the outcome costs no additional telemetry.
            if (context.Items.TryGetValue(CertTenantBinding.RequestItemKey, out var binding) && binding is string bindingOutcome)
                requestTelemetry.Properties["CertTenantBinding"] = bindingOutcome;

            // SESSION-OWNER-BINDING-SHADOW — set by SessionOwnerBindingObserver on register /
            // telemetry / error-report requests. Same carrier and same reasoning as above.
            if (context.Items.TryGetValue(SessionOwnershipPolicy.RequestItemKey, out var ownerBinding) && ownerBinding is string ownerOutcome)
                requestTelemetry.Properties["SessionOwnerBinding"] = ownerOutcome;

            // DEVICE-IDENTITY-BLOCK-BINDING — set by the kill-switch call sites (telemetry / config).
            if (context.Items.TryGetValue(DeviceIdentityBinding.RequestItemKey, out var identityBinding) && identityBinding is string identityOutcome)
                requestTelemetry.Properties[DeviceIdentityBinding.RequestItemKey] = identityOutcome;

            // DEVICE-VALIDATION — the admitting validator (or None / Transient / Rejected), set by
            // SecurityValidator once the device-validation chain has run. Same carrier and reasoning.
            if (context.Items.TryGetValue(RequestRowMarkers.DeviceValidationKey, out var deviceValidation) && deviceValidation is string deviceValidationOutcome)
                requestTelemetry.Properties[RequestRowMarkers.DeviceValidationKey] = deviceValidationOutcome;

            var reqCtx = context.GetRequestContext();
            var tenantId = ResolveTenantId(
                reqCtx.TenantId,
                context.Items,
                httpContext.Request.Path.Value ?? string.Empty,
                httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault());
            if (!string.IsNullOrEmpty(tenantId))
                requestTelemetry.Properties["TenantId"] = tenantId;
            if (!string.IsNullOrEmpty(reqCtx.UserPrincipalName))
                requestTelemetry.Properties["UserId"] = reqCtx.UserPrincipalName;
            if (!string.IsNullOrEmpty(reqCtx.UserRole))
                requestTelemetry.Properties["UserRole"] = reqCtx.UserRole;

            if (caughtException != null)
                requestTelemetry.Properties["ExceptionType"] = caughtException.GetType().Name;

            // Process-wide GC pause time that elapsed while this request was in flight — not the
            // request's own allocation cost. The point is the opposite: a slow request whose
            // duration is mostly GC pause was stalled by the process, not by its own work (the
            // ProcessStall event says which kind of stall). Stamped only when worth a look.
            var gcPause = GC.GetTotalPauseDuration() - gcPauseAtStart;
            if (gcPause >= GcPauseStampThreshold)
                requestTelemetry.Properties["GcPauseMs"] = Math.Round(gcPause.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Bypass the worker's adaptive sampling for this item. The SDK sampling processor
            // passes through any item whose SamplingPercentage is already set, so this is the
            // per-item opt-out that host.json "excludedTypes" (host process only) cannot provide.
            // Live-verified 2026-08-23 BEFORE this change: worker request rows carried
            // ItemCount 2-5 on weekdays (~30% of individual requests missing), so count()-based
            // queries undercounted. This copy is the single canonical request record — the host
            // duplicate is dropped by a workspace DCR transformation on AppRequests.
            ((ISupportSampling)requestTelemetry).SamplingPercentage = 100;

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
