using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Security.Claims;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Helpers;

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
        // Sanitized copies for logging only — never used for routing/authorization decisions.
        var logPath = LogSanitizer.Clean(requestPath);
        var logMethod = LogSanitizer.Clean(httpContext.Request.Method);
        if (SkipsJwtValidation(httpContext.Request.Method, requestPath))
        {
            _logger.LogDebug("[Auth Middleware] JWT-exempt route: {Method} {Path}", logMethod, logPath);
            await next(context);
            return;
        }

        // Extract Authorization header
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[Auth Middleware] Blocked unauthenticated request to {Path}", logPath);
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
                _logger.LogWarning("[Auth Middleware] Rejected token with missing/non-GUID tid for {Path}", logPath);
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
            var additionalClientIdsRaw = _configuration["EntraId:AdditionalClientIds"];
            var clientIds = ResolveConfiguredClientIds(
                _configuration["EntraId:ClientId"], additionalClientIdsRaw, out var rejectedAdditionalEntries);
            AuditClientIdTrustSet(additionalClientIdsRaw, clientIds, rejectedAdditionalEntries);
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
            _logger.LogWarning("[Auth Middleware] Blocked request with invalid token to {Path}", logPath);
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
    internal static bool IsValidTenantId([NotNullWhen(true)] string? tid) => Guid.TryParse(tid, out _);

    /// <summary>
    /// Maps a token issuer + tenant id to the OIDC authority to fetch signing keys from. Entra v1.0
    /// tokens carry an <c>sts.windows.net</c> issuer and use the bare authority; v2.0 tokens use the
    /// <c>/v2.0</c> authority. The tenant id is assumed already validated by <see cref="IsValidTenantId"/>.
    /// </summary>
    internal static string BuildTenantAuthority(string issuer, string tenantId)
    {
        var isV1Token = issuer != null && issuer.Contains("sts.windows.net");
        return isV1Token
            ? $"{Constants.EntraLoginBaseUrl}/{tenantId}"        // v1.0
            : $"{Constants.EntraLoginBaseUrl}/{tenantId}/v2.0";  // v2.0
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
                if (iss.StartsWith(Constants.EntraLoginBaseUrl + "/") ||
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
    /// optional comma/semicolon/whitespace-separated <c>EntraId:AdditionalClientIds</c>. Additional
    /// entries must be GUIDs (a client ID is always a GUID); non-GUID entries are dropped —
    /// fail-closed, they could never match a token's <c>aud</c> anyway — and reported via
    /// <paramref name="rejectedAdditionalEntries"/> so <see cref="AuditClientIdTrustSet"/> can make
    /// the misconfiguration operator-visible. Accepted entries are normalized to the canonical
    /// lowercase dashed GUID form (Entra <c>aud</c> claims are lowercase; audience comparison is
    /// ordinal), so brace/case/no-dash variants an operator might paste still match. The result is
    /// de-duplicated case-insensitively (primary first, verbatim — its handling is unchanged). When
    /// AdditionalClientIds is unset the result is exactly the primary id — today's behaviour, zero
    /// change — so adding a second app registration's client ID to that setting is the ONLY step
    /// needed to trust a second app during a tenant-move window
    /// (tasks/migration-cross-tenant-runbook.md).
    /// </summary>
    internal static string[] ResolveConfiguredClientIds(
        string? primaryClientId, string? additionalClientIds, out string[] rejectedAdditionalEntries)
    {
        var ids = new List<string?> { primaryClientId };
        var rejected = new List<string>();
        if (!string.IsNullOrWhiteSpace(additionalClientIds))
        {
            foreach (var entry in additionalClientIds.Split(
                new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Guid.TryParse(entry.Trim(), out var clientId))
                {
                    ids.Add(clientId.ToString("D"));
                }
                else
                {
                    rejected.Add(entry.Trim());
                }
            }
        }
        rejectedAdditionalEntries = rejected.ToArray();
        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Sentinel-free "already audited" marker: the last <c>EntraId:AdditionalClientIds</c> raw value
    /// this instance logged about. Initial <c>null</c> intentionally equals the unset-config case —
    /// an unset seam has nothing to report, so skipping it is correct, and the first non-null value
    /// (or any change) triggers a fresh audit.
    /// </summary>
    private string? _auditedAdditionalClientIds;

    /// <summary>
    /// Operator-visible audit of the audience trust set (daily-review follow-up, 2026-07-14):
    /// expanding the set of trusted app registrations via <c>EntraId:AdditionalClientIds</c> is
    /// security-relevant and used to be completely silent — a typo'd GUID was indistinguishable
    /// from "the new app registration doesn't work" during a tenant-move window. Logs once per
    /// distinct config value per instance, at Warning because nothing below Warning reaches
    /// App Insights from the worker. Rejected entries are truncated before logging in case an
    /// operator accidentally pastes a secret into the setting.
    /// </summary>
    private void AuditClientIdTrustSet(
        string? rawAdditionalClientIds, string[] clientIds, string[] rejectedAdditionalEntries)
    {
        if (string.Equals(_auditedAdditionalClientIds, rawAdditionalClientIds, StringComparison.Ordinal))
        {
            return;
        }
        // Benign race on concurrent first requests: at worst a duplicate audit line.
        _auditedAdditionalClientIds = rawAdditionalClientIds;

        if (rejectedAdditionalEntries.Length > 0)
        {
            _logger.LogWarning(
                "[Auth Middleware] EntraId:AdditionalClientIds contains {Count} malformed (non-GUID) entries — ignored: {Entries}",
                rejectedAdditionalEntries.Length,
                string.Join(", ", rejectedAdditionalEntries.Select(TruncateForLog)));
        }
        if (clientIds.Length > 1)
        {
            _logger.LogWarning(
                "[Auth Middleware] Token audience trust set expanded to {Count} client IDs via EntraId:AdditionalClientIds: {ClientIds}",
                clientIds.Length,
                string.Join(", ", clientIds));
        }
    }

    /// <summary>
    /// Truncates a rejected config entry for logging: enough to identify the typo, never the full
    /// value (an operator could accidentally paste a client secret into the setting).
    /// </summary>
    internal static string TruncateForLog(string entry) =>
        entry.Length <= 8 ? entry : $"{entry.Substring(0, 8)}…({entry.Length} chars)";
}
