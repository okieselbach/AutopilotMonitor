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

        /// <summary>
        /// Same unconditional replace as <see cref="SaveTenantConfigurationAsync(TenantConfiguration)"/>,
        /// but tags the pre-write backup snapshot with the write path and intent. A separate
        /// overload — NOT optional parameters — because Moq expression trees cannot omit
        /// optional arguments (CS0854) and the 1-arg signature is mocked all over the test suite.
        /// </summary>
        Task<bool> SaveTenantConfigurationAsync(TenantConfiguration config, string? backupSource, string? backupReason);

        /// <summary>
        /// Point read that also surfaces the row's ETag (as an opaque string, keeping this
        /// interface storage-agnostic) for use with <see cref="TryReplaceTenantConfigurationAsync"/>.
        /// Null when the tenant has no configuration row. Storage errors throw (fail-loud —
        /// this is the transactional read path, not a fail-soft helper).
        /// </summary>
        Task<(TenantConfiguration Config, string ETag)?> GetTenantConfigurationWithEtagAsync(string tenantId);

        /// <summary>
        /// Conditional full replace (If-Match). Returns false ONLY when the precondition failed
        /// (someone else wrote the row since the ETag was read — the caller re-reads and retries);
        /// any other storage failure throws. Deliberately does NOT run the pre-write backup hook:
        /// the transactional caller snapshots explicitly and fail-CLOSED before invoking this.
        /// </summary>
        Task<bool> TryReplaceTenantConfigurationAsync(TenantConfiguration config, string etag);

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
        /// <summary>
        /// Conditional insert: true when THIS call created the entry, false when the tenant
        /// was already whitelisted (concurrent duplicate or repeat approve). Storage errors
        /// throw — activation must never be silently reported as done.
        /// </summary>
        Task<bool> AddToPreviewWhitelistAsync(string tenantId, string addedBy);
        Task<bool> RemoveFromPreviewWhitelistAsync(string tenantId);
        Task<List<string>> GetPreviewWhitelistAsync();

        // --- Preview Config ---
        Task<Dictionary<string, string>> GetPreviewConfigAsync();
        Task<bool> SavePreviewConfigAsync(string key, string value);

        // --- Email Template Overrides (operator-level, PreviewConfig table, partition "EmailTemplates") ---
        /// <summary>Returns the stored override for the given kind, or null when the built-in template applies.</summary>
        Task<EmailTemplateOverride?> GetEmailTemplateOverrideAsync(string kind);
        Task SaveEmailTemplateOverrideAsync(EmailTemplateOverride overrideEntry);
        Task DeleteEmailTemplateOverrideAsync(string kind);

        // --- Preview Notification Email ---
        Task<string?> GetNotificationEmailAsync(string tenantId);
        Task SaveNotificationEmailAsync(string tenantId, string? email);

        // --- Welcome Email Sent Marker ---
        /// <summary>
        /// Conditionally inserts the once-per-activation welcome-email marker. True when this
        /// call created it (caller may send), false when it already existed. Storage errors throw.
        /// </summary>
        Task<bool> TryMarkWelcomeEmailSentAsync(string tenantId);
        /// <summary>Removes the welcome-email marker (no-op when absent, fail-soft).</summary>
        Task ClearWelcomeEmailSentMarkerAsync(string tenantId);
    }
}
