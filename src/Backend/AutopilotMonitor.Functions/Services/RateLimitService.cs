using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Rate limiting service to prevent DoS attacks
    /// Uses sliding window algorithm for fair rate limiting per device
    /// </summary>
    public class RateLimitService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<RateLimitService> _logger;
        private readonly TimeSpan _windowDuration;

        // The request history and its lock travel together in one cache entry. Whoever observes the
        // same list observes the same lock, so two threads can never mutate one list under different
        // locks. A thread holding an already-evicted bucket only mutates a discarded list, which is
        // harmless at the expiry boundary. A separate per-key lock dictionary is deliberately NOT
        // used: its eviction-callback cleanup raced with lock re-creation and leaked on replaced entries.
        private sealed class RequestBucket
        {
            public readonly List<DateTime> History = new();
            public readonly object Sync = new();
        }

        // IMemoryCache.GetOrCreate is not atomic: two first-time callers can each create a bucket and
        // the loser would count its request into a discarded list. Creation is serialized here
        // (cheap: only cache misses take it); the hot path is a lock-free TryGetValue.
        private readonly object _createLock = new();

        private RequestBucket GetOrCreateBucket(string cacheKey)
        {
            if (_cache.TryGetValue(cacheKey, out RequestBucket? existing) && existing != null)
                return existing;

            lock (_createLock)
            {
                return _cache.GetOrCreate(cacheKey, entry =>
                {
                    entry.SlidingExpiration = _windowDuration;
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                    return new RequestBucket();
                })!;
            }
        }

        public RateLimitService(IMemoryCache cache, ILogger<RateLimitService> logger)
        {
            _cache = cache;
            _logger = logger;
            _windowDuration = TimeSpan.FromMinutes(1);
        }

        /// <summary>
        /// Checks if a device has exceeded its rate limit
        /// </summary>
        /// <param name="deviceThumbprint">Certificate thumbprint identifying the device</param>
        /// <param name="maxRequestsPerMinute">Maximum requests per minute (from tenant configuration)</param>
        /// <returns>Rate limit result with details</returns>
        public RateLimitResult CheckRateLimit(string deviceThumbprint, int maxRequestsPerMinute = 100)
        {
            if (string.IsNullOrEmpty(deviceThumbprint))
            {
                return new RateLimitResult
                {
                    IsAllowed = false,
                    Message = "No device identifier provided"
                };
            }

            // Defense-in-depth: the sliding-window logic below assumes a positive limit. A value of 0
            // (or negative) makes `Count >= max` true even at Count==0, then `requestHistory.Min()`
            // throws on the empty history — 500 on the device path, fail-open on the user path. Clamp
            // any bad stored/config value to a sane floor so a misconfiguration can never break traffic.
            if (maxRequestsPerMinute < 1)
                maxRequestsPerMinute = 1;

            var cacheKey = $"ratelimit_{deviceThumbprint}";
            var now = DateTime.UtcNow;

            // Get or create request history for this device. The bucket is mutated in place; the
            // cache lookup itself refreshes the sliding expiration, so no re-Set is needed.
            var bucket = GetOrCreateBucket(cacheKey);

            lock (bucket.Sync)
            {
                var requestHistory = bucket.History;

                // Remove requests outside the sliding window
                var windowStart = now.Subtract(_windowDuration);
                requestHistory.RemoveAll(timestamp => timestamp < windowStart);

                // Check if limit exceeded
                if (requestHistory.Count >= maxRequestsPerMinute)
                {
                    var oldestRequest = requestHistory.Min();
                    var retryAfter = oldestRequest.Add(_windowDuration).Subtract(now);

                    // Structured fields so a 429 is queryable in App Insights (count vs limit vs
                    // window). The key may be a cert thumbprint, a bootstrap token, or a UPN
                    // depending on caller, so only an 8-char prefix is logged — never the full key
                    // (avoids leaking tokens/PII). Device/tenant/IP identity is on the paired 429
                    // request row (same operation_Id), enriched in RequestTelemetryMiddleware.
                    _logger.LogWarning(
                        "Rate limit exceeded: {RequestsInWindow}/{MaxRequests} requests in {WindowSeconds:F0}s window (key prefix {RateLimitKeyPrefix})",
                        requestHistory.Count,
                        maxRequestsPerMinute,
                        _windowDuration.TotalSeconds,
                        deviceThumbprint.Length >= 8 ? deviceThumbprint.Substring(0, 8) : deviceThumbprint);

                    return new RateLimitResult
                    {
                        IsAllowed = false,
                        Message = $"Rate limit exceeded: {maxRequestsPerMinute} requests per minute",
                        RequestsInWindow = requestHistory.Count,
                        MaxRequests = maxRequestsPerMinute,
                        RetryAfter = retryAfter,
                        WindowDuration = _windowDuration
                    };
                }

                // Add current request to history
                requestHistory.Add(now);

                return new RateLimitResult
                {
                    IsAllowed = true,
                    RequestsInWindow = requestHistory.Count,
                    MaxRequests = maxRequestsPerMinute,
                    WindowDuration = _windowDuration
                };
            }
        }
    }

    /// <summary>
    /// Result of rate limit check
    /// </summary>
    public class RateLimitResult
    {
        /// <summary>
        /// Whether the request is allowed
        /// </summary>
        public bool IsAllowed { get; set; }

        /// <summary>
        /// Error message if rate limit exceeded
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Number of requests in current window
        /// </summary>
        public int RequestsInWindow { get; set; }

        /// <summary>
        /// Maximum allowed requests
        /// </summary>
        public int MaxRequests { get; set; }

        /// <summary>
        /// Time to wait before retrying (if rate limited)
        /// </summary>
        public TimeSpan? RetryAfter { get; set; }

        /// <summary>
        /// Window duration for rate limiting
        /// </summary>
        public TimeSpan WindowDuration { get; set; }
    }
}
