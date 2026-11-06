using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Shared service for acquiring Microsoft Graph access tokens via client credentials flow.
    /// Used by AutopilotDeviceValidator and CorporateIdentifierValidator.
    /// </summary>
    public class GraphTokenService
    {
        private static readonly TimeSpan ConsentStatusTtl = TimeSpan.FromMinutes(2);

        // App-token cache tuning. TTL = expires_in - skew margin, clamped so a missing/pathological
        // expires_in can neither thrash the cache nor pin a stale token. Max stays under the typical
        // ~60-90 min AAD client-credentials lifetime; the 5-min margin covers clock skew between
        // this worker and the Graph resource server plus the slowest downstream call in a request.
        private static readonly TimeSpan TokenSkewMargin = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MinTokenTtl = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MaxTokenTtl = TimeSpan.FromMinutes(55);
        private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(35); // when AAD omits expires_in
        private static readonly TimeSpan TokenGateWait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RejectionLogWindow = TimeSpan.FromMinutes(15);

        // One coalescing gate per tenant (see GetAccessTokenAsync). A SemaphoreSlim is tiny and
        // tenant cardinality is bounded + validated upstream, so these are never evicted.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _tokenLocks = new();

        // Per-tenant invalidation generation. Closes the write-after-invalidate race: a token POST
        // that STARTED before InvalidateTenant captures the old generation and tags its cache entry
        // with it; InvalidateTenant bumps the generation, so that late stale write is rejected by
        // every reader (entry.Gen != current) even though it physically lands after the Remove.
        // Lock-free — the comparison happens at read time against the latest generation.
        private readonly ConcurrentDictionary<string, long> _tokenCacheGen = new();

        private long CurrentGen(string tenantId) => _tokenCacheGen.TryGetValue(tenantId, out var g) ? g : 0;

        /// <summary>Cached app token tagged with the invalidation generation in effect when its acquire started.</summary>
        private sealed record CachedAppToken(string Token, long Gen);

        private readonly ILogger<GraphTokenService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly EntraAppRegistry _appRegistry;
        private readonly Services.TenantConfigurationService _tenantConfigService;
        private readonly IGraphAccessHealthRecorder _accessHealth;

        public GraphTokenService(
            ILogger<GraphTokenService> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            EntraAppRegistry appRegistry,
            Services.TenantConfigurationService tenantConfigService,
            IGraphAccessHealthRecorder accessHealth)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _appRegistry = appRegistry;
            _tenantConfigService = tenantConfigService;
            _accessHealth = accessHealth;
        }

        /// <summary>
        /// Picks the app registration acting for this tenant (dual app-reg window). While the
        /// legacy pair is unconfigured this is always the primary app — today's behaviour. A
        /// config read failure falls back to the null-homing default (legacy when configured):
        /// wrong-app picks surface as an uncached permanent failure and self-heal on retry.
        /// </summary>
        private async Task<EntraAppCredentials> ResolveCredentialsAsync(string tenantId)
        {
            if (!_appRegistry.LegacyConfigured)
            {
                return _appRegistry.Primary;
            }
            try
            {
                var config = await _tenantConfigService.GetConfigurationIfExistsAsync(tenantId);
                return _appRegistry.ResolveForTenant(config);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to load tenant configuration for {TenantId} while resolving the Entra app — using the null-homing default",
                    tenantId);
                return _appRegistry.ResolveForTenant(null);
            }
        }

        public async Task<GraphConsentStatusResult> GetConsentStatusAsync(string tenantId, CancellationToken ct = default)
        {
            var credentials = await ResolveCredentialsAsync(tenantId);
            var cacheKey = ConsentCacheKey(tenantId, credentials.ClientId);
            if (_cache.TryGetValue(cacheKey, out GraphConsentStatusResult? cached) && cached != null)
            {
                return cached;
            }

            var tokenResult = await GetAccessTokenAsync(tenantId, ct);
            var result = new GraphConsentStatusResult
            {
                IsConsented = !string.IsNullOrWhiteSpace(tokenResult.AccessToken),
                IsTransient = tokenResult.IsTransient,
                Message = !string.IsNullOrWhiteSpace(tokenResult.AccessToken)
                    ? "Admin consent is available for this tenant."
                    : tokenResult.IsTransient
                        ? "Could not verify consent status due to a transient error. Will retry on next request."
                        : "Admin consent for Graph application permissions is missing or app credentials are invalid."
            };

            // Only cache definitive results — never cache transient failures
            if (!tokenResult.IsTransient)
            {
                _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ConsentStatusTtl
                });
            }
            else
            {
                _logger.LogWarning(
                    "Consent status check for tenant {TenantId} returned transient error — result NOT cached",
                    tenantId);
            }

            return result;
        }

        public virtual async Task<GraphTokenResult> GetAccessTokenAsync(string tenantId, CancellationToken ct = default)
        {
            // Resolve which app registration acts for this tenant FIRST — the cache key includes
            // the resolved client id, so a tenant flipped between apps can never be served a token
            // minted under the other app.
            var credentials = await ResolveCredentialsAsync(tenantId);
            var result = await GetAccessTokenWithCredentialsAsync(tenantId, credentials, ct);
            if (result.IsFreshlyMinted && TryGetApplicationRoles(result.AccessToken, out var roles))
                await _accessHealth.RecordMintedAsync(tenantId, roles);
            return result;
        }

        /// <summary>
        /// <see cref="GetAccessTokenAsync"/> for the device validators of the agent routes and the
        /// maintenance recheck, the only callers whose rejection counts as evidence of lost access.
        /// The consent flows in the portal see 401s as a matter of course while a consent propagates.
        /// </summary>
        public async Task<GraphTokenResult> GetAccessTokenForAgentValidationAsync(string tenantId, CancellationToken ct = default)
        {
            var result = await GetAccessTokenAsync(tenantId, ct);
            var refusal = result.Rejection
                ?? (result.IsFreshlyMinted && !TryGetApplicationRoles(result.AccessToken, out _) ? EntraTokenRejection.NoApplicationRole : null);
            if (refusal != null)
                await _accessHealth.RecordRejectedAsync(tenantId, refusal);
            return result;
        }

        /// <summary>
        /// Whether a token can do anything at Graph, and with which application roles. A user's
        /// sign-in consent re-creates a removed enterprise application without any application
        /// permission: Entra issues tokens again, and Graph refuses every call made with them. Only a
        /// role proves access.
        /// </summary>
        private static bool TryGetApplicationRoles(string? accessToken, out IReadOnlySet<string> roles)
        {
            if (!string.IsNullOrEmpty(accessToken)
                && GraphFeatureDetector.TryParseToken(accessToken, out roles, out _)
                && roles.Count > 0)
                return true;
            roles = new HashSet<string>();
            return false;
        }

        /// <summary>
        /// Acquires an app-only token for this tenant under EXPLICIT app credentials, skipping the
        /// homing resolution. Used by the app-homing consent probe: proving that the PRIMARY app
        /// has admin consent in a still-legacy-homed tenant requires minting under the primary
        /// pair while <see cref="GetAccessTokenAsync"/> would resolve legacy. Cache-safe: the
        /// token cache key is app-scoped, so the probe's token lands under the supplied app's key
        /// and the tenant's normal (homed) path can never be served it — after a flip it simply
        /// becomes the correct entry.
        /// </summary>
        public virtual Task<GraphTokenResult> GetAccessTokenForAppAsync(
            string tenantId, EntraAppCredentials credentials, CancellationToken ct = default)
        {
            return GetAccessTokenWithCredentialsAsync(tenantId, credentials, ct);
        }

        private async Task<GraphTokenResult> GetAccessTokenWithCredentialsAsync(
            string tenantId, EntraAppCredentials credentials, CancellationToken ct)
        {
            var clientId = credentials.ClientId;
            var clientSecret = credentials.ClientSecret;

            // Serve a still-valid cached app token for this tenant+app. The cache key is keyed
            // strictly on the (already authenticated upstream) tenantId — the same value that drives
            // the token URL below — so a token minted for one tenant can never be served to another.
            if (TryGetCachedToken(tenantId, clientId, out var cachedToken))
            {
                return GraphTokenResult.Success(cachedToken);
            }

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError(
                    "Device validation is enabled but Entra ID app credentials are not configured for the {App} app registration. Set the EntraId {SettingPair} client id/secret pair.",
                    credentials.IsLegacy ? "legacy" : "primary",
                    credentials.IsLegacy ? "LegacyClientId/LegacyClientSecret" : "ClientId/ClientSecret");
                return GraphTokenResult.PermanentFailure();
            }

            // Stampede guard: coalesce concurrent cache-misses for the same tenant so an enrollment
            // wave doesn't fan a burst of identical token POSTs at AAD (which would trip its own
            // throttling). One acquirer fetches + populates the cache; the rest hit it after the
            // gate. The wait is bounded so a pathological slow acquire (e.g. a no-consent tenant on
            // the long retry chain) degrades to today's parallel-fetch behaviour instead of pinning
            // callers indefinitely.
            var gate = _tokenLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
            bool gotGate;
            try
            {
                gotGate = await gate.WaitAsync(TokenGateWait, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller budget exhausted while waiting — transient by definition (never cache).
                return GraphTokenResult.TransientFailure();
            }

            try
            {
                // Double-check: another thread may have populated the cache while we waited. The
                // gen-aware read also rejects a stale entry written by an acquire that overlapped an
                // InvalidateTenant, so we fall through to a fresh mint instead of serving it.
                if (gotGate && TryGetCachedToken(tenantId, clientId, out var tokenAfterWait))
                {
                    return GraphTokenResult.Success(tokenAfterWait);
                }

                return await AcquireTokenViaClientCredentialsAsync(tenantId, clientId, clientSecret, ct);
            }
            finally
            {
                if (gotGate) gate.Release();
            }
        }

        /// <summary>
        /// Performs the client-credentials POST (with the consent-propagation retry chain) and
        /// caches a successful token until shortly before its expiry. Failures are never cached, so
        /// a tenant that loses consent re-acquires (and fails) on the next request after expiry.
        /// </summary>
        private async Task<GraphTokenResult> AcquireTokenViaClientCredentialsAsync(
            string tenantId, string clientId, string clientSecret, CancellationToken ct)
        {
            // Capture the invalidation generation BEFORE the POST. If an InvalidateTenant lands
            // while this POST is in flight, the generation changes and the token we cache below is
            // tagged with the now-stale value — so readers reject it and re-mint fresh.
            var genAtStart = CurrentGen(tenantId);
            var tokenUrl = $"{Constants.EntraLoginBaseUrl}/{tenantId}/oauth2/v2.0/token";

            // Retry with backoff to handle Azure AD consent propagation delays.
            // After admin consent is granted, the service principal may not be immediately
            // available in the tenant — Azure AD typically propagates within 30-90 seconds.
            // Optional-feature callers should pass a CancellationToken with a tight budget
            // so the long retry chain (5+15+30 s) cannot block the UI for a minute.
            var retryDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };
            string? responseBody = null;
            int attempt = 0;
            bool lastAttemptWasTransient = false;
            // Survives the budget: a caller cut off between retries still learns what Entra said.
            EntraTokenRejection? rejection = null;

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    // Caller's budget exhausted — treat as transient so we never cache "no consent"
                    // for a tenant just because the optional path timed out.
                    return GraphTokenResult.TransientFailure(rejection);
                }
                try
                {
                    var tokenClient = _httpClientFactory.CreateClient();
                    var body = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["client_secret"] = clientSecret,
                        ["scope"] = Constants.GraphBaseUrl + "/.default",
                        ["grant_type"] = "client_credentials"
                    });

                    var response = await tokenClient.PostAsync(tokenUrl, body, ct);
                    responseBody = await response.Content.ReadAsStringAsync(ct);

                    if (response.IsSuccessStatusCode)
                    {
                        var (accessToken, expiresInSeconds) = ReadTokenResponse(responseBody);
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            // Cache the freshly minted token (never log its value). TTL derives from
                            // the token's own expires_in, minus a skew margin, clamped to a safe band.
                            CacheToken(tenantId, clientId, accessToken!, genAtStart, expiresInSeconds);
                        }
                        return GraphTokenResult.Minted(accessToken);
                    }

                    // Classify the error: consent-propagation errors are retryable but ultimately permanent (no consent).
                    // Server errors (500, 502, 503, 504) and timeouts (408) are truly transient.
                    var statusCode = (int)response.StatusCode;
                    var isConsentError = response.StatusCode == System.Net.HttpStatusCode.BadRequest
                        && (responseBody.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
                            || responseBody.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase)
                            || responseBody.Contains("AADSTS7000215", StringComparison.OrdinalIgnoreCase));
                    var isServerError = statusCode == 408 || statusCode == 429
                        || statusCode >= 500 && statusCode <= 599;
                    var isRetryable = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        || isConsentError || isServerError;

                    lastAttemptWasTransient = isServerError;
                    rejection = isServerError ? null : EntraTokenRejection.Parse(statusCode, responseBody);
                    if (rejection != null)
                        LogRejectionOncePerWindow(tenantId, clientId, rejection);

                    if (!isRetryable || attempt >= retryDelays.Length)
                    {
                        _logger.LogWarning(
                            "Failed to acquire Graph token for tenant {TenantId} after {Attempts} attempt(s). Status: {StatusCode}. Body: {ResponseBody}",
                            tenantId,
                            attempt + 1,
                            statusCode,
                            responseBody);

                        // Server errors are transient (infrastructure issue), consent errors are permanent (no consent granted)
                        return lastAttemptWasTransient
                            ? GraphTokenResult.TransientFailure()
                            : GraphTokenResult.PermanentFailure(rejection);
                    }

                    var delay = retryDelays[attempt];

                    // Respect Retry-After header from Azure AD / Graph throttling, but cap
                    // at 120s to prevent a misbehaving server from blocking indefinitely
                    if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta && retryAfterDelta > delay)
                    {
                        delay = retryAfterDelta > TimeSpan.FromSeconds(120)
                            ? TimeSpan.FromSeconds(120)
                            : retryAfterDelta;
                    }

                    _logger.LogInformation(
                        "Graph token acquisition for tenant {TenantId} returned {StatusCode} (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s — likely Azure AD consent propagation delay.",
                        tenantId,
                        statusCode,
                        attempt + 1,
                        retryDelays.Length + 1,
                        delay.TotalSeconds);

                    await Task.Delay(delay, ct);
                    attempt++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller budget exhausted between retries — transient by definition.
                    return GraphTokenResult.TransientFailure(rejection);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Network errors and HttpClient-side timeouts are transient
                    if (attempt >= retryDelays.Length)
                    {
                        _logger.LogWarning(ex,
                            "Graph token acquisition for tenant {TenantId} failed with network error after {Attempts} attempt(s)",
                            tenantId, attempt + 1);
                        return GraphTokenResult.TransientFailure(rejection);
                    }

                    var delay = retryDelays[attempt];
                    _logger.LogWarning(ex,
                        "Graph token acquisition for tenant {TenantId} network error (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s",
                        tenantId, attempt + 1, retryDelays.Length + 1, delay.TotalSeconds);

                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return GraphTokenResult.TransientFailure(rejection);
                    }
                    attempt++;
                }
            }
        }

        /// <summary>
        /// The retry chain's own failure line only comes after its 50 s, which the validation budget
        /// never reaches, so without this line Entra's reason would not reach App Insights at all.
        /// </summary>
        private void LogRejectionOncePerWindow(string tenantId, string clientId, EntraTokenRejection rejection)
        {
            var key = $"graph-token-rejection-logged:{tenantId}:{clientId.ToLowerInvariant()}";
            if (_cache.TryGetValue(key, out _))
                return;
            _cache.Set(key, true, RejectionLogWindow);

            _logger.LogWarning(
                "Graph token rejected by Entra for tenant {TenantId}, app {ClientId}: status {StatusCode}, {ErrorCode}. {Description}",
                tenantId, clientId, rejection.StatusCode, rejection.ErrorCode, rejection.Description);
        }

        // Keys include the resolved client id: during the dual app-reg window a tenant flip
        // (HomedAppClientId change) must never be served a token/verdict minted under the other
        // app. Normalized to lowercase so casing differences in config can't split the cache.
        private static string TokenCacheKey(string tenantId, string? clientId) =>
            $"graph-token:{tenantId}:{clientId?.ToLowerInvariant()}";
        private static string ConsentCacheKey(string tenantId, string? clientId) =>
            $"graph-consent-status:{tenantId}:{clientId?.ToLowerInvariant()}";

        /// <summary>
        /// Drops this tenant's cached app tokens (and consent-status verdicts) — for BOTH app
        /// registrations — so the next acquire mints a fresh JWT from AAD. Required for the
        /// consent / Graph-permission fresh-read contract: after an admin grants consent or assigns
        /// app roles, the OLD token still carries the OLD <c>roles</c> claim until it expires —
        /// serving it would make a post-grant "Refresh" keep reporting "not granted" for up to the
        /// cache TTL. Also invoked on a HomedAppClientId flip. The single fresh-read entry point
        /// <c>GraphFeatureDetector.InvalidateTenant</c> calls this so BOTH cache layers are cleared
        /// together (the detector's parsed-roles cache AND this raw-token cache).
        /// </summary>
        public virtual void InvalidateTenant(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return;
            // Bump the generation FIRST so any token POST already in flight (which captured the old
            // generation) writes an entry that readers reject as stale — this closes the
            // write-after-invalidate race, not just the already-written entry. Then drop the current
            // entries (both apps' key shapes) to free memory and clear the consent-status verdicts.
            _tokenCacheGen.AddOrUpdate(tenantId, 1, (_, v) => v + 1);
            _cache.Remove(TokenCacheKey(tenantId, _appRegistry.Primary.ClientId));
            _cache.Remove(ConsentCacheKey(tenantId, _appRegistry.Primary.ClientId));
            if (_appRegistry.Legacy != null)
            {
                _cache.Remove(TokenCacheKey(tenantId, _appRegistry.Legacy.ClientId));
                _cache.Remove(ConsentCacheKey(tenantId, _appRegistry.Legacy.ClientId));
            }
        }

        private bool TryGetCachedToken(string tenantId, string? clientId, out string token)
        {
            // Accept the cached token only if its generation still matches the tenant's current one.
            // A mismatch means an InvalidateTenant ran since this entry's acquire started, so the
            // token may carry a stale roles claim — treat it as a miss and re-mint.
            if (_cache.TryGetValue(TokenCacheKey(tenantId, clientId), out CachedAppToken? cached)
                && cached != null
                && !string.IsNullOrEmpty(cached.Token)
                && cached.Gen == CurrentGen(tenantId))
            {
                token = cached.Token;
                return true;
            }
            token = string.Empty;
            return false;
        }

        /// <summary>
        /// Reads a successful token response: the access token, and expires_in only when a token came
        /// back. A body that is not a JSON object throws; the caller's retry loop handles it.
        /// </summary>
        internal static (string? AccessToken, long? ExpiresInSeconds) ReadTokenResponse(string responseBody)
        {
            var tokenJson = JsonConvert.DeserializeObject<JObject>(responseBody);
            var accessToken = tokenJson?["access_token"]?.ToString();
            return string.IsNullOrEmpty(accessToken) ? (accessToken, null) : (accessToken, (long?)tokenJson?["expires_in"]);
        }

        private void CacheToken(string tenantId, string clientId, string accessToken, long genAtStart, long? expiresInSeconds)
        {
            // expires_in is reported by AAD over TLS, so it is trustworthy — but still clamped so a
            // missing or pathological value cannot pin a stale token (cap) or cause thrash (floor).
            var lifetime = expiresInSeconds is > 0
                ? TimeSpan.FromSeconds(expiresInSeconds.Value)
                : DefaultTokenLifetime;
            var ttl = lifetime - TokenSkewMargin;
            if (ttl < MinTokenTtl) ttl = MinTokenTtl;
            if (ttl > MaxTokenTtl) ttl = MaxTokenTtl;

            _cache.Set(TokenCacheKey(tenantId, clientId), new CachedAppToken(accessToken, genAtStart), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
            });
        }
    }

    /// <summary>
    /// Result of a Graph token acquisition attempt.
    /// Distinguishes between success, permanent failure (no consent), and transient failure (network/server error).
    /// </summary>
    public class GraphTokenResult
    {
        public string? AccessToken { get; private set; }

        /// <summary>
        /// True when the failure is transient (network error, server error, timeout).
        /// Transient failures should NOT be cached as "no consent".
        /// </summary>
        public bool IsTransient { get; private set; }

        /// <summary>True when Entra issued this token just now; false for one from the cache.</summary>
        public bool IsFreshlyMinted { get; private set; }

        /// <summary>What Entra last refused with, also when the caller's budget ended the retries; null without a refusal.</summary>
        public EntraTokenRejection? Rejection { get; private set; }

        public static GraphTokenResult Success(string? token) => new() { AccessToken = token };
        public static GraphTokenResult Minted(string? token) => new() { AccessToken = token, IsFreshlyMinted = !string.IsNullOrEmpty(token) };
        public static GraphTokenResult PermanentFailure(EntraTokenRejection? rejection = null) => new() { AccessToken = null, IsTransient = false, Rejection = rejection };
        public static GraphTokenResult TransientFailure(EntraTokenRejection? rejection = null) => new() { AccessToken = null, IsTransient = true, Rejection = rejection };
    }

    public class GraphConsentStatusResult
    {
        public bool IsConsented { get; set; }

        /// <summary>
        /// True when the result is due to a transient error (network/server issue).
        /// Callers should treat this as "unknown" rather than "not consented".
        /// </summary>
        public bool IsTransient { get; set; }

        public string? Message { get; set; }
    }
}
