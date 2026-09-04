using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// The reverse index behind <c>TenantConfiguration.ManagedByProTenantId</c>: managed tenant → the
    /// permanent-Pro tenant whose owned self-service group (<c>msp-{tid}</c>) it belongs to. Built from ONE
    /// scan of the admin-scale Tenant Groups table plus a raw config read per owning tenant, cached
    /// process-wide for <see cref="Ttl"/>, so projecting the pointer onto a configuration costs a dictionary
    /// lookup — never a per-tenant table query on the agent or portal hot path.
    ///
    /// Invariants:
    /// <list type="bullet">
    /// <item>Only OWNED groups (meta row with OwnerTenantId — the self-service delegation model) confer; operator-created groups never do.</item>
    /// <item>Only a PERMANENT Pro owner confers (<see cref="FeatureEntitlementCatalog.IsPermanentProTier"/>): a trial MSP may delegate but does not upgrade its customers.</item>
    /// <item>The owner's tier is read RAW from the repository, never through <see cref="TenantConfigurationService"/> — that service consults this index, and two tenants managing each other would otherwise recurse.</item>
    /// <item>Fail-soft ⇒ fail-closed: any storage error yields an empty map (nobody is projected as managed, i.e. Community values) with a Warning, cached briefly so a hiccup does not hammer storage.</item>
    /// <item>Owners are visited in group-id order so a tenant managed by two Pro tenants resolves deterministically.</item>
    /// </list>
    /// </summary>
    public class ManagedTenantProIndex
    {
        private const string CacheKey = "managed-tenant-pro-index";
        /// <summary>Matches the tenant-config cache TTL — the staleness budget plan changes already accept.</summary>
        internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
        /// <summary>A failed build is cached briefly: fail-closed without a storage storm.</summary>
        internal static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly IAdminRepository _adminRepo;
        private readonly IConfigRepository _configRepo;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ManagedTenantProIndex> _logger;

        public ManagedTenantProIndex(
            IAdminRepository adminRepo,
            IConfigRepository configRepo,
            IMemoryCache cache,
            ILogger<ManagedTenantProIndex> logger)
        {
            _adminRepo = adminRepo;
            _configRepo = configRepo;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>Test seam for subclasses that never touch storage (see <see cref="None"/>).</summary>
        protected ManagedTenantProIndex()
        {
            _adminRepo = null!;
            _configRepo = null!;
            _cache = null!;
            _logger = null!;
        }

        /// <summary>
        /// An index that projects NO tenant as managed. For tests whose subject is not delegation and for
        /// the configuration service's storage-free test constructor — never registered in production DI.
        /// </summary>
        public static ManagedTenantProIndex None { get; } = new NoneIndex();

        /// <summary>The permanent-Pro tenant currently conferring Pro on <paramref name="tenantId"/>, or null.</summary>
        public virtual async Task<string?> GetConferringOwnerAsync(string? tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                return null;
            var map = await GetMapAsync();
            return map.TryGetValue(tenantId, out var owner) ? owner : null;
        }

        /// <summary>Drops the cached map on THIS instance; other instances refresh within <see cref="Ttl"/>.</summary>
        public virtual void Invalidate() => _cache.Remove(CacheKey);

        private async Task<IReadOnlyDictionary<string, string>> GetMapAsync()
        {
            if (_cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, string>? cached) && cached != null)
                return cached;

            var (map, ok) = await BuildAsync();
            _cache.Set(CacheKey, map, ok ? Ttl : FailureTtl);
            return map;
        }

        private async Task<(IReadOnlyDictionary<string, string> Map, bool Ok)> BuildAsync()
        {
            try
            {
                var groups = await _adminRepo.GetAllTenantGroupsAsync();
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var ownerTierCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                foreach (var group in groups
                             .Where(g => !string.IsNullOrWhiteSpace(g.OwnerTenantId) && g.TenantIds.Count > 0)
                             .OrderBy(g => g.GroupId, StringComparer.Ordinal))
                {
                    var owner = group.OwnerTenantId!.Trim().ToLowerInvariant();
                    if (!ownerTierCache.TryGetValue(owner, out var ownerIsPermanentPro))
                    {
                        // RAW read on purpose — see the class remarks (recursion through the config service).
                        var ownerConfig = await _configRepo.GetTenantConfigurationAsync(owner);
                        ownerIsPermanentPro = ownerConfig != null && FeatureEntitlementCatalog.IsPermanentProTier(ownerConfig.PlanTier);
                        ownerTierCache[owner] = ownerIsPermanentPro;
                    }
                    if (!ownerIsPermanentPro)
                        continue;

                    foreach (var tenantId in group.TenantIds)
                    {
                        var managed = tenantId.Trim().ToLowerInvariant();
                        if (managed.Length == 0 || string.Equals(managed, owner, StringComparison.OrdinalIgnoreCase))
                            continue;
                        result.TryAdd(managed, owner);
                    }
                }

                return (result, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ProConferral] Managed-tenant index build failed — no tenant is projected as managed until the next rebuild (fail-closed)");
                return (Empty, false);
            }
        }

        private sealed class NoneIndex : ManagedTenantProIndex
        {
            public override Task<string?> GetConferringOwnerAsync(string? tenantId) => Task.FromResult<string?>(null);
            public override void Invalidate() { }
        }
    }
}
