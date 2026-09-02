using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Caching
{
    /// <summary>
    /// Per-instance read cache with single-flight semantics: concurrent callers of the same key
    /// share ONE in-flight factory task instead of each starting their own (an aggregate that
    /// takes seconds must never be computed N times because N dashboard tabs refreshed at once).
    /// Entries expire after their TTL; a faulted factory is evicted immediately so a transient
    /// storage failure is never served for the rest of the TTL.
    ///
    /// Scope is deliberately ONE process: Flex Consumption instances do not share memory, so a
    /// write on instance A is visible on instance B only after B's entry expires. Every user of
    /// this cache therefore pairs it with (a) a short TTL and (b) explicit <see cref="Invalidate"/>
    /// calls on the write paths of the same instance.
    ///
    /// The factory runs WITHOUT any caller's cancellation token on purpose: the task is shared,
    /// so one caller's abort must not fault every other waiter — and a finished result is exactly
    /// what the aborted caller's retry wants to find.
    /// </summary>
    internal sealed class SingleFlightCache<T>
    {
        private sealed class Entry
        {
            public Entry(Lazy<Task<T>> task, DateTime expiresUtc)
            {
                Task = task;
                ExpiresUtc = expiresUtc;
            }

            public Lazy<Task<T>> Task { get; }
            public DateTime ExpiresUtc { get; }
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Func<DateTime> _utcNow;
        private readonly int _maxEntries;

        /// <param name="maxEntries">
        /// Upper bound on live entries. When a NEW key is added beyond it, every other key is
        /// dropped — a single-slot cache (maxEntries = 1) for large snapshots keeps at most one
        /// window's rows in memory instead of one per distinct window requested within the TTL.
        /// </param>
        public SingleFlightCache(int maxEntries = int.MaxValue) : this(() => DateTime.UtcNow, maxEntries) { }

        /// <summary>Clock seam for tests.</summary>
        internal SingleFlightCache(Func<DateTime> utcNow, int maxEntries = int.MaxValue)
        {
            if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            _utcNow = utcNow;
            _maxEntries = maxEntries;
        }

        /// <summary>
        /// Returns the cached value for <paramref name="key"/>, or runs <paramref name="factory"/>
        /// exactly once for all concurrent callers and caches its result for <paramref name="ttl"/>.
        /// A factory failure propagates to every waiter and leaves nothing behind.
        /// </summary>
        public async Task<T> GetOrAddAsync(string key, TimeSpan ttl, Func<Task<T>> factory)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            var now = _utcNow();
            EvictExpired(now);

            var added = false;
            var entry = _entries.GetOrAdd(key, _ =>
            {
                added = true;
                return CreateEntry(factory, now, ttl);
            });

            if (!added && entry.ExpiresUtc <= now)
            {
                // Expired: swap in a fresh entry, but only if nobody else already did — the loser
                // of the race simply joins the winner's in-flight task.
                var fresh = CreateEntry(factory, now, ttl);
                entry = _entries.TryUpdate(key, fresh, entry) ? fresh : _entries.GetOrAdd(key, fresh);
            }

            if (added && _entries.Count > _maxEntries)
            {
                foreach (var otherKey in _entries.Keys)
                {
                    if (!string.Equals(otherKey, key, StringComparison.Ordinal))
                        _entries.TryRemove(otherKey, out _);
                }
            }

            try
            {
                return await entry.Task.Value.ConfigureAwait(false);
            }
            catch
            {
                // Evict the faulted entry so the next caller retries instead of re-throwing a
                // cached exception for the rest of the TTL. Only remove OUR entry — a newer one
                // may already be in place.
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                throw;
            }
        }

        /// <summary>Drops one key. Safe to call for keys that were never cached.</summary>
        public void Invalidate(string key)
        {
            if (key == null) return;
            _entries.TryRemove(key, out _);
        }

        /// <summary>Drops every key.</summary>
        public void Clear() => _entries.Clear();

        /// <summary>Number of live entries — test/diagnostic seam.</summary>
        internal int Count => _entries.Count;

        private void EvictExpired(DateTime now)
        {
            // The dictionaries behind this type hold a handful of keys; a linear sweep per call is
            // cheaper than a timer and guarantees an expired snapshot does not outlive its TTL by
            // more than one access.
            foreach (var pair in _entries)
            {
                if (pair.Value.ExpiresUtc <= now)
                    _entries.TryRemove(pair);
            }
        }

        private static Entry CreateEntry(Func<Task<T>> factory, DateTime now, TimeSpan ttl)
        {
            // Lazy defers the factory to the first awaiter, so it runs on the caller's context
            // and any synchronous prefix stays attributable to that request.
            var lazy = new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
            return new Entry(lazy, now.Add(ttl));
        }
    }
}
