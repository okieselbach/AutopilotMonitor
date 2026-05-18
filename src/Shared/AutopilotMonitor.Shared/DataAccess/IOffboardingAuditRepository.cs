using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Offboarding;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Access to the <c>OffboardingAudit</c> table. Owns three PartitionKey patterns
    /// (Marker / History / ByTenant). All write paths fail-loud — Queue retry / poison
    /// owns recovery semantics.
    /// </summary>
    public interface IOffboardingAuditRepository
    {
        // ── Marker (offboarding lifecycle anchor; idempotency + cleanup) ───────

        /// <summary>
        /// Uncached point lookup used by <c>TenantOffboardFunction</c>'s idempotency check
        /// + <c>OffboardingMarkerCleanupFunction</c>'s timer sweep. NOT read in the agent
        /// or web auth hotpath any more — the active auth-gate is
        /// <c>TenantConfiguration.Disabled=true</c>. Returns null when no marker exists.
        /// </summary>
        Task<OffboardingMarkerEntry?> TryGetMarkerAsync(string normalizedTenantId, CancellationToken ct = default);

        /// <summary>Inserts a fresh marker. Throws if a marker for the tenant already exists.</summary>
        Task InsertMarkerAsync(OffboardingMarkerEntry marker, CancellationToken ct = default);

        /// <summary>Idempotent upsert; preferred path from worker phase transitions.</summary>
        Task UpsertMarkerAsync(OffboardingMarkerEntry marker, CancellationToken ct = default);

        /// <summary>Removes a marker by tenant id. 404 silently swallowed (idempotent).</summary>
        Task DeleteMarkerAsync(string normalizedTenantId, CancellationToken ct = default);

        /// <summary>Enumerates ALL active markers — used by <c>OffboardingMarkerCleanupFunction</c>.</summary>
        IAsyncEnumerable<OffboardingMarkerEntry> QueryMarkersAsync(CancellationToken ct = default);

        // ── History (audit trail, one row per offboarding attempt) ──────────────

        Task InsertHistoryAsync(OffboardingHistoryEntry history, CancellationToken ct = default);
        Task<OffboardingHistoryEntry?> TryGetHistoryAsync(string historyRowKey, CancellationToken ct = default);
        Task UpsertHistoryAsync(OffboardingHistoryEntry history, CancellationToken ct = default);

        // ── Pointer (O(1) re-onboarding index) ──────────────────────────────────
        //
        // Plan §4.4 update protocol is strictly Read-Modify-Write with ETag-CAS:
        //   1. TryGet → null + null ETag    → Insert with OffboardCount=1
        //   2. TryGet → (pointer, etag)     → modify in-memory + UpdateWithEtag(etag),
        //                                     412 on mismatch ⇒ re-read and retry.
        // The "blind upsert" path was removed because it cannot satisfy OffboardCount=existing+1
        // under concurrent writers — the read-modify-write window is the whole point of the
        // pointer's design.

        /// <summary>
        /// Reads the pointer row plus its ETag for CAS. Returns <c>(null, null)</c> on 404.
        /// </summary>
        Task<(OffboardingByTenantPointer? Pointer, string? ETag)> TryGetByTenantPointerAsync(
            string normalizedTenantId, CancellationToken ct = default);

        /// <summary>
        /// Inserts a fresh pointer row. Throws <see cref="Azure.RequestFailedException"/> with
        /// status 409 when a pointer for the tenant already exists — caller is expected to fall
        /// back to <see cref="UpdateByTenantPointerWithEtagAsync"/> in that case.
        /// </summary>
        Task InsertByTenantPointerAsync(OffboardingByTenantPointer pointer, CancellationToken ct = default);

        /// <summary>
        /// Conditional replace: succeeds only when the stored row's ETag still matches
        /// <paramref name="ifMatchEtag"/>. Throws <see cref="Azure.RequestFailedException"/> with
        /// status 412 on mismatch so the caller can re-read + retry within their bounded loop.
        /// </summary>
        Task UpdateByTenantPointerWithEtagAsync(
            OffboardingByTenantPointer pointer, string ifMatchEtag, CancellationToken ct = default);
    }
}
