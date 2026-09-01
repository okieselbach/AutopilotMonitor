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

            var entity = await GetDeviceBlockEntityAsync(tenantId, serialNumber);
            if (entity == null)
                return (false, null, "Block", null);

            var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;
            if (DateTime.UtcNow >= unblockAt)
                return (false, null, "Block", null);

            var action = entity.GetString("Action") ?? "Block";
            var blockedSessionIds = entity.GetString("BlockedSessionIds");
            return (true, unblockAt, action, blockedSessionIds);
        }

        public async Task<(bool isBlocked, DateTime? unblockAt, string action, string? blockedSessionIds, string? serialNumber)> IsDeviceIdentityBlockedAsync(
            string tenantId, string intuneDeviceId)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrWhiteSpace(intuneDeviceId))
                return (false, null, "Block", null, null);

            var entity = await TryGetEntityAsync(tenantId, IdentityRowKey(intuneDeviceId));
            if (entity == null)
                return (false, null, "Block", null, null);

            var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;
            if (DateTime.UtcNow >= unblockAt)
                return (false, null, "Block", null, null);

            return (true, unblockAt, entity.GetString("Action") ?? "Block",
                entity.GetString("BlockedSessionIds"), entity.GetString("SerialNumber"));
        }

        /// <summary>
        /// Point-reads the block row for a serial under its canonical key, falling back to the
        /// legacy verbatim-case key for rows written before serials were canonicalized.
        /// Returns null when neither exists.
        /// </summary>
        private async Task<TableEntity?> GetDeviceBlockEntityAsync(string tenantId, string serialNumber)
        {
            var entity = await TryGetEntityAsync(tenantId, DeviceRowKey(serialNumber));
            if (entity != null)
                return entity;

            var legacyKey = LegacyDeviceRowKey(serialNumber);
            return legacyKey == null ? null : await TryGetEntityAsync(tenantId, legacyKey);
        }

        private async Task<TableEntity?> TryGetEntityAsync(string partitionKey, string rowKey)
        {
            try
            {
                var response = await _blockedDevicesTable.GetEntityAsync<TableEntity>(partitionKey, rowKey);
                return response?.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
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

                // Alias rows mirror a primary row (whose AliasDeviceIds names them) — listing them
                // would show the same serial twice. Expired aliases are still swept above.
                if (IsIdentityRowKey(entity.RowKey))
                    continue;

                result.Add(MapToBlockedDeviceEntry(entity, tenantId, now));
                await MigrateLegacyRowKeyAsync(entity);
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

                if (IsIdentityRowKey(entity.RowKey))
                    continue;

                result.Add(MapToBlockedDeviceEntry(entity, entity.PartitionKey, now));
                await MigrateLegacyRowKeyAsync(entity);
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
            string blockedByEmail, string? reason = null, string action = "Block", string? blockedSessionId = null,
            IReadOnlyCollection<string>? aliasDeviceIds = null)
        {
            var now = DateTime.UtcNow;
            var unblockAt = now.AddHours(durationHours);
            var canonicalSerial = CanonicalizeSerial(serialNumber);

            // One read of the current row: merges session scope and keeps aliases an earlier
            // block already resolved (a re-block never drops an identity the device was seen with).
            var existing = await GetDeviceBlockEntityAsync(tenantId, serialNumber);

            // If a session-aware block already exists, merge session IDs
            string? blockedSessionIds = blockedSessionId;
            if (!string.IsNullOrEmpty(blockedSessionId))
            {
                var existingSessionIds = existing?.GetString("BlockedSessionIds");

                if (existingSessionIds == null)
                {
                    // Existing whole-device block takes precedence — don't downgrade to session-aware
                    if (existing != null)
                        blockedSessionIds = null;
                }
                else
                {
                    // Merge: append new session ID if not already present
                    blockedSessionIds = MergeSessionId(existingSessionIds, blockedSessionId);
                }
            }

            var aliases = MergeAliasDeviceIds(ParseAliasDeviceIds(existing?.GetString("AliasDeviceIds")), aliasDeviceIds);

            var entity = new TableEntity(tenantId, DeviceRowKey(serialNumber))
            {
                ["SerialNumber"] = canonicalSerial,
                ["BlockedAt"] = now,
                ["UnblockAt"] = unblockAt,
                ["BlockedByEmail"] = blockedByEmail ?? string.Empty,
                ["DurationHours"] = durationHours,
                ["Reason"] = reason ?? string.Empty,
                ["Action"] = action ?? "Block"
            };

            if (blockedSessionIds != null)
                entity["BlockedSessionIds"] = blockedSessionIds;
            if (aliases.Count > 0)
                entity["AliasDeviceIds"] = string.Join(",", aliases);

            await _blockedDevicesTable.UpsertEntityAsync(entity);

            // Alias rows: same verdict fields under the device's certificate identity, so the kill
            // switch matches the device even when it omits or forges X-Device-SerialNumber
            // (CWE-807). Never listed (IsAlias) — the primary row is the one operators see.
            foreach (var deviceId in aliases)
            {
                var alias = new TableEntity(tenantId, IdentityRowKey(deviceId));
                foreach (var kv in entity)
                {
                    if (kv.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag" or "ETag" or "AliasDeviceIds")
                        continue;
                    alias[kv.Key] = kv.Value;
                }
                alias["IsAlias"] = true;
                await _blockedDevicesTable.UpsertEntityAsync(alias);
            }

            await DeleteLegacyRowAsync(tenantId, serialNumber);
            await _publisher.PublishAsync("device.blocked", new { tenantId, serialNumber = canonicalSerial, action, durationHours }, tenantId);
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

        public async Task<IReadOnlyList<string>> UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            // Aliases first, then the primary: a crash in between leaves a listed row that a retry
            // (or expiry) cleans up, never an invisible alias that keeps the device blocked.
            var existing = await GetDeviceBlockEntityAsync(tenantId, serialNumber);
            var aliases = ParseAliasDeviceIds(existing?.GetString("AliasDeviceIds"));
            foreach (var deviceId in aliases)
                await DeleteIgnoringNotFoundAsync(tenantId, IdentityRowKey(deviceId));

            await DeleteIgnoringNotFoundAsync(tenantId, DeviceRowKey(serialNumber));
            await DeleteLegacyRowAsync(tenantId, serialNumber);

            await _publisher.PublishAsync("device.unblocked", new { tenantId, serialNumber = CanonicalizeSerial(serialNumber) }, tenantId);
            return aliases;
        }

        /// <summary>Alias ids as stored on the primary row (comma-separated, lower-case GUIDs).</summary>
        internal static List<string> ParseAliasDeviceIds(string? stored)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(stored)) return result;
            foreach (var raw in stored.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var id = NormalizeDeviceId(raw);
                if (id != null && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                    result.Add(id);
            }
            return result;
        }

        /// <summary>Union of stored and newly resolved alias ids, order preserved (stored first).</summary>
        internal static List<string> MergeAliasDeviceIds(List<string> stored, IReadOnlyCollection<string>? resolved)
        {
            var result = new List<string>(stored);
            if (resolved == null) return result;
            foreach (var raw in resolved)
            {
                var id = NormalizeDeviceId(raw);
                if (id != null && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                    result.Add(id);
            }
            return result;
        }

        /// <summary>Canonical (lower-case "D") GUID form, null when the value is not a GUID.</summary>
        internal static string? NormalizeDeviceId(string? raw)
            => Guid.TryParse(raw?.Trim(), out var parsed) ? parsed.ToString("D") : null;

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
                // AliasDeviceIds stays storage-internal: BlockedDeviceEntry is the listing wire
                // shape (admin UI, MCP) and the identity leg point-reads alias rows on demand.
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

        /// <summary>
        /// Serial numbers are matched case-insensitively everywhere else (agent inventory
        /// validation, BlockedDeviceService cache), so the storage key must not depend on the
        /// casing the admin typed or the agent reported: trim + upper-case invariant.
        /// </summary>
        internal static string CanonicalizeSerial(string? serialNumber)
            => (serialNumber ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>Canonical BlockedDevices RowKey for a serial number.</summary>
        internal static string DeviceRowKey(string? serialNumber)
            => EncodeRowKey(CanonicalizeSerial(serialNumber));

        /// <summary>
        /// RowKey prefix of alias rows keyed by certificate identity. Cannot collide with a serial
        /// key: serial keys pass through <see cref="EncodeRowKey"/>, which turns ':' into "%3A".
        /// </summary>
        internal const string IdentityRowKeyPrefix = "id:";

        /// <summary>Alias RowKey for an Intune device id (lower-case GUID; non-GUIDs are keyed verbatim-trimmed).</summary>
        internal static string IdentityRowKey(string intuneDeviceId)
            => IdentityRowKeyPrefix + (NormalizeDeviceId(intuneDeviceId) ?? intuneDeviceId.Trim().ToLowerInvariant());

        internal static bool IsIdentityRowKey(string? rowKey)
            => rowKey != null && rowKey.StartsWith(IdentityRowKeyPrefix, StringComparison.Ordinal);

        /// <summary>
        /// RowKey a pre-canonicalization row would have been written under (verbatim case),
        /// or null when it is identical to the canonical key.
        /// </summary>
        internal static string? LegacyDeviceRowKey(string? serialNumber)
        {
            var legacy = EncodeRowKey(serialNumber ?? string.Empty);
            return string.Equals(legacy, DeviceRowKey(serialNumber), StringComparison.Ordinal) ? null : legacy;
        }

        /// <summary>
        /// Re-keys a row written under a non-canonical RowKey to its canonical key (best effort).
        /// Runs from the list paths so the pre-canonicalization backlog drains over time.
        /// </summary>
        private async Task MigrateLegacyRowKeyAsync(TableEntity entity)
        {
            // An alias row carries the primary's SerialNumber by design — re-keying it onto the
            // serial key would clobber/duplicate the primary and delete the alias.
            if (IsIdentityRowKey(entity.RowKey))
                return;

            var serial = entity.GetString("SerialNumber") ?? DecodeRowKey(entity.RowKey);
            var canonicalKey = DeviceRowKey(serial);
            if (string.Equals(entity.RowKey, canonicalKey, StringComparison.Ordinal))
                return;

            try
            {
                var migrated = new TableEntity(entity.PartitionKey, canonicalKey);
                foreach (var kv in entity)
                {
                    if (kv.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag" or "ETag")
                        continue;
                    migrated[kv.Key] = kv.Value;
                }
                migrated["SerialNumber"] = CanonicalizeSerial(serial);

                // Never clobber a canonical row that already exists (it is the newer write).
                await _blockedDevicesTable.AddEntityAsync(migrated);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Canonical row already present — just drop the legacy duplicate below.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate legacy BlockedDevices row {PartitionKey}/{RowKey}", entity.PartitionKey, entity.RowKey);
                return;
            }

            await DeleteIgnoringNotFoundAsync(entity.PartitionKey, entity.RowKey);
        }

        private async Task DeleteLegacyRowAsync(string tenantId, string serialNumber)
        {
            var legacyKey = LegacyDeviceRowKey(serialNumber);
            if (legacyKey != null)
                await DeleteIgnoringNotFoundAsync(tenantId, legacyKey);
        }

        private async Task DeleteIgnoringNotFoundAsync(string partitionKey, string rowKey)
        {
            try
            {
                await _blockedDevicesTable.DeleteEntityAsync(partitionKey, rowKey);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already removed
            }
        }

        /// <summary>Azure Table RowKey must not contain /\#? and must be &lt;= 1KB. URL-encode to be safe.</summary>
        internal static string EncodeRowKey(string value)
            => Uri.EscapeDataString(value);

        internal static string DecodeRowKey(string encoded)
            => Uri.UnescapeDataString(encoded);
    }
}
