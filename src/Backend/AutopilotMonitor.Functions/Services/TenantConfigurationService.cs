using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
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
        /// Saves configuration for a tenant
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

                await _configRepo.SaveTenantConfigurationAsync(config);

                // Invalidate cache
                _cache.Remove($"tenant-config:{config.TenantId}");

                _logger.LogInformation($"Configuration saved for tenant {config.TenantId} by {config.UpdatedBy}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error saving configuration for tenant {config.TenantId}");
                throw;
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
        /// Returns (config, exists). exists=false when no row was found — does NOT auto-create.
        /// Use for agent security gates where unknown tenants must be rejected.
        /// </summary>
        public virtual async Task<(TenantConfiguration config, bool exists)> TryGetConfigurationAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return (TenantConfiguration.CreateDefault("unknown"), false);

            var cacheKey = $"tenant-config:{tenantId}";

            if (_cache.TryGetValue(cacheKey, out TenantConfiguration? cachedConfig) && cachedConfig != null)
                return (cachedConfig, true);

            try
            {
                var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
                if (config != null)
                {
                    _cache.Set(cacheKey, config, CacheDuration);
                    return (config, true);
                }

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
    }
}
