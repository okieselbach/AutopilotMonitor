using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Manages temporarily blocked devices (e.g. rogue devices sending excessive data).
    /// Uses IDeviceSecurityRepository for persistence, with a
    /// ConcurrentDictionary in-memory cache for fast lookups at ingest time.
    /// <para>
    /// A block is keyed by serial number (what admins and the watchdog know) and additionally
    /// mirrored onto every certificate identity (Intune device id from the client-cert CN) the
    /// device has registered sessions under — resolved once, at block time, from the Sessions
    /// rows. The kill switch checks both legs, so a device that omits or forges its serial header
    /// (CWE-807) still meets its block/kill.
    /// </para>
    /// </summary>
    public class BlockedDeviceService
    {
        private readonly IDeviceSecurityRepository _securityRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly ILogger<BlockedDeviceService> _logger;

        // Cache entries (positive AND negative) are re-validated against storage after this
        // window so that cross-instance mutations (new block from another instance, manual
        // unblock, Action upgrade Block->Kill, UnblockAt change) propagate within seconds.
        // Negative entries matter for cost: without them every ingest request from a healthy
        // (never-blocked) device paid one BlockedDevices point-read on the response path.
        private static readonly TimeSpan DefaultEntryRevalidateAfter = TimeSpan.FromSeconds(30);

        /// <summary>How many certificate identities a block is mirrored onto (newest sessions first).</summary>
        internal const int MaxAliasDeviceIds = 5;

        private readonly TimeSpan _entryRevalidateAfter;

        // Cache key: "tenantId|SERIAL" (upper-cased serial) for the serial leg and
        // "tenantId|id:<guid>" for the identity leg. Both hold a BlockCacheEntry with UnblockAt,
        // Action, BlockedSessionIds, LastCheckedUtc; identity entries also carry the primary
        // serial, serial entries the alias ids. Expired entries (UnblockAt past) are treated as
        // unblocked. Namespaces cannot collide: serial keys are upper-cased, the identity prefix
        // is lower-case.
        private readonly ConcurrentDictionary<string, BlockCacheEntry> _cache = new(StringComparer.Ordinal);

        // Tracks which tenants have had their block list loaded into the cache.
        // Lazy loading: populated on first lookup per tenant.
        private readonly ConcurrentDictionary<string, bool> _loadedTenants = new(StringComparer.OrdinalIgnoreCase);

        public BlockedDeviceService(IDeviceSecurityRepository securityRepo, ISessionRepository sessionRepo, ILogger<BlockedDeviceService> logger)
            : this(securityRepo, sessionRepo, logger, DefaultEntryRevalidateAfter)
        {
        }

        /// <summary>Test seam: lets tests shrink the revalidation window without a clock abstraction.</summary>
        internal BlockedDeviceService(
            IDeviceSecurityRepository securityRepo, ISessionRepository sessionRepo, ILogger<BlockedDeviceService> logger, TimeSpan entryRevalidateAfter)
        {
            _securityRepo = securityRepo;
            _sessionRepo = sessionRepo;
            _logger = logger;
            _entryRevalidateAfter = entryRevalidateAfter;
        }

        /// <summary>
        /// Checks whether a device is currently blocked by its serial number.
        /// Fast path: in-memory cache (loaded lazily per tenant from storage).
        /// Returns the action type ("Block" or "Kill") so callers can differentiate.
        /// When <paramref name="currentSessionId"/> is provided and the block is session-aware
        /// (BlockedSessionIds is set), auto-unblocks if the session is different.
        /// Kill actions are never auto-unblocked.
        /// <para>
        /// Cross-instance correctness: the cache is per Function App instance, but block-state
        /// mutations from <see cref="BlockDeviceAsync"/> / <see cref="UnblockDeviceAsync"/> only
        /// update the local instance's cache. Two safety nets bridge other instances to storage:
        /// <list type="bullet">
        ///   <item>Cache miss after the tenant was loaded → storage point-read on the spot,
        ///   then promote the answer (positive OR negative) into the cache. The negative entry
        ///   is the hot-path cost fix: healthy devices answer from memory instead of paying a
        ///   point-read per ingest request.</item>
        ///   <item>Cache hit older than the revalidation window → storage point-read to
        ///   re-confirm. Bounds cross-instance propagation (new Block/Kill, manual Unblock,
        ///   Block→Kill upgrade, UnblockAt change) to the window for both directions.</item>
        /// </list>
        /// </para>
        /// </summary>
        public async Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds)> IsBlockedAsync(
            string tenantId, string serialNumber, string? currentSessionId = null)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(serialNumber))
                return (false, null, "Block", null);

            var entry = await ResolveEntryAsync(
                tenantId, BuildCacheKey(tenantId, serialNumber),
                () => RefreshSerialEntryFromStorageAsync(tenantId, serialNumber));
            if (entry == null)
                return (false, null, "Block", null);

            var verdict = Evaluate(entry, BuildCacheKey(tenantId, serialNumber), tenantId, serialNumber, currentSessionId);
            return (verdict.isBlocked, verdict.unblockAt, verdict.action, verdict.blockedSessionIds);
        }

        /// <summary>
        /// Identity leg of the kill switch: is the device behind this certificate identity (Intune
        /// device id from the CN) blocked via an alias row? Same cache, same revalidation and the
        /// same session-scope semantics as <see cref="IsBlockedAsync"/>; the returned serial is the
        /// one the block was placed under (for logging and the auto-unblock, which always runs
        /// through the primary serial row so aliases go with it).
        /// </summary>
        public async Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds, string? serialNumber)> IsIdentityBlockedAsync(
            string tenantId, string intuneDeviceId, string? currentSessionId = null)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrWhiteSpace(intuneDeviceId))
                return (false, null, "Block", null, null);

            var cacheKey = BuildIdentityCacheKey(tenantId, intuneDeviceId);
            var entry = await ResolveEntryAsync(
                tenantId, cacheKey,
                () => RefreshIdentityEntryFromStorageAsync(tenantId, intuneDeviceId, cacheKey));
            if (entry == null)
                return (false, null, "Block", null, null);

            var verdict = Evaluate(entry, cacheKey, tenantId, entry.SerialNumber, currentSessionId);
            return (verdict.isBlocked, verdict.unblockAt, verdict.action, verdict.blockedSessionIds,
                verdict.isBlocked ? entry.SerialNumber : null);
        }

        /// <summary>
        /// Lazy tenant load, then cache hit (revalidated when stale) or miss (storage point-read,
        /// answer promoted positive or negative). Null = not blocked.
        /// </summary>
        private async Task<BlockCacheEntry?> ResolveEntryAsync(
            string tenantId, string cacheKey, Func<Task<BlockCacheEntry?>> refreshFromStorage)
        {
            // Lazy-load block list for this tenant if not yet done
            if (!_loadedTenants.ContainsKey(tenantId))
            {
                await LoadTenantBlockListAsync(tenantId);
            }

            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                // Stale entry (positive or negative) → revalidate against storage so cross-instance
                // mutations (new block, manual unblock, Block→Kill upgrade, UnblockAt change) propagate.
                if (DateTime.UtcNow - entry.LastCheckedUtc > _entryRevalidateAfter)
                    return await refreshFromStorage();
                return entry;
            }

            // Cache miss after tenant was loaded — another instance may have added a block
            // since our LoadTenantBlockListAsync ran. Fall through to storage for one point-read
            // so we don't blindly return "not blocked" for the lifetime of this instance.
            // The refresh caches the negative answer, so the next requests within the
            // revalidation window skip storage entirely.
            return await refreshFromStorage();
        }

        /// <summary>
        /// The verdict ladder shared by both legs: negative entry → expiry → Kill (never
        /// auto-unblocked) → whole-device → session-aware (blocked list returned when the caller
        /// has no session yet; different session ⇒ auto-unblock through the primary serial).
        /// </summary>
        private (bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds) Evaluate(
            BlockCacheEntry entry, string cacheKey, string tenantId, string? primarySerial, string? currentSessionId)
        {
            // Fresh negative entry — device known not-blocked, no storage round-trip needed.
            // Must run before the UnblockAt check below (negative entries carry no UnblockAt).
            if (!entry.IsBlocked)
                return (false, null, "Block", null);

            if (DateTime.UtcNow >= entry.UnblockAt)
            {
                // Block has expired — remove from cache
                _cache.TryRemove(cacheKey, out _);
                return (false, null, "Block", null);
            }

            // Kill actions are never auto-unblocked
            if (string.Equals(entry.Action, "Kill", StringComparison.OrdinalIgnoreCase))
                return (true, entry.UnblockAt, entry.Action, null);

            // Whole-device block (no session IDs) — always blocked
            if (string.IsNullOrEmpty(entry.BlockedSessionIds))
                return (true, entry.UnblockAt, entry.Action, null);

            // Session-aware block: caller hasn't provided session ID yet — return blocked with session IDs
            // so the caller can parse the body and call again with the actual session ID
            if (string.IsNullOrEmpty(currentSessionId))
                return (true, entry.UnblockAt, entry.Action, entry.BlockedSessionIds);

            // Session-aware block: check if the current session is one of the blocked ones
            if (TableDeviceSecurityRepository.SessionIdListContains(entry.BlockedSessionIds, currentSessionId))
                return (true, entry.UnblockAt, entry.Action, entry.BlockedSessionIds);

            // Different session — auto-unblock: new enrollment on same device
            _logger.LogWarning(
                "Auto-unblocked device (new session): TenantId={TenantId}, SerialNumber={SerialNumber}, " +
                "BlockedSessionIds={BlockedSessionIds}, NewSessionId={NewSessionId}",
                tenantId, primarySerial, entry.BlockedSessionIds, currentSessionId);

            // Remove block (primary + aliases) from storage and cache. An alias entry without a
            // known primary serial can only drop itself.
            if (!string.IsNullOrEmpty(primarySerial))
                _ = UnblockDeviceAsync(tenantId, primarySerial);
            else
                _cache.TryRemove(cacheKey, out _);
            return (false, null, "Block", null);
        }

        /// <summary>
        /// Reads the current state of (tenant, serial) directly from storage and updates the
        /// in-memory cache to match. Returns the fresh entry, or null if the device is not
        /// blocked. Used for both cache-miss fallback and stale-positive revalidation so a
        /// single instance can never get out of sync with storage by more than one read.
        /// </summary>
        private async Task<BlockCacheEntry?> RefreshSerialEntryFromStorageAsync(string tenantId, string serialNumber)
        {
            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            var (isBlocked, unblockAt, action, blockedSessionIds) =
                await _securityRepo.IsDeviceBlockedAsync(tenantId, serialNumber);

            return PromoteRefreshedEntry(cacheKey, isBlocked, unblockAt, action, blockedSessionIds,
                serialNumber: TableDeviceSecurityRepository.CanonicalizeSerial(serialNumber));
        }

        private async Task<BlockCacheEntry?> RefreshIdentityEntryFromStorageAsync(string tenantId, string intuneDeviceId, string cacheKey)
        {
            var (isBlocked, unblockAt, action, blockedSessionIds, serialNumber) =
                await _securityRepo.IsDeviceIdentityBlockedAsync(tenantId, intuneDeviceId);

            return PromoteRefreshedEntry(cacheKey, isBlocked, unblockAt, action, blockedSessionIds, serialNumber);
        }

        private BlockCacheEntry? PromoteRefreshedEntry(
            string cacheKey, bool isBlocked, DateTime? unblockAt, string? action, string? blockedSessionIds, string? serialNumber)
        {
            if (!isBlocked || unblockAt == null)
            {
                // Cache the negative answer so healthy devices don't pay a point-read per request.
                // Revalidated after the same window as positive entries, so a block set on another
                // instance still propagates within seconds.
                _cache[cacheKey] = new BlockCacheEntry
                {
                    IsBlocked = false,
                    LastCheckedUtc = DateTime.UtcNow,
                };
                return null;
            }

            var refreshed = new BlockCacheEntry
            {
                UnblockAt = unblockAt.Value,
                Action = action ?? "Block",
                BlockedSessionIds = blockedSessionIds,
                SerialNumber = serialNumber,
                LastCheckedUtc = DateTime.UtcNow,
            };
            _cache[cacheKey] = refreshed;
            return refreshed;
        }

        /// <summary>
        /// Blocks a device for the specified duration. Updates both storage and the in-memory cache.
        /// <paramref name="action"/> is "Block" (stop uploads) or "Kill" (remote self-destruct).
        /// Resolves the device's certificate identities from its sessions and mirrors the block
        /// onto them (alias rows) — callers stay serial-keyed.
        /// </summary>
        public async Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null)
        {
            var aliases = await ResolveAliasDeviceIdsAsync(tenantId, serialNumber);

            await _securityRepo.BlockDeviceAsync(tenantId, serialNumber, durationHours, blockedByEmail, reason, action, blockedSessionId, aliases);

            // Update cache immediately — merge session IDs if needed
            var unblockAt = DateTime.UtcNow.AddHours(durationHours);
            var canonicalSerial = TableDeviceSecurityRepository.CanonicalizeSerial(serialNumber);
            var cacheKey = BuildCacheKey(tenantId, serialNumber);

            var primary = _cache.AddOrUpdate(cacheKey,
                _ => new BlockCacheEntry
                {
                    UnblockAt = unblockAt,
                    Action = action ?? "Block",
                    BlockedSessionIds = blockedSessionId,
                    SerialNumber = canonicalSerial,
                    AliasDeviceIds = aliases.ToList(),
                    LastCheckedUtc = DateTime.UtcNow,
                },
                (_, existing) =>
                {
                    // A cached negative entry carries no real block scope — its null
                    // BlockedSessionIds must NOT be read as "whole-device block" below, or a
                    // session-aware auto-block would silently widen to the whole device (and
                    // skip the new-session auto-unblock) until the next revalidation.
                    var wasNegative = !existing.IsBlocked;
                    existing.IsBlocked = true;
                    existing.UnblockAt = unblockAt;
                    existing.Action = action ?? "Block";
                    existing.SerialNumber = canonicalSerial;
                    // Merge session IDs; a positive whole-device block (null) takes precedence
                    if (wasNegative)
                        existing.BlockedSessionIds = blockedSessionId; // take the new block's scope verbatim
                    else if (blockedSessionId != null && existing.BlockedSessionIds != null)
                        existing.BlockedSessionIds = TableDeviceSecurityRepository.MergeSessionId(existing.BlockedSessionIds, blockedSessionId);
                    else if (blockedSessionId == null)
                        existing.BlockedSessionIds = null; // Manual/whole-device block overrides session-aware
                    // else: existing is a positive whole-device block — keep it null
                    existing.AliasDeviceIds = wasNegative
                        ? aliases.ToList()
                        : TableDeviceSecurityRepository.MergeAliasDeviceIds(existing.AliasDeviceIds, aliases);
                    existing.LastCheckedUtc = DateTime.UtcNow;
                    return existing;
                });

            // Identity entries mirror the primary's (merged) verdict — same values, own key.
            foreach (var deviceId in primary.AliasDeviceIds)
            {
                _cache[BuildIdentityCacheKey(tenantId, deviceId)] = new BlockCacheEntry
                {
                    UnblockAt = primary.UnblockAt,
                    Action = primary.Action,
                    BlockedSessionIds = primary.BlockedSessionIds,
                    SerialNumber = canonicalSerial,
                    LastCheckedUtc = DateTime.UtcNow,
                };
            }

            _logger.LogWarning(
                "Device {Action}: TenantId={TenantId}, SerialNumber={SerialNumber}, BlockedBy={BlockedBy}, Until={UnblockAt}, Reason={Reason}, IdentityAliases={AliasCount}",
                action, tenantId, serialNumber, blockedByEmail, unblockAt, reason, primary.AliasDeviceIds.Count);
        }

        /// <summary>
        /// Certificate identities the device has registered sessions under. Fail-soft: an empty
        /// list degrades to today's serial-only block, never fails the block itself.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveAliasDeviceIdsAsync(string tenantId, string serialNumber)
        {
            try
            {
                return await _sessionRepo.GetOwnerDeviceIdsForSerialAsync(tenantId, serialNumber, MaxAliasDeviceIds)
                    ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alias resolution failed for tenant {TenantId} — block stays serial-only", tenantId);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Removes a device block immediately — primary row plus its identity aliases. Updates
        /// both storage and the in-memory cache.
        /// </summary>
        public async Task UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            var removedAliases = await _securityRepo.UnblockDeviceAsync(tenantId, serialNumber);

            // Remove from cache immediately: the serial entry, every alias storage reported and
            // every alias the local entry knew (covers an alias cached before storage was read).
            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            _cache.TryRemove(cacheKey, out var removedEntry);
            foreach (var deviceId in removedAliases.Concat(removedEntry?.AliasDeviceIds ?? new List<string>()))
                _cache.TryRemove(BuildIdentityCacheKey(tenantId, deviceId), out _);

            _logger.LogInformation("Device unblocked: TenantId={TenantId}, SerialNumber={SerialNumber}", tenantId, serialNumber);
        }

        /// <summary>
        /// Returns all currently active (non-expired) blocked devices for a tenant.
        /// Delegates to repository which also cleans up expired entries.
        /// </summary>
        public Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId)
            => _securityRepo.GetBlockedDevicesAsync(tenantId);

        /// <summary>
        /// Returns all currently active (non-expired) blocked devices across ALL tenants.
        /// Delegates to repository which also cleans up expired entries.
        /// </summary>
        public Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync()
            => _securityRepo.GetAllBlockedDevicesAsync();

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        private async Task LoadTenantBlockListAsync(string tenantId)
        {
            // Mark tenant as loaded first (before async call) to prevent parallel loads.
            // A race here just means two loads — acceptable for correctness.
            _loadedTenants[tenantId] = true;

            try
            {
                var entries = await _securityRepo.GetBlockedDevicesAsync(tenantId);
                var now = DateTime.UtcNow;

                foreach (var entry in entries)
                {
                    if (entry.UnblockAt == null || entry.UnblockAt <= now) continue;

                    // Serial keys only: the listing hides alias rows (its DTO is the admin/MCP wire
                    // shape), so identity keys are not seeded here — the identity leg point-reads
                    // its alias row on the first miss and caches the answer like the serial leg.
                    _cache[BuildCacheKey(tenantId, entry.SerialNumber)] = new BlockCacheEntry
                    {
                        UnblockAt = entry.UnblockAt.Value,
                        Action = entry.Action,
                        BlockedSessionIds = entry.BlockedSessionIds,
                        SerialNumber = TableDeviceSecurityRepository.CanonicalizeSerial(entry.SerialNumber),
                        LastCheckedUtc = now,
                    };
                }

                _logger.LogDebug("Loaded block list for tenant {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load block list for tenant {TenantId}", tenantId);
                // Remove loaded marker so it can be retried next time
                _loadedTenants.TryRemove(tenantId, out _);
            }
        }

        private class BlockCacheEntry
        {
            /// <summary>
            /// False = cached negative answer ("device not blocked", confirmed against storage at
            /// <see cref="LastCheckedUtc"/>). Negative entries carry no meaningful UnblockAt/Action.
            /// </summary>
            public bool IsBlocked { get; set; } = true;
            public DateTime UnblockAt { get; set; }
            public string Action { get; set; } = "Block";
            public string? BlockedSessionIds { get; set; }
            /// <summary>Canonical serial the block is keyed under (identity entries: the primary they mirror).</summary>
            public string? SerialNumber { get; set; }
            /// <summary>Serial entries only: identity keys mirroring this block.</summary>
            public List<string> AliasDeviceIds { get; set; } = new();
            /// <summary>
            /// Last time this entry was either loaded from storage or re-validated against it.
            /// Drives the EntryRevalidateAfter window so cross-instance mutations propagate.
            /// </summary>
            public DateTime LastCheckedUtc { get; set; }
        }

        private static string BuildCacheKey(string tenantId, string serialNumber)
            => $"{tenantId}|{serialNumber.ToUpperInvariant()}";

        private static string BuildIdentityCacheKey(string tenantId, string intuneDeviceId)
            => $"{tenantId}|{TableDeviceSecurityRepository.IdentityRowKey(intuneDeviceId)}";
    }

    // Note: BlockedDeviceEntry is now defined in AutopilotMonitor.Shared.DataAccess.IDeviceSecurityRepository
}
