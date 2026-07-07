using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Enforces the per-user MCP daily/monthly request quota. Runs after
/// <see cref="UserRateLimitMiddleware"/> (per-minute burst control) — this is the budget layer on
/// top. Applies ONLY to HTTP requests marked <c>X-Client-Source: mcp</c> with an authenticated
/// principal (oid); everything else passes through untouched. Global Admins are exempt.
///
/// Over-quota requests get 429 with a structured body, <c>Retry-After</c>, and
/// <c>X-MCP-Quota-*</c> headers; allowed MCP requests get the quota headers too so the MCP
/// client can surface remaining budget. Fail-open on storage/counter errors (handled inside
/// <see cref="McpQuotaService"/>), fail-closed on plan resolution (unknown plan → Community).
/// </summary>
public class McpQuotaEnforcementMiddleware : IFunctionsWorkerMiddleware
{
    private readonly McpQuotaService _quotaService;
    private readonly ILogger<McpQuotaEnforcementMiddleware> _logger;

    public McpQuotaEnforcementMiddleware(
        McpQuotaService quotaService,
        ILogger<McpQuotaEnforcementMiddleware> logger)
    {
        _quotaService = quotaService;
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
        if (string.IsNullOrEmpty(oid))
        {
            // Unauthenticated MCP probe — auth middleware / policy enforcement handle rejection.
            await next(context);
            return;
        }

        // GA exempt: platform operators are not customers of the quota.
        var requestContext = context.GetRequestContext();
        if (requestContext.IsGlobalAdmin)
        {
            await next(context);
            return;
        }

        McpQuotaDecision decision;
        try
        {
            var upn = principal!.GetUserPrincipalName();
            var tenantId = principal.GetTenantId();
            decision = await _quotaService.CheckAsync(oid, upn, tenantId);
        }
        catch (Exception ex)
        {
            // Belt-and-braces fail-open: the quota layer must never take MCP down.
            _logger.LogError(ex, "[McpQuota] Quota check threw for oid={Oid} — allowing request (fail-open)", oid);
            await next(context);
            return;
        }

        StampQuotaHeaders(httpContext, decision);

        if (decision.Allowed)
        {
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
