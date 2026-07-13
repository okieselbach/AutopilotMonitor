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
using AutopilotMonitor.Functions.Security;

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

    // Cache configuration managers per tenant to avoid repeated OIDC metadata fetches
    // Bounded with LRU eviction to prevent memory exhaustion from malicious tenant ID flooding
    private const int MaxCacheSize = 500;
    private readonly Dictionary<string, (IConfigurationManager<OpenIdConnectConfiguration> Manager, DateTime LastAccessed)> _configManagerCache;
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    public AuthenticationMiddleware(
        ILogger<AuthenticationMiddleware> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
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

            // Reject missing or non-GUID tenant IDs BEFORE building a tenant-specific authority.
            // The tid here is unverified (signature is checked later), so a garbage value would
            // otherwise drive an outbound OIDC-metadata fetch to a bogus authority — a DoS/cost
            // surface on malformed-token floods. Every legitimate Entra token (including consumer
            // MSA, tid 9188040d-…) carries a GUID tid, so this reject is loss-free.
            if (!IsValidTenantId(tenantId))
            {
                _logger.LogWarning("[Auth Middleware] Rejected token with missing/non-GUID tid for {Path}", requestPath);
                throw new SecurityTokenValidationException("Token tid claim is missing or not a valid GUID");
            }

            // Determine which endpoint to use based on the issuer (v1.0 vs v2.0)
            var tenantSpecificAuthority = BuildTenantAuthority(issuer, tenantId);

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

            var openIdConfig = await tenantConfigManager.GetConfigurationAsync(context.CancellationToken);

            // Build the security-critical validation parameters (issuer/audience/lifetime + the
            // RS256/PS256 algorithm whitelist that hard-blocks alg:none and HS-family confusion).
            // Extracted to a pure seam so those rejects are unit-testable against locally-minted
            // tokens without a live OIDC ConfigurationManager.
            //
            // Audience = the primary app registration (EntraId:ClientId) PLUS any additional
            // (secondary/rotated) client IDs from EntraId:AdditionalClientIds. That config is the
            // seam that lets the API trust a SECOND app registration during a tenant-move window
            // (see tasks/migration-cross-tenant-runbook.md). Unset today ⇒ exactly the primary id
            // ⇒ zero behaviour change.
            var clientIds = ResolveConfiguredClientIds(
                _configuration["EntraId:ClientId"], _configuration["EntraId:AdditionalClientIds"]);
            var validationParameters = BuildTokenValidationParameters(
                openIdConfig.SigningKeys, clientIds);

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

            // NOTE: MCP usage tracking moved to McpQuotaEnforcementMiddleware (Codex finding,
            // 2026-07-07): incrementing here — BEFORE the quota decision — raced the quota read
            // (the exact-limit request depended on whether the fire-and-forget increment won),
            // and denied (403/429) requests inflated the counters. The quota middleware now
            // counts strictly check-then-increment, and only requests that are actually served.
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

    /// <summary>
    /// Pre-signature gate on the (unverified) <c>tid</c> claim: it must be a GUID. Rejecting a
    /// missing/garbage tid here prevents an outbound OIDC-metadata fetch to a bogus authority on
    /// malformed-token floods. Loss-free — every legitimate Entra token carries a GUID tid.
    /// </summary>
    internal static bool IsValidTenantId(string? tid) => Guid.TryParse(tid, out _);

    /// <summary>
    /// Maps a token issuer + tenant id to the OIDC authority to fetch signing keys from. Entra v1.0
    /// tokens carry an <c>sts.windows.net</c> issuer and use the bare authority; v2.0 tokens use the
    /// <c>/v2.0</c> authority. The tenant id is assumed already validated by <see cref="IsValidTenantId"/>.
    /// </summary>
    internal static string BuildTenantAuthority(string issuer, string tenantId)
    {
        var isV1Token = issuer != null && issuer.Contains("sts.windows.net");
        return isV1Token
            ? $"https://login.microsoftonline.com/{tenantId}"        // v1.0
            : $"https://login.microsoftonline.com/{tenantId}/v2.0";  // v2.0
    }

    /// <summary>
    /// Builds the token validation parameters the middleware validates against: multi-tenant issuer
    /// format check, audience = <c>{clientId}</c> / <c>api://{clientId}</c> for each accepted client
    /// ID, lifetime, and — the algorithm-confusion defence — <c>RequireSignedTokens</c> plus the
    /// RS256/PS256 whitelist that hard-blocks alg:none and the HS family. Signing keys are supplied by
    /// the caller (the tenant's OIDC config at runtime; a test key under unit test). Accepts one or
    /// more client IDs (<c>params</c>) so a second/rotated app registration can be trusted alongside
    /// the primary — see <see cref="ResolveConfiguredClientIds"/>.
    /// </summary>
    internal static TokenValidationParameters BuildTokenValidationParameters(
        IEnumerable<SecurityKey> signingKeys, params string?[] clientIds)
    {
        var keys = signingKeys as ICollection<SecurityKey> ?? signingKeys?.ToList() ?? new List<SecurityKey>();

        return new TokenValidationParameters
        {
            // For multi-tenant, validate issuer format but accept any tenant.
            ValidateIssuer = true,
            IssuerValidator = (iss, token, parameters) =>
            {
                if (iss.StartsWith("https://login.microsoftonline.com/") ||
                    iss.StartsWith("https://sts.windows.net/"))
                {
                    return iss;
                }
                throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {iss}");
            },
            ValidateAudience = true,
            ValidAudiences = BuildValidAudiences(clientIds),
            ValidateLifetime = true,

            // Signature validation enabled (token minted for api://<clientId>/access_as_user).
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
            {
                var matched = keys.Where(k => k.KeyId == kid).ToList();
                return matched.Any() ? matched : keys;
            },

            // Algorithm whitelist: RS256 today, PS256 for Entra's announced rollout.
            // Hard-blocks HS-family and "none" (algorithm-confusion defence).
            ValidAlgorithms = new[] { "RS256", "PS256" }
        };
    }

    /// <summary>
    /// Expands a set of app (client) IDs into the accepted token audiences: for each id, both the
    /// bare <c>{id}</c> and the <c>api://{id}</c> form. Empty/whitespace ids are dropped and the
    /// result is de-duplicated case-insensitively (order preserved). An empty input yields an empty
    /// audience set, which — with <c>ValidateAudience=true</c> — rejects every token (fail-closed).
    /// </summary>
    internal static string[] BuildValidAudiences(IEnumerable<string?>? clientIds)
    {
        return (clientIds ?? Array.Empty<string?>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(id => new[] { id, $"api://{id}" })
            .ToArray();
    }

    /// <summary>
    /// Resolves the configured accepted app (client) IDs: the primary <c>EntraId:ClientId</c> plus an
    /// optional comma/semicolon/whitespace-separated <c>EntraId:AdditionalClientIds</c>. Trimmed,
    /// empties dropped, de-duplicated case-insensitively (primary first). When AdditionalClientIds is
    /// unset the result is exactly the primary id — today's behaviour, zero change — so adding a
    /// second app registration's client ID to that setting is the ONLY step needed to trust a second
    /// app during a tenant-move window (tasks/migration-cross-tenant-runbook.md).
    /// </summary>
    internal static string[] ResolveConfiguredClientIds(string? primaryClientId, string? additionalClientIds)
    {
        var ids = new List<string?> { primaryClientId };
        if (!string.IsNullOrWhiteSpace(additionalClientIds))
        {
            ids.AddRange(additionalClientIds.Split(
                new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        }
        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
