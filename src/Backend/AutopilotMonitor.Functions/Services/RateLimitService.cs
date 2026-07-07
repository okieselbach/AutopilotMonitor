using System;
using System.Collections.Concurrent;
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

        // Dedicated lock objects per device to avoid race conditions when cache evicts the request history list.
        // Using the list itself as a lock target is unsafe because cache eviction can create a new list instance,
        // causing different threads to lock on different objects.
        private readonly ConcurrentDictionary<string, object> _locks = new();

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

            // Get or create request history for this device
            var lockObj = _locks.GetOrAdd(cacheKey, _ => new object());
            var requestHistory = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.SlidingExpiration = _windowDuration;
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                entry.RegisterPostEvictionCallback((key, _, _, _) => _locks.TryRemove((string)key, out _));
                return new List<DateTime>();
            })!;

            lock (lockObj)
            {
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

                // Update cache
                _cache.Set(cacheKey, requestHistory, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = _windowDuration,
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
                });

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
