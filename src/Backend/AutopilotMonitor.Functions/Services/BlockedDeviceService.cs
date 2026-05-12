using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// </summary>
    public class BlockedDeviceService
    {
        private readonly IDeviceSecurityRepository _securityRepo;
        private readonly ILogger<BlockedDeviceService> _logger;

        // Positive cache entries are re-validated against storage after this window so that
        // cross-instance mutations (manual unblock, Action upgrade Block->Kill, UnblockAt change)
        // propagate. Negative answers from a cache miss go through the storage point-read
        // on every call (see IsBlockedAsync), so no TTL is needed there.
        private static readonly TimeSpan EntryRevalidateAfter = TimeSpan.FromSeconds(30);

        // Cache key: "tenantId|serialNumber" (upper-cased serial number for case-insensitive matching)
        // Cache value: BlockCacheEntry with UnblockAt, Action, BlockedSessionIds, LastCheckedUtc.
        // Expired entries (UnblockAt past) are treated as unblocked.
        private readonly ConcurrentDictionary<string, BlockCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Tracks which tenants have had their block list loaded into the cache.
        // Lazy loading: populated on first lookup per tenant.
        private readonly ConcurrentDictionary<string, bool> _loadedTenants = new(StringComparer.OrdinalIgnoreCase);

        public BlockedDeviceService(IDeviceSecurityRepository securityRepo, ILogger<BlockedDeviceService> logger)
        {
            _securityRepo = securityRepo;
            _logger = logger;
        }

        /// <summary>
        /// Checks whether a device is currently blocked.
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
        ///   then promote into the cache. Lets a newly-set Block/Kill on another instance reach
        ///   this instance immediately instead of waiting for the lazy load that never re-runs.</item>
        ///   <item>Positive cache hit older than <see cref="EntryRevalidateAfter"/> → storage
        ///   point-read to re-confirm. Lets manual Unblock, Action upgrade (Block→Kill), or
        ///   UnblockAt changes from another instance propagate within seconds.</item>
        /// </list>
        /// Each safety net is one indexed point-read on the BlockedDevices table — negligible
        /// next to ingest cost.
        /// </para>
        /// </summary>
        public async Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds)> IsBlockedAsync(
            string tenantId, string serialNumber, string? currentSessionId = null)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(serialNumber))
                return (false, null, "Block", null);

            // Lazy-load block list for this tenant if not yet done
            if (!_loadedTenants.ContainsKey(tenantId))
            {
                await LoadTenantBlockListAsync(tenantId);
            }

            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            BlockCacheEntry? entry;

            if (_cache.TryGetValue(cacheKey, out entry))
            {
                // Stale positive entry → revalidate against storage so cross-instance mutations
                // (manual unblock, Block→Kill upgrade, UnblockAt change) propagate.
                if (DateTime.UtcNow - entry.LastCheckedUtc > EntryRevalidateAfter)
                {
                    entry = await RefreshCacheEntryFromStorageAsync(tenantId, serialNumber, cacheKey);
                    if (entry == null)
                        return (false, null, "Block", null);
                }
            }
            else
            {
                // Cache miss after tenant was loaded — another instance may have added a block
                // since our LoadTenantBlockListAsync ran. Fall through to storage for one point-read
                // so we don't blindly return "not blocked" for the lifetime of this instance.
                entry = await RefreshCacheEntryFromStorageAsync(tenantId, serialNumber, cacheKey);
                if (entry == null)
                    return (false, null, "Block", null);
            }

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
                tenantId, serialNumber, entry.BlockedSessionIds, currentSessionId);

            // Remove block from storage and cache
            _ = UnblockDeviceAsync(tenantId, serialNumber);
            return (false, null, "Block", null);
        }

        /// <summary>
        /// Reads the current state of (tenant, serial) directly from storage and updates the
        /// in-memory cache to match. Returns the fresh entry, or null if the device is not
        /// blocked. Used for both cache-miss fallback and stale-positive revalidation so a
        /// single instance can never get out of sync with storage by more than one read.
        /// </summary>
        private async Task<BlockCacheEntry?> RefreshCacheEntryFromStorageAsync(
            string tenantId, string serialNumber, string cacheKey)
        {
            var (isBlocked, unblockAt, action, blockedSessionIds) =
                await _securityRepo.IsDeviceBlockedAsync(tenantId, serialNumber);

            if (!isBlocked || unblockAt == null)
            {
                _cache.TryRemove(cacheKey, out _);
                return null;
            }

            var refreshed = new BlockCacheEntry
            {
                UnblockAt = unblockAt.Value,
                Action = action ?? "Block",
                BlockedSessionIds = blockedSessionIds,
                LastCheckedUtc = DateTime.UtcNow,
            };
            _cache[cacheKey] = refreshed;
            return refreshed;
        }

        /// <summary>
        /// Blocks a device for the specified duration. Updates both storage and the in-memory cache.
        /// <paramref name="action"/> is "Block" (stop uploads) or "Kill" (remote self-destruct).
        /// </summary>
        public async Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null)
        {
            await _securityRepo.BlockDeviceAsync(tenantId, serialNumber, durationHours, blockedByEmail, reason, action, blockedSessionId);

            // Update cache immediately — merge session IDs if needed
            var unblockAt = DateTime.UtcNow.AddHours(durationHours);
            var cacheKey = BuildCacheKey(tenantId, serialNumber);

            _cache.AddOrUpdate(cacheKey,
                _ => new BlockCacheEntry
                {
                    UnblockAt = unblockAt,
                    Action = action ?? "Block",
                    BlockedSessionIds = blockedSessionId,
                    LastCheckedUtc = DateTime.UtcNow,
                },
                (_, existing) =>
                {
                    existing.UnblockAt = unblockAt;
                    existing.Action = action ?? "Block";
                    // Merge session IDs; whole-device block (null) takes precedence
                    if (blockedSessionId != null && existing.BlockedSessionIds != null)
                        existing.BlockedSessionIds = TableDeviceSecurityRepository.MergeSessionId(existing.BlockedSessionIds, blockedSessionId);
                    else if (blockedSessionId == null)
                        existing.BlockedSessionIds = null; // Manual/whole-device block overrides session-aware
                    // else: existing is null (whole-device) — keep it null
                    existing.LastCheckedUtc = DateTime.UtcNow;
                    return existing;
                });

            _logger.LogWarning(
                "Device {Action}: TenantId={TenantId}, SerialNumber={SerialNumber}, BlockedBy={BlockedBy}, Until={UnblockAt}, Reason={Reason}",
                action, tenantId, serialNumber, blockedByEmail, unblockAt, reason);
        }

        /// <summary>
        /// Removes a device block immediately. Updates both storage and the in-memory cache.
        /// </summary>
        public async Task UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            await _securityRepo.UnblockDeviceAsync(tenantId, serialNumber);

            // Remove from cache immediately
            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            _cache.TryRemove(cacheKey, out _);

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

                    _cache[BuildCacheKey(tenantId, entry.SerialNumber)] = new BlockCacheEntry
                    {
                        UnblockAt = entry.UnblockAt.Value,
                        Action = entry.Action,
                        BlockedSessionIds = entry.BlockedSessionIds,
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
            public DateTime UnblockAt { get; set; }
            public string Action { get; set; } = "Block";
            public string? BlockedSessionIds { get; set; }
            /// <summary>
            /// Last time this entry was either loaded from storage or re-validated against it.
            /// Drives the EntryRevalidateAfter window so cross-instance mutations propagate.
            /// </summary>
            public DateTime LastCheckedUtc { get; set; }
        }

        private static string BuildCacheKey(string tenantId, string serialNumber)
            => $"{tenantId}|{serialNumber.ToUpperInvariant()}";
    }

    // Note: BlockedDeviceEntry is now defined in AutopilotMonitor.Shared.DataAccess.IDeviceSecurityRepository
}
