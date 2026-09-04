using System;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// The WRITE side of conferred Pro ("Pro (MSP)"). The edition itself is never written — it is projected at
    /// read time from group membership (<see cref="ManagedTenantProIndex"/>). What must be written is the
    /// retention downgrade grace anchor: a managed tenant that raised its retention under conferred Pro would
    /// otherwise be clamped to the Community cap (hard deletes) the moment the relationship ends. Every path
    /// that ends a Pro-conferring relationship calls one of the record methods:
    /// <list type="bullet">
    /// <item>self-service removal / customer revoke and the Global Admin removing a tenant from an owned group → <see cref="RecordLossAsync"/></item>
    /// <item>the managing tenant losing its permanent Pro tier (plan endpoint) and its offboarding → <see cref="RecordLossForOwnedGroupAsync"/></item>
    /// </list>
    /// Accepting an invitation only needs the caches dropped (<see cref="NotifyDelegationChangedAsync"/>).
    /// </summary>
    public class ProConferralService
    {
        private const string BackupSource = "delegation";

        private readonly IAdminRepository _adminRepo;
        private readonly TenantConfigurationService _configs;
        private readonly ManagedTenantProIndex _index;
        private readonly ILogger<ProConferralService> _logger;
        private readonly TimeProvider _time;

        public ProConferralService(
            IAdminRepository adminRepo,
            TenantConfigurationService configs,
            ManagedTenantProIndex index,
            ILogger<ProConferralService> logger)
            : this(adminRepo, configs, index, logger, TimeProvider.System)
        {
        }

        /// <summary>Test seam — inject a fake <see cref="TimeProvider"/>.</summary>
        public ProConferralService(
            IAdminRepository adminRepo,
            TenantConfigurationService configs,
            ManagedTenantProIndex index,
            ILogger<ProConferralService> logger,
            TimeProvider time)
        {
            _adminRepo = adminRepo;
            _configs = configs;
            _index = index;
            _logger = logger;
            _time = time;
        }

        /// <summary>
        /// A managed tenant joined or left a group on THIS instance: drop the index and the tenant's cached
        /// configuration so the next read reflects the new standing immediately here (other instances follow
        /// within the cache TTL).
        /// </summary>
        public virtual Task NotifyDelegationChangedAsync(string managedTenantId)
        {
            _index.Invalidate();
            _configs.InvalidateCache(managedTenantId.ToLowerInvariant());
            return Task.CompletedTask;
        }

        /// <summary>
        /// A single managed tenant left <paramref name="homeTenantId"/>'s owned group. Stamps the grace anchor when
        /// the home tenant is a permanent-Pro tenant (a trial MSP conferred nothing, so nothing is lost).
        /// Returns whether an anchor was written.
        /// </summary>
        public virtual async Task<bool> RecordLossAsync(string managedTenantId, string homeTenantId, string reason)
        {
            var home = homeTenantId.ToLowerInvariant();
            var managed = managedTenantId.ToLowerInvariant();
            try
            {
                var homeConfig = await _configs.GetConfigurationIfExistsAsync(home);
                var conferred = homeConfig != null && FeatureEntitlementCatalog.IsPermanentProTier(homeConfig.PlanTier);
                if (!conferred)
                    return false;
                return await StampAsync(managed, home, reason);
            }
            finally
            {
                await NotifyDelegationChangedAsync(managed);
            }
        }

        /// <summary>
        /// Every tenant in <paramref name="homeTenantId"/>'s owned group loses conferred Pro at once (the home
        /// tenant lost its permanent Pro tier or is being offboarded). The caller has established that the home
        /// tenant WAS a permanent-Pro tenant before the change. Returns the number of anchors written.
        /// </summary>
        public virtual async Task<int> RecordLossForOwnedGroupAsync(string homeTenantId, string reason)
        {
            var home = homeTenantId.ToLowerInvariant();
            var groupId = Constants.TenantGroupIds.ForHomeTenant(home);
            var members = await _adminRepo.GetGroupTenantsAsync(groupId);
            var stamped = 0;
            foreach (var member in members)
            {
                var managed = member.ToLowerInvariant();
                if (string.Equals(managed, home, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (await StampAsync(managed, home, reason))
                    stamped++;
                _configs.InvalidateCache(managed);
            }
            _index.Invalidate();
            return stamped;
        }

        private async Task<bool> StampAsync(string managedTenantId, string homeTenantId, string reason)
        {
            // Fresh read: the anchor is a read-modify-write on the whole row; a stale cached instance would
            // rewind fields another writer just changed (the app-homing failure mode).
            var config = await _configs.GetConfigurationFreshAsync(managedTenantId);
            if (config == null)
                return false;

            var nowUtc = _time.GetUtcNow().UtcDateTime;
            config.ProDowngradedUtc = nowUtc;
            config.UpdatedBy = $"delegation:{homeTenantId}";
            await _configs.SaveConfigurationAsync(config, BackupSource, reason);
            _logger.LogInformation(
                "[ProConferral] {Managed} lost Pro conferred by {Home} ({Reason}); retention grace anchor set to {Anchor:O}",
                managedTenantId, homeTenantId, reason, nowUtc);
            return true;
        }
    }
}
