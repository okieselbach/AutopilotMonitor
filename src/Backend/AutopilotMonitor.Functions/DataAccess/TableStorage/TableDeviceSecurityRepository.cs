using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IDeviceSecurityRepository.
    /// Pure storage operations for blocked devices and blocked versions.
    /// Caching is handled by the consuming services (BlockedDeviceService, BlockedVersionService).
    /// </summary>
    public class TableDeviceSecurityRepository : IDeviceSecurityRepository
    {
        private readonly TableClient _blockedDevicesTable;
        private readonly TableClient _blockedVersionsTable;
        private readonly IDataEventPublisher _publisher;
        private readonly ILogger<TableDeviceSecurityRepository> _logger;

        public TableDeviceSecurityRepository(
            TableStorageService storage,
            IDataEventPublisher publisher,
            ILogger<TableDeviceSecurityRepository> logger)
        {
            _publisher = publisher;
            _logger = logger;
            _blockedDevicesTable = storage.GetTableClient(Constants.TableNames.BlockedDevices);
            _blockedVersionsTable = storage.GetTableClient(Constants.TableNames.BlockedVersions);
        }

        // -----------------------------------------------------------------------
        // Blocked Devices
        // -----------------------------------------------------------------------

        public async Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds)> IsDeviceBlockedAsync(string tenantId, string serialNumber)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(serialNumber))
                return (false, null, "Block", null);

            try
            {
                var response = await _blockedDevicesTable.GetEntityAsync<TableEntity>(tenantId, EncodeRowKey(serialNumber));
                var entity = response?.Value;
                if (entity == null)
                    return (false, null, "Block", null);

                var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;
                if (DateTime.UtcNow >= unblockAt)
                    return (false, null, "Block", null);

                var action = entity.GetString("Action") ?? "Block";
                var blockedSessionIds = entity.GetString("BlockedSessionIds");
                return (true, unblockAt, action, blockedSessionIds);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (false, null, "Block", null);
            }
        }

        public async Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId)
        {
            var result = new List<BlockedDeviceEntry>();
            var expiredRowKeys = new List<string>();
            var now = DateTime.UtcNow;

            await foreach (var entity in _blockedDevicesTable.QueryAsync<TableEntity>(e => e.PartitionKey == tenantId))
            {
                var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;

                if (now >= unblockAt)
                {
                    expiredRowKeys.Add(entity.RowKey);
                    continue;
                }

                result.Add(MapToBlockedDeviceEntry(entity, tenantId, now));
            }

            // Clean up expired entries (fire-and-forget, best effort)
            _ = CleanupExpiredDeviceEntriesAsync(tenantId, expiredRowKeys);

            return result;
        }

        public async Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync()
        {
            var result = new List<BlockedDeviceEntry>();
            var expiredKeys = new List<(string partitionKey, string rowKey)>();
            var now = DateTime.UtcNow;

            await foreach (var entity in _blockedDevicesTable.QueryAsync<TableEntity>())
            {
                var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;

                if (now >= unblockAt)
                {
                    expiredKeys.Add((entity.PartitionKey, entity.RowKey));
                    continue;
                }

                result.Add(MapToBlockedDeviceEntry(entity, entity.PartitionKey, now));
            }

            // Clean up expired entries (fire-and-forget, best effort)
            foreach (var (pk, rk) in expiredKeys)
            {
                try { await _blockedDevicesTable.DeleteEntityAsync(pk, rk); }
                catch { /* best effort */ }
            }

            return result;
        }

        public async Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours,
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null)
        {
            var now = DateTime.UtcNow;
            var unblockAt = now.AddHours(durationHours);

            // If a session-aware block already exists, merge session IDs
            string? blockedSessionIds = blockedSessionId;
            if (!string.IsNullOrEmpty(blockedSessionId))
            {
                try
                {
                    var existing = await _blockedDevicesTable.GetEntityAsync<TableEntity>(tenantId, EncodeRowKey(serialNumber));
                    var existingSessionIds = existing?.Value?.GetString("BlockedSessionIds");

                    if (existingSessionIds == null)
                    {
                        // Existing whole-device block takes precedence — don't downgrade to session-aware
                        if (existing?.Value != null)
                            blockedSessionIds = null;
                    }
                    else
                    {
                        // Merge: append new session ID if not already present
                        blockedSessionIds = MergeSessionId(existingSessionIds, blockedSessionId);
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // No existing record — use single session ID
                }
            }

            var entity = new TableEntity(tenantId, EncodeRowKey(serialNumber))
            {
                ["SerialNumber"] = serialNumber,
                ["BlockedAt"] = now,
                ["UnblockAt"] = unblockAt,
                ["BlockedByEmail"] = blockedByEmail ?? string.Empty,
                ["DurationHours"] = durationHours,
                ["Reason"] = reason ?? string.Empty,
                ["Action"] = action ?? "Block"
            };

            if (blockedSessionIds != null)
                entity["BlockedSessionIds"] = blockedSessionIds;

            await _blockedDevicesTable.UpsertEntityAsync(entity);
            await _publisher.PublishAsync("device.blocked", new { tenantId, serialNumber, action, durationHours }, tenantId);
        }

        internal static string MergeSessionId(string? existingList, string newSessionId)
        {
            if (string.IsNullOrEmpty(existingList)) return newSessionId;
            if (SessionIdListContains(existingList, newSessionId)) return existingList;
            return $"{existingList},{newSessionId}";
        }

        internal static bool SessionIdListContains(string? sessionIdList, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionIdList)) return false;
            foreach (var id in sessionIdList.Split(','))
            {
                if (string.Equals(id.Trim(), sessionId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public async Task UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            try
            {
                await _blockedDevicesTable.DeleteEntityAsync(tenantId, EncodeRowKey(serialNumber));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already removed
            }

            await _publisher.PublishAsync("device.unblocked", new { tenantId, serialNumber }, tenantId);
        }

        // -----------------------------------------------------------------------
        // Blocked Versions
        // -----------------------------------------------------------------------

        public async Task<(bool isBlocked, string action, string? matchedPattern)> IsVersionBlockedAsync(string agentVersion)
        {
            if (string.IsNullOrEmpty(agentVersion))
                return (false, "Block", null);

            // Load all rules and do version matching
            string? matchedAction = null;
            string? matchedPattern = null;

            await foreach (var entity in _blockedVersionsTable.QueryAsync<TableEntity>(e => e.PartitionKey == "global"))
            {
                var pattern = entity.GetString("VersionPattern") ?? DecodeRowKey(entity.RowKey);
                var action = entity.GetString("Action") ?? "Block";

                if (VersionMatchesPattern(agentVersion, pattern))
                {
                    // Kill takes priority over Block
                    if (matchedAction == null || string.Equals(action, "Kill", StringComparison.OrdinalIgnoreCase))
                    {
                        matchedAction = action;
                        matchedPattern = pattern;
                    }

                    // If we already found a Kill, no need to check more
                    if (string.Equals(matchedAction, "Kill", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }

            return matchedAction != null
                ? (true, matchedAction, matchedPattern)
                : (false, "Block", null);
        }

        public async Task<List<BlockedVersionEntry>> GetBlockedVersionsAsync()
        {
            var result = new List<BlockedVersionEntry>();

            await foreach (var entity in _blockedVersionsTable.QueryAsync<TableEntity>(e => e.PartitionKey == "global"))
            {
                result.Add(new BlockedVersionEntry
                {
                    VersionPattern = entity.GetString("VersionPattern") ?? DecodeRowKey(entity.RowKey),
                    Action = entity.GetString("Action") ?? "Block",
                    CreatedByEmail = entity.GetString("CreatedByEmail") ?? string.Empty,
                    CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.MinValue,
                    Reason = entity.GetString("Reason")
                });
            }

            return result;
        }

        public async Task BlockVersionAsync(string versionPattern, string action, string createdByEmail, string? reason = null)
        {
            var entity = new TableEntity("global", EncodeRowKey(versionPattern))
            {
                ["VersionPattern"] = versionPattern,
                ["Action"] = action,
                ["CreatedByEmail"] = createdByEmail ?? string.Empty,
                ["CreatedAt"] = DateTime.UtcNow,
                ["Reason"] = reason ?? string.Empty
            };

            await _blockedVersionsTable.UpsertEntityAsync(entity);
            await _publisher.PublishAsync("version.blocked", new { versionPattern, action }, null);
        }

        public async Task UnblockVersionAsync(string versionPattern)
        {
            try
            {
                await _blockedVersionsTable.DeleteEntityAsync("global", EncodeRowKey(versionPattern));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already removed
            }

            await _publisher.PublishAsync("version.unblocked", new { versionPattern }, null);
        }

        // -----------------------------------------------------------------------
        // Version Matching Logic (copied from BlockedVersionService)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Checks if an agent version matches a block pattern.
        /// - "1.*"      -> agent version starts with "1." (major match)
        /// - "1.0.*"    -> agent version starts with "1.0." (major.minor match)
        /// - "1.0.30"   -> agent version parsed as semver, matches if agentVersion &lt;= pattern
        /// - "=1.0.30"  -> exact match, only version 1.0.30
        /// </summary>
        internal static bool VersionMatchesPattern(string agentVersion, string pattern)
        {
            if (string.IsNullOrEmpty(agentVersion) || string.IsNullOrEmpty(pattern))
                return false;

            // Exact match: "=1.0.30" matches only that specific version
            if (pattern.StartsWith("="))
            {
                var exactVersion = pattern.Substring(1);
                if (TryParseVersion(agentVersion, out var agentExact) && TryParseVersion(exactVersion, out var patternExact))
                    return CompareVersionParts(agentExact!, patternExact!) == 0;
                return string.Equals(agentVersion, exactVersion, StringComparison.OrdinalIgnoreCase);
            }

            // Wildcard patterns: prefix match
            if (pattern.EndsWith(".*"))
            {
                var prefix = pattern.Substring(0, pattern.Length - 1); // "1." or "1.0."
                return agentVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            // Version ceiling: <= comparison
            if (TryParseVersion(agentVersion, out var agentParts) && TryParseVersion(pattern, out var patternParts))
            {
                return CompareVersionParts(agentParts!, patternParts!) <= 0;
            }

            // Fallback: exact string match
            return string.Equals(agentVersion, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseVersion(string version, out int[]? parts)
        {
            parts = null;
            if (string.IsNullOrEmpty(version)) return false;

            var dashIndex = version.IndexOf('-');
            if (dashIndex >= 0) version = version.Substring(0, dashIndex);
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0) version = version.Substring(0, plusIndex);

            var segments = version.Split('.');
            var parsed = new List<int>();

            foreach (var seg in segments)
            {
                if (int.TryParse(seg, out var n))
                    parsed.Add(n);
                else
                    return false;
            }

            if (parsed.Count == 0) return false;
            parts = parsed.ToArray();
            return true;
        }

        private static int CompareVersionParts(int[] a, int[] b)
        {
            var maxLen = Math.Max(a.Length, b.Length);
            for (int i = 0; i < maxLen; i++)
            {
                var av = i < a.Length ? a[i] : 0;
                var bv = i < b.Length ? b[i] : 0;
                if (av != bv) return av.CompareTo(bv);
            }
            return 0;
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        private static BlockedDeviceEntry MapToBlockedDeviceEntry(TableEntity entity, string tenantId, DateTime now)
        {
            return new BlockedDeviceEntry
            {
                TenantId = tenantId,
                SerialNumber = entity.GetString("SerialNumber") ?? DecodeRowKey(entity.RowKey),
                BlockedAt = entity.GetDateTimeOffset("BlockedAt")?.UtcDateTime ?? now,
                UnblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue,
                BlockedByEmail = entity.GetString("BlockedByEmail"),
                DurationHours = entity.GetInt32("DurationHours") ?? 12,
                Reason = entity.GetString("Reason"),
                Action = entity.GetString("Action") ?? "Block",
                BlockedSessionIds = entity.GetString("BlockedSessionIds")
            };
        }

        private async Task CleanupExpiredDeviceEntriesAsync(string tenantId, List<string> rowKeys)
        {
            foreach (var rowKey in rowKeys)
            {
                try { await _blockedDevicesTable.DeleteEntityAsync(tenantId, rowKey); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Azure Table RowKey must not contain /\#? and must be &lt;= 1KB. URL-encode to be safe.</summary>
        internal static string EncodeRowKey(string value)
            => Uri.EscapeDataString(value);

        internal static string DecodeRowKey(string encoded)
            => Uri.UnescapeDataString(encoded);
    }
}
