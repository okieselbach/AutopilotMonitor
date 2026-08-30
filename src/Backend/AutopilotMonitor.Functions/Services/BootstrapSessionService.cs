using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing bootstrap sessions (OOBE pre-enrollment agent deployment).
    /// Delegates storage operations to IBootstrapRepository while keeping
    /// caching, short code generation, and business logic.
    /// </summary>
    public class BootstrapSessionService
    {
        private readonly IBootstrapRepository _bootstrapRepo;
        private readonly ILogger<BootstrapSessionService> _logger;
        private readonly IMemoryCache _cache;

        // Charset for short codes: no ambiguous chars (0/O, 1/l/I)
        private const string ShortCodeCharset = "23456789abcdefghjkmnpqrstuvwxyz";
        private const int ShortCodeLength = 6;

        // Token validation cache TTL (short enough for revocation to propagate)
        private static readonly TimeSpan TokenCacheDuration = TimeSpan.FromSeconds(60);

        public BootstrapSessionService(
            IBootstrapRepository bootstrapRepo,
            ILogger<BootstrapSessionService> logger,
            IMemoryCache cache)
        {
            _bootstrapRepo = bootstrapRepo;
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// Creates a new bootstrap session with a unique short code and token.
        /// </summary>
        public async Task<BootstrapSession> CreateAsync(string tenantId, int validityHours, string createdByUpn, string label)
        {
            // Clamp validity to 1–168 hours (1 week max)
            validityHours = Math.Max(1, Math.Min(168, validityHours));

            var now = DateTime.UtcNow;
            var token = Guid.NewGuid().ToString();

            // Generate unique short code with collision guard
            string? shortCode = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                shortCode = GenerateShortCode();

                var session = new BootstrapSession
                {
                    TenantId = tenantId,
                    ShortCode = shortCode,
                    Token = token,
                    CreatedAt = now,
                    ExpiresAt = now.AddHours(validityHours),
                    CreatedByUpn = createdByUpn,
                    IsRevoked = false,
                    UsageCount = 0,
                    Label = label ?? ""
                };

                var created = await _bootstrapRepo.CreateBootstrapSessionAsync(session);
                if (created)
                {
                    _logger.LogInformation("Created bootstrap session {ShortCode} for tenant {TenantId}, expires {ExpiresAt}",
                        shortCode, tenantId, session.ExpiresAt);
                    return session;
                }

                _logger.LogWarning("Bootstrap short code collision on attempt {Attempt}: {Code}", attempt + 1, shortCode);
                shortCode = null;
            }

            throw new InvalidOperationException("Failed to generate unique short code after 5 attempts");
        }

        /// <summary>
        /// Lists bootstrap sessions for a tenant (includes active and recently expired/revoked).
        /// </summary>
        public async Task<List<BootstrapSession>> ListAsync(string tenantId)
        {
            return await _bootstrapRepo.GetBootstrapSessionsAsync(tenantId);
        }

        /// <summary>
        /// Revokes a bootstrap session owned by <paramref name="tenantId"/>. Already-running agents with
        /// the token will be rejected on next request. Codes owned by another tenant are reported as
        /// not found (false) — the repository enforces ownership against the CodeLookup partition.
        /// </summary>
        public async Task<bool> RevokeAsync(string tenantId, string shortCode)
        {
            // We need to invalidate the token cache entry before revoking
            // First get the session to find the token
            var sessions = await _bootstrapRepo.GetBootstrapSessionsAsync(tenantId);
            var session = sessions.FirstOrDefault(s => s.ShortCode == shortCode);
            if (session?.Token != null)
            {
                _cache.Remove($"bootstrap-token:{session.Token}");
            }

            var result = await _bootstrapRepo.RevokeBootstrapSessionAsync(tenantId, shortCode);
            if (result)
            {
                _logger.LogInformation("Revoked bootstrap session {ShortCode} for tenant {TenantId}", shortCode, tenantId);
            }
            return result;
        }

        /// <summary>
        /// Validates a bootstrap code (anonymous; called by GetBootstrapScriptFunction
        /// in-process and by the legacy validate endpoint / Next.js /go route).
        /// Returns the session if the code is valid and not expired/revoked.
        /// </summary>
        public async Task<BootstrapSession?> ValidateCodeAsync(string shortCode)
        {
            var session = await _bootstrapRepo.GetBootstrapSessionByCodeAsync(shortCode);
            if (session == null) return null;

            // Increment usage count (fire-and-forget)
            _ = _bootstrapRepo.IncrementBootstrapUsageAsync(shortCode);

            return session;
        }

        /// <summary>
        /// Validates a bootstrap token (called by SecurityValidator on every agent request with X-Bootstrap-Token).
        /// Results are cached for 60 seconds.
        /// </summary>
        public async Task<BootstrapSession?> ValidateTokenAsync(string token)
        {
            var cacheKey = $"bootstrap-token:{token}";

            if (_cache.TryGetValue(cacheKey, out BootstrapSession? cached))
            {
                // Check if cached result is still valid
                if (cached != null && !cached.IsRevoked && cached.ExpiresAt > DateTime.UtcNow)
                    return cached;

                // Cached as invalid or expired — re-validate
                _cache.Remove(cacheKey);
            }

            var session = await _bootstrapRepo.ValidateBootstrapTokenAsync(token);

            if (session == null)
            {
                // Cache negative result briefly to prevent repeated lookups
                _cache.Set<BootstrapSession?>(cacheKey, null, TimeSpan.FromSeconds(10));
                return null;
            }

            _cache.Set(cacheKey, session, TokenCacheDuration);
            return session;
        }

        /// <summary>
        /// Deletes bootstrap sessions that expired more than 7 days ago.
        /// Called by maintenance timer.
        /// </summary>
        public async Task CleanupExpiredAsync()
        {
            await _bootstrapRepo.CleanupExpiredAsync();
        }

        private static string GenerateShortCode()
        {
            var bytes = new byte[ShortCodeLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var chars = new char[ShortCodeLength];
            for (int i = 0; i < ShortCodeLength; i++)
            {
                chars[i] = ShortCodeCharset[bytes[i] % ShortCodeCharset.Length];
            }
            return new string(chars);
        }
    }
}
