using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Newest-first paging across the partitions of one table: global ops events (categories as
    /// partitions) and the global audit log (tenants as partitions). Azure Tables pages a
    /// cross-partition query in (PK asc, RK asc) order, which would surface one partition at a time,
    /// so every page runs one PartitionKey query per partition and merges the results.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invariant: every partition uses the same reverse-tick RowKey scheme, so the global order is
    /// (RowKey asc, Partition asc) and that order is the display order. The merge sorts by exactly
    /// that key and never by a resolved timestamp column — a merge key that disagrees with the
    /// RowKey inside one partition would let the watermark skip rows.
    /// </para>
    /// <para>
    /// The continuation is the (RowKey, Partition) of the last returned row, so its size does not
    /// depend on the number of partitions. The earlier per-partition continuation map kept one entry
    /// for every partition with rows and outgrew the host's URL limit (HTTP 414) on the tenant
    /// fan-out.
    /// </para>
    /// <para>
    /// Each partition reads at most <c>pageSize + 1</c> rows after the watermark. More rows exist
    /// exactly when more than <c>pageSize</c> were read in total: if no partition reached its limit,
    /// every partition is exhausted.
    /// </para>
    /// </remarks>
    internal static class PerPartitionFanOutMerge
    {
        /// <summary>The (RowKey, Partition) of the last row a page returned.</summary>
        internal sealed record Watermark(string RowKey, string Partition);

        /// <summary>Lower RowKey bound of one partition's read.</summary>
        internal sealed record RowKeyBound(string RowKey, bool Inclusive)
        {
            public string ToODataClause() => $"RowKey {(Inclusive ? "ge" : "gt")} '{RowKey.Replace("'", "''")}'";
        }

        /// <summary>
        /// Delegate that reads up to <c>limit</c> rows of one partition, newest first (RowKey
        /// ascending), restricted by <c>bound</c> when it is set. It must return every matching row
        /// up to the limit — a short read is taken as "partition exhausted".
        /// </summary>
        internal delegate Task<List<(string RowKey, T Item)>> PartitionReader<T>(
            string partition, RowKeyBound? bound, int limit, CancellationToken cancellationToken);

        /// <summary>
        /// The rows of <paramref name="partition"/> that come strictly after the watermark in
        /// (RowKey, Partition) order. A partition that sorts after the watermark's partition may still
        /// hold rows with the watermark's own RowKey; one that sorts before (or is the same) may not.
        /// </summary>
        internal static RowKeyBound? BoundAfter(Watermark? watermark, string partition)
        {
            if (watermark == null) return null;
            var inclusive = string.CompareOrdinal(partition, watermark.Partition) > 0;
            return new RowKeyBound(watermark.RowKey, inclusive);
        }

        public static async Task<RawPage<T>> FetchPageAsync<T>(
            IEnumerable<string> partitions,
            int pageSize,
            string? continuation,
            PartitionReader<T> readPartition,
            CancellationToken cancellationToken = default)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            Watermark? watermark = null;
            if (!string.IsNullOrEmpty(continuation))
            {
                watermark = DecodeWatermark(continuation);
                // The HMAC wrapper already rejected foreign tokens; one that passes it and still does
                // not decode was minted by an older deploy. End the list rather than restart it.
                if (watermark == null) return RawPage<T>.Empty;
            }

            var limit = pageSize + 1;
            var rows = await BoundedFanOut.RunAsync(partitions, BoundedFanOut.CrossTenantConcurrency, async (partition, ct) =>
            {
                var read = await readPartition(partition, BoundAfter(watermark, partition), limit, ct).ConfigureAwait(false);
                return read.Select(r => (Partition: partition, r.RowKey, r.Item)).ToList();
            }, cancellationToken).ConfigureAwait(false);

            return Merge(rows, pageSize);
        }

        /// <summary>
        /// Pure merge step: orders the rows read from all partitions by (RowKey, Partition), returns
        /// the first <paramref name="pageSize"/> and a continuation when rows are left over.
        /// </summary>
        internal static RawPage<T> Merge<T>(IEnumerable<(string Partition, string RowKey, T Item)> rows, int pageSize)
        {
            var ordered = rows
                .OrderBy(r => r.RowKey, StringComparer.Ordinal)
                .ThenBy(r => r.Partition, StringComparer.Ordinal)
                .ToList();
            var page = ordered.Take(pageSize).ToList();

            string? next = null;
            if (ordered.Count > pageSize)
            {
                var last = page[page.Count - 1];
                next = EncodeWatermark(new Watermark(last.RowKey, last.Partition));
            }
            return new RawPage<T>(page.Select(r => r.Item).ToList(), next);
        }

        private const int WireFormatVersion = 3;

        public static string EncodeWatermark(Watermark watermark)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["v"] = WireFormatVersion,
                ["rk"] = watermark.RowKey,
                ["p"] = watermark.Partition,
            });
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>Decodes a watermark; null for anything that is not a v3 watermark.</summary>
        public static Watermark? DecodeWatermark(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                using var doc = JsonDocument.Parse(Convert.FromBase64String(raw));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                    || !v.TryGetInt32(out var version) || version != WireFormatVersion)
                    return null;
                if (!root.TryGetProperty("rk", out var rk) || rk.ValueKind != JsonValueKind.String) return null;
                if (!root.TryGetProperty("p", out var p) || p.ValueKind != JsonValueKind.String) return null;

                var rowKey = rk.GetString();
                var partition = p.GetString();
                return string.IsNullOrEmpty(rowKey) || partition == null ? null : new Watermark(rowKey, partition);
            }
            catch (FormatException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
