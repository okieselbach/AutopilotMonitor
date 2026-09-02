using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Self-service delegation invitations AND slot release holds of a managing (MSP) tenant — one table,
    /// PK = home tenant id, RK = invitation id. Lifecycle: Pending → Accepted (the customer tenant joined the
    /// home tenant's owned group) → Released (removed again; the slot stays occupied until HoldUntilUtc) —
    /// or Pending → Cancelled. "Expired" is derived (Pending past ExpiresAt), never written. A Released row
    /// without a preceding invitation (operator added the tenant by hand) is created synthetically so the
    /// 24-hour hold applies uniformly.
    /// </summary>
    public interface IDelegationInvitationRepository
    {
        Task CreateAsync(DelegationInvitation invitation);

        /// <summary>One row (with its ETag), or null.</summary>
        Task<DelegationInvitation?> GetAsync(string homeTenantId, string invitationId);

        /// <summary>Every row of one home tenant (single partition).</summary>
        Task<List<DelegationInvitation>> GetByHomeTenantAsync(string homeTenantId);

        /// <summary>
        /// One-shot Pending → Accepted flip guarded by <paramref name="etag"/>: false when the row was
        /// consumed, cancelled or deleted concurrently (412/404) — the caller answers "already used".
        /// </summary>
        Task<bool> TryAcceptAsync(string homeTenantId, string invitationId, string etag, string acceptedTenantId, string acceptedBy, DateTime nowUtc);

        /// <summary>Cancelled (Pending only) or Released (+hold); GA "release now" rewrites HoldUntilUtc. False when the row is missing.</summary>
        Task<bool> SetStatusAsync(string homeTenantId, string invitationId, string status, DateTime nowUtc, string? actor, DateTime? holdUntilUtc);

        /// <summary>Retention: deletes rows created before the cutoff (all terminal by then). Returns the count.</summary>
        Task<int> DeleteOlderThanAsync(DateTime cutoffUtc);
    }

    /// <summary>One invitation / hold row.</summary>
    public class DelegationInvitation
    {
        public string InvitationId { get; set; } = string.Empty;
        /// <summary>The inviting (managing / MSP) tenant.</summary>
        public string HomeTenantId { get; set; } = string.Empty;
        /// <summary>Constants.DelegationInvitationStatus.</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>Always DelegatedReader for self-service.</summary>
        public string Role { get; set; } = string.Empty;
        /// <summary>Constants.DelegatedSource — CustomerDelegated for invitations, OperatorGranted for synthetic holds.</summary>
        public string Source { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? AcceptedAt { get; set; }
        public string? AcceptedBy { get; set; }
        /// <summary>The managed (accepting) tenant — set on accept (and on synthetic holds).</summary>
        public string? TenantId { get; set; }
        public DateTime? ReleasedAt { get; set; }
        public string? ReleasedBy { get; set; }
        /// <summary>While in the future, the released slot stays occupied.</summary>
        public DateTime? HoldUntilUtc { get; set; }
        /// <summary>Storage ETag of the row as read (for the one-shot accept).</summary>
        public string? ETag { get; set; }
    }
}
