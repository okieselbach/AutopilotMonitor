using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Enforces the per-user MCP daily/monthly request quota. Runs after
/// <see cref="UserRateLimitMiddleware"/> (per-minute burst control) — this is the budget layer on
/// top. Applies ONLY to HTTP requests marked <c>X-Client-Source: mcp</c> with an authenticated
/// principal (oid); everything else passes through untouched. Global Admins are exempt from the
/// quota (but their usage is still tracked for the metrics surface).
///
/// This middleware also OWNS the usage-counter increment (moved here from AuthenticationMiddleware,
/// Codex finding 2026-07-07): check-then-increment, and only for requests that are actually
/// served — denied requests (403 upstream, 429 here) no longer inflate the counters, and a
/// request can never be blocked by its OWN in-flight increment. The increment stays
/// fire-and-forget (never blocks the request path).
///
/// <b>The quota boundary is deliberately SOFT, not exact.</b> McpQuotaService caches the
/// per-user decision for 60 seconds (per instance), so an ALLOWED decision keeps admitting
/// requests inside that window even after the async increments push the stored counters past
/// the limit. Worst-case overshoot is bounded: ~60s × the caller's request rate (× instance
/// count on scaled-out Flex Consumption) — the same deliberate posture as the sliding-window
/// rate limiter, trading exactness for one counter read per user per minute instead of per
/// request. Pinned by McpQuotaServiceTests soft-boundary tests; do NOT re-document this as an
/// exact limit without reworking the decision cache.
///
/// Over-quota requests get 429 with a structured body, <c>Retry-After</c>, and
/// <c>X-MCP-Quota-*</c> headers; allowed MCP requests get the quota headers too so the MCP
/// client can surface remaining budget. Fail-open on storage/counter errors (handled inside
/// <see cref="McpQuotaService"/>), fail-closed on plan resolution (unknown plan → Community).
/// </summary>
public class McpQuotaEnforcementMiddleware : IFunctionsWorkerMiddleware
{
    private readonly McpQuotaService _quotaService;
    private readonly IUserUsageRepository _userUsageRepo;
    private readonly ILogger<McpQuotaEnforcementMiddleware> _logger;

    public McpQuotaEnforcementMiddleware(
        McpQuotaService quotaService,
        IUserUsageRepository userUsageRepo,
        ILogger<McpQuotaEnforcementMiddleware> logger)
    {
        _quotaService = quotaService;
        _userUsageRepo = userUsageRepo;
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var isMcpRequest = string.Equals(
            httpContext.Request.Headers["X-Client-Source"].FirstOrDefault(), "mcp", StringComparison.OrdinalIgnoreCase);
        if (!isMcpRequest)
        {
            await next(context);
            return;
        }

        var principal = context.GetUser();
        var oid = principal?.GetObjectId();
        if (principal == null || string.IsNullOrEmpty(oid))
        {
            // Unauthenticated MCP probe — auth middleware / policy enforcement handle rejection.
            await next(context);
            return;
        }

        var upn = principal.GetUserPrincipalName();
        var tenantId = principal.GetTenantId();

        // GA exempt from the QUOTA — but their usage is still tracked (metrics surface).
        var requestContext = context.GetRequestContext();
        if (requestContext.IsGlobalAdmin)
        {
            TrackUsage(httpContext, oid, upn, tenantId);
            await next(context);
            return;
        }

        McpQuotaDecision decision;
        try
        {
            decision = await _quotaService.CheckAsync(oid, upn, tenantId);
        }
        catch (Exception ex)
        {
            // Belt-and-braces fail-open: the quota layer must never take MCP down.
            _logger.LogError(ex, "[McpQuota] Quota check threw for oid={Oid} — allowing request (fail-open)", oid);
            TrackUsage(httpContext, oid, upn, tenantId);
            await next(context);
            return;
        }

        StampQuotaHeaders(httpContext, decision);

        if (decision.Allowed)
        {
            // Check-then-increment: the decision above reflects previously SERVED requests only.
            TrackUsage(httpContext, oid, upn, tenantId);
            await next(context);
            return;
        }

        var retryAfterSeconds = Math.Max(1, (int)(decision.ResetUtc - DateTime.UtcNow).TotalSeconds);
        _logger.LogWarning(
            "[McpQuota] BLOCKED oid={Oid} plan={Plan} scope={Scope} daily={DailyUsed}/{DailyLimit} monthly={MonthlyUsed}/{MonthlyLimit}",
            oid, decision.Plan, decision.Scope, decision.DailyUsed, decision.DailyLimit, decision.MonthlyUsed, decision.MonthlyLimit);

        httpContext.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        httpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsJsonAsync(new
        {
            quotaExceeded = true,
            plan = decision.Plan,
            scope = decision.Scope,
            limit = decision.Scope == "monthly" ? decision.MonthlyLimit : decision.DailyLimit,
            used = decision.Scope == "monthly" ? decision.MonthlyUsed : decision.DailyUsed,
            resetUtc = decision.ResetUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            message = $"MCP {decision.Scope} request quota exceeded for plan '{decision.Plan}'. Resets at {decision.ResetUtc:yyyy-MM-ddTHH:mm:ss}Z."
        });
    }

    /// <summary>
    /// Fire-and-forget usage increment (same posture as the previous AuthenticationMiddleware
    /// tracking — never blocks or fails the request path). Endpoint is normalized and prefixed
    /// with the X-MCP-Tool-Name when the MCP server supplies it.
    /// </summary>
    private void TrackUsage(HttpContext httpContext, string oid, string? upn, string? tenantId)
    {
        var normalizedEndpoint = EndpointNormalizer.Normalize(httpContext.Request.Path.Value ?? string.Empty);
        var mcpToolName = httpContext.Request.Headers["X-MCP-Tool-Name"].FirstOrDefault();
        if (!string.IsNullOrEmpty(mcpToolName))
            normalizedEndpoint = $"{mcpToolName}:{normalizedEndpoint}";

        var repo = _userUsageRepo;
        var logger = _logger;
        var upnValue = upn ?? "unknown";
        var tidValue = tenantId ?? "";
        _ = Task.Run(async () =>
        {
            try
            {
                await repo.IncrementUsageAsync(oid, upnValue, tidValue, normalizedEndpoint);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[McpQuota] Failed to record usage: user={UserId}, endpoint={Endpoint}", LogSanitizer.Clean(oid), LogSanitizer.Clean(normalizedEndpoint));
            }
        });
    }

    private static void StampQuotaHeaders(HttpContext httpContext, McpQuotaDecision decision)
    {
        // Direct-write pattern — same as UserRateLimitMiddleware's X-RateLimit-* headers.
        httpContext.Response.Headers["X-MCP-Quota-Plan"] = decision.Plan;
        httpContext.Response.Headers["X-MCP-Quota-Daily-Limit"] = decision.DailyLimit.ToString();
        httpContext.Response.Headers["X-MCP-Quota-Monthly-Limit"] = decision.MonthlyLimit.ToString();
        if (decision.DailyUsed >= 0)
        {
            httpContext.Response.Headers["X-MCP-Quota-Daily-Used"] = decision.DailyUsed.ToString();
            httpContext.Response.Headers["X-MCP-Quota-Monthly-Used"] = decision.MonthlyUsed.ToString();
        }
    }
}
