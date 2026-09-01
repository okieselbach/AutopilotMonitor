using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>Fetches one chunk of the EventTypeIndex walk (at most <c>chunkSize</c> index rows).</summary>
    public delegate Task<RawPage<EventTypeIndexEntry>> IndexPageFetcher(int chunkSize, string? continuation);

    /// <summary>Fetches the (already filtered) raw event rows of one candidate session.</summary>
    public delegate Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SessionEventsFetcher(EventTypeIndexEntry entry);

    public sealed class RawEventsScanOptions
    {
        /// <summary>Index rows per page — the wire <c>pageSize</c>.</summary>
        public int PageSize { get; init; } = QueryRawEventsPagination.DefaultPageSize;

        /// <summary>
        /// Index rows fetched and fanned out per unit of work. The continuation cursor only ever
        /// points after a fully processed chunk, so the chunk is also the resume granularity.
        /// </summary>
        public int ChunkSize { get; init; } = RawEventsScan.DefaultChunkSize;

        /// <summary>Concurrent per-session event fetches within a chunk.</summary>
        public int MaxParallel { get; init; } = RawEventsScan.DefaultMaxParallel;

        /// <summary>Wall-clock budget for one request; checked between chunks.</summary>
        public TimeSpan Budget { get; init; } = RawEventsScan.DefaultBudget;
    }

    public sealed class RawEventsScanResult
    {
        public List<IReadOnlyDictionary<string, object?>> Events { get; init; } = new();

        /// <summary>Azure continuation after the last fully processed chunk; null when drained.</summary>
        public string? NextRawToken { get; init; }

        /// <summary>True when the budget ended the page while index rows remained (NextRawToken set).</summary>
        public bool Partial { get; init; }

        public int ScannedIndexRows { get; init; }
    }

    /// <summary>
    /// The cross-session raw-events walk with a server-side budget. Replaces the former
    /// "one 1000-row index page, then 1000 serial per-session queries, no deadline" shape
    /// whose worst case exceeded every client timeout and lost all of its work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invariant: every call returns within roughly <see cref="RawEventsScanOptions.Budget"/>
    /// (plus at most one chunk of overshoot) and always makes forward progress — at least one
    /// chunk is processed per call, and the returned cursor is the Azure continuation after the
    /// last chunk that was walked AND fanned out completely. A page that stops on the budget
    /// carries <see cref="RawEventsScanResult.Partial"/>; nothing on it is missing up to the
    /// cursor, the caller simply follows <c>nextLink</c> again.
    /// </para>
    /// <para>
    /// The walk is pure over two delegates so the budget/cursor semantics are unit-testable
    /// without table storage.
    /// </para>
    /// </remarks>
    public static class RawEventsScan
    {
        public const int DefaultChunkSize = 100;
        public const int DefaultMaxParallel = 20;
        public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);

        public static async Task<RawEventsScanResult> RunAsync(
            IndexPageFetcher fetchIndexPage,
            SessionEventsFetcher fetchSessionEvents,
            string? startToken,
            RawEventsScanOptions options,
            Func<DateTime>? utcNow = null)
        {
            if (fetchIndexPage == null) throw new ArgumentNullException(nameof(fetchIndexPage));
            if (fetchSessionEvents == null) throw new ArgumentNullException(nameof(fetchSessionEvents));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.PageSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "PageSize must be >= 1");
            if (options.ChunkSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "ChunkSize must be >= 1");
            if (options.MaxParallel < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxParallel must be >= 1");

            var now = utcNow ?? (() => DateTime.UtcNow);
            var deadline = now() + options.Budget;

            var token = string.IsNullOrEmpty(startToken) ? null : startToken;
            var remaining = options.PageSize;
            var events = new List<IReadOnlyDictionary<string, object?>>();
            var scanned = 0;
            var partial = false;

            while (true)
            {
                var chunk = await fetchIndexPage(Math.Min(options.ChunkSize, remaining), token);
                scanned += chunk.Items.Count;
                remaining -= chunk.Items.Count;

                if (chunk.Items.Count > 0)
                    events.AddRange(await FanOutAsync(chunk.Items, fetchSessionEvents, options.MaxParallel));

                token = string.IsNullOrEmpty(chunk.NextRawToken) ? null : chunk.NextRawToken;

                if (token == null) break;              // index drained — last page
                if (remaining <= 0) break;             // page full — normal nextLink
                if (now() >= deadline)                 // budget spent with rows left — resumable
                {
                    partial = true;
                    break;
                }
            }

            return new RawEventsScanResult
            {
                Events = events,
                NextRawToken = token,
                Partial = partial,
                ScannedIndexRows = scanned,
            };
        }

        private static async Task<List<IReadOnlyDictionary<string, object?>>> FanOutAsync(
            IReadOnlyList<EventTypeIndexEntry> entries,
            SessionEventsFetcher fetchSessionEvents,
            int maxParallel)
        {
            using var gate = new SemaphoreSlim(maxParallel, maxParallel);
            var tasks = new Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                tasks[i] = FetchGatedAsync(entries[i], fetchSessionEvents, gate);
            }

            var perSession = await Task.WhenAll(tasks);

            // Chunk order is preserved so a re-run over the same cursor yields the same sequence.
            var merged = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var rows in perSession)
                merged.AddRange(rows);
            return merged;
        }

        private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> FetchGatedAsync(
            EventTypeIndexEntry entry, SessionEventsFetcher fetch, SemaphoreSlim gate)
        {
            await gate.WaitAsync();
            try
            {
                return await fetch(entry);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
