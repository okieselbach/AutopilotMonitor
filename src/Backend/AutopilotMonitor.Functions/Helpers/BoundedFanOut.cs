using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Runs one async unit of work per key with a concurrency ceiling and concatenates the
    /// results. Used for cross-tenant Table Storage reads: one <c>PartitionKey eq</c> query per
    /// tenant, in parallel, is a partition-server-parallel read that finishes in the time of the
    /// LARGEST tenant, whereas a single cross-partition property-filter scan walks the whole
    /// table serially. The ceiling keeps a 300-tenant fan-out from opening 300 sockets at once.
    /// </summary>
    internal static class BoundedFanOut
    {
        /// <summary>
        /// Concurrency ceiling for cross-tenant partition fan-outs. High enough that a fan-out
        /// over a few hundred tenants completes in a handful of rounds, low enough to keep the
        /// socket burst per instance bounded.
        /// </summary>
        internal const int CrossTenantConcurrency = 32;

        public static async Task<List<TResult>> RunAsync<TResult>(
            IEnumerable<string> keys,
            int concurrency,
            Func<string, CancellationToken, Task<List<TResult>>> perKey,
            CancellationToken cancellationToken)
        {
            if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));

            using var gate = new SemaphoreSlim(concurrency);
            var tasks = keys.Select(async key =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await perKey(key, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            var merged = new List<TResult>(results.Sum(r => r.Count));
            foreach (var list in results)
                merged.AddRange(list);
            return merged;
        }
    }
}
