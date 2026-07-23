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
        private readonly IConfiguration _configuration;

        public GraphTokenService(
            ILogger<GraphTokenService> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IConfiguration configuration)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _configuration = configuration;
        }

        public async Task<GraphConsentStatusResult> GetConsentStatusAsync(string tenantId, CancellationToken ct = default)
        {
            var cacheKey = ConsentCacheKey(tenantId);
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
            // Serve a still-valid cached app token for this tenant. The cache key is keyed strictly
            // on the (already authenticated upstream) tenantId — the same value that drives the token
            // URL below — so a token minted for one tenant can never be served to another.
            if (TryGetCachedToken(tenantId, out var cachedToken))
            {
                return GraphTokenResult.Success(cachedToken);
            }

            var clientId = _configuration["EntraId:ClientId"];
            var clientSecret = _configuration["EntraId:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError(
                    "Device validation is enabled but Entra ID app credentials are not configured. Set EntraId:ClientId and EntraId:ClientSecret.");
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
                if (gotGate && TryGetCachedToken(tenantId, out var tokenAfterWait))
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

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    // Caller's budget exhausted — treat as transient so we never cache "no consent"
                    // for a tenant just because the optional path timed out.
                    return GraphTokenResult.TransientFailure();
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
                        var tokenJson = JsonConvert.DeserializeObject<JObject>(responseBody);
                        var accessToken = tokenJson?["access_token"]?.ToString();
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            // Cache the freshly minted token (never log its value). TTL derives from
                            // the token's own expires_in, minus a skew margin, clamped to a safe band.
                            CacheToken(tenantId, accessToken!, genAtStart, (long?)tokenJson?["expires_in"]);
                        }
                        return GraphTokenResult.Success(accessToken);
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
                            : GraphTokenResult.PermanentFailure();
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
                    return GraphTokenResult.TransientFailure();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Network errors and HttpClient-side timeouts are transient
                    if (attempt >= retryDelays.Length)
                    {
                        _logger.LogWarning(ex,
                            "Graph token acquisition for tenant {TenantId} failed with network error after {Attempts} attempt(s)",
                            tenantId, attempt + 1);
                        return GraphTokenResult.TransientFailure();
                    }

                    var delay = retryDelays[attempt];
                    _logger.LogWarning(ex,
                        "Graph token acquisition for tenant {TenantId} network error (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s",
                        tenantId, attempt + 1, retryDelays.Length + 1, delay.TotalSeconds);

                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return GraphTokenResult.TransientFailure();
                    }
                    attempt++;
                }
            }
        }

        private static string TokenCacheKey(string tenantId) => $"graph-token:{tenantId}";
        private static string ConsentCacheKey(string tenantId) => $"graph-consent-status:{tenantId}";

        /// <summary>
        /// Drops this tenant's cached app token (and consent-status verdict) so the next acquire
        /// mints a fresh JWT from AAD. Required for the consent / Graph-permission fresh-read
        /// contract: after an admin grants consent or assigns app roles, the OLD token still carries
        /// the OLD <c>roles</c> claim until it expires — serving it would make a post-grant "Refresh"
        /// keep reporting "not granted" for up to the cache TTL. The single fresh-read entry point
        /// <c>GraphFeatureDetector.InvalidateTenant</c> calls this so BOTH cache layers are cleared
        /// together (the detector's parsed-roles cache AND this raw-token cache).
        /// </summary>
        public virtual void InvalidateTenant(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return;
            // Bump the generation FIRST so any token POST already in flight (which captured the old
            // generation) writes an entry that readers reject as stale — this closes the
            // write-after-invalidate race, not just the already-written entry. Then drop the current
            // entries to free memory and clear the coarse consent-status verdict.
            _tokenCacheGen.AddOrUpdate(tenantId, 1, (_, v) => v + 1);
            _cache.Remove(TokenCacheKey(tenantId));
            _cache.Remove(ConsentCacheKey(tenantId));
        }

        private bool TryGetCachedToken(string tenantId, out string token)
        {
            // Accept the cached token only if its generation still matches the tenant's current one.
            // A mismatch means an InvalidateTenant ran since this entry's acquire started, so the
            // token may carry a stale roles claim — treat it as a miss and re-mint.
            if (_cache.TryGetValue(TokenCacheKey(tenantId), out CachedAppToken? cached)
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

        private void CacheToken(string tenantId, string accessToken, long genAtStart, long? expiresInSeconds)
        {
            // expires_in is reported by AAD over TLS, so it is trustworthy — but still clamped so a
            // missing or pathological value cannot pin a stale token (cap) or cause thrash (floor).
            var lifetime = expiresInSeconds is > 0
                ? TimeSpan.FromSeconds(expiresInSeconds.Value)
                : DefaultTokenLifetime;
            var ttl = lifetime - TokenSkewMargin;
            if (ttl < MinTokenTtl) ttl = MinTokenTtl;
            if (ttl > MaxTokenTtl) ttl = MaxTokenTtl;

            _cache.Set(TokenCacheKey(tenantId), new CachedAppToken(accessToken, genAtStart), new MemoryCacheEntryOptions
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

        public static GraphTokenResult Success(string? token) => new() { AccessToken = token };
        public static GraphTokenResult PermanentFailure() => new() { AccessToken = null, IsTransient = false };
        public static GraphTokenResult TransientFailure() => new() { AccessToken = null, IsTransient = true };
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
