using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Monitoring
{
    /// <summary>
    /// Thin abstraction over Azure Storage Queue properties so the health-check
    /// watcher can be unit-tested without a live Storage account. One call per
    /// queue read; implementations should be safe to invoke in parallel.
    /// </summary>
    public interface IPoisonQueueProbe
    {
        /// <summary>
        /// Enumerates the names of all poison queues that currently exist in the
        /// storage account (suffix <c>-poison</c>). Poison queues are created
        /// lazily on the first poison-move, so a queue that is absent here has
        /// never had a failure — dynamic enumeration therefore covers every
        /// queue a static watch-list would, without the list to forget updating.
        /// Throws on transport/auth errors so callers can surface "backlog state
        /// unknown" instead of a silent all-clear.
        /// </summary>
        Task<IReadOnlyList<string>> ListPoisonQueuesAsync(CancellationToken ct);

        /// <summary>
        /// Returns the approximate message count of the queue named
        /// <paramref name="queueName"/>. A non-existent queue (404) is treated as
        /// zero — empty poison queues are not created until something fails for
        /// the first time, which is the healthy state we want to surface.
        /// Throws on any other transport/auth error so the caller can mark the
        /// check as unhealthy with a precise message.
        /// </summary>
        Task<long> GetApproximateMessageCountAsync(string queueName, CancellationToken ct);
    }
}
