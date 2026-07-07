using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing global admin configuration.
    /// Caching and business logic layer — delegates storage to IConfigRepository.
    /// </summary>
    public class AdminConfigurationService
    {
        private readonly IConfigRepository _configRepo;
        private readonly ILogger<AdminConfigurationService> _logger;
        private readonly IMemoryCache _cache;

        private const string CacheKey = "admin-config";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public AdminConfigurationService(IConfigRepository configRepo, ILogger<AdminConfigurationService> logger, IMemoryCache cache)
        {
            _configRepo = configRepo;
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// Gets global admin configuration (uses cache with 5-minute TTL).
        /// <para>
        /// Virtual so it can be mocked via Moq in consumer tests that need controlled
        /// flag values (e.g. <c>IndexReconcileTimer</c> flag-gate tests).
        /// </para>
        /// </summary>
        public virtual async Task<AdminConfiguration> GetConfigurationAsync()
        {
            if (_cache.TryGetValue(CacheKey, out AdminConfiguration? cachedConfig) && cachedConfig != null)
            {
                return cachedConfig;
            }

            try
            {
                // Load from repository
                var config = await _configRepo.GetAdminConfigurationAsync();

                if (config != null)
                {
                    _cache.Set(CacheKey, config, CacheDuration);
                    return config;
                }

                // Configuration not found - create and save default immediately
                _logger.LogInformation("Admin configuration not found, creating and saving default configuration");
                var defaultConfig = AdminConfiguration.CreateDefault();

                try
                {
                    // Save the default configuration via repository
                    await _configRepo.SaveAdminConfigurationAsync(defaultConfig);

                    _cache.Set(CacheKey, defaultConfig, CacheDuration);

                    _logger.LogInformation("Default admin configuration created and saved");
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "Failed to save default admin configuration");
                }

                return defaultConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading admin configuration");
                // Return default on error (fail-open)
                return AdminConfiguration.CreateDefault();
            }
        }

        /// <summary>
        /// Cache-bypassing read of <see cref="AdminConfiguration.SessionDeletionKillSwitch"/>
        /// (plan §5 PR5 finding 1). The general-purpose <see cref="GetConfigurationAsync"/>
        /// caches for 5 minutes per instance; that's wrong for an *emergency* switch — a
        /// flip-ON would not be honored across scaled-out Function-host instances until each
        /// one's cache expires independently. This helper goes straight to the repository so
        /// every call observes the current row.
        /// <para>
        /// <b>Fail-CLOSED on storage error:</b> if the repo throws, we return <c>true</c>
        /// (treat as kill-switch active). For an emergency switch, blocking new deletes on a
        /// transient storage failure is the safe default; the alternative would be silently
        /// failing to honor an in-progress operator action during the incident the switch was
        /// flipped to mitigate.
        /// </para>
        /// <para>
        /// Virtual so tests can mock it without going through the repository.
        /// </para>
        /// </summary>
        public virtual async Task<bool> IsSessionDeletionKillSwitchActiveAsync()
        {
            try
            {
                var config = await _configRepo.GetAdminConfigurationAsync();
                return config?.SessionDeletionKillSwitch ?? false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to read SessionDeletionKillSwitch from repository; failing CLOSED (treating as active) — plan §5 PR5 finding 1");
                return true;
            }
        }

        /// <summary>
        /// Saves global admin configuration and syncs rate limit to all tenant configurations
        /// </summary>
        public async Task SaveConfigurationAsync(AdminConfiguration config)
        {
            if (config == null)
            {
                throw new ArgumentException("Configuration is required");
            }

            try
            {
                config.LastUpdated = DateTime.UtcNow;

                await _configRepo.SaveAdminConfigurationAsync(config);

                // Invalidate cache
                _cache.Remove(CacheKey);

                _logger.LogInformation($"Admin configuration saved by {config.UpdatedBy}");

                // NOTE: Rate limits are no longer mirrored into every tenant row. The effective
                // per-tenant limit is computed at read time as `tenantOverride ?? global`
                // (see SecurityValidator for the device path and UserRateLimitMiddleware for the
                // user path). This removes the former background sync job — and the clobbering
                // foot-gun where a per-tenant edit to the base field was overwritten on the next
                // global save.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving admin configuration");
                throw;
            }
        }

        /// <summary>
        /// Invalidates cache (forces reload on next request)
        /// </summary>
        public void InvalidateCache()
        {
            _cache.Remove(CacheKey);
        }
    }
}
