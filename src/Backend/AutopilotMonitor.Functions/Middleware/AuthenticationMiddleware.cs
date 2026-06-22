using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Middleware to manually validate JWT tokens and populate ClaimsPrincipal
/// Required for Azure Functions .NET 8 Isolated Worker (Microsoft.Identity.Web doesn't work automatically)
/// </summary>
public class AuthenticationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<AuthenticationMiddleware> _logger;
    private readonly IConfiguration _configuration;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly IUserUsageRepository _userUsageRepo;

    // Cache configuration managers per tenant to avoid repeated OIDC metadata fetches
    // Bounded with LRU eviction to prevent memory exhaustion from malicious tenant ID flooding
    private const int MaxCacheSize = 500;
    private readonly Dictionary<string, (IConfigurationManager<OpenIdConnectConfiguration> Manager, DateTime LastAccessed)> _configManagerCache;
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    public AuthenticationMiddleware(
        ILogger<AuthenticationMiddleware> logger,
        IConfiguration configuration,
        IUserUsageRepository userUsageRepo)
    {
        _logger = logger;
        _configuration = configuration;
        _userUsageRepo = userUsageRepo;
        _tokenHandler = new JwtSecurityTokenHandler();
        _configManagerCache = new Dictionary<string, (IConfigurationManager<OpenIdConnectConfiguration> Manager, DateTime LastAccessed)>();

        // Disable PII logging in production for security
        Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = false;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            // Non-HTTP trigger (e.g. timer, queue) — no auth needed
            await next(context);
            return;
        }

        // Allow JWT-exempt routes (public + device/bootstrap-authenticated) through without
        // JWT validation. The exempt set is DERIVED from EndpointAccessPolicyCatalog — never a
        // hand-kept parallel list — so a new anonymous/device route can't drift out of sync here.
        var requestPath = httpContext.Request.Path.Value ?? string.Empty;
        if (SkipsJwtValidation(httpContext.Request.Method, requestPath))
        {
            _logger.LogDebug("[Auth Middleware] JWT-exempt route: {Method} {Path}", httpContext.Request.Method, requestPath);
            await next(context);
            return;
        }

        // Extract Authorization header
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[Auth Middleware] Blocked unauthenticated request to {Path}", requestPath);
            httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Authentication required. Please provide a valid JWT token."
            });
            return;
        }

        var token = authHeader.Substring("Bearer ".Length).Trim();
        bool authenticated = false;

        try
        {
            // First, decode the token to get the tenant ID (without validation)
            var jwtToken = _tokenHandler.ReadJwtToken(token);
            var tenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
            var issuer = jwtToken.Issuer;

            // Determine which endpoint to use based on the issuer (v1.0 vs v2.0)
            var isV1Token = issuer.Contains("sts.windows.net");
            var tenantSpecificAuthority = tenantId != null
                ? (isV1Token
                    ? $"https://login.microsoftonline.com/{tenantId}"  // v1.0
                    : $"https://login.microsoftonline.com/{tenantId}/v2.0")  // v2.0
                : "https://login.microsoftonline.com/common/v2.0";

            // Get or create cached configuration manager for this tenant
            IConfigurationManager<OpenIdConnectConfiguration>? tenantConfigManager = null;
            if (!await _cacheLock.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                _logger.LogError("[Auth Middleware] Semaphore timeout acquiring OIDC config cache lock");
                throw new TimeoutException("OIDC configuration cache lock timeout");
            }
            try
            {
                if (_configManagerCache.TryGetValue(tenantSpecificAuthority, out var cached))
                {
                    tenantConfigManager = cached.Manager;
                    _configManagerCache[tenantSpecificAuthority] = (cached.Manager, DateTime.UtcNow);
                }
                else
                {
                    var tenantMetadataAddress = $"{tenantSpecificAuthority}/.well-known/openid-configuration";
                    tenantConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        tenantMetadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever() { RequireHttps = true })
                    {
                        // Cache configuration for 24 hours (signing keys rarely change)
                        AutomaticRefreshInterval = TimeSpan.FromHours(24),
                        RefreshInterval = TimeSpan.FromHours(24)
                    };

                    if (_configManagerCache.Count >= MaxCacheSize)
                    {
                        // Evict least-recently-used entry
                        var lruKey = _configManagerCache.MinBy(kvp => kvp.Value.LastAccessed).Key;
                        _configManagerCache.Remove(lruKey);
                        _logger.LogDebug("[Auth Middleware] Evicted LRU config manager for {Authority}", lruKey);
                    }

                    _configManagerCache[tenantSpecificAuthority] = (tenantConfigManager, DateTime.UtcNow);
                    _logger.LogDebug("[Auth Middleware] Created new config manager for {Authority}", tenantSpecificAuthority);
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            if (tenantConfigManager == null)
            {
                throw new InvalidOperationException("Failed to get or create configuration manager");
            }

            var openIdConfig = await tenantConfigManager.GetConfigurationAsync(CancellationToken.None);

            // Set up token validation parameters with OIDC signing keys
            var validationParameters = new TokenValidationParameters
            {
                // For multi-tenant, validate issuer format but accept any tenant
                ValidateIssuer = true,
                IssuerValidator = (issuer, token, parameters) =>
                {
                    // Accept issuers from any Azure AD tenant
                    if (issuer.StartsWith("https://login.microsoftonline.com/") ||
                        issuer.StartsWith("https://sts.windows.net/"))
                    {
                        return issuer;
                    }
                    throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {issuer}");
                },
                ValidateAudience = true,
                ValidAudiences = new[]
                {
                    _configuration["EntraId:ClientId"],
                    $"api://{_configuration["EntraId:ClientId"]}"
                },
                ValidateLifetime = true,

                // Signature validation enabled
                // Token is requested for backend API (api://<clientId>/access_as_user)
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,

                // Use key resolver for dynamic key resolution
                IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
                {
                    var matchedKeys = openIdConfig.SigningKeys.Where(k => k.KeyId == kid).ToList();
                    return matchedKeys.Any() ? matchedKeys : openIdConfig.SigningKeys;
                },

                // Algorithm whitelist: RS256 today, PS256 for Entra's announced rollout.
                // Hard-blocks HS-family and "none" (algorithm-confusion defence).
                ValidAlgorithms = new[] { "RS256", "PS256" }
            };

            var principal = _tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            // Get user identifier for logging (same logic as TenantHelper.GetUserIdentifier)
            var userIdentifier = principal.FindFirst("upn")?.Value ??
                               principal.FindFirst(ClaimTypes.Upn)?.Value ??
                               principal.FindFirst(ClaimTypes.Email)?.Value ??
                               principal.FindFirst("preferred_username")?.Value ??
                               principal.FindFirst(ClaimTypes.Name)?.Value ??
                               principal.FindFirst("name")?.Value ??
                               "Unknown";

            _logger.LogDebug("[Auth Middleware] Authenticated: {User}", userIdentifier);

            // Set the principal on both the HTTP context AND the FunctionContext
            // This is critical for Azure Functions Isolated Worker (.NET 8)
            httpContext.User = principal;
            context.Items["ClaimsPrincipal"] = principal;
            authenticated = true;

            // Track MCP usage only (identified by X-Client-Source header, fire-and-forget, non-blocking)
            var isMcpRequest = string.Equals(
                httpContext.Request.Headers["X-Client-Source"].FirstOrDefault(), "mcp", StringComparison.OrdinalIgnoreCase);
            if (isMcpRequest)
            {
                var oid = principal.GetObjectId();
                if (!string.IsNullOrEmpty(oid))
                {
                    var upn = principal.GetUserPrincipalName() ?? "unknown";
                    var tid = principal.GetTenantId() ?? "";
                    var normalizedEndpoint = EndpointNormalizer.Normalize(requestPath);
                    var mcpToolName = httpContext.Request.Headers["X-MCP-Tool-Name"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(mcpToolName))
                        normalizedEndpoint = $"{mcpToolName}:{normalizedEndpoint}";
                    var repo = _userUsageRepo;
                    var logger = _logger;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await repo.IncrementUsageAsync(oid, upn, tid, normalizedEndpoint);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "[Auth Middleware] Failed to record usage: user={UserId}, endpoint={Endpoint}", oid, normalizedEndpoint);
                        }
                    });
                }
            }
        }
        catch (SecurityTokenValidationException ex)
        {
            _logger.LogWarning("[Auth Middleware] Token validation failed: {Error}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Auth Middleware] Error validating token");
        }

        if (!authenticated)
        {
            _logger.LogWarning("[Auth Middleware] Blocked request with invalid token to {Path}", requestPath);
            httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Authentication required. Please provide a valid JWT token."
            });
            return;
        }

        await next(context);
    }

    /// <summary>
    /// Whether a route is exempt from JWT validation in this middleware. SINGLE SOURCE OF TRUTH is
    /// <see cref="EndpointAccessPolicyCatalog"/>: a route is JWT-exempt iff its policy is
    /// <see cref="EndpointPolicy.PublicAnonymous"/> (fully public) or
    /// <see cref="EndpointPolicy.DeviceOrBootstrapAuth"/> (authenticated later, in-function, via
    /// device certificate / bootstrap token — not JWT). Deriving from the catalog instead of a
    /// hand-kept parallel allowlist prevents drift: a new anonymous/device route registered in the
    /// catalog is automatically exempt here, so it can never be 401'd before
    /// <c>PolicyEnforcementMiddleware</c> honors its policy (the bug that broke the ticket-gated
    /// <c>diagnostics/download</c> route). Unregistered routes (FindPolicy == null) are fail-closed:
    /// JWT required. Method-aware so e.g. an unexpected verb on a public path is not waved through.
    /// </summary>
    internal static bool SkipsJwtValidation(string httpMethod, string path)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, path);
        return entry is { Policy: EndpointPolicy.PublicAnonymous or EndpointPolicy.DeviceOrBootstrapAuth };
    }
}
