using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Storage for <see cref="SessionAnnotation"/> rows. Backed by the dedicated
    /// <c>SessionAnnotations</c> table (PK = tenantId, RK = <c>{sessionId}_{lane}</c>).
    /// Rows are wiped on tenant offboarding and cascade-deleted with their session;
    /// the table is in the critical-backup set because annotations are hand-labeled data.
    /// </summary>
    public interface ISessionAnnotationRepository
    {
        /// <summary>Returns the annotation for one session + lane, or null.</summary>
        Task<SessionAnnotation?> GetAsync(string tenantId, string sessionId, string lane);

        /// <summary>Returns all lanes present for one session (0–3 rows).</summary>
        Task<List<SessionAnnotation>> GetForSessionAsync(string tenantId, string sessionId);

        /// <summary>Creates or replaces the row for the annotation's session + lane.</summary>
        Task UpsertAsync(SessionAnnotation annotation);

        /// <summary>Deletes the row for one session + lane. Missing row is a no-op.</summary>
        Task DeleteAsync(string tenantId, string sessionId, string lane);

        /// <summary>
        /// Global evaluation query (cross-tenant filtered table scan; annotation volume is
        /// human-entered and small). All filters optional. <paramref name="ruleId"/> is
        /// matched client-side against <see cref="SessionAnnotation.RuleIds"/>, so this
        /// method back-fills short pages by looping the Azure continuation until
        /// <paramref name="pageSize"/> matches accumulate or the scan is exhausted
        /// (bounded round-trips) — a filtered-out row never consumes page budget.
        /// </summary>
        /// <returns>The page items plus the raw Azure continuation token for the next page (null when done).</returns>
        Task<(List<SessionAnnotation> Items, string? NextRawToken)> QueryPageAsync(
            string? tenantId,
            string? lane,
            string? verdict,
            string? ruleId,
            DateTime? dateFrom,
            DateTime? dateTo,
            int pageSize,
            string? continuation);
    }
}
