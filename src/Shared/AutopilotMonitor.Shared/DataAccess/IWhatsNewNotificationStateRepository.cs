using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.WhatsNew;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Persistence for the single <see cref="WhatsNewNotificationState"/> row. Same
    /// ETag-based compare-and-swap contract as <see cref="ISlaTenantStatusRepository"/>: the
    /// caller reads state + ETag, decides what is new, and commits the advanced watermark with
    /// the ETag it observed; a false return means another writer won and this run must not send.
    /// </summary>
    public interface IWhatsNewNotificationStateRepository
    {
        /// <summary>Returns (null, null) when the row does not exist yet (first run).</summary>
        Task<(WhatsNewNotificationState? State, string? ETag)> GetWithETagAsync();

        /// <summary>
        /// Conditional write: insert when <paramref name="ifMatchETag"/> is null (caller observed
        /// no row), otherwise replace if the ETag still matches. Returns false on conflict
        /// (row appeared / changed since the read) and on storage failure — never throws.
        /// </summary>
        Task<bool> TryUpsertAsync(WhatsNewNotificationState state, string? ifMatchETag);
    }
}
