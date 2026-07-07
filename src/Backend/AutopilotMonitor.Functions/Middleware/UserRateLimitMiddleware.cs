using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Per-user rate limiting for authenticated (JWT) requests.
/// Runs after PolicyEnforcementMiddleware so RequestContext (UPN, IsGlobalAdmin) is already resolved.
/// Agent/device routes have no RequestContext and are automatically skipped.
/// </summary>
public class UserRateLimitMiddleware : IFunctionsWorkerMiddleware
{
    private readonly RateLimitService _rateLimitService;
    private readonly AdminConfigurationService _adminConfigService;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly TenantEntitlementService _entitlementService;
    private readonly ILogger<UserRateLimitMiddleware> _logger;

    public UserRateLimitMiddleware(
        RateLimitService rateLimitService,
        AdminConfigurationService adminConfigService,
        TenantConfigurationService tenantConfigService,
        TenantEntitlementService entitlementService,
        ILogger<UserRateLimitMiddleware> logger)
    {
        _rateLimitService = rateLimitService;
        _adminConfigService = adminConfigService;
        _tenantConfigService = tenantConfigService;
        _entitlementService = entitlementService;
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

        var requestContext = context.GetRequestContext();
        if (string.IsNullOrEmpty(requestContext.UserPrincipalName))
        {
            // Anonymous or device-auth route — no user to rate limit
            await next(context);
            return;
        }

        // Fail-open: if rate limiting logic throws, let the request through.
        // A broken rate limiter must never take down the application.
        RateLimitResult result;
        try
        {
            var config = await _adminConfigService.GetConfigurationAsync();

            // Per-tenant overrides apply ONLY to genuinely tenant-scoped callers. Platform roles with
            // cross-tenant scope (Global Admin AND Global Reader — HasGlobalScope) must not be limited by
            // any single tenant's override, since they aren't bound to one tenant. Delegated admins remain
            // scoped to their target tenant, so they still observe that tenant's override.
            // The tenant-config read is served from a 5-minute in-memory cache. Use the strict point-read
            // (GetConfigurationIfExistsAsync) — NOT GetConfigurationAsync, which auto-creates+persists a
            // default row. A valid JWT from an unregistered tenant hitting a read/self-service endpoint must
            // never materialize a tenant config row. No row → inherit global.
            int? tenantOverride = null;
            int? entitlementFloor = null;
            if (!requestContext.HasGlobalScope && !string.IsNullOrEmpty(requestContext.TargetTenantId))
            {
                var tenantConfig = await _tenantConfigService.GetConfigurationIfExistsAsync(requestContext.TargetTenantId);
                tenantOverride = tenantConfig?.CustomUserRateLimitRequestsPerMinute;
            }

            // Edition entitlement floor (Enterprise: 150/min): follows the caller's HOME tenant
            // (JWT tid) — an MSP/delegated user rides on the edition of the tenant that pays for
            // their seat, not the tenant they happen to be viewing. Resolution is fail-closed
            // (any error → Community → null floor → admin default applies) and served from the
            // same 5-minute config cache. Global-scope callers have their own budgets — no floor.
            if (!requestContext.HasGlobalScope && !string.IsNullOrEmpty(requestContext.TenantId))
            {
                var entitlements = await _entitlementService.GetEntitlementsAsync(requestContext.TenantId);
                entitlementFloor = entitlements.UserRateLimitPerMinute;
            }

            // Base limit: Global Admins get the GA budget; everyone else (standard users AND read-only
            // Global Readers) gets the standard user default (with the tenant override applied above only
            // for tenant-scoped callers, raised to the edition floor when no override is set).
            var limit = RateLimitResolver.ResolveUserLimit(
                requestContext.IsGlobalAdmin,
                tenantOverride,
                config.UserRateLimitRequestsPerMinute,
                config.GlobalAdminRateLimitRequestsPerMinute,
                entitlementFloor);

            var key = $"user_ratelimit_{requestContext.UserPrincipalName.ToLowerInvariant()}";
            result = _rateLimitService.CheckRateLimit(key, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UserRateLimit] Rate limit check failed for user={User}, allowing request (fail-open)",
                requestContext.UserPrincipalName);
            await next(context);
            return;
        }

        // Always set rate limit headers for authenticated requests
        var remaining = Math.Max(0, result.MaxRequests - result.RequestsInWindow);
        var resetEpoch = DateTimeOffset.UtcNow.Add(result.WindowDuration).ToUnixTimeSeconds();

        httpContext.Response.Headers["X-RateLimit-Limit"] = result.MaxRequests.ToString();
        httpContext.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
        httpContext.Response.Headers["X-RateLimit-Reset"] = resetEpoch.ToString();

        if (result.IsAllowed)
        {
            await next(context);
            return;
        }

        // Throttled — return 429
        var retryAfterSeconds = result.RetryAfter.HasValue
            ? (int)result.RetryAfter.Value.TotalSeconds
            : 60;

        _logger.LogWarning(
            "[UserRateLimit] THROTTLED user={User} requests={Count}/{Max} retryAfter={RetryAfter}s",
            requestContext.UserPrincipalName, result.RequestsInWindow, result.MaxRequests, retryAfterSeconds);

        httpContext.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        httpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsJsonAsync(new
        {
            success = false,
            message = $"Rate limit exceeded: {result.MaxRequests} requests per minute",
            rateLimitExceeded = true,
            rateLimitInfo = new
            {
                requestsInWindow = result.RequestsInWindow,
                maxRequests = result.MaxRequests,
                windowDurationSeconds = result.WindowDuration.TotalSeconds,
                retryAfterSeconds
            }
        });
    }
}
