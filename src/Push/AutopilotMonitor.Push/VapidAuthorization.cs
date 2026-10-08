using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutopilotMonitor.Push;

/// <summary>
/// Builds the RFC 8292 <c>Authorization: vapid t=&lt;jwt&gt;, k=&lt;public key&gt;</c> header and caches
/// the signed token per (push-service origin, key id). A token lives 24 hours and is reused
/// while more than 12 hours remain, so a cache entry is re-minted at most every 12 hours — well
/// inside Apple's "do not refresh more than hourly". Thread-safe; one instance per key ring.
/// </summary>
public sealed class VapidAuthorization
{
    /// <summary>Lifetime written into the <c>exp</c> claim; RFC 8292 caps it at 24 hours.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    /// <summary>A cached token is reused while at least this much of its lifetime remains.</summary>
    public static readonly TimeSpan MinimumRemainingLifetime = TimeSpan.FromHours(12);

    private const string HeaderSegment = "eyJ0eXAiOiJKV1QiLCJhbGciOiJFUzI1NiJ9"; // base64url({"typ":"JWT","alg":"ES256"})

    private static readonly JsonSerializerOptions ClaimsJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ConcurrentDictionary<(string Audience, string Kid), CachedToken> _cache = new();
    private readonly object _mintLock = new();

    /// <summary>
    /// Returns the header value for a push resource. <paramref name="subject"/> must be a
    /// <c>mailto:</c> or <c>https://</c> URI; <paramref name="now"/> is the request time (injected
    /// so the cache is testable).
    /// </summary>
    public string BuildHeaderValue(Uri endpoint, VapidKey key, string subject, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(subject);
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The push endpoint must be an absolute URI.", nameof(endpoint));
        }

        if (!subject.StartsWith("mailto:", StringComparison.Ordinal) && !subject.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new ArgumentException("The VAPID subject must be a mailto: or https:// URI.", nameof(subject));
        }

        var audience = Origin(endpoint);
        var cacheKey = (audience, key.Kid);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires - now >= MinimumRemainingLifetime)
        {
            return cached.HeaderValue;
        }

        lock (_mintLock)
        {
            if (_cache.TryGetValue(cacheKey, out cached) && cached.Expires - now >= MinimumRemainingLifetime)
            {
                return cached.HeaderValue;
            }

            var expires = now + TokenLifetime;
            var token = Mint(audience, key, subject, expires);
            var entry = new CachedToken($"vapid t={token}, k={key.PublicKeyBase64Url}", expires);
            _cache[cacheKey] = entry;
            Prune(now);
            return entry.HeaderValue;
        }
    }

    /// <summary>The RFC 6454 origin of a push resource: scheme and host, plus the port when it is not the default.</summary>
    internal static string Origin(Uri endpoint) =>
        endpoint.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);

    private static string Mint(string audience, VapidKey key, string subject, DateTimeOffset expires)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(
            new VapidClaims(audience, expires.ToUnixTimeSeconds(), subject), ClaimsJson);
        var signingInput = HeaderSegment + "." + Base64Url.EncodeToString(claims);
        var signature = key.SignEs256(Encoding.ASCII.GetBytes(signingInput));
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _cache)
        {
            if (pair.Value.Expires <= now)
            {
                _cache.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record CachedToken(string HeaderValue, DateTimeOffset Expires);

    private sealed record VapidClaims(string aud, long exp, string sub);
}
