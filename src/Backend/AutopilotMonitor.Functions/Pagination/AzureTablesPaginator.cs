using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Thin wrapper around <see cref="TableClient.QueryAsync{T}"/> that surfaces
    /// the raw Azure-Tables continuation token for one page at a time.
    /// </summary>
    /// <remarks>
    /// The wider continuation envelope (tenant binding, filter fingerprint,
    /// expiry) lives in <c>AutopilotMonitor.Shared.Pagination.ContinuationToken</c>.
    /// This helper deliberately returns the SDK's opaque token so the caller
    /// can stitch the wire token together with its own filter fingerprint.
    /// </remarks>
    public static class AzureTablesPaginator
    {
        /// <summary>Largest <c>$top</c> Azure Tables accepts; a larger one is refused with 400 InvalidInput.</summary>
        public const int MaxServerPageSize = 1000;

        /// <summary>
        /// The <c>maxPerPage</c> for a read of up to <paramref name="rows"/> rows. Every query that
        /// passes a computed page size goes through here; the rows beyond one server page come
        /// from the following pages of the same enumeration.
        /// </summary>
        public static int ServerPageSize(int rows) => Math.Min(rows, MaxServerPageSize);

        /// <summary>
        /// Fetches a single page of entities. Honours <paramref name="pageSize"/>
        /// as both the SDK <c>maxPerPage</c> and the page-size hint when
        /// resuming from <paramref name="continuation"/>.
        /// </summary>
        /// <returns>
        /// A tuple of the page entities and the raw continuation string for
        /// the next page (<c>null</c> when the page is the last).
        /// </returns>
        public static async Task<(IReadOnlyList<T> Items, string? NextRawToken)> FetchPageAsync<T>(
            TableClient client,
            string? filter,
            int pageSize,
            string? continuation,
            IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
            where T : class, ITableEntity, new()
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize), "must be >= 1");

            string[]? selectArr = null;
            if (select != null)
            {
                var list = new List<string>();
                foreach (var s in select)
                {
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                if (list.Count > 0) selectArr = list.ToArray();
            }

            var query = client.QueryAsync<T>(
                filter: filter,
                maxPerPage: ServerPageSize(pageSize),
                select: selectArr,
                cancellationToken: cancellationToken);

            await foreach (var page in query.AsPages(continuationToken: continuation, pageSizeHint: pageSize)
                .WithCancellation(cancellationToken))
            {
                return (page.Values, page.ContinuationToken);
            }

            return (Array.Empty<T>(), null);
        }
    }
}
