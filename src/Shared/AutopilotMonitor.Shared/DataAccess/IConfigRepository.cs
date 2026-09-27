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

        // There is deliberately no unconditional save: a row is created only by the conditional
        // insert below and changed only by the ETag-conditional replace, so no writer can
        // overwrite a row it did not read (D-290).

        /// <summary>
        /// Conditional insert of a NEW row. Returns false when the row already exists (someone
        /// else created it since the caller read "no row" — re-read and update instead); any
        /// other storage failure throws. Nothing to snapshot: there is no previous state.
        /// </summary>
        Task<bool> TryCreateTenantConfigurationAsync(TenantConfiguration config);

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

        /// <summary>
        /// Conditional full replace preceded by the fail-soft pre-write backup (a snapshot of the
        /// stored row tagged with the write path and intent, skipped for noise-only changes) — for
        /// read-modify-write callers without a fail-closed snapshot of their own. Return and throw
        /// semantics as the 2-arg overload. A separate overload, not optional parameters: Moq
        /// expression trees cannot omit optional arguments (CS0854).
        /// </summary>
        Task<bool> TryReplaceTenantConfigurationAsync(TenantConfiguration config, string etag, string? backupSource, string? backupReason);

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
        /// A single-property conditional merge rather than a whole-model replace: the seeder runs as
        /// a side effect of other writes and must never carry a snapshot of the other fields. The
        /// implementation writes conditionally and touches no other property.
        /// </para>
        /// </summary>
        Task<bool> TrySeedTenantContactEmailAsync(string tenantId, string email);

        // --- Admin Configuration ---
        Task<AdminConfiguration?> GetAdminConfigurationAsync();

        /// <summary>
        /// The one write path of the admin configuration row. It reads the row fresh, runs
        /// <paramref name="mutate"/> on a copy and writes ONLY the columns whose value the mutation
        /// changed, conditionally on the row's ETag (re-read and retry on a concurrent write).
        /// Every other column keeps its stored value, including the ones only the agent release
        /// pipeline writes (<c>LatestAgentV2*</c>) and columns this build does not know. A mutation
        /// that changes nothing writes nothing. <paramref name="mutate"/> returns an error message
        /// to reject the change (nothing is written) or null to accept it. Storage failures throw.
        /// </summary>
        Task<AdminConfigurationUpdateResult> UpdateAdminConfigurationAsync(
            Func<AdminConfiguration, string?> mutate, string updatedBy, string? source = null);

        /// <summary>
        /// Creates the row from <paramref name="config"/> when none exists yet; never replaces an
        /// existing row. True when this call created it.
        /// </summary>
        Task<bool> CreateAdminConfigurationIfMissingAsync(AdminConfiguration config);

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

        /// <summary>
        /// Every stored notification address, keyed by LOWERCASED tenant id. One cross-partition
        /// scan instead of one point read per tenant — the operator console needs the whole set at
        /// once to resolve "which tenant received this welcome mail?" from an address alone.
        /// Keys are lowercased because the address rows are written from the JWT tid (lowercase)
        /// while a config PartitionKey's casing is not guaranteed; callers join on the lowercased id.
        /// Storage errors throw (fail-loud, like <see cref="GetPreviewWhitelistAsync"/>).
        /// </summary>
        Task<Dictionary<string, string>> GetAllNotificationEmailsAsync();

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
