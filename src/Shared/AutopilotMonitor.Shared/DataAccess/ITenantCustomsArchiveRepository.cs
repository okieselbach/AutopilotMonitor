using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Offboarding;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Access to <c>TenantOffboardingCustomsArchive</c>. Writes happen during Phase 2.D-archive
    /// of the offboarding worker; reads / deletes are driven by the Global Admin
    /// <c>/admin/customs-archive</c> UI.
    /// <para>
    /// All write paths fail-loud — when an Azure storage call throws, exceptions propagate
    /// to the caller (queue worker), which owns retry/poison semantics. Reads + deletes
    /// from the admin API similarly surface errors to the HTTP caller.
    /// </para>
    /// </summary>
    public interface ITenantCustomsArchiveRepository
    {
        /// <summary>
        /// Idempotent upsert (Replace). Used during Phase 2.D-archive so a worker that
        /// crashed mid-archive can re-pickup without duplicate-key errors.
        /// </summary>
        Task UpsertAsync(TenantOffboardingCustomsArchiveEntry entry, CancellationToken ct = default);

        /// <summary>Count entries within a single offboarding run filtered by source table.</summary>
        Task<int> CountByRunAndTableAsync(
            string normalizedTenantId, string historyRowKey, string originalTable, CancellationToken ct = default);

        /// <summary>List one offboarding run's archive partition (all 3 source tables interleaved).</summary>
        IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryByRunAsync(
            string normalizedTenantId, string historyRowKey, CancellationToken ct = default);

        /// <summary>List every archive partition for a tenant (one entry per row across all runs).</summary>
        IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryByTenantAsync(
            string normalizedTenantId, CancellationToken ct = default);

        /// <summary>List every archived run as a (tenantId, historyRowKey)-pair summary across all tenants.</summary>
        IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryAllAsync(CancellationToken ct = default);

        /// <summary>Fetch a single archive entry by (PK, RK).</summary>
        Task<TenantOffboardingCustomsArchiveEntry?> TryGetEntryAsync(
            string partitionKey, string rowKey, CancellationToken ct = default);

        /// <summary>Delete a single archived entry. 404 silently swallowed (idempotent).</summary>
        Task DeleteEntryAsync(string partitionKey, string rowKey, CancellationToken ct = default);

        /// <summary>
        /// Delete every entry in a single run's archive partition. Used by the Global Admin
        /// "Delete run" action after they have reviewed the contents.
        /// </summary>
        Task<int> DeleteRunAsync(
            string normalizedTenantId, string historyRowKey, CancellationToken ct = default);
    }
}
