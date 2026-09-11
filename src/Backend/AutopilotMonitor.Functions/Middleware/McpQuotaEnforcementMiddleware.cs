using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Enforces the MCP daily/monthly request quota — the caller's own budget AND the organization-wide
/// budget of the caller's HOME tenant (both counted here, both must be free). Runs after
/// <see cref="UserRateLimitMiddleware"/> (per-minute burst control) — this is the budget layer on
/// top. Applies ONLY to HTTP requests marked <c>X-Client-Source: mcp</c> with an authenticated
/// principal (oid); everything else passes through untouched.
///
/// <b>Which tenant is charged: always the caller's home tenant (JWT tid)</b> — "the budget follows the
/// delegating tenant". A member reading their own tenant, a delegated (MSP) admin reading a managed tenant,
/// a fleet aggregate over every managed tenant: one check, one charge, on home. A managed tenant is never
/// charged and never blocked by its manager's reads; what the manager gets in return is the slot growth of
/// its own windows (see <see cref="McpQuotaService"/>).
///
/// Global Admins are exempt from the check (platform operations must never be blocked by a budget) but
/// counted like everyone else — on their home tenant — so their usage stays measurable. Global Readers are
/// NOT exempt; see <see cref="IsExempt"/>.
///
/// This middleware also OWNS the usage-counter increment (moved here from AuthenticationMiddleware,
/// Codex finding 2026-07-07): check-then-increment, and only for requests that are actually
/// served — denied requests (403 upstream, 429 here) no longer inflate the counters, and a
/// request can never be blocked by its OWN in-flight increment. The increment stays
/// fire-and-forget (never blocks the request path).
///
/// <b>The quota boundary is deliberately SOFT, not exact.</b> McpQuotaService caches the usage
/// snapshots for 60 seconds (per instance; per user and per tenant), so an ALLOWED decision keeps
/// admitting requests inside that window even after the async increments push the stored counters past
/// the limit. Worst-case overshoot is bounded: ~60s × the request rate against that counter (× instance
/// count on scaled-out Flex Consumption) — the same deliberate posture as the sliding-window
/// rate limiter, trading exactness for one counter read per user / per tenant per minute instead of per
/// request. Pinned by McpQuotaServiceTests soft-boundary tests; do NOT re-document this as an
/// exact limit without reworking the snapshot cache.
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

        if (!ClientSourceHeader.IsMcp(httpContext))
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
        var homeTenantId = principal.GetTenantId() ?? string.Empty;

        // Exempt callers skip the check but are still counted on their home tenant — the usage of a platform
        // operator is what the operator wants to see, not what may block them.
        if (IsExempt(context.GetRequestContext()))
        {
            TrackUsage(httpContext, oid, upn, homeTenantId);
            await next(context);
            return;
        }

        McpQuotaDecision decision;
        try
        {
            decision = await _quotaService.CheckAsync(oid, upn, homeTenantId);
        }
        catch (Exception ex)
        {
            // Belt-and-braces fail-open: the quota layer must never take MCP down.
            _logger.LogError(ex, "[McpQuota] Quota check threw for oid={Oid} — allowing request (fail-open)", oid);
            TrackUsage(httpContext, oid, upn, homeTenantId);
            await next(context);
            return;
        }

        StampQuotaHeaders(httpContext, decision, homeTenantId);

        if (decision.Allowed)
        {
            // Check-then-increment: the decision above reflects previously SERVED requests only.
            TrackUsage(httpContext, oid, upn, homeTenantId);
            await next(context);
            return;
        }

        LogBlocked(oid, homeTenantId, decision);
        await WriteExceededAsync(context, httpContext, decision, BuildExceededResponse(decision));
    }

    /// <summary>
    /// Pure: who bypasses the check. Only the Global Admin — platform operations must never be blocked by a
    /// budget. The Global Reader is a customer-facing read role and stays within its home tenant's windows.
    /// Exempt callers are still COUNTED (see <see cref="Invoke"/>) so their usage remains measurable.
    /// </summary>
    internal static bool IsExempt(RequestContext ctx) => ctx.IsGlobalAdmin;

    /// <summary>
    /// The 429 body for a blocked decision. The message names WHOSE budget is exhausted — a member hitting the
    /// organization-wide window must not conclude their own plan is too small. Both windows belong to the
    /// caller's own tenant (a delegated read never draws on a managed tenant), so the fix is always on the
    /// caller's side: every Community block says that Community is sized for occasional use and that Pro
    /// lifts the window.
    /// </summary>
    internal static McpQuotaExceededResponse BuildExceededResponse(McpQuotaDecision decision)
    {
        var resetStamp = $"{decision.ResetUtc:yyyy-MM-ddTHH:mm:ss}Z";
        var reset = $"Resets at {resetStamp}.";
        string message;

        if (decision.Level == McpQuotaLevel.Tenant)
        {
            var upgrade = FeatureEntitlementCatalog.IsPermanentProTier(decision.TenantPlan)
                ? string.Empty
                : " The Community plan is sized for occasional use; upgrading your organization to Pro lifts its organization windows.";
            message = $"MCP {decision.Scope} request quota of your organization exceeded (tenant plan '{decision.TenantPlan}', shared by all its members).{upgrade} {reset}";
        }
        else
        {
            // Only the Community EDITION plan gets the Pro hint — a per-user override plan (any other name)
            // is already a deliberate individual budget, and "upgrade to Pro" would be wrong advice there.
            var upgrade = string.Equals(decision.Plan, FeatureEntitlementCatalog.CommunityTierName, StringComparison.OrdinalIgnoreCase)
                ? " The Community plan is sized for occasional use; Pro raises your daily and monthly windows."
                : string.Empty;
            message = $"MCP {decision.Scope} request quota exceeded for plan '{decision.Plan}'.{upgrade} {reset}";
        }

        return new McpQuotaExceededResponse
        {
            Error = message,
            QuotaExceeded = true,
            Plan = decision.Plan,
            Scope = decision.Scope,
            Level = decision.Level ?? McpQuotaLevel.User,
            Limit = decision.ExceededLimit,
            Used = decision.ExceededUsed,
            ResetUtc = resetStamp,
        };
    }

    private void LogBlocked(string oid, string tenantId, McpQuotaDecision decision)
    {
        _logger.LogWarning(
            "[McpQuota] BLOCKED oid={Oid} charged={ChargedTenant} level={Level} scope={Scope} plan={Plan} daily={DailyUsed}/{DailyLimit} monthly={MonthlyUsed}/{MonthlyLimit} tenantDaily={TenantDailyUsed}/{TenantDailyLimit} tenantMonthly={TenantMonthlyUsed}/{TenantMonthlyLimit}",
            oid, LogSanitizer.Clean(tenantId), decision.Level, decision.Scope, decision.Plan, decision.DailyUsed, decision.DailyLimit, decision.MonthlyUsed, decision.MonthlyLimit,
            decision.TenantDailyUsed, decision.TenantDailyLimit, decision.TenantMonthlyUsed, decision.TenantMonthlyLimit);
    }

    private static Task WriteExceededAsync(FunctionContext context, HttpContext httpContext, McpQuotaDecision decision, McpQuotaExceededResponse body)
    {
        var retryAfterSeconds = Math.Max(1, (int)(decision.ResetUtc - DateTime.UtcNow).TotalSeconds);
        return ApiErrorWriter.WriteAsync(httpContext, context.GetCorrelationId(), HttpStatusCode.TooManyRequests, body, retryAfterSeconds);
    }

    /// <summary>
    /// Fire-and-forget usage increment (same posture as the previous AuthenticationMiddleware
    /// tracking — never blocks or fails the request path). Both rows belong to the caller's HOME tenant:
    /// the per-user row (UserUsageLog) and the organization counter (McpTenantUsage). Endpoint is normalized
    /// and prefixed with the X-MCP-Tool-Name when the MCP server supplies it.
    /// </summary>
    private void TrackUsage(HttpContext httpContext, string oid, string? upn, string homeTenantId)
    {
        var normalizedEndpoint = EndpointNormalizer.Normalize(httpContext.Request.Path.Value ?? string.Empty);
        // Key-safe copy of the header: the value becomes part of the UserUsageLog row key.
        var mcpToolName = EndpointNormalizer.ToolNameKey(httpContext.Request.Headers["X-MCP-Tool-Name"].FirstOrDefault());
        if (mcpToolName.Length > 0)
            normalizedEndpoint = $"{mcpToolName}:{normalizedEndpoint}";

        var repo = _userUsageRepo;
        var logger = _logger;
        var upnValue = upn ?? "unknown";
        _ = Task.Run(async () =>
        {
            try
            {
                await repo.IncrementUsageAsync(oid, upnValue, homeTenantId, normalizedEndpoint);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[McpQuota] Failed to record usage: user={UserId}, endpoint={Endpoint}", LogSanitizer.Clean(oid), LogSanitizer.Clean(normalizedEndpoint));
            }

            if (string.IsNullOrEmpty(homeTenantId))
                return;

            // Organization-wide counter (the tenant quota's source). Separate try so a failure here never
            // hides the per-user write above.
            try
            {
                await repo.IncrementTenantUsageAsync(homeTenantId, oid, upn);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[McpQuota] Failed to record tenant usage: tenant={TenantId}, user={UserId}", LogSanitizer.Clean(homeTenantId), LogSanitizer.Clean(oid));
            }
        });
    }

    /// <summary>Quota headers: the caller's own windows and the home tenant's organization windows (named in X-MCP-Quota-Tenant-Id).</summary>
    private static void StampQuotaHeaders(HttpContext httpContext, McpQuotaDecision decision, string tenantId)
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

        if (!string.IsNullOrEmpty(tenantId))
            httpContext.Response.Headers["X-MCP-Quota-Tenant-Id"] = tenantId;
        httpContext.Response.Headers["X-MCP-Quota-Tenant-Plan"] = decision.TenantPlan;
        httpContext.Response.Headers["X-MCP-Quota-Tenant-Daily-Limit"] = decision.TenantDailyLimit.ToString();
        httpContext.Response.Headers["X-MCP-Quota-Tenant-Monthly-Limit"] = decision.TenantMonthlyLimit.ToString();
        if (decision.TenantDailyUsed >= 0)
        {
            httpContext.Response.Headers["X-MCP-Quota-Tenant-Daily-Used"] = decision.TenantDailyUsed.ToString();
            httpContext.Response.Headers["X-MCP-Quota-Tenant-Monthly-Used"] = decision.TenantMonthlyUsed.ToString();
        }
    }
}
