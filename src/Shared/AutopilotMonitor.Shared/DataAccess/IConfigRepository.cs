using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for tenant and admin configuration.
    /// Covers: TenantConfiguration, AdminConfiguration, PreviewWhitelist, PreviewConfig tables.
    /// </summary>
    public interface IConfigRepository
    {
        // --- Tenant Configuration ---
        Task<TenantConfiguration?> GetTenantConfigurationAsync(string tenantId);
        Task<bool> SaveTenantConfigurationAsync(TenantConfiguration config);
        Task<List<TenantConfiguration>> GetAllTenantConfigurationsAsync();

        /// <summary>
        /// One page of tenant configurations, ordered by TenantId (Azure cross-partition
        /// scan over RowKey eq 'config' is PartitionKey-ascending and PartitionKey == TenantId).
        /// Carries the store's opaque continuation token for the function layer to wrap.
        /// </summary>
        Task<RawPage<TenantConfiguration>> GetTenantConfigurationsPageAsync(int pageSize, string? continuation);

        /// <summary>
        /// Writes <paramref name="email"/> to the tenant's ContactEmail ONLY while that field is
        /// still empty, and only if nothing else wrote the row in the meantime. Returns true when
        /// the seed landed, false when the tenant already owns an address, has no config row, or
        /// lost the race.
        /// <para>
        /// Exists because the seed cannot be expressed as a read-modify-write of the whole model:
        /// <see cref="SaveTenantConfigurationAsync"/> replaces the entire row unconditionally, so a
        /// concurrent portal save would be clobbered by the seeder's stale snapshot — of every
        /// field, not just this one. The implementation must therefore write conditionally and
        /// touch no other property.
        /// </para>
        /// </summary>
        Task<bool> TrySeedTenantContactEmailAsync(string tenantId, string email);

        // --- Admin Configuration ---
        Task<AdminConfiguration?> GetAdminConfigurationAsync();
        Task<bool> SaveAdminConfigurationAsync(AdminConfiguration config);

        // --- Preview Whitelist ---
        Task<bool> IsInPreviewWhitelistAsync(string tenantId);
        Task<bool> AddToPreviewWhitelistAsync(string tenantId, string addedBy);
        Task<bool> RemoveFromPreviewWhitelistAsync(string tenantId);
        Task<List<string>> GetPreviewWhitelistAsync();

        // --- Preview Config ---
        Task<Dictionary<string, string>> GetPreviewConfigAsync();
        Task<bool> SavePreviewConfigAsync(string key, string value);

        // --- Preview Notification Email ---
        Task<string?> GetNotificationEmailAsync(string tenantId);
        Task SaveNotificationEmailAsync(string tenantId, string? email);
    }
}
