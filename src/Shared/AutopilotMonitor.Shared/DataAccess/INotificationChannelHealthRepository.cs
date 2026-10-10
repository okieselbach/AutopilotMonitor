using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Persistence for <see cref="NotificationChannelHealth"/> rows (one per channel and scope).
    /// Storage errors propagate; the recorder that writes on every send wraps them fail-soft.
    /// </summary>
    public interface INotificationChannelHealthRepository
    {
        /// <summary>
        /// The row of one channel plus its ETag for a compare-and-swap write; both null when no
        /// row exists — pass the null ETag to <see cref="TryUpsertAsync"/> to create it.
        /// </summary>
        Task<(NotificationChannelHealth? Row, string? ETag)> GetWithETagAsync(string scopeKey, string channelId);

        /// <summary>
        /// Writes the whole row: inserts when <paramref name="ifMatchETag"/> is null, otherwise
        /// replaces only if the row is unchanged. False when another writer won the race.
        /// </summary>
        Task<bool> TryUpsertAsync(NotificationChannelHealth row, string? ifMatchETag);

        /// <summary>Every row of one scope (a tenant's or the platform's channels).</summary>
        Task<List<NotificationChannelHealth>> ListAsync(string scopeKey);

        /// <summary>Every row with its ETag — the maintenance sweep's input (one row per channel, a few dozen in total).</summary>
        Task<List<(NotificationChannelHealth Row, string ETag)>> ListAllAsync();

        /// <summary>
        /// Deletes the row only if it is unchanged since it was read; false when a send rewrote it
        /// in the meantime or it is already gone.
        /// </summary>
        Task<bool> TryDeleteAsync(NotificationChannelHealth row, string ifMatchETag);
    }
}
