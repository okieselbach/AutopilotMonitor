using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Admin;
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
    ///
    /// Every configuration that leaves this service through a READ path carries the load-time projection
    /// <see cref="TenantConfiguration.ManagedByProTenantId"/> (see <see cref="ManagedTenantProIndex"/>): it is
    /// applied on cache hits and misses alike, so its staleness is bounded by the index TTL alone, and it is
    /// never applied to a synthesized default (a tenant without a row can never be projected as Pro).
    /// </summary>
    public class TenantConfigurationService
    {
        private readonly IConfigRepository _configRepo;
        private readonly ILogger<TenantConfigurationService> _logger;
        private readonly IMemoryCache _cache;
        private readonly ManagedTenantProIndex _proIndex;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        // Concurrent misses for one tenant (cold start, after invalidation) share one repository
        // read instead of each issuing their own (audit 2026-09-05 F12).
        private readonly ConcurrentDictionary<string, Lazy<Task<TenantConfiguration>>> _inFlight = new();

        public TenantConfigurationService(
            IConfigRepository configRepo,
            ILogger<TenantConfigurationService> logger,
            IMemoryCache cache,
            ManagedTenantProIndex proIndex)
        {
            _configRepo = configRepo;
            _logger = logger;
            _cache = cache;
            _proIndex = proIndex;
        }

        /// <summary>
        /// Test seam — no delegation index: no tenant is ever projected as managed. Production DI always
        /// resolves the four-argument constructor (the longest satisfiable one).
        /// </summary>
        public TenantConfigurationService(IConfigRepository configRepo, ILogger<TenantConfigurationService> logger, IMemoryCache cache)
            : this(configRepo, logger, cache, ManagedTenantProIndex.None)
        {
        }

        /// <summary>Applies the read-time projection (see the class remarks). Idempotent; null passes through.</summary>
        private async Task<TenantConfiguration?> ProjectAsync(TenantConfiguration? config)
        {
            if (config == null)
                return null;
            config.ManagedByProTenantId = await _proIndex.GetConferringOwnerAsync(config.TenantId);
            return config;
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
                return (await ProjectAsync(cachedConfig))!;
            }

            var load = _inFlight.GetOrAdd(cacheKey, _ => new Lazy<Task<TenantConfiguration>>(() => LoadAndCacheAsync(tenantId, cacheKey)));
            try
            {
                return await load.Value;
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<TenantConfiguration>>>(cacheKey, load));
            }
        }

        private async Task<TenantConfiguration> LoadAndCacheAsync(string tenantId, string cacheKey)
        {
            try
            {
                var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
                if (config == null)
                {
                    // No row yet: create the default conditionally, so a row another writer created
                    // since the read (a first-login seed) is never overwritten; a lost race reads it.
                    var defaultConfig = TenantConfiguration.CreateDefault(tenantId);
                    try
                    {
                        if (await _configRepo.TryCreateTenantConfigurationAsync(defaultConfig))
                        {
                            _logger.LogInformation("Default configuration created for tenant {TenantId}", tenantId);
                            // Returned unprojected, like any synthesized default (see the class remarks).
                            _cache.Set(cacheKey, defaultConfig, CacheDuration);
                            return defaultConfig;
                        }

                        config = await _configRepo.GetTenantConfigurationAsync(tenantId);
                    }
                    catch (Exception createEx)
                    {
                        _logger.LogError(createEx, "Failed to create the default configuration for tenant {TenantId}", tenantId);
                        return defaultConfig;
                    }

                    if (config == null)
                        return defaultConfig; // created and deleted again in between — nothing to cache
                }

                _cache.Set(cacheKey, config, CacheDuration);
                return (await ProjectAsync(config))!;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading configuration for tenant {tenantId}");
                // Return default on error (fail-open for now, can be changed to fail-closed)
                return TenantConfiguration.CreateDefault(tenantId);
            }
        }

        internal const int MaxUpdateAttempts = 3;

        /// <summary>
        /// The write path of a tenant configuration (D-289, D-290): callers hand in a mutation, never a
        /// configuration to save. <paramref name="mutate"/> runs on the FRESH, projected row and the write is
        /// conditional on that row's ETag. A cached view is up to 5 minutes stale per instance, so saving it
        /// would rewind whatever another writer changed meanwhile (the 2026-07-31 HomedAppClientId class) —
        /// including the offboarding tombstone during its drain barrier.
        /// <para>
        /// On a lost race the row is re-read and <paramref name="mutate"/> runs again, so it must derive
        /// everything from the row it is handed. Returning false declines the write. The offboarding tombstone
        /// is never written: the cascade owns the row until it deletes it. A missing row is
        /// <see cref="TenantConfigUpdateStatus.NotFound"/>; use <see cref="CreateOrUpdateAsync"/> where the write
        /// may create it. Storage errors other than a lost race throw; the cache is invalidated on every attempt.
        /// </para>
        /// </summary>
        public virtual Task<TenantConfigUpdate> UpdateAsync(
            string tenantId, Func<TenantConfiguration, bool> mutate, string backupSource, string backupReason)
            => WriteAsync(tenantId, mutate, backupSource, backupReason, createIfMissing: false);

        /// <summary>
        /// <see cref="UpdateAsync"/> that creates a missing row: <paramref name="mutate"/> then runs on
        /// <see cref="TenantConfiguration.CreateDefault"/> and the row is inserted conditionally. If another
        /// writer created it first, the row is re-read and the mutation decides again on what is stored.
        /// </summary>
        public virtual Task<TenantConfigUpdate> CreateOrUpdateAsync(
            string tenantId, Func<TenantConfiguration, bool> mutate, string backupSource, string backupReason)
            => WriteAsync(tenantId, mutate, backupSource, backupReason, createIfMissing: true);

        private async Task<TenantConfigUpdate> WriteAsync(
            string tenantId, Func<TenantConfiguration, bool> mutate, string backupSource, string backupReason,
            bool createIfMissing)
        {
            for (var attempt = 1; attempt <= MaxUpdateAttempts; attempt++)
            {
                var read = await _configRepo.GetTenantConfigurationWithEtagAsync(tenantId);
                if (read == null)
                {
                    if (!createIfMissing)
                        return new TenantConfigUpdate(TenantConfigUpdateStatus.NotFound, null);

                    // A synthesized row is never projected (see the class remarks).
                    var created = TenantConfiguration.CreateDefault(tenantId);
                    if (!mutate(created))
                        return new TenantConfigUpdate(TenantConfigUpdateStatus.Declined, created);

                    created.LastUpdated = DateTime.UtcNow;
                    bool inserted;
                    try
                    {
                        inserted = await _configRepo.TryCreateTenantConfigurationAsync(created);
                    }
                    finally
                    {
                        InvalidateCache(tenantId);
                    }

                    if (inserted)
                    {
                        _logger.LogInformation("Configuration created for tenant {TenantId} by {UpdatedBy}", tenantId, created.UpdatedBy);
                        return new TenantConfigUpdate(TenantConfigUpdateStatus.Updated, created);
                    }

                    _logger.LogInformation(
                        "Configuration create for tenant {TenantId} lost the race (attempt {Attempt}/{Max})",
                        tenantId, attempt, MaxUpdateAttempts);
                    continue;
                }

                var (config, etag) = read.Value;
                await ProjectAsync(config);

                if (TenantOffboardFunction.IsOffboardingTombstone(config))
                    return new TenantConfigUpdate(TenantConfigUpdateStatus.OffboardingInProgress, config);

                if (!mutate(config))
                    return new TenantConfigUpdate(TenantConfigUpdateStatus.Declined, config);

                config.LastUpdated = DateTime.UtcNow;
                bool replaced;
                try
                {
                    replaced = await _configRepo.TryReplaceTenantConfigurationAsync(config, etag, backupSource, backupReason);
                }
                finally
                {
                    InvalidateCache(tenantId);
                }

                if (replaced)
                {
                    _logger.LogInformation("Configuration updated for tenant {TenantId} by {UpdatedBy}", tenantId, config.UpdatedBy);
                    return new TenantConfigUpdate(TenantConfigUpdateStatus.Updated, config);
                }

                _logger.LogInformation(
                    "Configuration update for tenant {TenantId} lost the ETag race (attempt {Attempt}/{Max})",
                    tenantId, attempt, MaxUpdateAttempts);
            }

            return new TenantConfigUpdate(TenantConfigUpdateStatus.Conflict, null);
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
        /// Fail-soft by design, unlike <see cref="UpdateAsync"/>: every caller is a side
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
            _inFlight.TryRemove($"tenant-config:{tenantId}", out _);
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
                return await ProjectAsync(cachedConfig);

            var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
            if (config != null)
                _cache.Set(cacheKey, config, CacheDuration);

            return await ProjectAsync(config);
        }

        /// <summary>
        /// Cache-BYPASSING read (refreshes this instance's cache on hit); null when no row exists,
        /// never auto-creates. For read-modify-write side-effect writers that persist the WHOLE
        /// entity (e.g. AuthFunction's LastAuthClientId tracking): a cached view can be up to
        /// 5 minutes stale on OTHER instances, and blindly saving it would silently rewind any
        /// field another writer just changed — most critically HomedAppClientId right after a
        /// consent-driven app-homing flip (the exact login that follows a flip is the one that
        /// carries a changed audience and triggers the tracking write).
        /// </summary>
        public virtual async Task<TenantConfiguration?> GetConfigurationFreshAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return null;

            var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
            if (config != null)
                _cache.Set($"tenant-config:{tenantId}", config, CacheDuration);

            return await ProjectAsync(config);
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
                var configs = await _configRepo.GetAllTenantConfigurationsAsync();
                foreach (var config in configs)
                    await ProjectAsync(config);
                return configs;
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
                var page = await _configRepo.GetTenantConfigurationsPageAsync(pageSize, continuation);
                foreach (var config in page.Items)
                    await ProjectAsync(config);
                return page;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tenant configurations page");
                throw;
            }
        }
    }

    public enum TenantConfigUpdateStatus
    {
        /// <summary>Written: replaced, or created by <see cref="TenantConfigurationService.CreateOrUpdateAsync"/>.</summary>
        Updated,
        /// <summary>The mutation returned false; nothing was written.</summary>
        Declined,
        NotFound,
        /// <summary>The row is the offboarding tombstone; nothing was written.</summary>
        OffboardingInProgress,
        /// <summary>Every conditional write lost the ETag race.</summary>
        Conflict,
    }

    /// <summary>
    /// Outcome of <see cref="TenantConfigurationService.UpdateAsync"/>. <see cref="Config"/> is the
    /// row as written (Updated) or as read (Declined, OffboardingInProgress — for a declined create, the
    /// synthesized default the mutation saw); null otherwise.
    /// </summary>
    public sealed record TenantConfigUpdate(TenantConfigUpdateStatus Status, TenantConfiguration? Config);
}
