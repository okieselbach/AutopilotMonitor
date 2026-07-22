using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing tenant-specific configuration.
    /// Caching and business logic layer — delegates storage to IConfigRepository.
    /// </summary>
    public class TenantConfigurationService
    {
        private readonly IConfigRepository _configRepo;
        private readonly ILogger<TenantConfigurationService> _logger;
        private readonly IMemoryCache _cache;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public TenantConfigurationService(IConfigRepository configRepo, ILogger<TenantConfigurationService> logger, IMemoryCache cache)
        {
            _configRepo = configRepo;
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// Gets configuration for a tenant (uses cache with 5-minute TTL)
        /// </summary>
        public virtual async Task<TenantConfiguration> GetConfigurationAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogWarning("GetConfiguration called with empty tenantId");
                return TenantConfiguration.CreateDefault("unknown");
            }

            var cacheKey = $"tenant-config:{tenantId}";

            if (_cache.TryGetValue(cacheKey, out TenantConfiguration? cachedConfig) && cachedConfig != null)
            {
                return cachedConfig;
            }

            try
            {
                // Load from repository
                var config = await _configRepo.GetTenantConfigurationAsync(tenantId);

                if (config != null)
                {
                    _cache.Set(cacheKey, config, CacheDuration);
                    return config;
                }

                // Configuration not found - create and save default immediately
                _logger.LogInformation($"Configuration not found for tenant {tenantId}, creating and saving default configuration");
                var defaultConfig = TenantConfiguration.CreateDefault(tenantId);

                try
                {
                    // Save the default configuration via repository
                    await _configRepo.SaveTenantConfigurationAsync(defaultConfig);

                    _cache.Set(cacheKey, defaultConfig, CacheDuration);

                    _logger.LogInformation($"Default configuration created and saved for tenant {tenantId}");
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, $"Failed to save default configuration for tenant {tenantId}");
                }

                return defaultConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading configuration for tenant {tenantId}");
                // Return default on error (fail-open for now, can be changed to fail-closed)
                return TenantConfiguration.CreateDefault(tenantId);
            }
        }

        /// <summary>
        /// Saves configuration for a tenant. THROWS when the write did not persist — the repository
        /// swallows storage exceptions and reports failure via its bool return, so ignoring it would
        /// let callers audit + return 200 for a save that never happened (Codex finding, 2026-07-07).
        /// The cached row is invalidated on EVERY attempted save (finally): on success it is stale;
        /// on failure the caller may have mutated the cached instance in place (plan/trial endpoints),
        /// which must not linger as phantom-saved state for the 5-minute TTL.
        /// </summary>
        public virtual async Task SaveConfigurationAsync(TenantConfiguration config)
        {
            if (config == null || string.IsNullOrEmpty(config.TenantId))
            {
                throw new ArgumentException("Configuration and TenantId are required");
            }

            try
            {
                config.LastUpdated = DateTime.UtcNow;

                var saved = await _configRepo.SaveTenantConfigurationAsync(config);
                if (!saved)
                {
                    throw new InvalidOperationException(
                        $"Tenant configuration save failed for {config.TenantId} — storage write did not persist (see repository logs)");
                }

                _logger.LogInformation($"Configuration saved for tenant {config.TenantId} by {config.UpdatedBy}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error saving configuration for tenant {config.TenantId}");
                throw;
            }
            finally
            {
                // Invalidate cache — success AND failure paths (see summary).
                _cache.Remove($"tenant-config:{config.TenantId}");
            }
        }

        /// <summary>
        /// One-way seed of the tenant contact address: writes <paramref name="email"/> only while
        /// the tenant has none, and only if nothing else wrote the configuration in the meantime.
        /// Returns true when the seed landed.
        /// <para>
        /// Single owner for both seed paths (preview notification-email save and the maintenance
        /// backfill), because both need the same three things: the conditional single-property
        /// write, the "never overwrite what the tenant owns" invariant, and the cache invalidation
        /// below — the repository writes behind this cache, so without it a freshly seeded address
        /// stays invisible for the 5-minute TTL.
        /// </para>
        /// <para>
        /// Fail-soft by design, unlike <see cref="SaveConfigurationAsync"/>: every caller is a side
        /// effect of an operation that has already succeeded, and a lost seed is recoverable on the
        /// next maintenance run. Enforced here rather than left to the repository, so the guarantee
        /// holds for any implementation.
        /// </para>
        /// </summary>
        public virtual async Task<bool> TrySeedContactEmailAsync(string tenantId, string? email)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var seeded = await _configRepo.TrySeedTenantContactEmailAsync(tenantId, email!.Trim());
                if (seeded)
                {
                    _cache.Remove($"tenant-config:{tenantId}");
                    _logger.LogInformation("Seeded contact address for tenant {TenantId}", tenantId);
                }

                return seeded;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not seed contact address for tenant {TenantId} — the triggering write still stands", tenantId);
                return false;
            }
        }

        /// <summary>
        /// Invalidates cache for a tenant (forces reload on next request)
        /// </summary>
        public void InvalidateCache(string tenantId)
        {
            _cache.Remove($"tenant-config:{tenantId}");
        }

        /// <summary>
        /// Strict point-read: returns the config, or null when no row exists (404) — does NOT
        /// auto-create. Any other storage failure PROPAGATES to the caller, so "read failed" can
        /// never be conflated with "tenant not configured". Use where a dropped tenant is worse
        /// than a failed request (e.g. the delegated config/all subset).
        /// </summary>
        public virtual async Task<TenantConfiguration?> GetConfigurationIfExistsAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return null;

            var cacheKey = $"tenant-config:{tenantId}";

            if (_cache.TryGetValue(cacheKey, out TenantConfiguration? cachedConfig) && cachedConfig != null)
                return cachedConfig;

            var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
            if (config != null)
                _cache.Set(cacheKey, config, CacheDuration);

            return config;
        }

        /// <summary>
        /// Returns (config, exists). exists=false when no row was found — does NOT auto-create.
        /// Use for agent security gates where unknown tenants must be rejected: storage errors are
        /// mapped to exists=false (fail closed). Callers that must instead surface read failures
        /// use <see cref="GetConfigurationIfExistsAsync"/>.
        /// </summary>
        public virtual async Task<(TenantConfiguration config, bool exists)> TryGetConfigurationAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return (TenantConfiguration.CreateDefault("unknown"), false);

            try
            {
                var config = await GetConfigurationIfExistsAsync(tenantId);
                if (config != null)
                    return (config, true);

                return (TenantConfiguration.CreateDefault(tenantId), false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading configuration for tenant {tenantId} in TryGetConfigurationAsync");
                // On error, treat as non-existent to fail safely
                return (TenantConfiguration.CreateDefault(tenantId), false);
            }
        }

        /// <summary>
        /// Gets all tenant configurations (for Global Admin use)
        /// </summary>
        public async Task<List<TenantConfiguration>> GetAllConfigurationsAsync()
        {
            try
            {
                return await _configRepo.GetAllTenantConfigurationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading all tenant configurations");
                throw;
            }
        }

        /// <summary>
        /// Gets one page of tenant configurations (for Global Admin use), ordered by TenantId.
        /// Unlike <see cref="GetAllConfigurationsAsync"/> this does not load every tenant at once;
        /// callers follow the page's continuation token until it is null.
        /// </summary>
        public async Task<RawPage<TenantConfiguration>> GetConfigurationsPageAsync(int pageSize, string? continuation)
        {
            try
            {
                return await _configRepo.GetTenantConfigurationsPageAsync(pageSize, continuation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tenant configurations page");
                throw;
            }
        }
    }
}
