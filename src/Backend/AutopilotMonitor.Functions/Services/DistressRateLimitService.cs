using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Three-layer rate limiter for the unauthenticated distress channel.
    /// Separate from <see cref="RateLimitService"/> which uses cert thumbprints (post-auth).
    ///
    /// Layers:
    ///   1. Per-IP:     5 requests per 15 minutes (sliding window)
    ///   2. Per-Tenant: 20 requests per hour (sliding window)
    ///   3. Global circuit breaker: 200 requests/minute → reject all for 5 minutes
    /// </summary>
    public class DistressRateLimitService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<DistressRateLimitService> _logger;

        // History + lock in one cache entry (same pattern as RateLimitService): same list ⇒ same lock.
        private sealed class RequestBucket
        {
            public readonly List<DateTime> History = new();
            public readonly object Sync = new();
        }

        // Serializes bucket creation (GetOrCreate is not atomic); hot path is a lock-free TryGetValue.
        private readonly object _createLock = new();

        // Layer 1: Per-IP
        private const int MaxPerIp = 5;
        private static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(15);

        // Layer 2: Per-Tenant
        private const int MaxPerTenant = 20;
        private static readonly TimeSpan TenantWindow = TimeSpan.FromHours(1);

        // Layer 3: Global circuit breaker
        private const int CircuitBreakerThreshold = 200;
        private static readonly TimeSpan CircuitBreakerWindow = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan CircuitBreakerCooldown = TimeSpan.FromMinutes(5);

        private readonly object _globalLock = new();
        private readonly List<DateTime> _globalHistory = new();
        private DateTime? _circuitOpenUntil;

        public DistressRateLimitService(IMemoryCache cache, ILogger<DistressRateLimitService> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Checks all three rate limit layers. Returns whether the request is allowed.
        /// </summary>
        public DistressRateLimitResult Check(string clientIp, string tenantId)
        {
            var now = DateTime.UtcNow;

            // Layer 3 first (cheapest check — single lock, no cache lookup)
            if (IsCircuitOpen(now))
            {
                return new DistressRateLimitResult { IsAllowed = false, RejectedBy = "CircuitBreaker" };
            }

            // Layer 1: Per-IP
            if (!string.IsNullOrEmpty(clientIp))
            {
                if (!CheckSlidingWindow($"distress-ip:{clientIp}", MaxPerIp, IpWindow, now))
                {
                    _logger.LogWarning("Distress rate limit: IP {ClientIp} exceeded {Max}/{WindowMin}min",
                        MaskIp(clientIp), MaxPerIp, IpWindow.TotalMinutes);
                    return new DistressRateLimitResult { IsAllowed = false, RejectedBy = "IP" };
                }
            }

            // Layer 2: Per-Tenant
            if (!CheckSlidingWindow($"distress-tenant:{tenantId}", MaxPerTenant, TenantWindow, now))
            {
                _logger.LogWarning("Distress rate limit: Tenant {TenantId} exceeded {Max}/{WindowMin}min",
                    tenantId, MaxPerTenant, TenantWindow.TotalMinutes);
                return new DistressRateLimitResult { IsAllowed = false, RejectedBy = "Tenant" };
            }

            // Track global volume and possibly trip the circuit breaker
            TrackGlobal(now);

            return new DistressRateLimitResult { IsAllowed = true };
        }

        /// <summary>
        /// Sliding window check using IMemoryCache (same pattern as RateLimitService).
        /// Returns true if the request is within limits.
        /// </summary>
        private bool CheckSlidingWindow(string cacheKey, int maxRequests, TimeSpan window, DateTime now)
        {
            var bucket = GetOrCreateBucket(cacheKey, window);

            lock (bucket.Sync)
            {
                var history = bucket.History;

                // Remove entries outside the window
                var windowStart = now.Subtract(window);
                history.RemoveAll(ts => ts < windowStart);

                if (history.Count >= maxRequests)
                {
                    return false;
                }

                history.Add(now);

                return true;
            }
        }

        private RequestBucket GetOrCreateBucket(string cacheKey, TimeSpan window)
        {
            if (_cache.TryGetValue(cacheKey, out RequestBucket? existing) && existing != null)
                return existing;

            lock (_createLock)
            {
                return _cache.GetOrCreate(cacheKey, entry =>
                {
                    entry.SlidingExpiration = window;
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2);
                    return new RequestBucket();
                })!;
            }
        }

        private bool IsCircuitOpen(DateTime now)
        {
            lock (_globalLock)
            {
                return _circuitOpenUntil.HasValue && now < _circuitOpenUntil.Value;
            }
        }

        private void TrackGlobal(DateTime now)
        {
            lock (_globalLock)
            {
                // Clean old entries
                var windowStart = now.Subtract(CircuitBreakerWindow);
                _globalHistory.RemoveAll(ts => ts < windowStart);
                _globalHistory.Add(now);

                if (_globalHistory.Count >= CircuitBreakerThreshold)
                {
                    _circuitOpenUntil = now.Add(CircuitBreakerCooldown);
                    _globalHistory.Clear();
                    _logger.LogError("Distress circuit breaker OPEN: {Threshold} requests in {Window}s, blocking for {Cooldown}min",
                        CircuitBreakerThreshold, CircuitBreakerWindow.TotalSeconds, CircuitBreakerCooldown.TotalMinutes);
                }
            }
        }

        private static string MaskIp(string ip)
        {
            // Mask last octet for privacy in logs
            if (string.IsNullOrEmpty(ip)) return "unknown";
            var lastDot = ip.LastIndexOf('.');
            return lastDot > 0 ? ip.Substring(0, lastDot) + ".***" : ip;
        }
    }

    /// <summary>
    /// Result of a distress rate limit check.
    /// </summary>
    public class DistressRateLimitResult
    {
        public bool IsAllowed { get; set; }

        /// <summary>
        /// Which layer rejected the request: "IP", "Tenant", "CircuitBreaker", or null if allowed.
        /// </summary>
        public string? RejectedBy { get; set; }
    }
}
